namespace SimpleVoiceChat.Networking;

public sealed class VoiceProbeTracker
{
    private const int SampleCapacity = 20;
    private readonly object sync = new();
    private readonly Dictionary<int, long> pendingByNonce = new();
    private readonly Queue<bool> recentResults = new();
    private long lastReplyMilliseconds = long.MinValue;
    private double smoothedRttMilliseconds = -1;

    public double SmoothedRttMilliseconds
    {
        get
        {
            lock (sync)
            {
                return smoothedRttMilliseconds;
            }
        }
    }

    public double LossPercent
    {
        get
        {
            lock (sync)
            {
                return recentResults.Count == 0
                    ? 0
                    : recentResults.Count(result => !result) * 100d / recentResults.Count;
            }
        }
    }

    public void MarkSent(int nonce, long nowMilliseconds)
    {
        if (nonce <= 0)
        {
            return;
        }

        lock (sync)
        {
            pendingByNonce[nonce] = nowMilliseconds;
        }
    }

    public bool MarkReply(int nonce, long nowMilliseconds)
    {
        lock (sync)
        {
            if (!pendingByNonce.Remove(nonce, out long sentMilliseconds))
            {
                return false;
            }

            double sample = Math.Clamp(nowMilliseconds - sentMilliseconds, 0, 60_000);
            smoothedRttMilliseconds = smoothedRttMilliseconds < 0
                ? sample
                : smoothedRttMilliseconds * 0.8d + sample * 0.2d;
            lastReplyMilliseconds = nowMilliseconds;
            RecordResult(received: true);
            return true;
        }
    }

    public void Expire(long nowMilliseconds, long timeoutMilliseconds)
    {
        lock (sync)
        {
            foreach (int nonce in pendingByNonce
                         .Where(pair => nowMilliseconds - pair.Value > timeoutMilliseconds)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                pendingByNonce.Remove(nonce);
                RecordResult(received: false);
            }
        }
    }

    public bool IsResponsive(long nowMilliseconds, long timeoutMilliseconds)
    {
        lock (sync)
        {
            return lastReplyMilliseconds != long.MinValue
                && nowMilliseconds - lastReplyMilliseconds <= timeoutMilliseconds;
        }
    }

    public void Reset()
    {
        lock (sync)
        {
            pendingByNonce.Clear();
            recentResults.Clear();
            lastReplyMilliseconds = long.MinValue;
            smoothedRttMilliseconds = -1;
        }
    }

    private void RecordResult(bool received)
    {
        recentResults.Enqueue(received);
        while (recentResults.Count > SampleCapacity)
        {
            recentResults.Dequeue();
        }
    }
}
