namespace SimpleVoiceChat.Audio;

public static class VoiceProcessingCapabilities
{
    public static bool NoiseSuppressionAvailable => false;
    public static bool EchoCancellationAvailable => false;
    public static string BackendName => "Basic AGC / gate";
}
