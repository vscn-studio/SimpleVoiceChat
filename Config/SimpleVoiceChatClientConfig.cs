namespace SimpleVoiceChat.Config;

public sealed class SimpleVoiceChatClientConfig
{
    public const string AlibabaSpeechRecognitionProvider = "alibaba";
    public const string SiliconFlowSpeechRecognitionProvider = "siliconflow";
    public const string DeepgramSpeechRecognitionProvider = "deepgram";
    public const string WhisperSpeechRecognitionProvider = "whisper";
    public const string AlibabaSpeechRecognitionModel = "qwen3-asr-flash";
    public const string AlibabaSpeechRecognitionEndpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";
    public const string SiliconFlowSpeechRecognitionModel = "FunAudioLLM/SenseVoiceSmall";
    public const string SiliconFlowSpeechRecognitionEndpoint = "https://api.siliconflow.cn/v1/audio/transcriptions";
    public const string DeepgramSpeechRecognitionModel = "nova-3";
    public const string DeepgramSpeechRecognitionEndpoint = "https://api.deepgram.com/v1/listen?model=nova-3&smart_format=true";

    private const int CurrentConfigVersion = 7;
    private const int MaxServerProfiles = 128;

    public int ConfigVersion { get; set; } = CurrentConfigVersion;
    public string InputDeviceName { get; set; } = string.Empty;
    public string OutputDeviceName { get; set; } = string.Empty;
    public float OutputVolume { get; set; } = 1f;
    public float MicGain { get; set; } = 1f;
    public float NoiseGate { get; set; } = 0.015f;
    public float VoiceActivationThreshold { get; set; } = 0.08f;
    public string PushToTalkKey { get; set; } = "N";
    public string ModeCycleKey { get; set; } = "LBracket";
    public bool PreferVoiceActivation { get; set; }
    // Legacy setting retained so existing configuration files migrate cleanly.
    public bool PreferContinuousTalk { get; set; }
    public bool InitialSetupCompleted { get; set; }
    public bool InitialSetupPromptShown { get; set; }
    public bool ShowHudIndicator { get; set; } = true;
    public bool ShowMicrophoneHud { get; set; } = true;
    public bool EnableOcclusionEffects { get; set; } = true;
    public bool PerformanceMode { get; set; } = false;
    public bool AdaptiveJitterBuffer { get; set; } = true;
    public int PreferredOpusBitrateKbps { get; set; }
    public bool EnableNoiseSuppression { get; set; } = false;
    public bool EnableEchoCancellation { get; set; } = false;
    public bool EnableSpeechRecognition { get; set; } = false;
    public string SpeechRecognitionProvider { get; set; } = AlibabaSpeechRecognitionProvider;
    public string SpeechRecognitionApiKey { get; set; } = string.Empty;
    public string SpeechRecognitionModel { get; set; } = AlibabaSpeechRecognitionModel;
    public string SpeechRecognitionEndpoint { get; set; } = AlibabaSpeechRecognitionEndpoint;
    public Dictionary<string, SpeechRecognitionProviderConfig> SpeechRecognitionProviders { get; set; } = new(StringComparer.Ordinal);
    public float ChannelOutputVolume { get; set; } = 1f;
    public string SelectedChannelId { get; set; } = string.Empty;
    public Networking.VoiceTransmitTarget TransmitTarget { get; set; } = Networking.VoiceTransmitTarget.ProximityAndChannel;
    public Dictionary<string, float> PlayerVolumeOverrides { get; set; } = new(StringComparer.Ordinal);
    public List<string> MutedPlayerUids { get; set; } = new();
    public Dictionary<string, SimpleVoiceChatServerProfile> ServerProfiles { get; set; } = new(StringComparer.Ordinal);
    public bool NeedsServerProfileMigration { get; set; }

    private string activeServerId = string.Empty;

    internal string ActiveServerId => activeServerId;

