using System.Collections.Generic;

namespace SimpleVoiceChat.Audio;

public sealed class JitterBuffer
{
    private readonly SortedDictionary<ushort, short[]> frames = new();
    private bool initialized;
    private ushort nextSequence;

    public int Count => frames.Count;

    public void Enqueue(ushort sequence, short[] samples)
    {
        if (!initialized)
        {
            nextSequence = sequence;
            initialized = true;
        }

        if (IsOlder(sequence, nextSequence))
        {
            return;
        }

        frames[sequence] = samples;
        while (frames.Count > 12)
        {
            frames.Remove(frames.Keys.Min());
        }
    }

    public bool TryDequeue(out short[] samples)
    {
        samples = Array.Empty<short>();
        if (!initialized || frames.Count < 4)
        {
            return false;
        }

        if (frames.Remove(nextSequence, out samples!))
        {
            nextSequence++;
            return true;
        }

        ushort first = frames.Keys.Min();
        if (SequenceDistance(nextSequence, first) > 3)
        {
            nextSequence = first;
            samples = frames[first];
            frames.Remove(first);
            nextSequence++;
            return true;
        }

        nextSequence++;
        samples = new short[VoiceConstants.SamplesPerFrame];
        return true;
    }

    private static bool IsOlder(ushort value, ushort reference)
    {
        return unchecked((short)(value - reference)) < -32;
    }

    private static int SequenceDistance(ushort a, ushort b)
    {
        return Math.Abs(unchecked((short)(b - a)));
    }
}
