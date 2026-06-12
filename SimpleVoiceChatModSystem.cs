using SimpleVoiceChat.Config;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SimpleVoiceChat;

public sealed class SimpleVoiceChatModSystem : ModSystem
{
    private ClientVoiceController? clientController;
    private ServerVoiceController? serverController;

    public override void StartClientSide(ICoreClientAPI api)
    {
        SimpleVoiceChatClientConfig config = LoadClientConfig(api);
        clientController = new ClientVoiceController(api, config);
        clientController.Start();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        SimpleVoiceChatServerConfig config = ServerVoiceController.LoadConfig(api);
        serverController = new ServerVoiceController(api, config);
        serverController.Start();
    }

    public override void Dispose()
    {
        clientController?.Dispose();
        clientController = null;
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
