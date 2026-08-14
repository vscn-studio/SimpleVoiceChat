using System.Diagnostics;

namespace SimpleVoiceChat.Networking;

public static class MonotonicClock
{
    private static readonly long Origin = Stopwatch.GetTimestamp();

    public static long NowMilliseconds
        => Stopwatch.GetElapsedTime(Origin).Ticks / TimeSpan.TicksPerMillisecond;
}

/// <summary>Continuously estimates serverTime = clientTime + Offset using NTP-style samples.</summary>
public sealed class ServerClockEstimator
{
    private const int MaximumSamples = 16;
    private readonly Queue<ClockSample> samples = new();

    public double OffsetMilliseconds { get; private set; }
    public double BestRoundTripMilliseconds { get; private set; } = double.PositiveInfinity;
    public int SampleCount => samples.Count;
    public bool HasEstimate => samples.Count > 0;
    public bool IsStable => samples.Count >= 3 && BestRoundTripMilliseconds <= 1_000d;

    public void AddSample(long clientSentMilliseconds, long serverReceivedMilliseconds, long clientReceivedMilliseconds)
    {
        long roundTrip = clientReceivedMilliseconds - clientSentMilliseconds;
        if (clientSentMilliseconds < 0 || serverReceivedMilliseconds < 0 || roundTrip is < 0 or > 10_000)
        {
            return;
        }

        double offset = serverReceivedMilliseconds - (clientSentMilliseconds + clientReceivedMilliseconds) / 2d;
        ClockSample sample = new(offset, roundTrip);
        samples.Enqueue(sample);
        while (samples.Count > MaximumSamples)
        {
            samples.Dequeue();
        }

        ClockSample[] best = samples.OrderBy(value => value.RoundTripMilliseconds).Take(Math.Min(4, samples.Count)).ToArray();
        OffsetMilliseconds = best.Average(value => value.OffsetMilliseconds);
        BestRoundTripMilliseconds = best[0].RoundTripMilliseconds;
    }

    public long ToServerTime(long clientMilliseconds)
        => Math.Max(0L, (long)Math.Round(clientMilliseconds + OffsetMilliseconds));

    public long ToClientTime(long serverMilliseconds)
        => Math.Max(0L, (long)Math.Round(serverMilliseconds - OffsetMilliseconds));

    public void Reset()
    {
        samples.Clear();
        OffsetMilliseconds = 0d;
        BestRoundTripMilliseconds = double.PositiveInfinity;
    }

    private readonly record struct ClockSample(double OffsetMilliseconds, double RoundTripMilliseconds);
}
