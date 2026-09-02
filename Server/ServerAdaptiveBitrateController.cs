namespace SimpleVoiceChat.Server;

internal readonly record struct ServerBitrateDecision(int TargetBitrate, int PacketLossPercent);

internal static class ServerAdaptiveBitrateController
{
    private static readonly int[] BitrateSteps = { 12_000, 16_000, 20_000, 24_000, 32_000, 48_000 };

    internal static ServerBitrateDecision Evaluate(
        int maximumBitrate,
        int fanOut,
        double listenerLossP75,
        double egressBudgetPressure)
    {
        int maximum = Math.Clamp(maximumBitrate, 12_000, 48_000);
        int target = fanOut switch
        {
            <= 4 => maximum,
            <= 8 => Math.Min(maximum, 20_000),
            <= 16 => Math.Min(maximum, 16_000),
            <= 32 => Math.Min(maximum, 12_000),
            _ => 12_000
        };

        int downshifts = listenerLossP75 switch
        {
            >= 18d => 2,
            >= 8d => 1,
            _ => 0
        };
        downshifts += egressBudgetPressure switch
        {
            >= 0.75d => 2,
            >= 0.45d => 1,
            _ => 0
        };

        while (downshifts-- > 0)
        {
            target = NextLower(target);
        }

        int packetLossPercent = Math.Clamp((int)Math.Ceiling(listenerLossP75), 2, 20);
        return new ServerBitrateDecision(Math.Clamp(target, 12_000, maximum), packetLossPercent);
    }

    private static int NextLower(int current)
    {
        for (int i = BitrateSteps.Length - 1; i >= 0; i--)
        {
            if (BitrateSteps[i] < current)
            {
                return BitrateSteps[i];
            }
        }
        return BitrateSteps[0];
    }
}
