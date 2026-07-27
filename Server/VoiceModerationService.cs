namespace SimpleVoiceChat.Server;

public sealed class VoiceModerationService
{
    private const int StrikeLimit = 20;
    private const long StrikeWindowMilliseconds = 60_000;
    private const long AutomaticSuspensionMilliseconds = 60_000;

    private readonly Dictionary<string, long> temporaryMuteUntilByUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> deafenUntilByUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StrikeWindow> strikesByUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> automaticSuspensionUntilByUid = new(StringComparer.Ordinal);

    public bool CanTransmit(string playerUid, long nowMilliseconds)
    {
        return !IsActive(temporaryMuteUntilByUid, playerUid, nowMilliseconds)
            && !IsActive(automaticSuspensionUntilByUid, playerUid, nowMilliseconds);
    }

    public bool CanReceive(string playerUid, long nowMilliseconds)
    {
        return !IsActive(deafenUntilByUid, playerUid, nowMilliseconds);
    }

    public void SetTemporaryMute(string playerUid, long nowMilliseconds, TimeSpan duration)
    {
        SetExpiry(temporaryMuteUntilByUid, playerUid, nowMilliseconds, duration);
    }

    public void SetDeafened(string playerUid, long nowMilliseconds, TimeSpan duration)
    {
        SetExpiry(deafenUntilByUid, playerUid, nowMilliseconds, duration);
    }

    public bool AddInvalidPacketStrike(string playerUid, long nowMilliseconds)
    {
        if (!strikesByUid.TryGetValue(playerUid, out StrikeWindow? strikes)
            || nowMilliseconds - strikes.WindowStartMilliseconds > StrikeWindowMilliseconds)
        {
            strikes = new StrikeWindow(nowMilliseconds);
            strikesByUid[playerUid] = strikes;
        }

        strikes.Count++;
        if (strikes.Count < StrikeLimit)
        {
            return false;
        }

        automaticSuspensionUntilByUid[playerUid] = nowMilliseconds + AutomaticSuspensionMilliseconds;
        strikes.Count = 0;
        strikes.WindowStartMilliseconds = nowMilliseconds;
        return true;
    }

    public ModerationPlayerSnapshot Snapshot(string playerUid, long nowMilliseconds)
    {
        strikesByUid.TryGetValue(playerUid, out StrikeWindow? strikes);
        return new ModerationPlayerSnapshot(
            Remaining(temporaryMuteUntilByUid, playerUid, nowMilliseconds),
            Remaining(deafenUntilByUid, playerUid, nowMilliseconds),
            Remaining(automaticSuspensionUntilByUid, playerUid, nowMilliseconds),
            strikes?.Count ?? 0);
    }

    public void Prune(long nowMilliseconds)
    {
        PruneExpiries(temporaryMuteUntilByUid, nowMilliseconds);
        PruneExpiries(deafenUntilByUid, nowMilliseconds);
        PruneExpiries(automaticSuspensionUntilByUid, nowMilliseconds);
        foreach (string uid in strikesByUid
                     .Where(pair => nowMilliseconds - pair.Value.WindowStartMilliseconds > StrikeWindowMilliseconds)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            strikesByUid.Remove(uid);
        }
    }

    private static bool IsActive(Dictionary<string, long> values, string uid, long now)
    {
        if (!values.TryGetValue(uid, out long until))
        {
            return false;
        }
        if (until > now)
        {
            return true;
        }
        values.Remove(uid);
        return false;
    }

    private static long Remaining(Dictionary<string, long> values, string uid, long now)
    {
        return values.TryGetValue(uid, out long until) ? Math.Max(0, until - now) : 0;
    }

    private static void SetExpiry(Dictionary<string, long> values, string uid, long now, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            values.Remove(uid);
            return;
        }
        values[uid] = now + (long)Math.Clamp(duration.TotalMilliseconds, 1_000, 86_400_000);
    }

    private static void PruneExpiries(Dictionary<string, long> values, long now)
    {
        foreach (string uid in values.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
        {
            values.Remove(uid);
        }
    }

    private sealed class StrikeWindow
    {
        public StrikeWindow(long windowStartMilliseconds)
        {
            WindowStartMilliseconds = windowStartMilliseconds;
        }

        public long WindowStartMilliseconds;
        public int Count;
    }
}

public readonly record struct ModerationPlayerSnapshot(
    long TemporaryMuteRemainingMilliseconds,
    long DeafenRemainingMilliseconds,
    long AutomaticSuspensionRemainingMilliseconds,
    int InvalidPacketStrikes);
