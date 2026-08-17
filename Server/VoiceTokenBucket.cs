namespace SimpleVoiceChat.Server;

public sealed class VoiceTokenBucket
{
    private readonly double tokensPerMillisecond;
    private readonly double capacity;
    private double tokens;
    private long lastRefillMilliseconds;

    public VoiceTokenBucket(double tokensPerSecond, double burstCapacity, long nowMilliseconds)
    {
        if (tokensPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokensPerSecond));
        }
        if (burstCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(burstCapacity));
        }

        tokensPerMillisecond = tokensPerSecond / 1000d;
        capacity = burstCapacity;
        tokens = burstCapacity;
        lastRefillMilliseconds = nowMilliseconds;
    }

    public bool TryConsume(double amount, long nowMilliseconds)
    {
        if (amount < 0)
        {
            return false;
        }

        Refill(nowMilliseconds);
        if (tokens + 0.0001d < amount)
        {
            return false;
        }

        tokens -= amount;
        return true;
    }

    public double Available(long nowMilliseconds)
    {
        Refill(nowMilliseconds);
        return tokens;
    }

    public double Pressure(long nowMilliseconds)
    {
        Refill(nowMilliseconds);
        return capacity <= 0d ? 1d : Math.Clamp(1d - tokens / capacity, 0d, 1d);
    }

    private void Refill(long nowMilliseconds)
    {
        if (nowMilliseconds <= lastRefillMilliseconds)
        {
            return;
        }

        long elapsed = nowMilliseconds - lastRefillMilliseconds;
        tokens = Math.Min(capacity, tokens + elapsed * tokensPerMillisecond);
        lastRefillMilliseconds = nowMilliseconds;
    }
}
