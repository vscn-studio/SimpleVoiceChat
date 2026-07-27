namespace SimpleVoiceChat.Server;

public sealed class ListenerStreamArbiter
{
    private const long StreamTimeoutMilliseconds = 350;
    private readonly Dictionary<string, Dictionary<string, Slot>> slotsByListener = new(StringComparer.Ordinal);

    public bool TryAdmit(
        string listenerUid,
        string speakerUid,
        int priority,
        double distanceSquared,
        int maxStreams,
        long nowMilliseconds,
        bool proximity = false,
        int maxProximityStreams = int.MaxValue)
    {
        if (!slotsByListener.TryGetValue(listenerUid, out Dictionary<string, Slot>? slots))
        {
            slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
            slotsByListener[listenerUid] = slots;
        }

        RemoveExpired(slots, nowMilliseconds);
        int boundedMaxStreams = Math.Max(1, maxStreams);
        int boundedMaxProximityStreams = Math.Clamp(maxProximityStreams, 1, boundedMaxStreams);
        int proximityStreams = slots.Values.Count(slot => slot.Proximity);
        if (slots.TryGetValue(speakerUid, out Slot current))
        {
            if (proximity && !current.Proximity && proximityStreams >= boundedMaxProximityStreams)
            {
                return false;
            }

            slots[speakerUid] = new Slot(priority, distanceSquared, nowMilliseconds, proximity);
            return true;
        }

        bool proximityLimitReached = proximity && proximityStreams >= boundedMaxProximityStreams;
        if (slots.Count < boundedMaxStreams && !proximityLimitReached)
        {
            slots[speakerUid] = new Slot(priority, distanceSquared, nowMilliseconds, proximity);
            return true;
        }

        KeyValuePair<string, Slot> worst = default;
        bool hasWorst = false;
        foreach (KeyValuePair<string, Slot> candidate in slots)
        {
            if (proximityLimitReached && !candidate.Value.Proximity)
            {
                continue;
            }
            if (!hasWorst || IsWorse(candidate.Value, worst.Value))
            {
                worst = candidate;
                hasWorst = true;
            }
        }

        if (!hasWorst
            || priority < worst.Value.Priority
            || (priority == worst.Value.Priority && distanceSquared >= worst.Value.DistanceSquared))
        {
            return false;
        }

        slots.Remove(worst.Key);
        slots[speakerUid] = new Slot(priority, distanceSquared, nowMilliseconds, proximity);
        return true;
    }

    public void RemovePlayer(string playerUid)
    {
        slotsByListener.Remove(playerUid);
        foreach (Dictionary<string, Slot> slots in slotsByListener.Values)
        {
            slots.Remove(playerUid);
        }
    }

    public int ActiveSlotCount(long nowMilliseconds)
    {
        int count = 0;
        foreach (Dictionary<string, Slot> slots in slotsByListener.Values)
        {
            RemoveExpired(slots, nowMilliseconds);
            count += slots.Count;
        }
        return count;
    }

    private static void RemoveExpired(Dictionary<string, Slot> slots, long nowMilliseconds)
    {
        List<string>? expired = null;
        foreach (KeyValuePair<string, Slot> pair in slots)
        {
            if (nowMilliseconds - pair.Value.LastSeenMilliseconds <= StreamTimeoutMilliseconds)
            {
                continue;
            }

            expired ??= new List<string>();
            expired.Add(pair.Key);
        }

        if (expired == null)
        {
            return;
        }
        foreach (string uid in expired)
        {
            slots.Remove(uid);
        }
    }

    private static bool IsWorse(Slot candidate, Slot current)
    {
        return candidate.Priority < current.Priority
            || candidate.Priority == current.Priority && candidate.DistanceSquared > current.DistanceSquared
            || candidate.Priority == current.Priority
                && candidate.DistanceSquared.Equals(current.DistanceSquared)
                && candidate.LastSeenMilliseconds < current.LastSeenMilliseconds;
    }

    private readonly record struct Slot(int Priority, double DistanceSquared, long LastSeenMilliseconds, bool Proximity);
}
