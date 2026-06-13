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
            .WithDescription(SVCLang.Get("command-description-server"))
            .RequiresPrivilege(Privilege.chat)
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
                    SVCLang.Get("server-status", config.Enabled, config.WhisperRange.ToString("0.#"), config.TalkRange.ToString("0.#"), config.ShoutRange.ToString("0.#"), config.MaxRange.ToString("0.#"), squadMembersByUid.Count, config.GloballyMutedPlayerUids.Count, config.ForceBlockedPlayerUids.Count));

            case "bind":
                return HandleSquadBindCommand(args);

            case "unbind":
                return HandleSquadLeaveCommand(args);

            case "squad":
                return HandleSquadStatusCommand(args);

            case "reload":
                if (!HasServerControl(args))
                {
                    return NoServerControl();
                }
                config = LoadConfig(sapi);
                BroadcastConfig();
                return TextCommandResult.Success(SVCLang.Get("server-config-reloaded"));

            case "enable":
                if (!HasServerControl(args))
                {
                    return NoServerControl();
                }
                config.Enabled = true;
                SaveConfig();
                BroadcastConfig();
                return TextCommandResult.Success(SVCLang.Get("server-enabled"));

            case "disable":
                if (!HasServerControl(args))
                {
                    return NoServerControl();
                }
                config.Enabled = false;
                SaveConfig();
                BroadcastConfig();
                return TextCommandResult.Success(SVCLang.Get("server-disabled"));

            case "setrange":
            {
                if (!HasServerControl(args))
                {
                    return NoServerControl();
                }
                string mode = args.RawArgs.PopWord("").ToLowerInvariant();
                float range = args.RawArgs.PopFloat(-1f) ?? -1f;
                if (range <= 0)
                {
                    return TextCommandResult.Error(SVCLang.Get("server-setrange-usage"));
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
                        return TextCommandResult.Error(SVCLang.Get("server-setrange-usage"));
                }

                config.Normalize();
                SaveConfig();
                BroadcastConfig();
                return TextCommandResult.Success(SVCLang.Get("server-setrange-ok", mode, range.ToString("0.#")));
            }

            case "adminmute":
            case "adminunmute":
            case "forceblock":
            case "unforceblock":
            {
                if (!HasServerControl(args))
                {
                    return NoServerControl();
                }
                string target = args.RawArgs.PopWord("");
                if (string.IsNullOrWhiteSpace(target))
                {
                    return TextCommandResult.Error(SVCLang.Get("server-admin-usage", sub));
                }

                return HandleAdminVoiceControl(sub, target);
            }

            case "adminmutes":
                if (!HasServerControl(args))
                {
                    return NoServerControl();
                }
                return TextCommandResult.Success(BuildAdminMuteList());

            default:
                return TextCommandResult.Error(SVCLang.Get("server-command-usage-root"));
        }
    }

    private TextCommandResult HandleSquadBindCommand(TextCommandCallingArgs args)
    {
        if (!config.EnableSquadChannels)
        {
            return TextCommandResult.Error(SVCLang.Get("server-squad-disabled"));
        }

        if (GetCommandPlayer(args) is not { Entity: not null } player)
        {
            return TextCommandResult.Error(SVCLang.Get("server-player-only"));
        }

        string targetNameOrUid = args.RawArgs.PopWord("");
        IServerPlayer? target = !string.IsNullOrWhiteSpace(targetNameOrUid)
            ? FindOnlinePlayer(targetNameOrUid)
            : FindSelectedSquadTarget(player) ?? FindOnlyNearbySquadTarget(player);

        if (target == null)
        {
            return TextCommandResult.Error(SVCLang.Get("server-bind-instruction", config.SquadBindRange.ToString("0.#")));
        }

        return BindSquadPlayers(player, target);
    }

    private TextCommandResult HandleSquadLeaveCommand(TextCommandCallingArgs args)
    {
        if (GetCommandPlayer(args) is not { Entity: not null } player)
        {
            return TextCommandResult.Error(SVCLang.Get("server-player-only"));
        }

        LeaveSquad(player.PlayerUID);
        SendSquadHud(player);
        return TextCommandResult.Success(SVCLang.Get("server-left-squad"));
    }

    private TextCommandResult HandleSquadStatusCommand(TextCommandCallingArgs args)
    {
        if (GetCommandPlayer(args) is not { Entity: not null } player)
        {
            return TextCommandResult.Error(SVCLang.Get("server-player-only"));
        }

        return TextCommandResult.Success(BuildSquadStatusText(player));
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
            SendPlayerMessage(fromPlayer, SVCLang.Get("server-squad-disabled"));
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
            SendPlayerMessage(fromPlayer, SVCLang.Get("server-left-squad"));
            SendSquadHud(fromPlayer);
            return;
        }

        if (packet.DisbandSquad)
        {
            DisbandSquad(fromPlayer);
            return;
        }

        IServerPlayer? target = FindOnlinePlayer(packet.TargetPlayerUid);
        TextCommandResult result = BindSquadPlayers(fromPlayer, target);
        SendPlayerMessage(fromPlayer, result.StatusMessage);
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
            SendPlayerMessage(fromPlayer, SVCLang.Get("server-no-voice-admin-permission"));
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

            if (AreSquadmates(fromPlayer.PlayerUID, player.PlayerUID))
            {
                squadRecipients.Add(player);
                continue;
            }

            double distance = player.Entity.Pos.XYZ.DistanceTo(speakerPos);
            if (distance <= range + 1.0)
            {
                distanceRecipients.Add(player);
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

    private void DisbandSquad(IServerPlayer initiator)
    {
        if (!squadMembersByUid.TryGetValue(initiator.PlayerUID, out HashSet<string>? members) || members.Count == 0)
        {
            SendPlayerMessage(initiator, SVCLang.Get("server-no-squad-to-disband"));
            SendSquadHud(initiator);
            return;
        }

        HashSet<string> allMembers = new(StringComparer.Ordinal) { initiator.PlayerUID };
        allMembers.UnionWith(members);
        foreach (string uid in allMembers)
        {
            squadMembersByUid.Remove(uid);
        }

        foreach (string uid in allMembers)
        {
            IServerPlayer? player = FindOnlinePlayer(uid);
            if (player == null)
            {
                continue;
            }

            SendSquadHud(player);
            string message = uid == initiator.PlayerUID
                ? SVCLang.Get("server-disbanded-squad-self")
                : SVCLang.Get("server-disbanded-squad-other", initiator.PlayerName);
            SendPlayerMessage(player, message);
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
        SendSquadHud(player);
        SendPlayerMessage(player, BuildSquadStatusText(player));
    }

    private string BuildSquadStatusText(IServerPlayer player)
    {
        if (!squadMembersByUid.TryGetValue(player.PlayerUID, out HashSet<string>? members) || members.Count == 0)
        {
            return SVCLang.Get("server-no-squad-bound");
        }

        string names = string.Join("、", members.Select(uid => FindOnlinePlayer(uid)?.PlayerName ?? uid));
        return SVCLang.Get("server-squad-members", names);
    }

    private TextCommandResult BindSquadPlayers(IServerPlayer fromPlayer, IServerPlayer? target)
    {
        if (target == null || target == fromPlayer || target.Entity == null || fromPlayer.Entity == null)
        {
            return TextCommandResult.Error(SVCLang.Get("server-no-bind-target"));
        }

        double distance = fromPlayer.Entity.Pos.XYZ.DistanceTo(target.Entity.Pos.XYZ);
        if (distance > config.SquadBindRange)
        {
            return TextCommandResult.Error(SVCLang.Get("server-bind-target-too-far", config.SquadBindRange.ToString("0.#")));
        }

        BindSquads(fromPlayer.PlayerUID, target.PlayerUID);
        SendPlayerMessage(target, SVCLang.Get("server-bound-with-you", fromPlayer.PlayerName));
        SendSquadHud(fromPlayer);
        SendSquadHud(target);
        return TextCommandResult.Success(SVCLang.Get("server-bound-squad", target.PlayerName));
    }

    private IServerPlayer? FindSelectedSquadTarget(IServerPlayer player)
    {
        long selectedEntityId = player.CurrentEntitySelection?.Entity?.EntityId ?? 0;
        if (selectedEntityId <= 0)
        {
            return null;
        }

        return sapi.World.AllOnlinePlayers
            .OfType<IServerPlayer>()
            .FirstOrDefault(candidate =>
                candidate != player
                && candidate.Entity != null
                && candidate.Entity.EntityId == selectedEntityId);
    }

    private IServerPlayer? FindOnlyNearbySquadTarget(IServerPlayer player)
    {
        if (player.Entity == null)
        {
            return null;
        }

        IServerPlayer? nearest = null;
        double nearestDistance = double.MaxValue;
        int nearbyCount = 0;
        Vec3d playerPos = player.Entity.Pos.XYZ;

        foreach (IServerPlayer candidate in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (candidate == player || candidate.Entity == null)
            {
                continue;
            }

            double distance = playerPos.DistanceTo(candidate.Entity.Pos.XYZ);
            if (distance > config.SquadBindRange)
            {
                continue;
            }

            nearbyCount++;
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = candidate;
            }
        }

        return nearbyCount == 1 ? nearest : null;
    }

    private static IServerPlayer? GetCommandPlayer(TextCommandCallingArgs args)
    {
        return args.Caller.Player as IServerPlayer;
    }

    private static bool HasServerControl(TextCommandCallingArgs args)
    {
        return args.Caller.HasPrivilege(Privilege.controlserver);
    }

    private static TextCommandResult NoServerControl()
    {
        return TextCommandResult.Error(SVCLang.Get("server-no-server-control"));
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
            return TextCommandResult.Error(SVCLang.Get("server-admin-control-usage"));
        }

        switch (action)
        {
            case "adminmute":
                SetListValue(config.GloballyMutedPlayerUids, uid, true);
                SaveConfig();
                return TextCommandResult.Success(SVCLang.Get("server-adminmuted", display));

            case "adminunmute":
                SetListValue(config.GloballyMutedPlayerUids, uid, false);
                SaveConfig();
                return TextCommandResult.Success(SVCLang.Get("server-adminunmuted", display));

            case "forceblock":
                SetListValue(config.ForceBlockedPlayerUids, uid, true);
                SaveConfig();
                return TextCommandResult.Success(SVCLang.Get("server-forceblocked", display));

            case "unforceblock":
                SetListValue(config.ForceBlockedPlayerUids, uid, false);
                SaveConfig();
                return TextCommandResult.Success(SVCLang.Get("server-unforceblocked", display));

            default:
                return TextCommandResult.Error(SVCLang.Get("server-admin-control-usage"));
        }
    }

    private string BuildAdminMuteList()
    {
        string muted = config.GloballyMutedPlayerUids.Count == 0
            ? SVCLang.Get("server-list-none")
            : string.Join(", ", config.GloballyMutedPlayerUids.Select(uid => FindOnlinePlayer(uid)?.PlayerName ?? uid));
        string blocked = config.ForceBlockedPlayerUids.Count == 0
            ? SVCLang.Get("server-list-none")
            : string.Join(", ", config.ForceBlockedPlayerUids.Select(uid => FindOnlinePlayer(uid)?.PlayerName ?? uid));
        return SVCLang.Get("server-admin-list", muted, blocked);
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
