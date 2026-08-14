using System.Text;
using System.Text.Json;
using Vintagestory.API.Client;

namespace SimpleVoiceChat.Audio;

public enum VoiceRecordingMode
{
    InputOnly,
    InputAndOutput,
    MultiTrack
}

/// <summary>
/// Holds microphone-test samples in memory so the settings page can verify
/// capture and playback without creating a recording file.
/// </summary>
public sealed class VoiceTestRecordingBuffer
{
    private readonly object gate = new();
    private readonly List<short> samples = new();
    private RecordedAudioClip? lastClip;
    private bool recording;

    public bool IsRecording
    {
        get
        {
            lock (gate)
            {
                return recording;
            }
        }
    }

    public RecordedAudioClip? LastClip
    {
        get
        {
            lock (gate)
            {
                return lastClip;
            }
        }
    }

    public void Start()
    {
        lock (gate)
        {
            samples.Clear();
            lastClip = null;
            recording = true;
        }
    }

    public bool Stop()
    {
        lock (gate)
        {
            if (!recording)
            {
                return false;
            }

            recording = false;
            if (samples.Count == 0)
            {
                lastClip = null;
                return false;
            }

            lastClip = RecordedAudioClip.FromPcm(samples.ToArray());
            return true;
        }
    }

    public void AppendInput(ReadOnlySpan<short> input)
    {
        lock (gate)
        {
            if (!recording || input.IsEmpty)
            {
                return;
            }

            samples.AddRange(input.ToArray());
        }
    }
}

/// <summary>
/// Streams local microphone and playback samples into a PCM WAV file. The
/// input+output mode stores input on the left channel and playback on the
/// right channel so the two sources remain distinguishable when replayed.
/// </summary>
public sealed class VoiceRecordingService : IDisposable
{
    private readonly string directoryPath;
    private readonly object gate = new();
    private FileStream? fileStream;
    private BinaryWriter? writer;
    private string activePath = string.Empty;
    private VoiceRecordingMode activeMode;
    private long dataBytes;
    private long sampleFrames;
    private int[]? outputAccumulator;
    private int outputFrameCount;
    private string lastRecordingPath = string.Empty;
    private VoiceRecordingMode? lastRecordingMode;
    private string sessionPath = string.Empty;
    private string sessionId = string.Empty;
    private long sessionStartUtcUnixMilliseconds;
    private long sessionStartMilliseconds;
    private long sessionEndMilliseconds;
    private long sessionClientStartMilliseconds;
    private double sessionClockOffsetMilliseconds;
    private long sessionSampleFrames;
    private bool sessionClockStarted;
    private readonly Dictionary<string, MultiTrackWriter> multiTrackWriters = new(StringComparer.Ordinal);
    private string localPlayerUid = string.Empty;
    private string localPlayerName = string.Empty;
    private bool multiTrackActive;
    private bool disposed;

    public VoiceRecordingService(ICoreClientAPI capi)
    {
        directoryPath = capi.GetOrCreateDataPath(Path.Combine("ModData", "SimpleVoiceChat"));
        Directory.CreateDirectory(directoryPath);
        localPlayerUid = capi.World.Player?.PlayerUID ?? string.Empty;
        localPlayerName = capi.World.Player?.PlayerName ?? string.Empty;
    }

    public string DirectoryPath => directoryPath;

    public bool IsRecording
    {
        get
        {
            lock (gate)
            {
                return writer != null || multiTrackActive;
            }
        }
    }

    public VoiceRecordingMode? Mode
    {
        get
        {
            lock (gate)
            {
                return writer == null && !multiTrackActive ? null : activeMode;
            }
        }
    }

    public string LastRecordingPath
    {
        get
        {
            lock (gate)
            {
                return lastRecordingPath;
            }
        }
    }

    public bool HasRecording => File.Exists(LastRecordingPath);

    public AudioRecordingSession? ActiveMultiTrackSession
    {
        get
        {
            lock (gate)
            {
                return multiTrackActive
                    ? new AudioRecordingSession(sessionId, sessionStartMilliseconds, sessionStartUtcUnixMilliseconds, sessionPath)
                    : null;
            }
        }
    }

    public bool CanPlayLastRecording => lastRecordingMode != VoiceRecordingMode.MultiTrack
        && Path.GetExtension(LastRecordingPath).Equals(".wav", StringComparison.OrdinalIgnoreCase);

