namespace SimpleVoiceChat.Audio;

public sealed class EncodedJitterBuffer
{
    private const int MinimumStartupFrames = 2;
    private const int MaximumStartupFrames = 6;
    private const int MaxBufferedFrames = 12;
    private const int MaxConcealedMissingFrames = 2;

    private readonly Dictionary<ushort, byte[]> frames = new();
    private bool adaptive;
    private bool initialized;
    private bool started;
    private ushort nextSequence;
    private bool hasArrivalSample;
    private ushort lastArrivalSequence;
    private long lastArrivalMilliseconds;
    private double estimatedJitterMilliseconds;
    private int startupFrames = 3;

    public EncodedJitterBuffer(bool adaptive = true)
    {
        this.adaptive = adaptive;
        startupFrames = adaptive ? 3 : MinimumStartupFrames;
    }

    public int Count => frames.Count;
    public int TargetDelayMilliseconds => startupFrames * VoiceConstants.FrameMilliseconds;
    public long DuplicateFrames { get; private set; }
    public long LateFrames { get; private set; }
    public long ConcealedFrames { get; private set; }
    public long FecFrames { get; private set; }

    public void Reset()
    {
        frames.Clear();
        initialized = false;
        started = false;
        nextSequence = 0;
        hasArrivalSample = false;
        estimatedJitterMilliseconds = 0;
        startupFrames = adaptive ? 3 : MinimumStartupFrames;
        DuplicateFrames = 0;
        LateFrames = 0;
        ConcealedFrames = 0;
        FecFrames = 0;
    }

    public void SetAdaptive(bool enabled)
    {
        if (adaptive == enabled)
        {
            return;
        }
        adaptive = enabled;
        Reset();
    }

    public void Enqueue(ushort sequence, byte[] payload, long arrivalMilliseconds)
    {
        UpdateJitterEstimate(sequence, arrivalMilliseconds);
        if (!initialized)
        {
            initialized = true;
            nextSequence = sequence;
        }
        else if (!started && IsEarlierWithinWindow(sequence, nextSequence))
        {
            nextSequence = sequence;
        }

        if (started && IsEarlier(sequence, nextSequence))
        {
            LateFrames++;
            return;
        }

        if (!frames.TryAdd(sequence, payload))
        {
            DuplicateFrames++;
            frames[sequence] = payload;
        }
        TrimOverflow();
    }

    public bool TryDequeue(out EncodedJitterFrame frame)
    {
        frame = default;
        if (!initialized || frames.Count == 0)
        {
            return false;
        }

        if (!started)
        {
            int required = Math.Clamp(startupFrames, MinimumStartupFrames, MaximumStartupFrames);
            if (frames.Count < required)
            {
                return false;
            }
            started = true;
        }

        if (frames.Remove(nextSequence, out byte[]? payload))
        {
            frame = new EncodedJitterFrame(payload, false, false);
            nextSequence++;
            return true;
        }

        if (!TryFindNearestFuture(nextSequence, out ushort first, out int distance))
        {
            return false;
        }

        if (distance > MaxConcealedMissingFrames)
        {
            payload = frames[first];
            frames.Remove(first);
            nextSequence = unchecked((ushort)(first + 1));
            frame = new EncodedJitterFrame(payload, false, false);
            return true;
        }

        if (distance == 1)
        {
            FecFrames++;
            frame = new EncodedJitterFrame(frames[first], true, true);
        }
        else
        {
            ConcealedFrames++;
            frame = new EncodedJitterFrame(Array.Empty<byte>(), true, false);
        }
        nextSequence++;
        return true;
    }

    private void UpdateJitterEstimate(ushort sequence, long arrivalMilliseconds)
    {
        if (!adaptive)
        {
            return;
        }
        if (hasArrivalSample)
        {
            int advance = ForwardDistance(lastArrivalSequence, sequence);
            if (advance is > 0 and < 128)
            {
                double expected = advance * VoiceConstants.FrameMilliseconds;
                double actual = Math.Max(0, arrivalMilliseconds - lastArrivalMilliseconds);
                estimatedJitterMilliseconds += (Math.Abs(actual - expected) - estimatedJitterMilliseconds) / 16d;
                startupFrames = Math.Clamp(
                    MinimumStartupFrames + (int)Math.Ceiling(estimatedJitterMilliseconds / VoiceConstants.FrameMilliseconds),
                    MinimumStartupFrames,
                    MaximumStartupFrames);
            }
        }
        hasArrivalSample = true;
        lastArrivalSequence = sequence;
        lastArrivalMilliseconds = arrivalMilliseconds;
    }

    private void TrimOverflow()
    {
        while (frames.Count > MaxBufferedFrames)
        {
            ushort oldest = frames.Keys
                .OrderBy(sequence => ForwardDistance(nextSequence, sequence))
                .First();
            frames.Remove(oldest);
            if (oldest == nextSequence)
            {
                nextSequence++;
            }
        }
    }

    private bool TryFindNearestFuture(ushort reference, out ushort sequence, out int distance)
    {
        sequence = 0;
        distance = int.MaxValue;
        bool found = false;
        foreach (ushort candidate in frames.Keys)
        {
            int candidateDistance = ForwardDistance(reference, candidate);
            if (candidateDistance <= 0 || candidateDistance >= 32768 || candidateDistance >= distance)
            {
                continue;
            }
            found = true;
            sequence = candidate;
            distance = candidateDistance;
        }
        return found;
    }

    private static bool IsEarlier(ushort value, ushort reference)
    {
        return unchecked((short)(value - reference)) < 0;
    }

    private static bool IsEarlierWithinWindow(ushort value, ushort reference)
    {
        return IsEarlier(value, reference) && Math.Abs(unchecked((short)(value - reference))) <= MaxBufferedFrames;
    }

    private static int ForwardDistance(ushort from, ushort to)
    {
        return unchecked((ushort)(to - from));
    }
}

public readonly record struct EncodedJitterFrame(byte[] Payload, bool Concealment, bool UseFec);
