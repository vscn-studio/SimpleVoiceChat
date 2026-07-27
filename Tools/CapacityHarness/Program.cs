using System.Diagnostics;
using SimpleVoiceChat.Networking;
using SimpleVoiceChat.Server;

const int playerCount = 100;
const double normalBatchP95LimitMilliseconds = 3.0;
int iterations = int.TryParse(Environment.GetEnvironmentVariable("SVC_CAPACITY_ITERATIONS"), out int configuredIterations)
    ? Math.Clamp(configuredIterations, 50, 10_000)
    : 200;

string[] playerUids = Enumerable.Range(0, playerCount).Select(index => $"p{index}").ToArray();
SpatialScenario clustered = CreateClusteredScenario(playerUids);
SpatialScenario dispersed = CreateDispersedScenario(playerUids);

ScenarioResult dispersedResult = MeasureScenario("dispersed-10-talkers", dispersed, talkerCount: 10, radius: 20);
ScenarioResult normal = MeasureScenario("gathering-25-talkers", clustered, talkerCount: 25, radius: 40);
ScenarioResult malicious = MeasureScenario("malicious-100-talkers", clustered, talkerCount: 100, radius: 40);

ChannelService channels = new();
VoiceChannel civilization = channels.Create(VoiceChannelKind.Civilization, "capacity", playerUids[0], playerCount, 3);
for (int i = 1; i < playerCount; i++)
{
    channels.AddMember(civilization.Id, playerUids[i], VoiceChannelRole.Member);
}
int channelTalkers = playerUids.Count(uid => civilization.TryAdmitTalker(uid, 0));

Print(dispersedResult);
Print(normal);
Print(malicious);
Console.WriteLine($"channel_members={civilization.Members.Count} channel_admitted_talkers={channelTalkers} channel_limit={civilization.MaxActiveTalkers}");

bool bounded = dispersedResult.MaximumListenerSlots <= playerCount * 8
    && normal.MaximumListenerSlots <= playerCount * 8
    && malicious.MaximumListenerSlots <= playerCount * 8
    && civilization.Members.Count == playerCount
    && channelTalkers == 3
    && normal.BatchP95Milliseconds <= normalBatchP95LimitMilliseconds;
Console.WriteLine($"capacity_result={(bounded ? "PASS" : "FAIL")}");
return bounded ? 0 : 1;

ScenarioResult MeasureScenario(string name, SpatialScenario scenario, int talkerCount, double radius)
{
    List<double> elapsedMilliseconds = new(iterations);
    List<VoiceSpatialCandidate> candidates = new(playerCount);
    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    int gen0Before = GC.CollectionCount(0);
    int gen1Before = GC.CollectionCount(1);
    int gen2Before = GC.CollectionCount(2);
    int maximumSlots = 0;
    for (int iteration = 0; iteration < iterations; iteration++)
    {
        ListenerStreamArbiter arbiter = new();
        long started = Stopwatch.GetTimestamp();
        for (int talker = 0; talker < talkerCount; talker++)
        {
            PlayerPosition position = scenario.Positions[talker];
            string talkerUid = scenario.PlayerUids[talker];
            scenario.Spatial.Query(position.X, position.Y, position.Z, radius, candidates);
            foreach (VoiceSpatialCandidate candidate in candidates)
            {
                if (candidate.PlayerUid == talkerUid)
                {
                    continue;
                }
                arbiter.TryAdmit(candidate.PlayerUid, talkerUid, 1, candidate.DistanceSquared, 8, iteration, proximity: true, maxProximityStreams: 6);
            }
        }
        elapsedMilliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        maximumSlots = Math.Max(maximumSlots, arbiter.ActiveSlotCount(iteration));
    }
    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    elapsedMilliseconds.Sort();
    return new ScenarioResult(
        name,
        elapsedMilliseconds.Average(),
        Percentile(elapsedMilliseconds, 0.50),
        Percentile(elapsedMilliseconds, 0.95),
        Percentile(elapsedMilliseconds, 0.99),
        Percentile(elapsedMilliseconds, 0.95) / talkerCount,
        allocated / (double)iterations,
        maximumSlots,
        GC.CollectionCount(0) - gen0Before,
        GC.CollectionCount(1) - gen1Before,
        GC.CollectionCount(2) - gen2Before);
}

static SpatialScenario CreateClusteredScenario(string[] playerUids)
{
    PlayerPosition[] positions = new PlayerPosition[playerUids.Length];
    for (int i = 0; i < playerUids.Length; i++)
    {
        double angle = i * Math.PI * 2 / playerUids.Length;
        positions[i] = new PlayerPosition(Math.Cos(angle) * 8, 0, Math.Sin(angle) * 8);
    }
    return CreateSpatialScenario(playerUids, positions);
}

static SpatialScenario CreateDispersedScenario(string[] playerUids)
{
    PlayerPosition[] positions = new PlayerPosition[playerUids.Length];
    for (int i = 0; i < playerUids.Length; i++)
    {
        positions[i] = new PlayerPosition(i % 10 * 80, 0, i / 10 * 80);
    }
    return CreateSpatialScenario(playerUids, positions);
}

static SpatialScenario CreateSpatialScenario(string[] playerUids, PlayerPosition[] positions)
{
    VoiceSpatialIndex spatial = new(16);
    for (int i = 0; i < playerUids.Length; i++)
    {
        PlayerPosition position = positions[i];
        spatial.Update(playerUids[i], position.X, position.Y, position.Z);
    }
    return new SpatialScenario(spatial, playerUids, positions);
}

static double Percentile(IReadOnlyList<double> sorted, double percentile)
{
    int index = Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1);
    return sorted[index];
}

static void Print(ScenarioResult result)
{
    Console.WriteLine(
        $"scenario={result.Name} batch_ms_avg={result.BatchAverageMilliseconds:0.0000} batch_ms_p50={result.BatchP50Milliseconds:0.0000} batch_ms_p95={result.BatchP95Milliseconds:0.0000} batch_ms_p99={result.BatchP99Milliseconds:0.0000} per_talker_ms_p95={result.PerTalkerP95Milliseconds:0.0000} allocated_bytes_per_iteration={result.AllocatedBytesPerIteration:0} gc={result.Gen0Collections}/{result.Gen1Collections}/{result.Gen2Collections} max_listener_slots={result.MaximumListenerSlots}");
}

internal readonly record struct ScenarioResult(
    string Name,
    double BatchAverageMilliseconds,
    double BatchP50Milliseconds,
    double BatchP95Milliseconds,
    double BatchP99Milliseconds,
    double PerTalkerP95Milliseconds,
    double AllocatedBytesPerIteration,
    int MaximumListenerSlots,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

internal readonly record struct SpatialScenario(
    VoiceSpatialIndex Spatial,
    string[] PlayerUids,
    PlayerPosition[] Positions);

internal readonly record struct PlayerPosition(double X, double Y, double Z);
