using System.Collections.Concurrent;

namespace SimpleVoiceChat.Audio;

internal sealed class PcmFramePool
{
    internal static PcmFramePool Shared { get; } = new(256);

    private readonly ConcurrentStack<short[]> frames = new();
    private readonly int capacity;
    private int retainedCount;

    internal PcmFramePool(int capacity)
    {
        this.capacity = Math.Max(1, capacity);
    }

    internal int RetainedCount => Volatile.Read(ref retainedCount);

    internal short[] Rent()
    {
        if (frames.TryPop(out short[]? frame))
        {
            Interlocked.Decrement(ref retainedCount);
            return frame;
        }

        return new short[VoiceConstants.SamplesPerFrame];
    }

    internal void Return(short[]? frame)
    {
        if (frame == null || frame.Length != VoiceConstants.SamplesPerFrame)
        {
            return;
        }

        Array.Clear(frame);
        if (Interlocked.Increment(ref retainedCount) <= capacity)
        {
            frames.Push(frame);
            return;
        }

        Interlocked.Decrement(ref retainedCount);
    }
}