    public void Normalize()
    {
        Migrate();
        InputDeviceName = Limit(InputDeviceName, 256);
        OutputDeviceName = Limit(OutputDeviceName, 256);
        PushToTalkKey = Limit(PushToTalkKey, 64);
        ModeCycleKey = Limit(ModeCycleKey, 64);
        SpeechRecognitionProvider = NormalizeSpeechRecognitionProvider(SpeechRecognitionProvider);
        SpeechRecognitionApiKey = Limit(SpeechRecognitionApiKey, 512);
        SpeechRecognitionModel = Limit(SpeechRecognitionModel, 2048);
        SpeechRecognitionEndpoint = Limit(SpeechRecognitionEndpoint, 1024);
        SpeechRecognitionProviders ??= new Dictionary<string, SpeechRecognitionProviderConfig>(StringComparer.Ordinal);
        SpeechRecognitionProviders = SpeechRecognitionProviders
            .Where(pair => IsSpeechRecognitionProvider(pair.Key) && pair.Value != null)
            .Take(4)
            .ToDictionary(
                pair => NormalizeSpeechRecognitionProvider(pair.Key),
                pair => pair.Value.Normalize(),
                StringComparer.Ordinal);
        StoreSpeechRecognitionProviderSettings();
        SelectedChannelId = Limit(SelectedChannelId, Networking.VoiceProtocol.MaxControlStringLength);
        if (TransmitTarget is < Networking.VoiceTransmitTarget.Proximity or > Networking.VoiceTransmitTarget.ProximityAndChannel)
        {
            TransmitTarget = Networking.VoiceTransmitTarget.ProximityAndChannel;
        }
        OutputVolume = Math.Clamp(OutputVolume, 0f, 2f);
        MicGain = Math.Clamp(MicGain, 0.1f, 4f);
        NoiseGate = Math.Clamp(NoiseGate, 0f, 0.2f);
        VoiceActivationThreshold = Math.Clamp(VoiceActivationThreshold, 0.005f, 0.2f);
        if (VoiceActivationThreshold < NoiseGate)
        {
            VoiceActivationThreshold = NoiseGate;
        }
        ChannelOutputVolume = Math.Clamp(ChannelOutputVolume, 0f, 2f);
        PreferredOpusBitrateKbps = NormalizePreferredOpusBitrate(PreferredOpusBitrateKbps);
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

        ServerProfiles ??= new Dictionary<string, SimpleVoiceChatServerProfile>(StringComparer.Ordinal);
        ServerProfiles = ServerProfiles
            .Where(pair => IsValidServerId(pair.Key) && pair.Value != null)
            .Take(MaxServerProfiles)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Normalize(),
                StringComparer.Ordinal);
    }

    private void Migrate()
    {
        if (PreferContinuousTalk)
        {
            PreferVoiceActivation = true;
            PreferContinuousTalk = false;
        }

        if (ConfigVersion < 2)
        {
            ShowMicrophoneHud = ShowHudIndicator;
            ConfigVersion = 2;
        }

        if (ConfigVersion < 3)
        {
            NeedsServerProfileMigration = true;
            ConfigVersion = 3;
        }

        // Existing installations predate the setup wizard. Keep their current
        // audio choices and avoid showing a first-run prompt after upgrading.
        if (ConfigVersion < 4)
        {
            InitialSetupCompleted = true;
            InitialSetupPromptShown = true;
            ConfigVersion = 4;
        }

        if (ConfigVersion < 5)
        {
            ConfigVersion = 5;
        }

        if (ConfigVersion < 6)
        {
            ConfigVersion = 6;
        }

        if (ConfigVersion < 7)
        {
            PreferredOpusBitrateKbps = 0;
            ConfigVersion = 7;
        }

        ConfigVersion = Math.Max(CurrentConfigVersion, ConfigVersion);
    }

    internal static int NormalizePreferredOpusBitrate(int value)
        => value is 8 or 12 or 16 or 20 or 24 or 32 ? value : 0;

    internal bool SelectSpeechRecognitionProvider(string? value)
    {
        string provider = NormalizeSpeechRecognitionProvider(value);
        if (provider == SpeechRecognitionProvider)
        {
            return false;
        }

        StoreSpeechRecognitionProviderSettings();
        SpeechRecognitionProvider = provider;
        if (SpeechRecognitionProviders.TryGetValue(provider, out SpeechRecognitionProviderConfig? saved))
        {
            saved.ApplyTo(this);
        }
        else
        {
            CreateSpeechRecognitionProviderDefaults(provider).ApplyTo(this);
            StoreSpeechRecognitionProviderSettings();
        }
        return true;
    }

    internal void StoreSpeechRecognitionProviderSettings()
    {
        SpeechRecognitionProviders ??= new Dictionary<string, SpeechRecognitionProviderConfig>(StringComparer.Ordinal);
        SpeechRecognitionProviders[SpeechRecognitionProvider] = new SpeechRecognitionProviderConfig
        {
            ApiKey = Limit(SpeechRecognitionApiKey, 512),
            Model = Limit(SpeechRecognitionModel, 2048),
            Endpoint = Limit(SpeechRecognitionEndpoint, 1024)
        };
    }

    private static SpeechRecognitionProviderConfig CreateSpeechRecognitionProviderDefaults(string provider)
    {
        return provider switch
        {
            SiliconFlowSpeechRecognitionProvider => new SpeechRecognitionProviderConfig
            {
                Model = SiliconFlowSpeechRecognitionModel,
                Endpoint = SiliconFlowSpeechRecognitionEndpoint
            },
            DeepgramSpeechRecognitionProvider => new SpeechRecognitionProviderConfig
            {
                Model = DeepgramSpeechRecognitionModel,
                Endpoint = DeepgramSpeechRecognitionEndpoint
            },
            WhisperSpeechRecognitionProvider => new SpeechRecognitionProviderConfig(),
            _ => new SpeechRecognitionProviderConfig
            {
                Model = AlibabaSpeechRecognitionModel,
                Endpoint = AlibabaSpeechRecognitionEndpoint
            }
        };
    }

    private static bool IsSpeechRecognitionProvider(string? value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is AlibabaSpeechRecognitionProvider
            or SiliconFlowSpeechRecognitionProvider
            or DeepgramSpeechRecognitionProvider
            or WhisperSpeechRecognitionProvider;
    }

    private static string NormalizeSpeechRecognitionProvider(string? value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            SiliconFlowSpeechRecognitionProvider => SiliconFlowSpeechRecognitionProvider,
            DeepgramSpeechRecognitionProvider => DeepgramSpeechRecognitionProvider,
            WhisperSpeechRecognitionProvider => WhisperSpeechRecognitionProvider,
            _ => AlibabaSpeechRecognitionProvider
        };
    }

    internal bool ActivateServer(string? serverIdentifier)
    {
        string serverId = NormalizeServerId(serverIdentifier);
        if (serverId.Length == 0)
        {
            return false;
        }

        Normalize();
        if (activeServerId == serverId)
        {
            return true;
        }

        StoreActiveServerProfile();
        activeServerId = serverId;

        if (ServerProfiles.TryGetValue(serverId, out SimpleVoiceChatServerProfile? profile))
        {
            profile.ApplyTo(this);
        }
        else if (NeedsServerProfileMigration)
        {
            ServerProfiles[serverId] = SimpleVoiceChatServerProfile.From(this);
            NeedsServerProfileMigration = false;
        }
        else
        {
            SimpleVoiceChatServerProfile defaults = new();
            defaults.ApplyTo(this);
            ServerProfiles[serverId] = defaults;
        }

        StoreActiveServerProfile();
        return true;
    }

    internal void StoreActiveServerProfile()
    {
        if (activeServerId.Length == 0)
        {
            return;
        }

        ServerProfiles ??= new Dictionary<string, SimpleVoiceChatServerProfile>(StringComparer.Ordinal);
        ServerProfiles[activeServerId] = SimpleVoiceChatServerProfile.From(this);
        while (ServerProfiles.Count > MaxServerProfiles)
        {
            string oldest = ServerProfiles.Keys.First();
            if (oldest == activeServerId)
            {
                oldest = ServerProfiles.Keys.Skip(1).First();
            }
            ServerProfiles.Remove(oldest);
        }
    }

    private static string NormalizeServerId(string? value)
    {
        string normalized = (value ?? string.Empty).Trim();
        return IsValidServerId(normalized) ? normalized : string.Empty;
    }

    private static bool IsValidServerId(string value)
    {
        return value.Length is > 0 and <= 256;
    }

    private static string Limit(string? value, int maximumLength)
    {
        string normalized = value ?? string.Empty;
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }
}

