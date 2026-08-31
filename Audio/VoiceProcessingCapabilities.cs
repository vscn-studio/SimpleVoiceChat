namespace SimpleVoiceChat.Audio;

public static class VoiceProcessingCapabilities
{
    public static bool NoiseSuppressionAvailable => RnnoiseNoiseSuppressor.IsAvailable;
    public static bool EchoCancellationAvailable => false;
    public static string BackendName => NoiseSuppressionAvailable ? "RNNoise + AGC / gate" : "Basic AGC / gate";
}