    public bool Start(VoiceRecordingMode mode, out string error)
        => Start(mode, 0L, out error);

    public bool Start(VoiceRecordingMode mode, long startTimestampMilliseconds, out string error)
        => Start(mode, string.Empty, startTimestampMilliseconds, 0L, 0d, out error);

    public bool Start(
        VoiceRecordingMode mode,
        string requestedSessionId,
        long startServerTimestampMilliseconds,
        long startUtcUnixMilliseconds,
        double clockOffsetMilliseconds,
        out string error)
    {
        lock (gate)
        {
            error = string.Empty;
            if (disposed)
            {
                error = "The recording service is unavailable.";
                return false;
            }

            CloseActiveRecording(deleteEmpty: true);
            try
            {
                Directory.CreateDirectory(directoryPath);
                activeMode = mode;
                sessionStartMilliseconds = Math.Max(0L, startServerTimestampMilliseconds);
                sessionEndMilliseconds = 0L;
                sessionClockOffsetMilliseconds = clockOffsetMilliseconds;
                sessionClientStartMilliseconds = Math.Max(0L, (long)Math.Round(sessionStartMilliseconds - clockOffsetMilliseconds));
                sessionSampleFrames = 0;
                sessionClockStarted = mode == VoiceRecordingMode.MultiTrack;
                if (mode == VoiceRecordingMode.MultiTrack)
                {
                    sessionId = string.IsNullOrWhiteSpace(requestedSessionId)
                        ? $"multitrack-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"
                        : SanitizeFileName(requestedSessionId);
                    sessionPath = Path.Combine(directoryPath, sessionId);
                    sessionStartUtcUnixMilliseconds = startUtcUnixMilliseconds > 0
                        ? startUtcUnixMilliseconds
                        : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    Directory.CreateDirectory(sessionPath);
                    lastRecordingPath = string.Empty;
                    multiTrackActive = true;
                    return true;
                }
                activePath = CreateRecordingPath();
                fileStream = new FileStream(activePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                writer = new BinaryWriter(fileStream, Encoding.UTF8, leaveOpen: true);
                WriteHeader(writer, (short)(mode == VoiceRecordingMode.InputAndOutput ? 2 : 1));
                dataBytes = 0;
                sampleFrames = 0;
                outputAccumulator = mode == VoiceRecordingMode.InputAndOutput
                    ? new int[VoiceConstants.SamplesPerFrame]
                    : null;
                outputFrameCount = 0;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                CloseActiveRecording(deleteEmpty: true);
                return false;
            }
        }
    }

    public bool Stop(out string path)
        => Stop(0L, out path);

    public bool Stop(long endTimestampMilliseconds, out string path)
    {
        lock (gate)
        {
            path = string.Empty;
            if (writer == null && !multiTrackActive)
            {
                return false;
            }

            if (activeMode == VoiceRecordingMode.MultiTrack)
            {
                if (sessionClockStarted && endTimestampMilliseconds >= sessionStartMilliseconds)
                {
                    sessionEndMilliseconds = endTimestampMilliseconds;
                    sessionSampleFrames = Math.Max(
                        sessionSampleFrames,
                        (endTimestampMilliseconds - sessionStartMilliseconds) * VoiceConstants.SampleRate / 1000L);
                }
                bool saved = StopMultiTrack(out path);
                CloseActiveRecording(deleteEmpty: true);
                return saved;
            }

            bool hasSamples = sampleFrames > 0;
            string completedPath = activePath;
            CloseActiveRecording(deleteEmpty: !hasSamples);
            if (!hasSamples || !File.Exists(completedPath))
            {
                return false;
            }

            lastRecordingPath = completedPath;
            lastRecordingMode = activeMode;
            path = completedPath;
            return true;
        }
    }

    public void AppendInput(ReadOnlySpan<short> samples)
        => AppendInput(samples, 0L);

    public void AppendInput(ReadOnlySpan<short> samples, long timestampMilliseconds)
    {
        lock (gate)
        {
            if ((!multiTrackActive && writer == null) || samples.IsEmpty)
            {
                return;
            }

            if (activeMode == VoiceRecordingMode.MultiTrack)
            {
                AppendMultiTrack("local", localPlayerUid, localPlayerName, samples, timestampMilliseconds);
                return;
            }

            if (activeMode == VoiceRecordingMode.InputOnly)
            {
                BinaryWriter activeWriter = writer!;
                for (int i = 0; i < samples.Length; i++)
                {
                    activeWriter.Write(samples[i]);
                }
                dataBytes += samples.Length * sizeof(short);
                sampleFrames += samples.Length;
                return;
            }

            int[] accumulator = outputAccumulator ??= new int[VoiceConstants.SamplesPerFrame];
            int count = Math.Min(samples.Length, accumulator.Length);
            int outputCount = outputFrameCount;
            BinaryWriter outputWriter = writer!;
            for (int i = 0; i < samples.Length; i++)
            {
                short output = i < count && outputCount > 0
                    ? (short)Math.Clamp(accumulator[i] / outputCount, short.MinValue, short.MaxValue)
                    : (short)0;
                outputWriter.Write(samples[i]);
                outputWriter.Write(output);
            }
            dataBytes += samples.Length * sizeof(short) * 2L;
            sampleFrames += samples.Length;
            Array.Clear(accumulator, 0, accumulator.Length);
            outputFrameCount = 0;
        }
    }

    public void AppendOutput(ReadOnlySpan<short> samples)
    {
        lock (gate)
        {
            if (writer == null || activeMode != VoiceRecordingMode.InputAndOutput || samples.IsEmpty)
            {
                return;
            }

            int[] accumulator = outputAccumulator ??= new int[VoiceConstants.SamplesPerFrame];
            int count = Math.Min(samples.Length, accumulator.Length);
            for (int i = 0; i < count; i++)
            {
                accumulator[i] = Math.Clamp(accumulator[i] + samples[i], int.MinValue + 1, int.MaxValue);
            }
            outputFrameCount++;
        }
    }

    /// <summary>Appends a decoded remote speaker frame to the active multi-track session.</summary>
    public void AppendRemote(string speakerUid, string? speakerName, ReadOnlySpan<short> samples, long timestampMilliseconds)
    {
        lock (gate)
        {
            if (activeMode != VoiceRecordingMode.MultiTrack || string.IsNullOrWhiteSpace(speakerUid) || samples.IsEmpty)
            {
                return;
            }

            AppendMultiTrack(speakerUid, speakerUid, speakerName ?? speakerUid, samples, timestampMilliseconds);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            CloseActiveRecording(deleteEmpty: true);
        }
    }

    private string CreateRecordingPath()
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string path = Path.Combine(directoryPath, $"recording-{timestamp}.wav");
        if (!File.Exists(path))
        {
            return path;
        }

        return Path.Combine(directoryPath, $"recording-{timestamp}-{Guid.NewGuid():N}.wav");
    }