public sealed class SpeechRecognitionProviderConfig
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;

    internal SpeechRecognitionProviderConfig Normalize()
    {
        ApiKey = Limit(ApiKey, 512);
        Model = Limit(Model, 2048);
        Endpoint = Limit(Endpoint, 1024);
        return this;
    }

    internal void ApplyTo(SimpleVoiceChatClientConfig target)
    {
        target.SpeechRecognitionApiKey = ApiKey;
        target.SpeechRecognitionModel = Model;
        target.SpeechRecognitionEndpoint = Endpoint;
    }

    private static string Limit(string? value, int maximumLength)
    {
        string normalized = value ?? string.Empty;
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }
}

public sealed class SimpleVoiceChatServerProfile
{
    public bool EnableOcclusionEffects { get; set; } = true;
    public bool AdaptiveJitterBuffer { get; set; } = true;
    public float ChannelOutputVolume { get; set; } = 1f;
    public string SelectedChannelId { get; set; } = string.Empty;
    public Networking.VoiceTransmitTarget TransmitTarget { get; set; } = Networking.VoiceTransmitTarget.ProximityAndChannel;
    public Dictionary<string, float> PlayerVolumeOverrides { get; set; } = new(StringComparer.Ordinal);
    public List<string> MutedPlayerUids { get; set; } = new();

