using SimpleVoiceChat.Config;
using SimpleVoiceChat.Audio;
using SimpleVoiceChat.Integration;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SimpleVoiceChat;

public sealed class SimpleVoiceChatModSystem : ModSystem
{
    private ClientVoiceController? clientController;
    private ServerVoiceController? serverController;
    private readonly List<IVoiceChannelProvider> channelProviders = new();
    private readonly HashSet<string> channelProviderIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly VoiceSettingsExtensionRegistry clientSettingsExtensions = new();
    private AudioBusMixer? clientAudioBuses;

    public static SimpleVoiceChatModSystem? Current { get; private set; }

    /// <summary>Returns the player-voice mixer when the client is running.</summary>
    public AudioBusMixer? ClientAudioBuses => clientAudioBuses;

    /// <summary>Microphone frame API for optional client companion mods.</summary>
    public ISimpleVoiceChatClientAudioApi? ClientAudioApi => clientController?.ClientAudioApi;

    /// <summary>
    /// Client-side controls and windows contributed by other mods. Register
    /// extensions during client startup, then call ShowWindow when needed.
    /// </summary>
    public VoiceSettingsExtensionRegistry ClientSettingsExtensions => clientSettingsExtensions;

    public bool RegisterVoiceChannelProvider(IVoiceChannelProvider provider)
    {
        if (provider is null)
        {
            return false;
        }

        string providerId;
        try
        {
            providerId = provider.ProviderId;
        }
        catch
        {
            return false;
        }

        if (!VoiceChannelProviderId.IsValid(providerId)
            || channelProviders.Count >= 32
            || !channelProviderIds.Add(providerId))
        {
            return false;
        }

        channelProviders.Add(provider);
        serverController?.SetChannelProviders(channelProviders);
        return true;
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        Current = this;
        SimpleVoiceChatClientConfig config = LoadClientConfig(api);
        clientController = new ClientVoiceController(api, config, clientSettingsExtensions);
        clientController.Start();
        clientAudioBuses = clientController.AudioBuses;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        SimpleVoiceChatServerConfig config = ServerVoiceController.LoadConfig(api);
        serverController = new ServerVoiceController(api, config, channelProviders);
        serverController.Start();
    }

    public override void Dispose()
    {
        clientController?.Dispose();
        clientController = null;
        clientAudioBuses = null;
        clientSettingsExtensions.Clear();
        serverController?.Dispose();
        serverController = null;
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
        base.Dispose();
    }

    private static SimpleVoiceChatClientConfig LoadClientConfig(ICoreClientAPI api)
    {
        SimpleVoiceChatClientConfig config;
        try
        {
            config = api.LoadModConfig<SimpleVoiceChatClientConfig>(VoiceConstants.ClientConfigFileName) ?? new SimpleVoiceChatClientConfig();
        }
        catch
        {
            config = new SimpleVoiceChatClientConfig();
        }

        config.Normalize();
        api.StoreModConfig(config, VoiceConstants.ClientConfigFileName);
        return config;
    }
}
