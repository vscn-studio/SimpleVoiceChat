using SimpleVoiceChat.Networking;

namespace SimpleVoiceChat.Server;

public sealed class VoiceMetrics
{
    private const long RollingWindowMilliseconds = 60_000;
    private const int SamplesPerSecond = 256;

    private readonly object sync = new();
    private readonly Queue<MetricBucket> rollingBuckets = new();
    private long receivedPackets;
    private long relayedPackets;
    private long relayedBytes;
    private long droppedRateLimit;
    private long droppedInvalid;
    private long droppedNoSlot;
    private long droppedBudget;

    public void Received(long? nowMilliseconds = null)
    {
        lock (sync)
        {
            receivedPackets++;
            GetBucket(Now(nowMilliseconds)).ReceivedPackets++;
        }
    }

    public void Relayed(int recipients, int estimatedPacketBytes, long? nowMilliseconds = null)
    {
        if (recipients <= 0)
        {
            return;
        }

        lock (sync)
        {
            long bytes = (long)recipients * Math.Max(0, estimatedPacketBytes);
            relayedPackets += recipients;
            relayedBytes += bytes;
            MetricBucket bucket = GetBucket(Now(nowMilliseconds));
            bucket.RelayedPackets += recipients;
            bucket.RelayedBytes += bytes;
            bucket.AddFanOut(recipients);
        }
    }

    public void RecordRoute(double elapsedMilliseconds, int spatialCandidates, long? nowMilliseconds = null)
    {
        lock (sync)
        {
            MetricBucket bucket = GetBucket(Now(nowMilliseconds));
            bucket.AddRoute(Math.Max(0, elapsedMilliseconds));
            bucket.AddSpatialCandidates(Math.Max(0, spatialCandidates));
        }
    }

    public void DropRateLimit(long? nowMilliseconds = null) => Drop(DropReason.RateLimit, nowMilliseconds);
    public void DropInvalid(long? nowMilliseconds = null) => Drop(DropReason.Invalid, nowMilliseconds);
    public void DropNoSlot(long? nowMilliseconds = null) => Drop(DropReason.NoSlot, nowMilliseconds);
    public void DropBudget(long? nowMilliseconds = null) => Drop(DropReason.Budget, nowMilliseconds);

    public VoiceDiagnosticsPacket Snapshot(
        int handshakenClients,
        int activeTalkers,
        int channels,
        int activeListenerStreams = 0,
        int pendingInvites = 0,
        long? nowMilliseconds = null)
    {
        lock (sync)
        {
            Prune(Now(nowMilliseconds));
            double[] fanOut = rollingBuckets.SelectMany(bucket => bucket.FanOutSamples).ToArray();
            double[] routeTimes = rollingBuckets.SelectMany(bucket => bucket.RouteTimeSamples).ToArray();
            double[] spatialCandidates = rollingBuckets.SelectMany(bucket => bucket.SpatialCandidateSamples).ToArray();
            return new VoiceDiagnosticsPacket
            {
                ReceivedPackets = receivedPackets,
                RelayedPackets = relayedPackets,
                RelayedBytes = relayedBytes,
                DroppedRateLimit = droppedRateLimit,
                DroppedInvalid = droppedInvalid,
                DroppedNoSlot = droppedNoSlot,
                DroppedBudget = droppedBudget,
                HandshakenClients = handshakenClients,
                ActiveTalkers = activeTalkers,
                Channels = channels,
                RollingReceivedPackets = rollingBuckets.Sum(bucket => bucket.ReceivedPackets),
                RollingRelayedPackets = rollingBuckets.Sum(bucket => bucket.RelayedPackets),
                RollingRelayedBytes = rollingBuckets.Sum(bucket => bucket.RelayedBytes),
                RollingDroppedPackets = rollingBuckets.Sum(bucket => bucket.DroppedPackets),
                ActiveListenerStreams = Math.Max(0, activeListenerStreams),
                AverageFanOut = Average(fanOut),
                P95FanOut = Percentile(fanOut, 0.95),
                P95RouteMilliseconds = Percentile(routeTimes, 0.95),
                AverageSpatialCandidates = Average(spatialCandidates),
                PendingInvites = Math.Max(0, pendingInvites)
            };
        }
    }

    public void ResetRolling()
    {
        lock (sync)
        {
            rollingBuckets.Clear();
        }
    }

    private void Drop(DropReason reason, long? nowMilliseconds)
    {
        lock (sync)
        {
            switch (reason)
            {
                case DropReason.RateLimit:
                    droppedRateLimit++;
                    break;
                case DropReason.Invalid:
                    droppedInvalid++;
                    break;
                case DropReason.NoSlot:
                    droppedNoSlot++;
                    break;
                case DropReason.Budget:
                    droppedBudget++;
                    break;
            }
            GetBucket(Now(nowMilliseconds)).DroppedPackets++;
        }
    }

    private MetricBucket GetBucket(long now)
    {
        Prune(now);
        long secondStart = now / 1_000 * 1_000;
        if (!rollingBuckets.TryPeek(out _)
            || rollingBuckets.Last().StartMilliseconds != secondStart)
        {
            rollingBuckets.Enqueue(new MetricBucket(secondStart));
        }
        return rollingBuckets.Last();
    }

    private void Prune(long now)
    {
        while (rollingBuckets.TryPeek(out MetricBucket? bucket)
            && now - bucket.StartMilliseconds >= RollingWindowMilliseconds)
        {
            rollingBuckets.Dequeue();
        }
    }

    private static double Average(double[] values) => values.Length == 0 ? 0 : values.Average();

    private static double Percentile(double[] values, double percentile)
    {
        if (values.Length == 0)
        {
            return 0;
        }
        Array.Sort(values);
        int index = Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1);
        return values[index];
    }

    private static long Now(long? nowMilliseconds) => nowMilliseconds ?? Environment.TickCount64;

    private sealed class MetricBucket
    {
        private int fanOutSeen;
        private int routeSeen;
        private int spatialSeen;

        public MetricBucket(long startMilliseconds)
        {
            StartMilliseconds = startMilliseconds;
        }

        public long StartMilliseconds { get; }
        public long ReceivedPackets { get; set; }
        public long RelayedPackets { get; set; }
        public long RelayedBytes { get; set; }
        public long DroppedPackets { get; set; }
        public List<double> FanOutSamples { get; } = new(SamplesPerSecond);
        public List<double> RouteTimeSamples { get; } = new(SamplesPerSecond);
        public List<double> SpatialCandidateSamples { get; } = new(SamplesPerSecond);

        public void AddFanOut(double value) => AddSample(FanOutSamples, value, ref fanOutSeen);
        public void AddRoute(double value) => AddSample(RouteTimeSamples, value, ref routeSeen);
        public void AddSpatialCandidates(double value) => AddSample(SpatialCandidateSamples, value, ref spatialSeen);

        private static void AddSample(List<double> samples, double value, ref int seen)
        {
            int index = seen++;
            if (samples.Count < SamplesPerSecond)
            {
                samples.Add(value);
            }
            else
            {
                samples[index % SamplesPerSecond] = value;
            }
        }
    }

    private enum DropReason
    {
        RateLimit,
        Invalid,
        NoSlot,
        Budget
    }
}
