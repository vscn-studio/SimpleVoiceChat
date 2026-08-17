namespace SimpleVoiceChat.Audio;

internal sealed class AdaptiveVoiceBitrateController
{
    private const long EvaluationIntervalMilliseconds = 1_000;
    private const long DownshiftCooldownMilliseconds = 2_000;
    private const int HealthyEvaluationsBeforeUpshift = 10;
    private static readonly int[] BitrateSteps = { 8_000, 12_000, 16_000, 20_000, 24_000, 32_000 };

    private int maximumBitrate = 20_000;
    private int healthyEvaluations;
    private int impairedEvaluations;
    private long lastEvaluationMilliseconds;
    private long lastBitrateChangeMilliseconds;

    internal int CurrentBitrate { get; private set; } = 20_000;
    internal int PacketLossPercent { get; private set; } = 5;

    internal bool IsEvaluationDue(long nowMilliseconds)
        => nowMilliseconds - lastEvaluationMilliseconds >= EvaluationIntervalMilliseconds;

    internal void Reset(int maximum, long nowMilliseconds)
    {
        maximumBitrate = Math.Clamp(maximum, 8_000, 32_000);
        CurrentBitrate = maximumBitrate;
        PacketLossPercent = 5;
        healthyEvaluations = 0;
        impairedEvaluations = 0;
        lastEvaluationMilliseconds = nowMilliseconds;
        lastBitrateChangeMilliseconds = nowMilliseconds;
    }

    internal bool Update(
        long nowMilliseconds,
        bool udpResponsive,
        double roundTripMilliseconds,
        double lossPercent)
    {
        if (nowMilliseconds - lastEvaluationMilliseconds < EvaluationIntervalMilliseconds)
        {
            return false;
        }
        lastEvaluationMilliseconds = nowMilliseconds;

        int nextPacketLoss = roundTripMilliseconds < 0
            ? 5
            : Math.Clamp((int)Math.Round(lossPercent), 2, 20);
        bool changed = nextPacketLoss != PacketLossPercent;
        PacketLossPercent = nextPacketLoss;

        // Wait for the first probe result so connection startup does not look like packet loss.
        if (roundTripMilliseconds < 0)
        {
            return changed;
        }

        bool severe = !udpResponsive || lossPercent >= 20d || roundTripMilliseconds >= 400d;
        bool impaired = lossPercent >= 8d || roundTripMilliseconds >= 220d;
        bool healthy = udpResponsive && lossPercent <= 2d && roundTripMilliseconds <= 120d;

        if (severe)
        {
            healthyEvaluations = 0;
            impairedEvaluations = 0;
            if (nowMilliseconds - lastBitrateChangeMilliseconds >= DownshiftCooldownMilliseconds)
            {
                changed |= SetBitrate(NextLowerBitrate(), nowMilliseconds);
            }
            return changed;
        }

        if (impaired)
        {
            healthyEvaluations = 0;
            impairedEvaluations++;
            if (impairedEvaluations >= 2
                && nowMilliseconds - lastBitrateChangeMilliseconds >= DownshiftCooldownMilliseconds)
            {
                impairedEvaluations = 0;
                changed |= SetBitrate(NextLowerBitrate(), nowMilliseconds);
            }
            return changed;
        }

        impairedEvaluations = 0;
        if (!healthy)
        {
            healthyEvaluations = 0;
            return changed;
        }

        healthyEvaluations++;
        if (healthyEvaluations >= HealthyEvaluationsBeforeUpshift)
        {
            healthyEvaluations = 0;
            changed |= SetBitrate(NextHigherBitrate(), nowMilliseconds);
        }
        return changed;
    }

    private bool SetBitrate(int bitrate, long nowMilliseconds)
    {
        if (bitrate == CurrentBitrate)
        {
            return false;
        }

        CurrentBitrate = bitrate;
        lastBitrateChangeMilliseconds = nowMilliseconds;
        return true;
    }

    private int NextLowerBitrate()
    {
        for (int i = BitrateSteps.Length - 1; i >= 0; i--)
        {
            if (BitrateSteps[i] < CurrentBitrate)
            {
                return BitrateSteps[i];
            }
        }
        return CurrentBitrate;
    }

    private int NextHigherBitrate()
    {
        foreach (int step in BitrateSteps)
        {
            if (step > CurrentBitrate && step <= maximumBitrate)
            {
                return step;
            }
        }
        return maximumBitrate > CurrentBitrate ? maximumBitrate : CurrentBitrate;
    }
}
