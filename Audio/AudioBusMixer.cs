namespace SimpleVoiceChat.Audio;

/// <summary>Stable categories exposed to recording/OBS integrations.</summary>
public enum AudioBusKind
{
    PlayerVoice = 0
}

public readonly record struct AudioBusFrame(
    AudioBusKind Bus,
    long TimestampMilliseconds,
    short[] Samples);

public readonly record struct AudioRecordingSession(
    string SessionId,
    long StartClockMilliseconds,
    long StartUtcUnixMilliseconds,
    string SessionDirectory = "");

/// <summary>
/// Player-voice PCM mixer. It deliberately has no dependency on OBS: native
/// OBS sources or another mod can subscribe to FrameReady and transport the
/// same fixed bus IDs over IPC.
/// </summary>
public sealed class AudioBusMixer : IDisposable
{
    private readonly object gate = new();
    private readonly int sampleCount;
    private readonly int[][] accumulators;
    private readonly SortedDictionary<long, int[][]> scheduledAccumulators = new();
    private bool disposed;

    public AudioBusMixer(int samplesPerFrame = VoiceConstants.SamplesPerFrame)
    {
        sampleCount = Math.Max(1, samplesPerFrame);
        accumulators = new[] { new int[sampleCount] };
    }

    public event Action<AudioBusFrame>? FrameReady;
    public event Action<AudioRecordingSession>? RecordingSessionStarted;

    public int SamplesPerFrame => sampleCount;

    public void Submit(AudioBusKind bus, ReadOnlySpan<short> samples)
    {
        if (disposed || samples.IsEmpty)
        {
            return;
        }

        int index = (int)bus;
        if (index < 0 || index >= accumulators.Length)
        {
            return;
        }

        lock (gate)
        {
            int[] accumulator = accumulators[index];
            int count = Math.Min(sampleCount, samples.Length);
            for (int i = 0; i < count; i++)
            {
                accumulator[i] = Math.Clamp(accumulator[i] + samples[i], short.MinValue, short.MaxValue);
            }
        }
    }

    /// <summary>Schedules a PCM frame on the shared game-clock timeline.</summary>
    public void SubmitAt(AudioBusKind bus, ReadOnlySpan<short> samples, long timestampMilliseconds)
    {
        if (disposed || samples.IsEmpty || !Enum.IsDefined(bus))
        {
            return;
        }

        lock (gate)
        {
            long timestamp = Math.Max(0L, timestampMilliseconds);
            if (!scheduledAccumulators.TryGetValue(timestamp, out int[][]? target))
            {
                target = new[] { new int[sampleCount] };
                scheduledAccumulators[timestamp] = target;
            }
            AddSamples(target[(int)bus], samples);

            while (scheduledAccumulators.Count > 128)
            {
                scheduledAccumulators.Remove(scheduledAccumulators.Keys.First());
            }
        }
    }

    public void Flush(long timestampMilliseconds)
    {
        if (disposed)
        {
            return;
        }

        List<AudioBusFrame[]> frames;
        lock (gate)
        {
            frames = new List<AudioBusFrame[]>();
            int[][]? immediate = TakeFrames(accumulators);
            bool immediateWritten = false;
            foreach (long scheduledTimestamp in scheduledAccumulators.Keys
                         .TakeWhile(scheduledTimestamp => scheduledTimestamp <= timestampMilliseconds)
                         .ToArray())
            {
                int[][] scheduled = scheduledAccumulators[scheduledTimestamp];
                if (!immediateWritten && scheduledTimestamp == timestampMilliseconds)
                {
                    Merge(scheduled, immediate);
                    immediateWritten = true;
                }
                frames.Add(ToFrames(scheduled, scheduledTimestamp));
                scheduledAccumulators.Remove(scheduledTimestamp);
            }

            if (!immediateWritten)
            {
                frames.Add(ToFrames(immediate, timestampMilliseconds));
            }
        }

        Action<AudioBusFrame>? handler = FrameReady;
        if (handler == null)
        {
            return;
        }

        foreach (AudioBusFrame[] frameSet in frames)
        {
            foreach (AudioBusFrame frame in frameSet)
            {
                try
                {
                    handler(frame);
                }
                catch
                {
                    // An integration callback must not interrupt the game audio tick.
                }
            }
        }
    }

    public void NotifyRecordingSessionStarted(AudioRecordingSession session)
    {
        if (!disposed)
        {
            RecordingSessionStarted?.Invoke(session);
        }
    }

    public void Dispose()
    {
        disposed = true;
        scheduledAccumulators.Clear();
        FrameReady = null;
        RecordingSessionStarted = null;
    }

    private void AddSamples(int[] target, ReadOnlySpan<short> samples)
    {
        int count = Math.Min(sampleCount, samples.Length);
        for (int i = 0; i < count; i++)
        {
            target[i] = Math.Clamp(target[i] + samples[i], short.MinValue, short.MaxValue);
        }
    }

    private int[][] TakeFrames(int[][] source)
    {
        int[][] copied = new[] { new int[sampleCount] };
        for (int i = 0; i < source.Length; i++)
        {
            Array.Copy(source[i], copied[i], sampleCount);
            Array.Clear(source[i], 0, sampleCount);
        }
        return copied;
    }

    private static void Merge(int[][] target, int[][] source)
    {
        for (int i = 0; i < target.Length; i++)
        {
            for (int j = 0; j < target[i].Length; j++)
            {
                target[i][j] = Math.Clamp(target[i][j] + source[i][j], short.MinValue, short.MaxValue);
            }
        }
    }

    private AudioBusFrame[] ToFrames(int[][] source, long timestampMilliseconds)
    {
        AudioBusFrame[] frames = new AudioBusFrame[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            short[] samples = new short[sampleCount];
            for (int j = 0; j < sampleCount; j++)
            {
                samples[j] = (short)source[i][j];
            }
            frames[i] = new AudioBusFrame((AudioBusKind)i, timestampMilliseconds, samples);
        }
        return frames;
    }
}
