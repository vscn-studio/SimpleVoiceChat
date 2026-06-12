using SimpleVoiceChat.Config;
using SimpleVoiceChat.Networking;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SimpleVoiceChat;

public sealed class ServerVoiceController
{
    private readonly ICoreServerAPI sapi;
    private SimpleVoiceChatServerConfig config;
    private IServerNetworkChannel? controlChannel;
    private IServerNetworkChannel? voiceChannel;
    private readonly Dictionary<string, ClientVoiceStatePacket> statesByUid = new();
    private readonly Dictionary<string, HashSet<string>> mutedByListenerUid = new();
    private readonly Dictionary<string, PacketRateWindow> packetRates = new();

    public ServerVoiceController(ICoreServerAPI sapi, SimpleVoiceChatServerConfig config)
    {
        this.sapi = sapi;
        this.config = config;
    }

    public void Start()
    {
        RegisterChannels();
        RegisterCommands();
        sapi.Event.PlayerJoin += OnPlayerJoin;
        sapi.Event.PlayerLeave += OnPlayerLeave;
    }

    private void RegisterChannels()
    {
        controlChannel = sapi.Network.RegisterChannel(VoiceConstants.ControlChannelName)
            .RegisterMessageType<ClientVoiceStatePacket>()
            .RegisterMessageType<ServerVoiceConfigPacket>()
            .RegisterMessageType<MutePlayerPacket>()
            .SetMessageHandler<ClientVoiceStatePacket>(OnClientState)
            .SetMessageHandler<MutePlayerPacket>(OnMutePlayer);

        voiceChannel = sapi.Network.RegisterUdpChannel(VoiceConstants.VoiceChannelName)
            .RegisterMessageType<VoiceFramePacket>()
            .SetMessageHandler<VoiceFramePacket>(OnVoiceFrame);
    }

    private void RegisterCommands()
    {
        sapi.ChatCommands.Create("svc")
            .WithDescription("SimpleVoiceChat server controls")
            .RequiresPrivilege(Privilege.controlserver)
            .IgnoreAdditionalArgs()
            .HandleWith(HandleServerCommand);
    }

    private TextCommandResult HandleServerCommand(TextCommandCallingArgs args)
    {
        string sub = args.RawArgs.PopWord("status").ToLowerInvariant();
        switch (sub)
        {
            case "status":
                return TextCommandResult.Success(
                    $"SimpleVoiceChat enabled={config.Enabled}, ranges whisper/talk/shout={config.WhisperRange:0.#}/{config.TalkRange:0.#}/{config.ShoutRange:0.#}, max={config.MaxRange:0.#}");

            case "reload":
                config = LoadConfig(sapi);
                BroadcastConfig();
                return TextCommandResult.Success("SimpleVoiceChat config reloaded.");

            case "enable":
                config.Enabled = true;
                SaveConfig();
                BroadcastConfig();
                return TextCommandResult.Success("SimpleVoiceChat enabled.");

            case "disable":
                config.Enabled = false;
                SaveConfig();
                BroadcastConfig();
                return TextCommandResult.Success("SimpleVoiceChat disabled.");

            case "setrange":
            {
                string mode = args.RawArgs.PopWord("").ToLowerInvariant();
                float range = args.RawArgs.PopFloat(-1f) ?? -1f;
                if (range <= 0)
                {
                    return TextCommandResult.Error("Usage: /svc setrange whisper|talk|shout <blocks>");
                }

                switch (mode)
                {
                    case "whisper":
                        config.WhisperRange = range;
                        break;
                    case "talk":
                        config.TalkRange = range;
                        break;
                    case "shout":
                        config.ShoutRange = range;
                        break;
                    default:
                        return TextCommandResult.Error("Usage: /svc setrange whisper|talk|shout <blocks>");
                }

                config.Normalize();
                SaveConfig();
                BroadcastConfig();
                return TextCommandResult.Success($"SimpleVoiceChat {mode} range set to {range:0.#}.");
            }

            default:
                return TextCommandResult.Error("Usage: /svc status|reload|enable|disable|setrange");
        }
    }

    private void OnPlayerJoin(IServerPlayer player)
    {
        SendConfig(player);
    }

    private void OnPlayerLeave(IServerPlayer player)
    {
        statesByUid.Remove(player.PlayerUID);
        mutedByListenerUid.Remove(player.PlayerUID);
        packetRates.Remove(player.PlayerUID);
    }

