namespace SimpleVoiceChat.Server;

public sealed class ListenerEgressBudget
{
    private readonly Dictionary<string, VoiceTokenBucket> budgetsByListener = new(StringComparer.Ordinal);
    private double bytesPerSecond;

    public ListenerEgressBudget(int kilobitsPerSecond)
    {
        SetLimit(kilobitsPerSecond);
    }

    public int ListenerCount => budgetsByListener.Count;

    public bool HasCapacity(string listenerUid, int bytes, long nowMilliseconds)
    {
        if (string.IsNullOrWhiteSpace(listenerUid) || bytes <= 0)
        {
            return false;
        }

        VoiceTokenBucket budget = GetOrCreate(listenerUid, nowMilliseconds);
        return budget.Available(nowMilliseconds) + 0.0001d >= bytes;
    }

    public bool TryConsume(string listenerUid, int bytes, long nowMilliseconds)
    {
        return HasCapacity(listenerUid, bytes, nowMilliseconds)
            && budgetsByListener[listenerUid].TryConsume(bytes, nowMilliseconds);
    }

    public void SetLimit(int kilobitsPerSecond)
    {
        bytesPerSecond = Math.Clamp(kilobitsPerSecond, 64, 8_192) * 1000d / 8d;
        budgetsByListener.Clear();
    }

    public void Remove(string listenerUid) => budgetsByListener.Remove(listenerUid);

    public void Clear() => budgetsByListener.Clear();

    private VoiceTokenBucket GetOrCreate(string listenerUid, long nowMilliseconds)
    {
        if (!budgetsByListener.TryGetValue(listenerUid, out VoiceTokenBucket? budget))
        {
            budget = new VoiceTokenBucket(bytesPerSecond, bytesPerSecond * 1.25d, nowMilliseconds);
            budgetsByListener[listenerUid] = budget;
        }
        return budget;
    }
}
