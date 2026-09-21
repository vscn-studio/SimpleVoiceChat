using System.Security.Cryptography;

namespace SimpleVoiceChat.Networking;

/// <summary>One pending credential and one revocable connection per player.</summary>
internal sealed class WebMicrophoneCredentials
{
    private readonly object gate = new();
    private readonly Dictionary<string, Credential> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WebMicrophoneLease> active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceCredential> devices = new(StringComparer.Ordinal);
    private static readonly TimeSpan DeviceLifetime = TimeSpan.FromDays(30);

    public bool Issue(string playerUid, string token, DateTimeOffset expiresAt, int sessionId, int epoch, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 256
            || sessionId <= 0 || expiresAt <= now || expiresAt > now.AddMinutes(10)) return false;
        lock (gate)
        {
            foreach (string key in pending.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToArray())
                pending.Remove(key);
            if (pending.ContainsKey(token)) return false;
            Revoke(playerUid);
            pending[token] = new(playerUid, expiresAt, sessionId, epoch);
            return true;
        }
    }

    public WebMicrophoneLease? Consume(string token, DateTimeOffset now)
    {
        lock (gate)
        {
            string playerUid;
            int sessionId;
            int epoch;
            string deviceToken;
            if (pending.Remove(token, out Credential? credential))
            {
                if (credential.ExpiresAt <= now) return null;
                playerUid = credential.PlayerUid;
                sessionId = credential.SessionId;
                epoch = credential.Epoch;
                deviceToken = CreateDeviceToken();
                devices[deviceToken] = new(playerUid, sessionId, epoch, now.Add(DeviceLifetime));
            }
            else if (devices.TryGetValue(token, out DeviceCredential? device) && device.ExpiresAt > now)
            {
                playerUid = device.PlayerUid;
                sessionId = device.SessionId;
                epoch = device.Epoch;
                deviceToken = token;
                device.ExpiresAt = now.Add(DeviceLifetime);
            }
            else return null;

            if (active.Remove(playerUid, out var previous)) previous.Revoked.Cancel();
            var lease = new WebMicrophoneLease(playerUid, sessionId, epoch, deviceToken);
            active[playerUid] = lease;
            return lease;
        }
    }

    public bool IsActive(WebMicrophoneLease lease)
    {
        lock (gate)
            return active.TryGetValue(lease.PlayerUid, out var current) && ReferenceEquals(current, lease);
    }

    public void Revoke(string playerUid)
    {
        lock (gate)
        {
            foreach (string key in pending.Where(p => p.Value.PlayerUid == playerUid).Select(p => p.Key).ToArray())
                pending.Remove(key);
            foreach (string key in devices.Where(p => p.Value.PlayerUid == playerUid).Select(p => p.Key).ToArray())
                devices.Remove(key);
            if (active.Remove(playerUid, out var lease)) lease.Revoked.Cancel();
        }
    }

    public void Release(WebMicrophoneLease lease)
    {
        lock (gate)
        {
            if (active.TryGetValue(lease.PlayerUid, out var current) && ReferenceEquals(current, lease))
                active.Remove(lease.PlayerUid);
            lease.Revoked.Dispose();
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            pending.Clear();
            devices.Clear();
            foreach (var lease in active.Values) lease.Revoked.Cancel();
            active.Clear();
        }
    }

    private sealed record Credential(string PlayerUid, DateTimeOffset ExpiresAt, int SessionId, int Epoch);
    private sealed class DeviceCredential(string playerUid, int sessionId, int epoch, DateTimeOffset expiresAt)
    {
        public string PlayerUid { get; } = playerUid;
        public int SessionId { get; } = sessionId;
        public int Epoch { get; } = epoch;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
    }

    private static string CreateDeviceToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

internal sealed class WebMicrophoneLease(string playerUid, int sessionId, int epoch, string deviceToken)
{
    public string PlayerUid { get; } = playerUid;
    public int SessionId { get; } = sessionId;
    public int Epoch { get; } = epoch;
    public string DeviceToken { get; } = deviceToken;
    public CancellationTokenSource Revoked { get; } = new();
    public ushort Sequence { get; set; }
    public long ActivationUntilMilliseconds { get; set; }
    public long LastLevelMilliseconds { get; set; }
    public long FrameWindowMilliseconds { get; set; }
    public int FrameCount { get; set; }
    public ushort FeedbackSequence { get; set; }
    public int LastTestId { get; set; }
    public long LastTestFrameMilliseconds { get; set; }
    private readonly object frameGate = new();
    private PendingWebFrame? pendingFrame;
    private readonly Queue<PendingWebFrame> pendingTestFrames = new();
    private bool framePumpActive;
    private float pendingLevel;
    private bool hasPendingLevel;
    private bool levelPumpActive;

    public bool QueueFrame(PendingWebFrame frame)
    {
        lock (frameGate)
        {
            if (frame.TestId != 0)
            {
                // Test playback must preserve timing. Keep a bounded FIFO for
                // test frames; live voice still uses latest-frame replacement.
                if (pendingTestFrames.Count >= 150) pendingTestFrames.Dequeue();
                pendingTestFrames.Enqueue(frame);
            }
            else
            {
                pendingFrame = frame;
            }
            if (framePumpActive) return false;
            framePumpActive = true;
            return true;
        }
    }

    public bool TryTakeFrame(out PendingWebFrame? frame)
    {
        lock (frameGate)
        {
            if (pendingTestFrames.Count > 0)
            {
                frame = pendingTestFrames.Dequeue();
                return true;
            }
            if (pendingFrame == null)
            {
                framePumpActive = false;
                frame = null;
                return false;
            }
            frame = pendingFrame;
            pendingFrame = null;
            return true;
        }
    }

    public bool QueueLevel(float rms)
    {
        lock (frameGate)
        {
            pendingLevel = rms;
            hasPendingLevel = true;
            if (levelPumpActive) return false;
            levelPumpActive = true;
            return true;
        }
    }

    public bool TryTakeLevel(out float rms)
    {
        lock (frameGate)
        {
            if (!hasPendingLevel)
            {
                levelPumpActive = false;
                rms = 0;
                return false;
            }
            rms = pendingLevel;
            hasPendingLevel = false;
            return true;
        }
    }
}

internal sealed record PendingWebFrame(short[] Samples, int TestId, byte[] Payload, double Rms);