    internal static SimpleVoiceChatServerProfile From(SimpleVoiceChatClientConfig source)
    {
        return new SimpleVoiceChatServerProfile
        {
            EnableOcclusionEffects = source.EnableOcclusionEffects,
            AdaptiveJitterBuffer = source.AdaptiveJitterBuffer,
            ChannelOutputVolume = source.ChannelOutputVolume,
            SelectedChannelId = source.SelectedChannelId,
            TransmitTarget = source.TransmitTarget,
            PlayerVolumeOverrides = new Dictionary<string, float>(source.PlayerVolumeOverrides, StringComparer.Ordinal),
            MutedPlayerUids = source.MutedPlayerUids.ToList()
        };
    }

    internal void ApplyTo(SimpleVoiceChatClientConfig target)
    {
        target.EnableOcclusionEffects = EnableOcclusionEffects;
        target.AdaptiveJitterBuffer = AdaptiveJitterBuffer;
        target.ChannelOutputVolume = ChannelOutputVolume;
        target.SelectedChannelId = SelectedChannelId;
        target.TransmitTarget = TransmitTarget;
        target.PlayerVolumeOverrides = new Dictionary<string, float>(PlayerVolumeOverrides ?? new Dictionary<string, float>(), StringComparer.Ordinal);
        target.MutedPlayerUids = (MutedPlayerUids ?? new List<string>()).ToList();
    }

    internal SimpleVoiceChatServerProfile Normalize()
    {
        SelectedChannelId = Limit(SelectedChannelId, Networking.VoiceProtocol.MaxControlStringLength);
        if (TransmitTarget is < Networking.VoiceTransmitTarget.Proximity or > Networking.VoiceTransmitTarget.ProximityAndChannel)
        {
            TransmitTarget = Networking.VoiceTransmitTarget.ProximityAndChannel;
        }
        ChannelOutputVolume = Math.Clamp(ChannelOutputVolume, 0f, 2f);
        PlayerVolumeOverrides ??= new Dictionary<string, float>(StringComparer.Ordinal);
        PlayerVolumeOverrides = PlayerVolumeOverrides
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Key.Length <= Networking.VoiceProtocol.MaxControlStringLength)
            .Take(256)
            .ToDictionary(pair => pair.Key, pair => Math.Clamp(pair.Value, 0f, 2f), StringComparer.Ordinal);
        MutedPlayerUids ??= new List<string>();
        MutedPlayerUids = MutedPlayerUids
            .Where(uid => !string.IsNullOrWhiteSpace(uid) && uid.Length <= Networking.VoiceProtocol.MaxControlStringLength)
            .Distinct(StringComparer.Ordinal)
            .Take(256)
            .ToList();
        return this;
    }

    private static string Limit(string? value, int maximumLength)
    {
        string normalized = value ?? string.Empty;
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }
}
