using SimpleVoiceChat.Config;
using SimpleVoiceChat.Integration;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SimpleVoiceChat;

public sealed class SimpleVoiceChatModSystem : ModSystem
{
    private ClientVoiceController? clientController;
    private ServerVoiceController? serverController;
    private readonly List<IVoiceGroupProvider> groupProviders = new();
    private readonly HashSet<string> groupProviderIds = new(StringComparer.OrdinalIgnoreCase);

    public bool RegisterVoiceGroupProvider(IVoiceGroupProvider provider)
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

        if (!VoiceGroupProviderId.IsValid(providerId)
            || groupProviders.Count >= 32
            || !groupProviderIds.Add(providerId))
        {
            return false;
        }

        groupProviders.Add(provider);
        serverController?.SetGroupProviders(groupProviders);
        return true;
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        SimpleVoiceChatClientConfig config = LoadClientConfig(api);
        clientController = new ClientVoiceController(api, config);
        clientController.Start();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        SimpleVoiceChatServerConfig config = ServerVoiceController.LoadConfig(api);
        serverController = new ServerVoiceController(api, config, groupProviders);
        serverController.Start();
    }

    public override void Dispose()
    {
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
