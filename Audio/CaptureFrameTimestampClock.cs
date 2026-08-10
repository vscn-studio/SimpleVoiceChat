namespace SimpleVoiceChat.Audio;

/// <summary>Maps capture-buffer positions onto the director client's monotonic clock.</summary>
internal sealed class CaptureFrameTimestampClock
{
    private const int MaximumClockJitterMilliseconds = 10;

    private bool hasTimestamp;
    private long lastTimestampMilliseconds;

    internal long ResolveFrameEndTimestamp(long nowMilliseconds, int samplesAvailableBeforeRead)
    {
        long now = Math.Max(0L, nowMilliseconds);
        int samplesQueuedAfterRead = Math.Max(0, samplesAvailableBeforeRead - VoiceConstants.SamplesPerFrame);
        long queuedMilliseconds = samplesQueuedAfterRead * 1000L / VoiceConstants.SampleRate;
        long candidate = Math.Max(0L, now - queuedMilliseconds);
        if (!hasTimestamp)
        {
            hasTimestamp = true;
            lastTimestampMilliseconds = candidate;
            return candidate;
        }

        long expected = lastTimestampMilliseconds + VoiceConstants.FrameMilliseconds;
        if (candidate <= expected + MaximumClockJitterMilliseconds)
        {
            lastTimestampMilliseconds = expected;
        }
        else
        {
            // A capture-device overrun created a real discontinuity; retain it.
            lastTimestampMilliseconds = candidate;
        }
        return lastTimestampMilliseconds;
    }

    internal void Reset()
    {
        hasTimestamp = false;
        lastTimestampMilliseconds = 0L;
    }
}

/// <summary>Keeps decoded relay frames on their sender sequence timeline.</summary>
internal sealed class VoiceFrameSequenceTimeline
{
    private bool hasFrame;
    private ushort lastSequence;
    private long lastTimestampMilliseconds;

    internal long Resolve(ushort sequence, long initialTimestampMilliseconds)
    {
        if (!hasFrame)
        {
            hasFrame = true;
            lastSequence = sequence;
            lastTimestampMilliseconds = Math.Max(0L, initialTimestampMilliseconds);
            return lastTimestampMilliseconds;
        }

        int advance = unchecked((ushort)(sequence - lastSequence));
        if (advance is <= 0 or > 4096)
        {
            advance = 1;
        }
        lastSequence = sequence;
        lastTimestampMilliseconds += (long)advance * VoiceConstants.FrameMilliseconds;
        return lastTimestampMilliseconds;
    }

    internal void Reset()
    {
        hasFrame = false;
        lastSequence = 0;
        lastTimestampMilliseconds = 0L;
    }
}
