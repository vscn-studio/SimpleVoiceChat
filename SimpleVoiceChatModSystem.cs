using SimpleVoiceChat.Config;
using SimpleVoiceChat.Integration;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SimpleVoiceChat;

public sealed class SimpleVoiceChatModSystem : ModSystem
{
    private ClientVoiceController? clientController;
    private ServerVoiceController? serverController;
    private Harmony? clientHarmony;
    private readonly List<IVoiceChannelProvider> channelProviders = new();
    private readonly HashSet<string> channelProviderIds = new(StringComparer.OrdinalIgnoreCase);

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
        clientHarmony = new Harmony("simplevoicechat.gui");
        clientHarmony.PatchAll(typeof(SimpleVoiceChatModSystem).Assembly);
        SimpleVoiceChatClientConfig config = LoadClientConfig(api);
        clientController = new ClientVoiceController(api, config);
        clientController.Start();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        SimpleVoiceChatServerConfig config = ServerVoiceController.LoadConfig(api);
        serverController = new ServerVoiceController(api, config, channelProviders);
        serverController.Start();
    }

    public override void Dispose()
    {
        clientHarmony?.UnpatchAll("simplevoicechat.gui");
        clientHarmony = null;
        clientController?.Dispose();
        clientController = null;
        serverController?.Dispose();
        serverController = null;
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
