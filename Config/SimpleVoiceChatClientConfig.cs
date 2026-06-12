namespace SimpleVoiceChat.Config;

public sealed class SimpleVoiceChatClientConfig
{
    public string InputDeviceName { get; set; } = string.Empty;
    public float OutputVolume { get; set; } = 1f;
    public float MicGain { get; set; } = 1f;
    public float NoiseGate { get; set; } = 0.015f;
    public string PushToTalkKey { get; set; } = "N";
    public string ModeCycleKey { get; set; } = "LBracket";
    public bool ShowHudIndicator { get; set; } = true;
    public bool ShowMicrophoneHud { get; set; } = true;
    public bool EnableOcclusionEffects { get; set; } = true;
    public bool PerformanceMode { get; set; } = false;
    public List<string> MutedPlayerUids { get; set; } = new();

    public void Normalize()
    {
        OutputVolume = Math.Clamp(OutputVolume, 0f, 2f);
        MicGain = Math.Clamp(MicGain, 0.1f, 4f);
        NoiseGate = Math.Clamp(NoiseGate, 0f, 0.2f);
        MutedPlayerUids ??= new List<string>();
    }
}