    private void CloseActiveRecording(bool deleteEmpty)
    {
        if (activeMode == VoiceRecordingMode.MultiTrack)
        {
            foreach (MultiTrackWriter track in multiTrackWriters.Values)
            {
                track.Dispose();
            }
            multiTrackWriters.Clear();
            sessionPath = string.Empty;
            sessionId = string.Empty;
            sessionStartUtcUnixMilliseconds = 0;
            sessionStartMilliseconds = 0;
            sessionEndMilliseconds = 0;
            sessionClientStartMilliseconds = 0;
            sessionClockOffsetMilliseconds = 0d;
            sessionSampleFrames = 0;
            sessionClockStarted = false;
            activeMode = default;
            multiTrackActive = false;
            return;
        }

        if (writer == null || fileStream == null)
        {
            writer = null;
            fileStream = null;
            activePath = string.Empty;
            outputAccumulator = null;
            outputFrameCount = 0;
            dataBytes = 0;
            sampleFrames = 0;
            return;
        }

        string path = activePath;
        bool hasSamples = sampleFrames > 0;
        try
        {
            writer.Flush();
            if (!deleteEmpty || hasSamples)
            {
                PatchHeader(writer, fileStream, dataBytes);
                writer.Flush();
            }
        }
        catch
        {
        }
        finally
        {
            writer.Dispose();
            fileStream.Dispose();
            if (hasSamples && File.Exists(path))
            {
                lastRecordingPath = path;
            }
            writer = null;
            fileStream = null;
            activePath = string.Empty;
            outputAccumulator = null;
            outputFrameCount = 0;
            dataBytes = 0;
            sampleFrames = 0;
        }

        if (deleteEmpty && !hasSamples && File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }

    private void AppendMultiTrack(
        string key,
        string speakerUid,
        string speakerName,
        ReadOnlySpan<short> samples,
        long timestampMilliseconds)
    {
        long timestamp = Math.Max(0L, timestampMilliseconds);
        if (!sessionClockStarted)
        {
            sessionStartMilliseconds = timestamp;
            sessionClockStarted = true;
        }
        // A server-scheduled session has a shared zero. Frames decoded before
        // that zero belong to the pre-roll and must not be compressed to 0ms.
        if (timestamp < sessionStartMilliseconds)
        {
            return;
        }
        long targetFrame = Math.Max(0L, timestamp - sessionStartMilliseconds) * VoiceConstants.SampleRate / 1000L;
        MultiTrackWriter track = GetMultiTrackWriter(key, speakerUid, speakerName);
        if (!track.TryWriteAt(targetFrame, samples))
        {
            return;
        }
        sessionSampleFrames = Math.Max(sessionSampleFrames, targetFrame + samples.Length);
    }

    private MultiTrackWriter GetMultiTrackWriter(string key, string speakerUid, string speakerName)
    {
        if (multiTrackWriters.TryGetValue(key, out MultiTrackWriter? existing))
        {
            return existing;
        }

        string safeKey = SanitizeFileName(key);
        string path = Path.Combine(sessionPath, $"{safeKey}.wav");
        MultiTrackWriter created = new(path, speakerUid, speakerName);
        multiTrackWriters[key] = created;
        return created;
    }

    private bool StopMultiTrack(out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(sessionPath) || multiTrackWriters.Count == 0)
        {
            return false;
        }

        foreach (MultiTrackWriter track in multiTrackWriters.Values)
        {
            track.PadTo(sessionSampleFrames);
            track.Complete();
        }

        var manifest = new
        {
            sessionId,
            timeline = new
            {
                serverStartMilliseconds = sessionStartMilliseconds,
                serverEndMilliseconds = sessionEndMilliseconds,
                clientStartMilliseconds = sessionClientStartMilliseconds,
                clientMinusServerMilliseconds = -sessionClockOffsetMilliseconds,
                utcStartUnixMilliseconds = sessionStartUtcUnixMilliseconds
            },
            startUtcUnixMilliseconds = sessionStartUtcUnixMilliseconds,
            sampleRate = VoiceConstants.SampleRate,
            frameMilliseconds = VoiceConstants.FrameMilliseconds,
            sampleFrames = sessionSampleFrames,
            tracks = multiTrackWriters.Values.Select(track => new
            {
                uid = track.SpeakerUid,
                name = track.SpeakerName,
                file = Path.GetFileName(track.Path)
            }).ToArray()
        };
        string manifestPath = Path.Combine(sessionPath, "session.json");
        string corePath = Path.Combine(sessionPath, "session.core.json");
        File.WriteAllText(corePath, JsonSerializer.Serialize(manifest, JsonOptions));
        MultiTrackSessionManifest.Merge(sessionPath);
        lastRecordingPath = manifestPath;
        lastRecordingMode = VoiceRecordingMode.MultiTrack;
        path = manifestPath;
        return true;
    }

