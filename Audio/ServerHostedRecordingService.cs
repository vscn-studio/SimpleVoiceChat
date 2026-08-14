using System.Text;
using System.Text.Json;
using SimpleVoiceChat.Networking;

namespace SimpleVoiceChat.Audio;

/// <summary>
/// Authoritative multi-track recorder owned by the server. Voice frames arrive
/// over the reliable control channel, so an administrator client can disappear
/// without losing or terminating the server-side session.
/// </summary>
public sealed class ServerHostedRecordingService : IDisposable
{
    public const string StateFileName = "recording-state.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object gate = new();
    private readonly string rootDirectory;
    private readonly long checkpointIntervalMilliseconds;
    private readonly Dictionary<string, HostedTrack> tracks = new(StringComparer.Ordinal);
    private HostedRecordingState? state;
    private long lastCheckpointMilliseconds;
    private bool disposed;

    public ServerHostedRecordingService(string rootDirectory, int checkpointSeconds = 5)
    {
        this.rootDirectory = Path.GetFullPath(rootDirectory);
        checkpointIntervalMilliseconds = Math.Clamp(checkpointSeconds, 1, 60) * 1000L;
        Directory.CreateDirectory(this.rootDirectory);
        RecoverInterruptedSessions();
    }

    public string RootDirectory => rootDirectory;

    public bool IsActive
    {
        get
        {
            lock (gate)
            {
                return state?.Status == "active";
            }
        }
    }

    public string ActiveSessionId
    {
        get
        {
            lock (gate)
            {
                return state?.Status == "active" ? state.SessionId : string.Empty;
            }
        }
    }

