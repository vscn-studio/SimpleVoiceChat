using System.Collections.Generic;

namespace SimpleVoiceChat.Audio;

public sealed class JitterBuffer
{
    private const int MinimumStartupFrames = 2;
    private const int PreferredStartupFrames = 3;
    private const int MaxBufferedFrames = 12;
    private const int MaxConcealedMissingFrames = 2;

    private static readonly short[] SilenceFrame = new short[VoiceConstants.SamplesPerFrame];

    private readonly SortedDictionary<ushort, short[]> frames = new();
    private bool initialized;
    private ushort nextSequence;
    private bool started;

    public int Count => frames.Count;

    public void Reset()
    {
        frames.Clear();
        initialized = false;
        nextSequence = 0;
        started = false;
    }

    public void Enqueue(ushort sequence, short[] samples)
    {
        if (!initialized)
        {
            nextSequence = sequence;
            initialized = true;
        }
        else if (!started && IsEarlierWithinWindow(sequence, nextSequence))
        {
            nextSequence = sequence;
        }

        if (started && IsEarlier(sequence, nextSequence))
        {
            return;
        }

        frames[sequence] = samples;
        TrimOverflow();
    }

    public bool TryDequeue(out short[] samples)
    {
        samples = Array.Empty<short>();
        if (!initialized || frames.Count == 0)
        {
            return false;
        }

        if (!started)
        {
            if (frames.Count < MinimumStartupFrames)
            {
                return false;
            }

            if (frames.Count < PreferredStartupFrames && !CanStartEarly())
            {
                return false;
            }

            nextSequence = frames.Keys.Min();
            started = true;
        }

        if (frames.Remove(nextSequence, out samples!))
        {
            nextSequence++;
            return true;
        }

        ushort first = frames.Keys.Min();
        if (SequenceDistance(nextSequence, first) > MaxConcealedMissingFrames)
        {
            nextSequence = first;
            samples = frames[first];
            frames.Remove(first);
            nextSequence++;
            return true;
        }

        nextSequence++;
        samples = SilenceFrame;
        return true;
    }

    private bool CanStartEarly()
    {
        ushort oldest = frames.Keys.Min();
        ushort newest = frames.Keys.Max();
        return oldest == 0 && newest <= 1;
    }

    private void TrimOverflow()
    {
        while (frames.Count > MaxBufferedFrames)
        {
            frames.Remove(frames.Keys.Max());
        }
    }

    private static bool IsEarlier(ushort value, ushort reference)
    {
        return unchecked((short)(value - reference)) < 0;
    }

    private static bool IsEarlierWithinWindow(ushort value, ushort reference)
    {
        return IsEarlier(value, reference) && SequenceDistance(value, reference) <= MaxBufferedFrames;
    }

    private static int SequenceDistance(ushort a, ushort b)
    {
        return Math.Abs(unchecked((short)(b - a)));
    }
}