    public void RefreshMultiTrackManifest(string completedSessionPath)
    {
        if (!string.IsNullOrWhiteSpace(completedSessionPath))
        {
            MultiTrackSessionManifest.Merge(completedSessionPath);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string SanitizeFileName(string value)
    {
        string result = string.IsNullOrWhiteSpace(value) ? "speaker" : value;
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(invalid, '_');
        }
        return result.Length > 80 ? result[..80] : result;
    }

    private sealed class MultiTrackWriter : IDisposable
    {
        private readonly FileStream stream;
        private readonly BinaryWriter writer;
        private long sampleFrames;
        private bool completed;

        internal MultiTrackWriter(string path, string speakerUid, string speakerName)
        {
            Path = path;
            SpeakerUid = speakerUid;
            SpeakerName = speakerName;
            stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            WriteHeader(writer, 1);
        }

        internal string Path { get; }
        internal string SpeakerUid { get; }
        internal string SpeakerName { get; }

        internal void PadTo(long target)
        {
            while (sampleFrames < target)
            {
                int count = (int)Math.Min(VoiceConstants.SamplesPerFrame, target - sampleFrames);
                for (int i = 0; i < count; i++) writer.Write((short)0);
                sampleFrames += count;
            }
        }

        internal bool TryWriteAt(long targetFrame, ReadOnlySpan<short> samples)
        {
            if (targetFrame < sampleFrames)
            {
                return false;
            }

            PadTo(targetFrame);
            foreach (short sample in samples) writer.Write(sample);
            sampleFrames += samples.Length;
            return true;
        }

        internal void Complete()
        {
            if (completed) return;
            PatchHeader(writer, stream, sampleFrames * sizeof(short));
            writer.Flush();
            completed = true;
        }

        public void Dispose()
        {
            if (!completed) Complete();
            writer.Dispose();
            stream.Dispose();
        }
    }

    private static void WriteHeader(BinaryWriter writer, short channels)
    {
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(0);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(VoiceConstants.SampleRate);
        writer.Write(VoiceConstants.SampleRate * channels * sizeof(short));
        writer.Write((short)(channels * sizeof(short)));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(0);
    }

    private static void PatchHeader(BinaryWriter writer, FileStream stream, long dataBytes)
    {
        long boundedDataBytes = Math.Min(dataBytes, uint.MaxValue);
        stream.Seek(4, SeekOrigin.Begin);
        writer.Write((int)Math.Min(int.MaxValue, 36 + boundedDataBytes));
        stream.Seek(40, SeekOrigin.Begin);
        writer.Write((int)boundedDataBytes);
        stream.Seek(0, SeekOrigin.End);
    }
}

public sealed class RecordedAudioClip
{
    private RecordedAudioClip(short[] samples, int channels, int sampleRate)
    {
        Samples = samples;
        Channels = channels;
        SampleRate = sampleRate;
    }

