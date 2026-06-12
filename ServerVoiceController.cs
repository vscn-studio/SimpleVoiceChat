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
    private readonly Dictionary<string, HashSet<string>> squadMembersByUid = new(StringComparer.Ordinal);
    private long lastSquadHudBroadcastMs;

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
        sapi.Event.RegisterGameTickListener(OnSlowTick, 500);
    }

    private void RegisterChannels()
    {
        controlChannel = sapi.Network.RegisterChannel(VoiceConstants.ControlChannelName)
            .RegisterMessageType<ClientVoiceStatePacket>()
            .RegisterMessageType<ServerVoiceConfigPacket>()
            .RegisterMessageType<MutePlayerPacket>()
            .RegisterMessageType<SquadBindPacket>()
            .RegisterMessageType<AdminVoiceControlPacket>()
            .RegisterMessageType<SquadHudPacket>()
            .SetMessageHandler<ClientVoiceStatePacket>(OnClientState)
            .SetMessageHandler<MutePlayerPacket>(OnMutePlayer)
            .SetMessageHandler<SquadBindPacket>(OnSquadBind)
            .SetMessageHandler<AdminVoiceControlPacket>(OnAdminVoiceControl);

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
                    $"SimpleVoiceChat enabled={config.Enabled}, ranges whisper/talk/shout={config.WhisperRange:0.#}/{config.TalkRange:0.#}/{config.ShoutRange:0.#}, max={config.MaxRange:0.#}, squads={squadMembersByUid.Count}, adminMuted={config.GloballyMutedPlayerUids.Count}, forceBlocked={config.ForceBlockedPlayerUids.Count}");

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

            case "adminmute":
            case "adminunmute":
            case "forceblock":
            case "unforceblock":
            {
                string target = args.RawArgs.PopWord("");
                if (string.IsNullOrWhiteSpace(target))
                {
                    return TextCommandResult.Error($"Usage: /svc {sub} <player>");
                }

                return HandleAdminVoiceControl(sub, target);
            }

            case "adminmutes":
                return TextCommandResult.Success(BuildAdminMuteList());

            default:
                return TextCommandResult.Error("Usage: /svc status|reload|enable|disable|setrange|adminmute|adminunmute|forceblock|unforceblock|adminmutes");
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
        LeaveSquad(player.PlayerUID);
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

    private void OnSquadBind(IServerPlayer fromPlayer, SquadBindPacket packet)
    {
        if (!config.EnableSquadChannels)
        {
            SendPlayerMessage(fromPlayer, "简单语音对话：服务器未启用小队频道。");
            return;
        }

        if (packet.RequestStatus)
        {
            SendSquadStatus(fromPlayer);
            return;
        }

        if (packet.LeaveSquad)
        {
            LeaveSquad(fromPlayer.PlayerUID);
            SendPlayerMessage(fromPlayer, "简单语音对话：你已离开小队频道。");
            SendSquadHud(fromPlayer);
            return;
        }

        IServerPlayer? target = FindOnlinePlayer(packet.TargetPlayerUid);
        if (target == null || target == fromPlayer || target.Entity == null || fromPlayer.Entity == null)
        {
            SendPlayerMessage(fromPlayer, "简单语音对话：没有找到可绑定的目标玩家。");
            return;
        }

        double distance = fromPlayer.Entity.Pos.XYZ.DistanceTo(target.Entity.Pos.XYZ);
        if (distance > config.SquadBindRange)
        {
            SendPlayerMessage(fromPlayer, $"简单语音对话：目标太远，需要 {config.SquadBindRange:0.#} 格内面对绑定。");
            return;
        }

        BindSquads(fromPlayer.PlayerUID, target.PlayerUID);
        SendPlayerMessage(fromPlayer, $"简单语音对话：已与 {target.PlayerName} 绑定小队频道。");
        SendPlayerMessage(target, $"简单语音对话：{fromPlayer.PlayerName} 已与你绑定小队频道。");
        SendSquadHud(fromPlayer);
        SendSquadHud(target);
    }

    private void OnSlowTick(float dt)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (now - lastSquadHudBroadcastMs < 500)
        {
            return;
        }

        lastSquadHudBroadcastMs = now;
        foreach (IServerPlayer player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (squadMembersByUid.ContainsKey(player.PlayerUID))
            {
                SendSquadHud(player);
            }
        }
    }

    private void OnAdminVoiceControl(IServerPlayer fromPlayer, AdminVoiceControlPacket packet)
    {
        if (!fromPlayer.HasPrivilege(Privilege.controlserver))
        {
            SendPlayerMessage(fromPlayer, "简单语音对话：你没有语音管理权限。");
            return;
        }

        string action = packet.Action.ToLowerInvariant();
        if (action == "adminmutes")
        {
            SendPlayerMessage(fromPlayer, BuildAdminMuteList());
            return;
        }

        TextCommandResult result = HandleAdminVoiceControl(action, packet.TargetNameOrUid);
        SendPlayerMessage(fromPlayer, result.StatusMessage);
    }

    private void OnVoiceFrame(IServerPlayer fromPlayer, VoiceFramePacket packet)
    {
        if (!config.Enabled || fromPlayer.Entity == null || packet.Payload.Length == 0)
        {
            return;
        }

        if (IsAdminSuppressedSpeaker(fromPlayer.PlayerUID))
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

        List<IServerPlayer> distanceRecipients = new();
        List<IServerPlayer> squadRecipients = new();
        foreach (IServerPlayer player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (player == fromPlayer || player.Entity == null)
            {
                continue;
            }

            if (statesByUid.TryGetValue(player.PlayerUID, out ClientVoiceStatePacket? recipientState)
                && recipientState.GlobalMuted)
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
                distanceRecipients.Add(player);
            }
            else if (AreSquadmates(fromPlayer.PlayerUID, player.PlayerUID))
            {
                squadRecipients.Add(player);
            }
        }

        if (distanceRecipients.Count > 0)
        {
            packet.SquadRelay = false;
            voiceChannel?.SendPacket(packet, distanceRecipients.ToArray());
        }

        if (squadRecipients.Count > 0)
        {
            VoiceFramePacket squadPacket = CopyVoicePacket(packet);
            squadPacket.SquadRelay = true;
            voiceChannel?.SendPacket(squadPacket, squadRecipients.ToArray());
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

    private bool IsAdminSuppressedSpeaker(string playerUid)
    {
        return config.GloballyMutedPlayerUids.Contains(playerUid)
            || config.ForceBlockedPlayerUids.Contains(playerUid);
    }

    private bool AreSquadmates(string firstUid, string secondUid)
    {
        return squadMembersByUid.TryGetValue(firstUid, out HashSet<string>? members) && members.Contains(secondUid);
    }

    private void BindSquads(string firstUid, string secondUid)
    {
        HashSet<string> combined = new(StringComparer.Ordinal) { firstUid, secondUid };
        if (squadMembersByUid.TryGetValue(firstUid, out HashSet<string>? firstMembers))
        {
            combined.UnionWith(firstMembers);
        }
        if (squadMembersByUid.TryGetValue(secondUid, out HashSet<string>? secondMembers))
        {
            combined.UnionWith(secondMembers);
        }

        foreach (string uid in combined)
        {
            squadMembersByUid[uid] = new HashSet<string>(combined.Where(member => member != uid), StringComparer.Ordinal);
        }
    }

    private void LeaveSquad(string playerUid)
    {
        if (!squadMembersByUid.TryGetValue(playerUid, out HashSet<string>? members))
        {
            return;
        }

        squadMembersByUid.Remove(playerUid);
        foreach (string memberUid in members.ToArray())
        {
            if (squadMembersByUid.TryGetValue(memberUid, out HashSet<string>? memberSet))
            {
                memberSet.Remove(playerUid);
                if (memberSet.Count == 0)
                {
                    squadMembersByUid.Remove(memberUid);
                }
            }

            IServerPlayer? member = FindOnlinePlayer(memberUid);
            if (member != null)
            {
                SendSquadHud(member);
            }
        }
    }

    private void SendSquadHud(IServerPlayer player)
    {
        if (!squadMembersByUid.TryGetValue(player.PlayerUID, out HashSet<string>? members) || members.Count == 0)
        {
            controlChannel?.SendPacket(new SquadHudPacket(), player);
            return;
        }

        string[] uids = members.ToArray();
        string[] names = new string[uids.Length];
        bool[] speaking = new bool[uids.Length];

        for (int i = 0; i < uids.Length; i++)
        {
            IServerPlayer? member = FindOnlinePlayer(uids[i]);
            names[i] = member?.PlayerName ?? uids[i];
            speaking[i] = statesByUid.TryGetValue(uids[i], out ClientVoiceStatePacket? state)
                && state.IsSpeaking
                && !state.LocalMuted
                && !state.GlobalMuted
                && !IsAdminSuppressedSpeaker(uids[i]);
        }

        controlChannel?.SendPacket(new SquadHudPacket
        {
            MemberUids = uids,
            MemberNames = names,
            Speaking = speaking
        }, player);
    }

    private void SendSquadStatus(IServerPlayer player)
    {
        if (!squadMembersByUid.TryGetValue(player.PlayerUID, out HashSet<string>? members) || members.Count == 0)
        {
            SendPlayerMessage(player, "简单语音对话：你当前没有绑定小队频道。");
            return;
        }

        string names = string.Join("、", members.Select(uid => FindOnlinePlayer(uid)?.PlayerName ?? uid));
        SendPlayerMessage(player, $"简单语音对话：当前小队成员：{names}");
    }

    private IServerPlayer? FindOnlinePlayer(string nameOrUid)
    {
        if (string.IsNullOrWhiteSpace(nameOrUid))
        {
            return null;
        }

        return sapi.World.AllOnlinePlayers
            .OfType<IServerPlayer>()
            .FirstOrDefault(player =>
                player.PlayerUID.Equals(nameOrUid, StringComparison.Ordinal)
                || player.PlayerName.Equals(nameOrUid, StringComparison.OrdinalIgnoreCase));
    }

    private TextCommandResult HandleAdminVoiceControl(string action, string targetNameOrUid)
    {
        IServerPlayer? target = FindOnlinePlayer(targetNameOrUid);
        string uid = target?.PlayerUID ?? targetNameOrUid;
        string display = target?.PlayerName ?? targetNameOrUid;
        if (string.IsNullOrWhiteSpace(uid))
        {
            return TextCommandResult.Error("Usage: /svc adminmute|adminunmute|forceblock|unforceblock <player>");
        }

        switch (action)
        {
            case "adminmute":
                SetListValue(config.GloballyMutedPlayerUids, uid, true);
                SaveConfig();
                return TextCommandResult.Success($"SimpleVoiceChat: {display} 已被管理员全局禁言。");

            case "adminunmute":
                SetListValue(config.GloballyMutedPlayerUids, uid, false);
                SaveConfig();
                return TextCommandResult.Success($"SimpleVoiceChat: {display} 已取消管理员全局禁言。");

            case "forceblock":
                SetListValue(config.ForceBlockedPlayerUids, uid, true);
                SaveConfig();
                return TextCommandResult.Success($"SimpleVoiceChat: {display} 已被管理员强制屏蔽，全服不会听到该玩家。");

            case "unforceblock":
                SetListValue(config.ForceBlockedPlayerUids, uid, false);
                SaveConfig();
                return TextCommandResult.Success($"SimpleVoiceChat: {display} 已取消管理员强制屏蔽。");

            default:
                return TextCommandResult.Error("Usage: /svc adminmute|adminunmute|forceblock|unforceblock <player>");
        }
    }

    private string BuildAdminMuteList()
    {
        string muted = config.GloballyMutedPlayerUids.Count == 0
            ? "无"
            : string.Join(", ", config.GloballyMutedPlayerUids.Select(uid => FindOnlinePlayer(uid)?.PlayerName ?? uid));
        string blocked = config.ForceBlockedPlayerUids.Count == 0
            ? "无"
            : string.Join(", ", config.ForceBlockedPlayerUids.Select(uid => FindOnlinePlayer(uid)?.PlayerName ?? uid));
        return $"SimpleVoiceChat admin muted: {muted}; force blocked: {blocked}";
    }

    private static void SetListValue(List<string> values, string value, bool enabled)
    {
        if (enabled)
        {
            if (!values.Contains(value))
            {
                values.Add(value);
            }
        }
        else
        {
            values.Remove(value);
        }
    }

    private void SendPlayerMessage(IServerPlayer player, string message)
    {
        player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification, null);
    }

    private static VoiceFramePacket CopyVoicePacket(VoiceFramePacket packet)
    {
        return new VoiceFramePacket
        {
            SenderUidHash = packet.SenderUidHash,
            SenderEntityId = packet.SenderEntityId,
            SessionId = packet.SessionId,
            Sequence = packet.Sequence,
            Mode = packet.Mode,
            Rms = packet.Rms,
            Flags = packet.Flags,
            Payload = packet.Payload,
            X = packet.X,
            Y = packet.Y,
            Z = packet.Z,
            SquadRelay = packet.SquadRelay
        };
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
