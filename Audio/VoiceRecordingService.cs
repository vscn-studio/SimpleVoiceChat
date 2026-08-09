using System.Text;
using Vintagestory.API.Client;

namespace SimpleVoiceChat.Audio;

public enum VoiceRecordingMode
{
    InputOnly,
    InputAndOutput
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
    private bool disposed;

    public VoiceRecordingService(ICoreClientAPI capi)
    {
        directoryPath = capi.GetOrCreateDataPath(Path.Combine("ModData", "SimpleVoiceChat"));
        Directory.CreateDirectory(directoryPath);
    }

    public string DirectoryPath => directoryPath;

    public bool IsRecording
    {
        get
        {
            lock (gate)
            {
                return writer != null;
            }
        }
    }

    public VoiceRecordingMode? Mode
    {
        get
        {
            lock (gate)
            {
                return writer == null ? null : activeMode;
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

    public bool Start(VoiceRecordingMode mode, out string error)
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
    {
        lock (gate)
        {
            path = string.Empty;
            if (writer == null)
            {
                return false;
            }

            bool hasSamples = sampleFrames > 0;
            string completedPath = activePath;
            CloseActiveRecording(deleteEmpty: !hasSamples);
            if (!hasSamples || !File.Exists(completedPath))
            {
                return false;
            }

            lastRecordingPath = completedPath;
            path = completedPath;
            return true;
        }
    }

    public void AppendInput(ReadOnlySpan<short> samples)
    {
        lock (gate)
        {
            if (writer == null || samples.IsEmpty)
            {
                return;
            }

            if (activeMode == VoiceRecordingMode.InputOnly)
            {
                for (int i = 0; i < samples.Length; i++)
                {
                    writer.Write(samples[i]);
                }
                dataBytes += samples.Length * sizeof(short);
                sampleFrames += samples.Length;
                return;
            }

            int[] accumulator = outputAccumulator ??= new int[VoiceConstants.SamplesPerFrame];
            int count = Math.Min(samples.Length, accumulator.Length);
            int outputCount = outputFrameCount;
            for (int i = 0; i < samples.Length; i++)
            {
                short output = i < count && outputCount > 0
                    ? (short)Math.Clamp(accumulator[i] / outputCount, short.MinValue, short.MaxValue)
                    : (short)0;
                writer.Write(samples[i]);
                writer.Write(output);
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