    public short[] Samples { get; }
    public int Channels { get; }
    public int SampleRate { get; }

    public static RecordedAudioClip FromPcm(short[] samples, int channels = 1, int sampleRate = VoiceConstants.SampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0)
        {
            throw new ArgumentException("PCM samples cannot be empty.", nameof(samples));
        }
        if (channels is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        return new RecordedAudioClip((short[])samples.Clone(), channels, sampleRate);
    }

    public static bool TryLoad(string path, out RecordedAudioClip? clip, out string error)
    {
        clip = null;
        error = string.Empty;
        try
        {
            using FileStream stream = File.OpenRead(path);
            using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);
            if (ReadFourCc(reader) != "RIFF") throw new InvalidDataException("Missing RIFF header.");
            _ = reader.ReadInt32();
            if (ReadFourCc(reader) != "WAVE") throw new InvalidDataException("Missing WAVE header.");

            short format = 0;
            short channels = 0;
            int sampleRate = 0;
            short bitsPerSample = 0;
            byte[]? data = null;
            while (stream.Position + 8 <= stream.Length)
            {
                string chunk = ReadFourCc(reader);
                int chunkSize = reader.ReadInt32();
                if (chunkSize < 0 || stream.Position + chunkSize > stream.Length)
                {
                    throw new InvalidDataException("Invalid WAV chunk length.");
                }

                switch (chunk)
                {
                    case "fmt ":
                        if (chunkSize < 16) throw new InvalidDataException("Invalid WAV format chunk.");
                        format = reader.ReadInt16();
                        channels = reader.ReadInt16();
                        sampleRate = reader.ReadInt32();
                        _ = reader.ReadInt32();
                        _ = reader.ReadInt16();
                        bitsPerSample = reader.ReadInt16();
                        stream.Seek(chunkSize - 16, SeekOrigin.Current);
                        break;
                    case "data":
                        data = reader.ReadBytes(chunkSize);
                        break;
                    default:
                        stream.Seek(chunkSize, SeekOrigin.Current);
                        break;
                }

                if ((chunkSize & 1) != 0 && stream.Position < stream.Length)
                {
                    stream.Seek(1, SeekOrigin.Current);
                }
            }

            if (format != 1 || channels is < 1 or > 2 || sampleRate <= 0 || bitsPerSample != 16 || data == null || data.Length < 2)
            {
                throw new InvalidDataException("Only 16-bit PCM mono or stereo WAV files are supported.");
            }

            int sampleCount = data.Length / sizeof(short);
            short[] samples = new short[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = (short)(data[i * 2] | data[i * 2 + 1] << 8);
            }
            clip = new RecordedAudioClip(samples, channels, sampleRate);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ReadFourCc(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        return bytes.Length == 4 ? Encoding.ASCII.GetString(bytes) : string.Empty;
    }
}