    public HostedRecordingSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return BuildSnapshot();
            }
        }
    }

    public bool Start(
        string sessionId,
        string ownerUid,
        string ownerName,
        long startServerTimestampMilliseconds,
        long startUtcUnixMilliseconds,
        out string error)
    {
        lock (gate)
        {
            error = string.Empty;
            if (disposed)
            {
                error = "The server recording service is unavailable.";
                return false;
            }
            if (state?.Status == "active")
            {
                error = "A server-hosted recording is already active.";
                return false;
            }

            try
            {
                string safeSessionId = SanitizeComponent(sessionId, "multitrack", 120);
                string directory = GetSessionDirectory(safeSessionId);
                Directory.CreateDirectory(directory);
                tracks.Clear();
                state = new HostedRecordingState
                {
                    Status = "active",
                    SessionId = safeSessionId,
                    OwnerUid = ownerUid,
                    OwnerName = ownerName,
                    StartServerTimestampMilliseconds = Math.Max(0L, startServerTimestampMilliseconds),
                    StartUtcUnixMilliseconds = Math.Max(0L, startUtcUnixMilliseconds),
                    LastServerTimestampMilliseconds = Math.Max(0L, startServerTimestampMilliseconds),
                    UpdatedUtcUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                lastCheckpointMilliseconds = 0L;
                WriteState(flushTracks: true);
                return true;
            }
            catch (Exception ex)
            {
                state = null;
                tracks.Clear();
                error = ex.Message;
                return false;
            }
        }
    }

    public void Append(
        string speakerUid,
        string speakerName,
        int connectionEpoch,
        int voiceSessionId,
        ushort sequence,
        int codec,
        ReadOnlySpan<byte> payload,
        long captureServerTimestampMilliseconds,
        long receivedServerTimestampMilliseconds)
    {
        lock (gate)
        {
            if (disposed || state?.Status != "active" || string.IsNullOrWhiteSpace(speakerUid) || payload.IsEmpty)
            {
                return;
            }

            long timestamp = captureServerTimestampMilliseconds > 0
                ? captureServerTimestampMilliseconds
                : Math.Max(0L, receivedServerTimestampMilliseconds);
            if (timestamp < state.StartServerTimestampMilliseconds)
            {
                return;
            }

            long streamSessionKey = ((long)connectionEpoch << 32) | unchecked((uint)voiceSessionId);
            HostedTrack track = GetOrCreateTrack(speakerUid, speakerName, codec);
            track.ObservePacket(streamSessionKey, sequence, captureServerTimestampMilliseconds <= 0);
            if (!track.TryDecode(codec, streamSessionKey, payload, out short[] samples))
            {
                track.DecodeFailures++;
                return;
            }

            long targetFrame = (timestamp - state.StartServerTimestampMilliseconds) * VoiceConstants.SampleRate / 1000L;
            if (!track.Writer.TryWriteAt(targetFrame, samples))
            {
                track.LateFrames++;
                return;
            }

            track.DecodedFrames++;
            state.LastServerTimestampMilliseconds = Math.Max(state.LastServerTimestampMilliseconds, timestamp);
            if (receivedServerTimestampMilliseconds - lastCheckpointMilliseconds >= checkpointIntervalMilliseconds)
            {
                lastCheckpointMilliseconds = receivedServerTimestampMilliseconds;
                WriteState(flushTracks: true);
            }
        }
    }

    public void Checkpoint(long nowServerTimestampMilliseconds)
    {
        lock (gate)
        {
            if (disposed || state?.Status != "active"
                || nowServerTimestampMilliseconds - lastCheckpointMilliseconds < checkpointIntervalMilliseconds)
            {
                return;
            }
            lastCheckpointMilliseconds = nowServerTimestampMilliseconds;
            WriteState(flushTracks: true);
        }
    }

    public void ObserveParticipant(
        string playerUid,
        string playerName,
        bool connected,
        long serverTimestampMilliseconds,
        string reason)
    {
        lock (gate)
        {
            if (disposed || state?.Status != "active" || string.IsNullOrWhiteSpace(playerUid))
            {
                return;
            }

            state.ParticipantEvents ??= new List<HostedParticipantEvent>();
            state.ParticipantEvents.Add(new HostedParticipantEvent
            {
                PlayerUid = playerUid,
                PlayerName = string.IsNullOrWhiteSpace(playerName) ? playerUid : playerName,
                Connected = connected,
                ServerTimestampMilliseconds = Math.Max(state.StartServerTimestampMilliseconds, serverTimestampMilliseconds),
                Reason = reason ?? string.Empty
            });
            state.LastServerTimestampMilliseconds = Math.Max(
                state.LastServerTimestampMilliseconds,
                serverTimestampMilliseconds);
            WriteState(flushTracks: true);
        }
    }

    public bool Stop(long endServerTimestampMilliseconds, string reason, out HostedRecordingSessionResult result, out string error)
    {
        lock (gate)
        {
            result = default;
            error = string.Empty;
            if (state?.Status != "active")
            {
                error = "No server-hosted recording is active.";
                return false;
            }

            try
            {
                long end = Math.Max(state.StartServerTimestampMilliseconds, endServerTimestampMilliseconds);
                long sessionFrames = Math.Max(
                    (end - state.StartServerTimestampMilliseconds) * VoiceConstants.SampleRate / 1000L,
                    tracks.Values.Select(track => track.Writer.SampleFrames).DefaultIfEmpty(0L).Max());
                end = state.StartServerTimestampMilliseconds
                    + (long)Math.Ceiling(sessionFrames * 1000d / VoiceConstants.SampleRate);
                foreach (HostedTrack track in tracks.Values)
                {
                    track.Writer.PadTo(sessionFrames);
                    track.Writer.Complete();
                }

                state.Status = "completed";
                state.StopReason = string.IsNullOrWhiteSpace(reason) ? "requested" : reason;
                state.EndServerTimestampMilliseconds = end;
                state.LastServerTimestampMilliseconds = Math.Max(state.LastServerTimestampMilliseconds, end);
                state.SampleFrames = sessionFrames;
                state.UpdatedUtcUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                SynchronizeTrackStates();
                WriteCoreManifest(state, GetSessionDirectory(state.SessionId));
                WriteState(flushTracks: true);

                string completedSessionId = state.SessionId;
                string completedDirectory = GetSessionDirectory(completedSessionId);
                result = new HostedRecordingSessionResult(
                    completedSessionId,
                    completedDirectory,
                    state.SampleFrames,
                    tracks.Count,
                    tracks.Values.Sum(track => track.MissingPackets),
                    tracks.Values.Sum(track => track.FallbackTimestampFrames));
                DisposeTracks();
                state = null;
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    if (state is { } failedState)
                    {
                        failedState.Status = "incomplete";
                        failedState.StopReason = $"{reason}-finalization-error";
                        failedState.EndServerTimestampMilliseconds = Math.Max(
                            failedState.StartServerTimestampMilliseconds,
                            endServerTimestampMilliseconds);
                        failedState.UpdatedUtcUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        SynchronizeTrackStates();
                        WriteState(flushTracks: false);
                    }
                }
                catch
                {
                }
                DisposeTracks();
                state = null;
                error = ex.Message;
                return false;
            }
        }
    }

    public bool TryGetCompletedSession(string sessionId, out HostedRecordingSessionFiles session)
    {
        lock (gate)
        {
            session = default;
            string safeSessionId = SanitizeComponent(sessionId, string.Empty, 120);
            if (safeSessionId.Length == 0 || !string.Equals(safeSessionId, sessionId, StringComparison.Ordinal))
            {
                return false;
            }

            string directory = GetSessionDirectory(safeSessionId);
            string statePath = Path.Combine(directory, StateFileName);
            string corePath = Path.Combine(directory, "session.core.json");
            if (!File.Exists(statePath) || !File.Exists(corePath))
            {
                return false;
            }

            HostedRecordingState? stored;
            try
            {
                stored = JsonSerializer.Deserialize<HostedRecordingState>(File.ReadAllText(statePath));
            }
            catch
            {
                return false;
            }
            if (stored == null || stored.Status is not ("completed" or "recovered"))
            {
                return false;
            }

            string[] files = Directory.EnumerateFiles(directory)
                .Where(path => Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path) is "session.core.json" or StateFileName)
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToArray();
            if (!files.Any(path => Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
            session = new HostedRecordingSessionFiles(safeSessionId, directory, files);
            return true;
        }
    }

    public string[] ListCompletedSessionIds(int maximum = 20)
    {
        lock (gate)
        {
            return Directory.EnumerateDirectories(rootDirectory, "multitrack-*")
                .Where(directory => File.Exists(Path.Combine(directory, "session.core.json")))
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .Take(Math.Clamp(maximum, 1, 100))
                .Select(Path.GetFileName)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
        }
    }

    public static string BuildTrackFileName(string speakerName, string speakerUid)
    {
        string safeName = SanitizeComponent(speakerName, "player", 48);
        string safeUid = SanitizeComponent(speakerUid, "unknown", 72);
        return $"{safeName}-{safeUid}.wav";
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            if (state?.Status == "active")
            {
                _ = Stop(state.LastServerTimestampMilliseconds, "server-shutdown", out _, out _);
            }
            disposed = true;
            DisposeTracks();
            state = null;
        }
    }

    private HostedTrack GetOrCreateTrack(string uid, string name, int codec)
    {
        if (tracks.TryGetValue(uid, out HostedTrack? existing))
        {
            return existing;
        }

        string directory = GetSessionDirectory(state!.SessionId);
        string fileName = BuildTrackFileName(name, uid);
        if (tracks.Values.Any(track => string.Equals(track.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
        {
            fileName = $"{Path.GetFileNameWithoutExtension(fileName)}-{unchecked((uint)VoiceMath.StableUidHash(uid)):x8}.wav";
        }
        string path = Path.Combine(directory, fileName);
        HostedTrack created = new(uid, name, fileName, path, codec);
        tracks[uid] = created;
        SynchronizeTrackStates();
        WriteState(flushTracks: true);
        return created;
    }

    private HostedRecordingSnapshot BuildSnapshot()
    {
        if (state?.Status != "active")
        {
            return default;
        }
        return new HostedRecordingSnapshot(
            true,
            state.SessionId,
            state.OwnerUid,
            state.OwnerName,
            state.StartServerTimestampMilliseconds,
            tracks.Count,
            tracks.Values.Sum(track => track.PacketCount),
            tracks.Values.Sum(track => track.MissingPackets),
            tracks.Values.Sum(track => track.FallbackTimestampFrames),
            tracks.Values.Sum(track => track.Writer.SampleFrames * sizeof(short)));
    }

    private void WriteState(bool flushTracks)
    {
        if (state == null)
        {
            return;
        }
        if (flushTracks)
        {
            foreach (HostedTrack track in tracks.Values)
            {
                track.Writer.FlushToDisk();
            }
        }
        SynchronizeTrackStates();
        state.UpdatedUtcUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        WriteJsonAtomically(Path.Combine(GetSessionDirectory(state.SessionId), StateFileName), state);
    }

    private void SynchronizeTrackStates()
    {
        if (state == null)
        {
            return;
        }
        state.Tracks = tracks.Values
            .OrderBy(track => track.SpeakerUid, StringComparer.Ordinal)
            .Select(track => track.ToState())
            .ToList();
    }

    private static void WriteCoreManifest(HostedRecordingState state, string directory)
    {
        var manifest = new
        {
            sessionId = state.SessionId,
            serverHosted = true,
            status = state.Status,
            stopReason = state.StopReason,
            owner = new { uid = state.OwnerUid, name = state.OwnerName },
            timeline = new
            {
                serverStartMilliseconds = state.StartServerTimestampMilliseconds,
                serverEndMilliseconds = state.EndServerTimestampMilliseconds,
                utcStartUnixMilliseconds = state.StartUtcUnixMilliseconds
            },
            startUtcUnixMilliseconds = state.StartUtcUnixMilliseconds,
            sampleRate = VoiceConstants.SampleRate,
            frameMilliseconds = VoiceConstants.FrameMilliseconds,
            sampleFrames = state.SampleFrames,
            tracks = state.Tracks.Select(track => new
            {
                uid = track.SpeakerUid,
                name = track.SpeakerName,
                file = track.FileName,
                packets = track.PacketCount,
                missingPackets = track.MissingPackets,
                fallbackTimestampFrames = track.FallbackTimestampFrames,
                decodeFailures = track.DecodeFailures,
                lateFrames = track.LateFrames
            }).ToArray(),
            participantEvents = (state.ParticipantEvents ?? new List<HostedParticipantEvent>()).Select(item => new
            {
                uid = item.PlayerUid,
                name = item.PlayerName,
                connected = item.Connected,
                serverTimestampMilliseconds = item.ServerTimestampMilliseconds,
                reason = item.Reason
            }).ToArray()
        };
        WriteJsonAtomically(Path.Combine(directory, "session.core.json"), manifest);
        MultiTrackSessionManifest.Merge(directory);
    }

    private void RecoverInterruptedSessions()
    {
        foreach (string directory in Directory.EnumerateDirectories(rootDirectory, "multitrack-*"))
        {
            string statePath = Path.Combine(directory, StateFileName);
            if (!File.Exists(statePath))
            {
                continue;
            }

            try
            {
                HostedRecordingState? interrupted = JsonSerializer.Deserialize<HostedRecordingState>(File.ReadAllText(statePath));
                if (interrupted?.Status != "active")
                {
                    continue;
                }

                interrupted.Tracks ??= new List<HostedTrackState>();
                interrupted.ParticipantEvents ??= new List<HostedParticipantEvent>();
                long maximumFrames = 0L;
                foreach (HostedTrackState track in interrupted.Tracks)
                {
                    string path = Path.Combine(directory, Path.GetFileName(track.FileName));
                    track.SampleFrames = RepairWaveFile(path);
                    maximumFrames = Math.Max(maximumFrames, track.SampleFrames);
                }
                foreach (HostedTrackState track in interrupted.Tracks)
                {
                    string path = Path.Combine(directory, Path.GetFileName(track.FileName));
                    PadRecoveredWave(path, maximumFrames);
                    track.SampleFrames = maximumFrames;
                }

                interrupted.Status = "recovered";
                interrupted.StopReason = "server-restart-recovery";
                interrupted.SampleFrames = maximumFrames;
                interrupted.EndServerTimestampMilliseconds = interrupted.StartServerTimestampMilliseconds
                    + maximumFrames * 1000L / VoiceConstants.SampleRate;
                interrupted.LastServerTimestampMilliseconds = interrupted.EndServerTimestampMilliseconds;
                interrupted.UpdatedUtcUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                WriteCoreManifest(interrupted, directory);
                WriteJsonAtomically(statePath, interrupted);
            }
            catch
            {
                // Leave unreadable state untouched so an administrator can inspect it.
            }
        }
    }

    private static long RepairWaveFile(string path)
    {
        if (!File.Exists(path))
        {
            return 0L;
        }
        using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        long dataBytes = Math.Max(0L, stream.Length - 44L) & ~1L;
        if (stream.Length != dataBytes + 44L)
        {
            stream.SetLength(dataBytes + 44L);
        }
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        PatchHeader(writer, stream, dataBytes);
        writer.Flush();
        stream.Flush(true);
        return dataBytes / sizeof(short);
    }

    private static void PadRecoveredWave(string path, long targetFrames)
    {
        if (!File.Exists(path))
        {
            return;
        }
        using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        long currentFrames = Math.Max(0L, stream.Length - 44L) / sizeof(short);
        stream.Seek(0, SeekOrigin.End);
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        WriteSilence(writer, targetFrames - currentFrames);
        PatchHeader(writer, stream, targetFrames * sizeof(short));
        writer.Flush();
        stream.Flush(true);
    }

    private string GetSessionDirectory(string sessionId)
    {
        string directory = Path.GetFullPath(Path.Combine(rootDirectory, sessionId));
        string prefix = rootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? rootDirectory
            : rootDirectory + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Recording session path escaped the configured root.");
        }
        return directory;
    }

    private void DisposeTracks()
    {
        foreach (HostedTrack track in tracks.Values)
        {
            try
            {
                track.Dispose();
            }
            catch
            {
            }
        }
        tracks.Clear();
    }

    private static string SanitizeComponent(string? value, string fallback, int maximumLength)
    {
        string result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(invalid, '_');
        }
        foreach (char invalid in "<>:\"/\\|?*")
        {
            result = result.Replace(invalid, '_');
        }
        result = new string(result.Select(character => character < 32 ? '_' : character).ToArray());
        result = result.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_');
        result = result.Trim(' ', '.');
        if (result.Length == 0)
        {
            result = fallback;
        }
        return result.Length <= maximumLength ? result : result[..maximumLength];
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void WriteHeader(BinaryWriter writer)
    {
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(0);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(VoiceConstants.SampleRate);
        writer.Write(VoiceConstants.SampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(0);
    }

    private static void PatchHeader(BinaryWriter writer, FileStream stream, long dataBytes)
    {
        long bounded = Math.Min(dataBytes, uint.MaxValue);
        stream.Seek(4, SeekOrigin.Begin);
        writer.Write((int)Math.Min(int.MaxValue, 36L + bounded));
        stream.Seek(40, SeekOrigin.Begin);
        writer.Write((int)Math.Min(int.MaxValue, bounded));
        stream.Seek(0, SeekOrigin.End);
    }

    private static void WriteSilence(BinaryWriter writer, long frameCount)
    {
        if (frameCount <= 0)
        {
            return;
        }
        byte[] zeros = new byte[VoiceConstants.SamplesPerFrame * sizeof(short)];
        long remainingBytes = frameCount * sizeof(short);
        while (remainingBytes > 0)
        {
            int count = (int)Math.Min(zeros.Length, remainingBytes);
            writer.Write(zeros, 0, count);
            remainingBytes -= count;
        }
    }

    private sealed class HostedTrack : IDisposable
    {
        private IVoiceDecoder decoder;
        private int codec;
        private long observedStreamSessionKey = -1L;
        private long decoderStreamSessionKey = -1L;
        private bool sequenceInitialized;
        private ushort lastSequence;

        internal HostedTrack(string uid, string name, string fileName, string path, int codec)
        {
            SpeakerUid = uid;
            SpeakerName = string.IsNullOrWhiteSpace(name) ? uid : name;
            FileName = fileName;
            this.codec = codec;
            decoder = VoiceCodecFactory.CreateDecoder(codec);
            Writer = new HostedWaveWriter(path);
        }

        internal string SpeakerUid { get; }
        internal string SpeakerName { get; }
        internal string FileName { get; }
        internal HostedWaveWriter Writer { get; }
        internal long PacketCount { get; private set; }
        internal long MissingPackets { get; private set; }
        internal long FallbackTimestampFrames { get; private set; }
        internal long DecodeFailures { get; set; }
        internal long LateFrames { get; set; }
        internal long DecodedFrames { get; set; }

        internal void ObservePacket(long nextStreamSessionKey, ushort sequence, bool fallbackTimestamp)
        {
            PacketCount++;
            if (fallbackTimestamp)
            {
                FallbackTimestampFrames++;
            }
            if (nextStreamSessionKey != observedStreamSessionKey)
            {
                observedStreamSessionKey = nextStreamSessionKey;
                sequenceInitialized = true;
                lastSequence = sequence;
                return;
            }
            if (sequenceInitialized)
            {
                int advance = unchecked((ushort)(sequence - lastSequence));
                if (advance is > 1 and < 32768)
                {
                    MissingPackets += advance - 1L;
                }
            }
            sequenceInitialized = true;
            lastSequence = sequence;
        }

        internal bool TryDecode(int nextCodec, long nextStreamSessionKey, ReadOnlySpan<byte> payload, out short[] samples)
        {
            if (nextCodec != codec)
            {
                decoder.Dispose();
                codec = nextCodec;
                decoder = VoiceCodecFactory.CreateDecoder(codec);
                decoderStreamSessionKey = nextStreamSessionKey;
            }
            else if (nextStreamSessionKey != decoderStreamSessionKey)
            {
                decoderStreamSessionKey = nextStreamSessionKey;
                decoder.Reset();
            }
            samples = new short[VoiceConstants.SamplesPerFrame];
            return VoiceDecoderSafety.DecodeOrSilence(decoder, payload, samples);
        }

        internal HostedTrackState ToState()
            => new()
            {
                SpeakerUid = SpeakerUid,
                SpeakerName = SpeakerName,
                FileName = FileName,
                SampleFrames = Writer.SampleFrames,
                PacketCount = PacketCount,
                MissingPackets = MissingPackets,
                FallbackTimestampFrames = FallbackTimestampFrames,
                DecodeFailures = DecodeFailures,
                LateFrames = LateFrames,
                DecodedFrames = DecodedFrames
            };

        public void Dispose()
        {
            decoder.Dispose();
            Writer.Dispose();
        }
    }

    private sealed class HostedWaveWriter : IDisposable
    {
        private readonly FileStream stream;
        private readonly BinaryWriter writer;
        private bool completed;

        internal HostedWaveWriter(string path)
        {
            stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            WriteHeader(writer);
        }

        internal long SampleFrames { get; private set; }

        internal bool TryWriteAt(long targetFrame, ReadOnlySpan<short> samples)
        {
            if (completed || targetFrame < SampleFrames)
            {
                return false;
            }
            PadTo(targetFrame);
            foreach (short sample in samples)
            {
                writer.Write(sample);
            }
            SampleFrames += samples.Length;
            return true;
        }

        internal void PadTo(long targetFrame)
        {
            if (completed || targetFrame <= SampleFrames)
            {
                return;
            }
            WriteSilence(writer, targetFrame - SampleFrames);
            SampleFrames = targetFrame;
        }

        internal void FlushToDisk()
        {
            writer.Flush();
            PatchHeader(writer, stream, SampleFrames * sizeof(short));
            writer.Flush();
            stream.Flush(true);
        }

        internal void Complete()
        {
            if (completed)
            {
                return;
            }
            FlushToDisk();
            completed = true;
        }

        public void Dispose()
        {
            try
            {
                if (!completed)
                {
                    Complete();
                }
            }
            finally
            {
                writer.Dispose();
                stream.Dispose();
            }
        }
    }
}

public readonly record struct HostedRecordingSnapshot(
    bool Active,
    string SessionId,
    string OwnerUid,
    string OwnerName,
    long StartServerTimestampMilliseconds,
    int TrackCount,
    long PacketCount,
    long MissingPackets,
    long FallbackTimestampFrames,
    long StoredPcmBytes);

public readonly record struct HostedRecordingSessionResult(
    string SessionId,
    string Directory,
    long SampleFrames,
    int TrackCount,
    long MissingPackets,
    long FallbackTimestampFrames);

public readonly record struct HostedRecordingSessionFiles(string SessionId, string Directory, string[] Files)
{
    public long TotalBytes => Files.Sum(path => new FileInfo(path).Length);
}

public sealed class HostedRecordingState
{
    public string Status { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string OwnerUid { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string StopReason { get; set; } = string.Empty;
    public long StartServerTimestampMilliseconds { get; set; }
    public long EndServerTimestampMilliseconds { get; set; }
    public long LastServerTimestampMilliseconds { get; set; }
    public long StartUtcUnixMilliseconds { get; set; }
    public long UpdatedUtcUnixMilliseconds { get; set; }
    public long SampleFrames { get; set; }
    public List<HostedTrackState> Tracks { get; set; } = new();
    public List<HostedParticipantEvent> ParticipantEvents { get; set; } = new();
}

public sealed class HostedParticipantEvent
{
    public string PlayerUid { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public bool Connected { get; set; }
    public long ServerTimestampMilliseconds { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class HostedTrackState
{
    public string SpeakerUid { get; set; } = string.Empty;
    public string SpeakerName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SampleFrames { get; set; }
    public long PacketCount { get; set; }
    public long MissingPackets { get; set; }
    public long FallbackTimestampFrames { get; set; }
    public long DecodeFailures { get; set; }
    public long LateFrames { get; set; }
    public long DecodedFrames { get; set; }
}