    private void OnClientState(IServerPlayer fromPlayer, ClientVoiceStatePacket packet)
    {
        statesByUid[fromPlayer.PlayerUID] = packet;
    }

    private void OnMutePlayer(IServerPlayer fromPlayer, MutePlayerPacket packet)
    {
        if (string.IsNullOrWhiteSpace(packet.PlayerUid))
        {
            return;
        }

        if (!mutedByListenerUid.TryGetValue(fromPlayer.PlayerUID, out HashSet<string>? muted))
        {
            muted = new HashSet<string>(StringComparer.Ordinal);
            mutedByListenerUid[fromPlayer.PlayerUID] = muted;
        }

        if (packet.Muted)
        {
            muted.Add(packet.PlayerUid);
        }
        else
        {
            muted.Remove(packet.PlayerUid);
        }
    }

    private void OnVoiceFrame(IServerPlayer fromPlayer, VoiceFramePacket packet)
    {
        if (!config.Enabled || fromPlayer.Entity == null || packet.Payload.Length == 0)
        {
            return;
        }

        if (!AllowPacket(fromPlayer))
        {
            return;
        }

        if (statesByUid.TryGetValue(fromPlayer.PlayerUID, out ClientVoiceStatePacket? state)
            && (state.LocalMuted || state.GlobalMuted))
        {
            return;
        }

        VoiceMode effectiveMode = NormalizeMode(packet.Mode);
        float range = Math.Min(config.GetRange(effectiveMode), config.MaxRange);
        Vec3d speakerPos = fromPlayer.Entity.Pos.XYZ;
        packet.SenderUidHash = Audio.VoiceMath.StableUidHash(fromPlayer.PlayerUID);
        packet.SenderEntityId = fromPlayer.Entity.EntityId;
        packet.Mode = effectiveMode;
        packet.X = (float)speakerPos.X;
        packet.Y = (float)speakerPos.Y;
        packet.Z = (float)speakerPos.Z;

        List<IServerPlayer> recipients = new();
        foreach (IServerPlayer player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (player == fromPlayer || player.Entity == null)
            {
                continue;
            }

            if (mutedByListenerUid.TryGetValue(player.PlayerUID, out HashSet<string>? muted) && muted.Contains(fromPlayer.PlayerUID))
            {
                continue;
            }

            double distance = player.Entity.Pos.XYZ.DistanceTo(speakerPos);
            if (distance <= range + 1.0)
            {
                recipients.Add(player);
            }
        }

        if (recipients.Count > 0)
        {
            voiceChannel?.SendPacket(packet, recipients.ToArray());
        }
    }

    private bool AllowPacket(IServerPlayer player)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (!packetRates.TryGetValue(player.PlayerUID, out PacketRateWindow? window))
        {
            window = new PacketRateWindow(now);
            packetRates[player.PlayerUID] = window;
        }

        if (now - window.WindowStartMs >= 1000)
        {
            window.WindowStartMs = now;
            window.Count = 0;
        }

        window.Count++;
        return window.Count <= config.MaxVoicePacketsPerSecond;
    }

    private VoiceMode NormalizeMode(VoiceMode requested)
    {
        return requested switch
        {
            VoiceMode.Whisper when config.AllowWhisper => VoiceMode.Whisper,
            VoiceMode.Shout when config.AllowShout => VoiceMode.Shout,
            _ => VoiceMode.Talk
        };
    }

    private void SendConfig(IServerPlayer player)
    {
        controlChannel?.SendPacket(PacketMapper.ToPacket(config), player);
    }

    private void BroadcastConfig()
    {
        controlChannel?.BroadcastPacket(PacketMapper.ToPacket(config));
    }

    private void SaveConfig()
    {
        config.Normalize();
        sapi.StoreModConfig(config, VoiceConstants.ServerConfigFileName);
    }

    public static SimpleVoiceChatServerConfig LoadConfig(ICoreAPI api)
    {
        SimpleVoiceChatServerConfig config;
        try
        {
            config = api.LoadModConfig<SimpleVoiceChatServerConfig>(VoiceConstants.ServerConfigFileName) ?? new SimpleVoiceChatServerConfig();
        }
        catch
        {
            config = new SimpleVoiceChatServerConfig();
        }

        config.Normalize();
        api.StoreModConfig(config, VoiceConstants.ServerConfigFileName);
        return config;
    }

    private sealed class PacketRateWindow
    {
        public PacketRateWindow(long windowStartMs)
        {
            WindowStartMs = windowStartMs;
        }

        public long WindowStartMs;
        public int Count;
    }
}
