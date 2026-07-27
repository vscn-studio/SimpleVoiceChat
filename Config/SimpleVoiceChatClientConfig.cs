namespace SimpleVoiceChat.Config;

public sealed class SimpleVoiceChatClientConfig
{
    private const int CurrentConfigVersion = 2;

    public int ConfigVersion { get; set; } = 1;
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
    public bool AdaptiveJitterBuffer { get; set; } = true;
    public bool EnableNoiseSuppression { get; set; } = false;
    public bool EnableEchoCancellation { get; set; } = false;
    public float ChannelOutputVolume { get; set; } = 1f;
    public string SelectedChannelId { get; set; } = string.Empty;
    public Networking.VoiceTransmitTarget TransmitTarget { get; set; } = Networking.VoiceTransmitTarget.ProximityAndChannel;
    public Dictionary<string, float> PlayerVolumeOverrides { get; set; } = new(StringComparer.Ordinal);
    public List<string> MutedPlayerUids { get; set; } = new();

    public void Normalize()
    {
        Migrate();
        InputDeviceName = Limit(InputDeviceName, 256);
        SelectedChannelId = Limit(SelectedChannelId, Networking.VoiceProtocol.MaxControlStringLength);
        if (TransmitTarget is < Networking.VoiceTransmitTarget.Proximity or > Networking.VoiceTransmitTarget.ProximityAndChannel)
        {
            TransmitTarget = Networking.VoiceTransmitTarget.ProximityAndChannel;
        }
        OutputVolume = Math.Clamp(OutputVolume, 0f, 2f);
        MicGain = Math.Clamp(MicGain, 0.1f, 4f);
        NoiseGate = Math.Clamp(NoiseGate, 0f, 0.2f);
        ChannelOutputVolume = Math.Clamp(ChannelOutputVolume, 0f, 2f);
        ShowHudIndicator = ShowMicrophoneHud;
        PlayerVolumeOverrides ??= new Dictionary<string, float>(StringComparer.Ordinal);
        PlayerVolumeOverrides = PlayerVolumeOverrides
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key)
                && pair.Key.Length <= Networking.VoiceProtocol.MaxControlStringLength)
            .Take(256)
            .ToDictionary(pair => pair.Key, pair => Math.Clamp(pair.Value, 0f, 2f), StringComparer.Ordinal);
        MutedPlayerUids ??= new List<string>();
        MutedPlayerUids = MutedPlayerUids
            .Where(uid => !string.IsNullOrWhiteSpace(uid)
                && uid.Length <= Networking.VoiceProtocol.MaxControlStringLength)
            .Distinct(StringComparer.Ordinal)
            .Take(256)
            .ToList();
    }

    private void Migrate()
    {
        if (ConfigVersion < 2)
        {
            ShowMicrophoneHud = ShowHudIndicator;
            ConfigVersion = 2;
        }

        ConfigVersion = Math.Max(CurrentConfigVersion, ConfigVersion);
    }

    private static string Limit(string? value, int maximumLength)
    {
        string normalized = value ?? string.Empty;
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }
}
