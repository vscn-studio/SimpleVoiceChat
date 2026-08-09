using SimpleVoiceChat.Config;
using SimpleVoiceChat.Integration;
using SimpleVoiceChat.Networking;
using SimpleVoiceChat.Server;
using System.Diagnostics;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SimpleVoiceChat;

public sealed class ServerVoiceController : IDisposable
{
    private readonly ICoreServerAPI sapi;
    private readonly ControllerLifecycle lifecycle = new();
    private SimpleVoiceChatServerConfig config;
    private IServerNetworkChannel? controlChannel;
    private IServerNetworkChannel? voiceChannel;
    private readonly Dictionary<string, ClientVoiceStatePacket> statesByUid = new();
    private readonly Dictionary<string, HashSet<string>> mutedByListenerUid = new();
    private readonly Dictionary<string, PacketRateWindow> packetRates = new();
    private readonly Dictionary<string, IServerPlayer> onlinePlayersByUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VoiceClientSession> sessionsByUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VoiceTokenBucket> handshakeRatesByUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveTalkerNotification> activeTalkersByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> channelProviderWarningMilliseconds = new(StringComparer.Ordinal);
    private readonly ChannelService channels = new();
    private readonly ListenerStreamArbiter streamArbiter = new();
    private readonly VoiceMetrics metrics = new();
    private readonly VoiceModerationService moderation = new();
    private VoiceAuditLog auditLog;
    private IReadOnlyList<IVoiceChannelProvider> channelProviders;
    private VoiceSpatialIndex spatialIndex;
    private VoiceTokenBucket egressBudget;
    private readonly ListenerEgressBudget listenerEgressBudget;
    private long slowTickListenerId;
    private long spatialTickListenerId;
    private long lastChannelProviderSyncMs;

    public ServerVoiceController(
        ICoreServerAPI sapi,
        SimpleVoiceChatServerConfig config,
        IReadOnlyList<IVoiceChannelProvider>? channelProviders = null)
    {
        this.sapi = sapi;
        this.config = config;
        this.channelProviders = channelProviders?.Take(32).ToArray() ?? Array.Empty<IVoiceChannelProvider>();
        config.Normalize();
        auditLog = LoadAuditLog(sapi, config.AuditRetention);
        spatialIndex = new VoiceSpatialIndex(config.SpatialCellSize);
        egressBudget = CreateEgressBudget(sapi.World.ElapsedMilliseconds);
        listenerEgressBudget = new ListenerEgressBudget(config.MaxListenerEgressKbps);
        RestorePersistentChannels();
    }

    public void SetChannelProviders(IReadOnlyList<IVoiceChannelProvider> providers)
    {
        channelProviders = providers?.Take(32).ToArray() ?? Array.Empty<IVoiceChannelProvider>();
        SynchronizeChannelProviders();
    }

    public void Start()
    {
        if (!lifecycle.TryStart(this))
        {
            return;
        }
        RegisterChannels();
        RegisterCommands();
        sapi.Event.PlayerJoin += OnPlayerJoin;
        sapi.Event.PlayerLeave += OnPlayerLeave;
        slowTickListenerId = sapi.Event.RegisterGameTickListener(OnSlowTick, 250);
        spatialTickListenerId = sapi.Event.RegisterGameTickListener(OnSpatialTick, 100);
        RefreshOnlinePlayerSnapshot();
        SynchronizeChannelProviders();
    }

    private void RegisterChannels()
    {
        controlChannel = sapi.Network.RegisterChannel(VoiceConstants.ControlChannelName)
            .RegisterMessageType<ClientVoiceStatePacket>()
            .RegisterMessageType<ServerVoiceConfigPacket>()
            .RegisterMessageType<MutePlayerPacket>()
            .RegisterMessageType<AdminVoiceControlPacket>()
            .RegisterMessageType<VoiceHelloPacket>()
            .RegisterMessageType<VoiceWelcomePacket>()
            .RegisterMessageType<ChannelCommandPacket>()
            .RegisterMessageType<ChannelSnapshotPacket>()
            .RegisterMessageType<ChannelMemberDeltaPacket>()
            .RegisterMessageType<ChannelMemberPagePacket>()
            .RegisterMessageType<TalkerStateDeltaPacket>()
            .RegisterMessageType<VoiceFeedbackPacket>()
            .RegisterMessageType<VoiceDiagnosticsPacket>()
            .SetMessageHandler<ClientVoiceStatePacket>(OnClientState)
            .SetMessageHandler<MutePlayerPacket>(OnMutePlayer)
            .SetMessageHandler<AdminVoiceControlPacket>(OnAdminVoiceControl)
            .SetMessageHandler<VoiceHelloPacket>(OnVoiceHello)
            .SetMessageHandler<ChannelCommandPacket>(OnChannelCommand);

        voiceChannel = sapi.Network.RegisterUdpChannel(VoiceConstants.VoiceChannelName)
            .RegisterMessageType<VoiceFrameV3Packet>()
            .RegisterMessageType<VoiceRelayFrameV3Packet>()
            .RegisterMessageType<VoicePingPacket>()
            .RegisterMessageType<VoicePongPacket>()
            .SetMessageHandler<VoiceFrameV3Packet>(OnVoiceFrameV3)
            .SetMessageHandler<VoicePingPacket>(OnVoicePing);
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
        if (!lifecycle.IsStarted)
        {
            return TextCommandResult.Error(SVCLang.Get("command-controller-unavailable"));
        }
        string sub = args.RawArgs.PopWord("status").ToLowerInvariant();
        switch (sub)
        {
            case "status":
                return TextCommandResult.Success(
                    SVCLang.Get("server-status", config.Enabled, config.WhisperRange.ToString("0.#"), config.TalkRange.ToString("0.#"), config.ShoutRange.ToString("0.#"), config.MaxRange.ToString("0.#"), channels.ChannelCount, config.GloballyMutedPlayerUids.Count, config.ForceBlockedPlayerUids.Count));

            case "channel":
                return HandleChannelStatusCommand(args);

            case "channelinvite":
                {
                    if (GetCommandPlayer(args) is not { } player)
                    {
                        return TextCommandResult.Error(SVCLang.Get("server-player-only"));
                    }
                    string channelId = args.RawArgs.PopWord("");
                    string target = args.RawArgs.PopWord("");
                    if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(target))
                    {
                        return TextCommandResult.Error(SVCLang.Get("server-channel-invite-usage"));
                    }
                    OnChannelCommand(player, new ChannelCommandPacket
                    {
                        Action = "invite",
                        ChannelId = channelId,
                        TargetPlayerUid = target
                    });
                    return TextCommandResult.Success(SVCLang.Get("server-channel-management-requested"));
                }

            case "channelleave":
                {
                    if (GetCommandPlayer(args) is not { } player)
                    {
                        return TextCommandResult.Error(SVCLang.Get("server-player-only"));
                    }
                    string channelId = args.RawArgs.PopWord("");
                    if (string.IsNullOrWhiteSpace(channelId))
                    {
                        return TextCommandResult.Error(SVCLang.Get("server-channel-leave-usage"));
                    }
                    OnChannelCommand(player, new ChannelCommandPacket { Action = "leave", ChannelId = channelId });
                    return TextCommandResult.Success(SVCLang.Get("server-channel-management-requested"));
                }

            case "reload":
                if (!HasServerControl(args))
                {
                    return NoServerControl();
                }
                if (!TryReloadConfig(out string reloadError))
                {
                    return TextCommandResult.Error(SVCLang.Get("server-config-reload-failed", reloadError));
                }
                RebuildRoutingLimits();
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

                    return HandleAdminVoiceControl(sub, target, GetCommandPlayer(args));
                }

            case "adminmutes":
                if (!HasServerControl(args))
                {
                    return NoServerControl();
                }
                return TextCommandResult.Success(BuildAdminMuteList());

            case "diag":
                {
                    if (!HasServerControl(args))
                    {
                        return NoServerControl();
                    }
                    VoiceDiagnosticsPacket diag = BuildDiagnosticsSnapshot();
                    return TextCommandResult.Success(
                        SVCLang.Get("server-diagnostics", diag.HandshakenClients, diag.ActiveTalkers, diag.Channels, diag.ReceivedPackets, diag.RelayedPackets, diag.RelayedBytes, diag.DroppedRateLimit, diag.DroppedInvalid, diag.DroppedNoSlot, diag.DroppedBudget, diag.P95FanOut.ToString("0.0"), diag.P95RouteMilliseconds.ToString("0.000"), diag.ActiveListenerStreams));
                }

            case "metrics":
                {
                    if (!HasServerControl(args))
                    {
                        return NoServerControl();
                    }
                    string metricsAction = args.RawArgs.PopWord("").ToLowerInvariant();
                    if (metricsAction != "reset")
                    {
                        return TextCommandResult.Error(SVCLang.Get("server-metrics-usage"));
                    }
                    metrics.ResetRolling();
                    return TextCommandResult.Success(SVCLang.Get("server-metrics-reset"));
                }

            case "playerdiag":
                {
                    if (!HasServerControl(args))
                    {
                        return NoServerControl();
                    }
                    string targetText = args.RawArgs.PopWord("");
                    IServerPlayer? target = FindOnlinePlayer(targetText);
                    if (target == null)
                    {
                        return TextCommandResult.Error(SVCLang.Get("command-player-not-found", targetText));
                    }
                    ModerationPlayerSnapshot moderationSnapshot = moderation.Snapshot(target.PlayerUID, sapi.World.ElapsedMilliseconds);
                    bool handshaken = sessionsByUid.TryGetValue(target.PlayerUID, out VoiceClientSession? targetSession);
                    int activeChannels = channels.GetForPlayer(target.PlayerUID).Count();
                    return TextCommandResult.Success(SVCLang.Get(
                        "server-player-diagnostics",
                        target.PlayerName,
                        handshaken,
                        targetSession?.Codec ?? 0,
                        activeChannels,
                        moderationSnapshot.TemporaryMuteRemainingMilliseconds,
                        moderationSnapshot.DeafenRemainingMilliseconds,
                        moderationSnapshot.AutomaticSuspensionRemainingMilliseconds,
                        moderationSnapshot.InvalidPacketStrikes));
                }

            case "audit":
                {
                    if (!HasServerControl(args))
                    {
                        return NoServerControl();
                    }
                    string entries = auditLog.Entries.Count == 0
                        ? SVCLang.Get("server-list-none")
                        : string.Join("; ", auditLog.Entries.TakeLast(10).Select(entry =>
                            $"{entry.TimestampUtc:O} {entry.ActorName} {entry.Action} target={entry.Target} scope={entry.Scope} {entry.Reason}"));
                    return TextCommandResult.Success(entries);
                }

            case "channels":
                {
                    if (!HasServerControl(args))
                    {
                        return NoServerControl();
                    }
                    string channelSummary = channels.ChannelCount == 0
                        ? SVCLang.Get("server-list-none")
                        : string.Join("; ", channels.Channels.Select(channel =>
                            $"{channel.Id} {channel.Name} members={channel.Members.Count} talkers={channel.ActiveTalkerCount}/{channel.MaxActiveTalkers} locked={channel.Locked}"));
                    return TextCommandResult.Success(channelSummary);
                }

            case "channelcreate":
                {
                    if (!HasServerControl(args) || GetCommandPlayer(args) is not { } player)
                    {
                        return NoServerControl();
                    }
                    string name = args.RawArgs.PopAll();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return TextCommandResult.Error(SVCLang.Get("server-channel-create-usage"));
                    }
                    OnChannelCommand(player, new ChannelCommandPacket { Action = "create", Name = name });
                    return TextCommandResult.Success(SVCLang.Get("server-channel-management-requested"));
                }

            case "channeladd":
            case "channelremove":
                {
                    if (!HasServerControl(args) || GetCommandPlayer(args) is not { } player)
                    {
                        return NoServerControl();
                    }
                    string channelId = args.RawArgs.PopWord("");
                    string target = args.RawArgs.PopWord("");
                    if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(target))
                    {
                        return TextCommandResult.Error($"Usage: /svc {sub} <channel-id> <player-or-uid>");
                    }
                    OnChannelCommand(player, new ChannelCommandPacket
                    {
                        Action = sub == "channeladd" ? "add" : "remove",
                        ChannelId = channelId,
                        TargetPlayerUid = target
                    });
                    return TextCommandResult.Success(SVCLang.Get("server-channel-management-requested"));
                }

            case "channelrole":
                {
                    if (!HasServerControl(args) || GetCommandPlayer(args) is not { } player)
                    {
                        return NoServerControl();
                    }
                    string channelId = args.RawArgs.PopWord("");
                    string target = args.RawArgs.PopWord("");
                    string role = args.RawArgs.PopWord("");
                    if (string.IsNullOrWhiteSpace(channelId)
                        || string.IsNullOrWhiteSpace(target)
                        || !Enum.TryParse(role, true, out VoiceChannelRole parsedRole)
                        || parsedRole is VoiceChannelRole.Owner or VoiceChannelRole.Banned)
                    {
                        return TextCommandResult.Error(SVCLang.Get("server-channel-role-usage"));
                    }
                    OnChannelCommand(player, new ChannelCommandPacket
                    {
                        Action = "role",
                        ChannelId = channelId,
                        TargetPlayerUid = target,
                        Name = parsedRole.ToString()
                    });
                    return TextCommandResult.Success(SVCLang.Get("server-channel-management-requested"));
                }

            case "channellock":
            case "channelunlock":
                {
                    if (!HasServerControl(args) || GetCommandPlayer(args) is not { } player)
                    {
                        return NoServerControl();
                    }
                    string channelId = args.RawArgs.PopWord("");
                    if (string.IsNullOrWhiteSpace(channelId))
                    {
                        return TextCommandResult.Error(SVCLang.Get("server-channel-lock-usage"));
                    }
                    OnChannelCommand(player, new ChannelCommandPacket { Action = sub == "channellock" ? "lock" : "unlock", ChannelId = channelId });
                    return TextCommandResult.Success(SVCLang.Get("server-channel-management-requested"));
                }

            case "channelmute":
            case "channelunmute":
            case "channelban":
            case "channelunban":
                {
                    if (!HasServerControl(args) || GetCommandPlayer(args) is not { } player)
                    {
                        return NoServerControl();
                    }
                    string channelId = args.RawArgs.PopWord("");
                    string target = args.RawArgs.PopWord("");
                    if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(target))
                    {
                        return TextCommandResult.Error(SVCLang.Get("server-channel-target-usage", sub));
                    }
                    string action = sub switch
                    {
                        "channelmute" => "mute",
                        "channelunmute" => "unmute",
                        "channelban" => "ban",
                        _ => "unban"
                    };
                    OnChannelCommand(player, new ChannelCommandPacket { Action = action, ChannelId = channelId, TargetPlayerUid = target });
                    return TextCommandResult.Success(SVCLang.Get("server-channel-management-requested"));
                }

            case "tempmute":
            case "deafen":
                {
                    if (!HasServerControl(args) || GetCommandPlayer(args) is not { } actor)
                    {
                        return NoServerControl();
                    }
                    string targetText = args.RawArgs.PopWord("");
                    int seconds = Math.Clamp(args.RawArgs.PopInt(60) ?? 60, 0, 86_400);
                    IServerPlayer? target = FindOnlinePlayer(targetText);
                    if (target == null)
                    {
                        return TextCommandResult.Error(SVCLang.Get("command-player-not-found", targetText));
                    }
                    TimeSpan duration = TimeSpan.FromSeconds(seconds);
                    if (sub == "tempmute")
                    {
                        moderation.SetTemporaryMute(target.PlayerUID, sapi.World.ElapsedMilliseconds, duration);
                        SendTransmitAccessState(target, sapi.World.ElapsedMilliseconds);
                    }
                    else
                    {
                        moderation.SetDeafened(target.PlayerUID, sapi.World.ElapsedMilliseconds, duration);
                    }
                    RecordAudit(actor, sub, target.PlayerUID, "global", $"duration={seconds}s");
                    return TextCommandResult.Success(SVCLang.Get("server-temporary-action-ok", sub, target.PlayerName, seconds));
                }

            default:
                return TextCommandResult.Error(SVCLang.Get("server-command-usage-root"));
        }
    }

    private TextCommandResult HandleChannelStatusCommand(TextCommandCallingArgs args)
    {
        if (GetCommandPlayer(args) is not { Entity: not null } player)
        {
            return TextCommandResult.Error(SVCLang.Get("server-player-only"));
        }

        return TextCommandResult.Success(BuildChannelStatusText(player));
    }

    private string BuildChannelStatusText(IServerPlayer player)
    {
        VoiceChannel[] playerChannels = channels.GetForPlayer(player.PlayerUID).ToArray();
        if (playerChannels.Length == 0)
        {
            PendingChannelInvite? invite = channels.GetPendingInvite(player.PlayerUID, sapi.World.ElapsedMilliseconds);
            return invite is { } pending
                ? SVCLang.Get("channel-status-invite", pending.InviterName)
                : SVCLang.Get("server-no-channel-bound");
        }

        return string.Join("; ", playerChannels.Select(channel =>
            $"{channel.Name}: {string.Join("、", channel.Members.Keys.Select(uid => onlinePlayersByUid.TryGetValue(uid, out IServerPlayer? online) ? online.PlayerName : SVCLang.Get("player-offline")))}"));
    }

    private void OnPlayerJoin(IServerPlayer player)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        onlinePlayersByUid[player.PlayerUID] = player;
        UpdateSpatialEntry(player);
        SendConfig(player);
    }

    private void OnPlayerLeave(IServerPlayer player)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        statesByUid.Remove(player.PlayerUID);
        mutedByListenerUid.Remove(player.PlayerUID);
        packetRates.Remove(player.PlayerUID);
        string[] affectedChannelMembers = channels.RemovePlayerFromTemporaryChannels(player.PlayerUID);
        onlinePlayersByUid.Remove(player.PlayerUID);
        sessionsByUid.Remove(player.PlayerUID);
        listenerEgressBudget.Remove(player.PlayerUID);
        handshakeRatesByUid.Remove(player.PlayerUID);
        spatialIndex.Remove(player.PlayerUID);
        streamArbiter.RemovePlayer(player.PlayerUID);
        channels.RemoveOnlineState(player.PlayerUID);
        RemoveActiveTalkerNotifications(player.PlayerUID);
        SendSnapshots(affectedChannelMembers);
    }

    private void OnClientState(IServerPlayer fromPlayer, ClientVoiceStatePacket packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        long now = sapi.World.ElapsedMilliseconds;
        if (!sessionsByUid.TryGetValue(fromPlayer.PlayerUID, out VoiceClientSession? session)
            || !session.StateRate.TryConsume(1, now))
        {
            return;
        }
        statesByUid[fromPlayer.PlayerUID] = packet;
    }

    private void OnMutePlayer(IServerPlayer fromPlayer, MutePlayerPacket packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        long now = sapi.World.ElapsedMilliseconds;
        if (string.IsNullOrWhiteSpace(packet.PlayerUid)
            || packet.PlayerUid.Length > VoiceProtocol.MaxControlStringLength
            || !sessionsByUid.TryGetValue(fromPlayer.PlayerUID, out VoiceClientSession? session)
            || !session.MuteRate.TryConsume(1, now))
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
            if (muted.Count >= 256 && !muted.Contains(packet.PlayerUid))
            {
                return;
            }
            muted.Add(packet.PlayerUid);
        }
        else
        {
            muted.Remove(packet.PlayerUid);
        }
    }

    private void OnSlowTick(float dt)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        long now = sapi.World.ElapsedMilliseconds;
        channels.Prune(now);
        moderation.Prune(now);
        if (now - lastChannelProviderSyncMs >= 5_000)
        {
            lastChannelProviderSyncMs = now;
            SynchronizeChannelProviders();
        }
        foreach (KeyValuePair<string, ActiveTalkerNotification> pair in activeTalkersByKey.ToArray())
        {
            if (now - pair.Value.LastPacketMilliseconds <= 350)
            {
                continue;
            }

            activeTalkersByKey.Remove(pair.Key);
            if (channels.TryGet(pair.Value.ChannelId, out VoiceChannel channel))
            {
                SendToOnlineChannelMembers(channel, new TalkerStateDeltaPacket
                {
                    ChannelId = channel.Id,
                    SenderUidHash = Audio.VoiceMath.StableUidHash(pair.Value.SenderUid),
                    SenderName = pair.Value.SenderName,
                    Speaking = false
                });
            }
        }

    }

    private void OnAdminVoiceControl(IServerPlayer fromPlayer, AdminVoiceControlPacket packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        string action = (packet.Action ?? string.Empty).Trim().ToLowerInvariant();
        string targetNameOrUid = (packet.TargetNameOrUid ?? string.Empty).Trim();
        long now = sapi.World.ElapsedMilliseconds;
        if (action.Length == 0
            || action.Length > 32
            || targetNameOrUid.Length > VoiceProtocol.MaxControlStringLength
            || !sessionsByUid.TryGetValue(fromPlayer.PlayerUID, out VoiceClientSession? session)
            || !session.ControlRate.TryConsume(1, now))
        {
            return;
        }
        if (!fromPlayer.HasPrivilege(Privilege.controlserver))
        {
            SendPlayerMessage(fromPlayer, SVCLang.Get("server-no-voice-admin-permission"));
            return;
        }

        if (action == "adminmutes")
        {
            SendPlayerMessage(fromPlayer, BuildAdminMuteList());
            return;
        }
        if (action is "tempmute" or "deafen")
        {
            IServerPlayer? target = FindOnlinePlayer(targetNameOrUid);
            if (target == null)
            {
                SendPlayerMessage(fromPlayer, SVCLang.Get("command-player-not-found", targetNameOrUid));
                return;
            }

            int seconds = Math.Clamp(packet.DurationSeconds <= 0 ? 60 : packet.DurationSeconds, 1, 86_400);
            if (action == "tempmute")
            {
                moderation.SetTemporaryMute(target.PlayerUID, sapi.World.ElapsedMilliseconds, TimeSpan.FromSeconds(seconds));
                SendTransmitAccessState(target, sapi.World.ElapsedMilliseconds);
            }
            else
            {
                moderation.SetDeafened(target.PlayerUID, sapi.World.ElapsedMilliseconds, TimeSpan.FromSeconds(seconds));
            }
            RecordAudit(fromPlayer, action, target.PlayerUID, "global", $"duration={seconds}s");
            SendPlayerMessage(fromPlayer, SVCLang.Get("server-temporary-action-ok", action, target.PlayerName, seconds));
            return;
        }

        TextCommandResult result = HandleAdminVoiceControl(action, targetNameOrUid, fromPlayer);
        SendPlayerMessage(fromPlayer, result.StatusMessage);
    }

    private void OnVoiceHello(IServerPlayer player, VoiceHelloPacket packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        long now = sapi.World.ElapsedMilliseconds;
        if (!handshakeRatesByUid.TryGetValue(player.PlayerUID, out VoiceTokenBucket? handshakeRate))
        {
            handshakeRate = new VoiceTokenBucket(1, 3, now);
            handshakeRatesByUid[player.PlayerUID] = handshakeRate;
        }
        if (!handshakeRate.TryConsume(1, now)
            || packet.ModVersion == null
            || packet.ModVersion.Length > VoiceProtocol.MaxControlStringLength
            || packet.SupportedCodecs == null
            || packet.SupportedCodecs.Length > 8)
        {
            return;
        }
        if (!VoiceProtocol.IsCompatible(packet.ProtocolVersion))
        {
            controlChannel?.SendPacket(new VoiceWelcomePacket
            {
                Accepted = false,
                Message = "protocol-required",
                ProtocolVersion = VoiceProtocol.CurrentVersion
            }, player);
            return;
        }

        int selectedCodec = packet.SupportedCodecs?.Contains(VoiceProtocol.CodecOpus) == true
            ? VoiceProtocol.CodecOpus
            : config.AllowAdpcmFallback && packet.SupportedCodecs?.Contains(VoiceProtocol.CodecImaAdpcm) == true
                ? VoiceProtocol.CodecImaAdpcm
                : 0;
        if (selectedCodec == 0)
        {
            controlChannel?.SendPacket(new VoiceWelcomePacket
            {
                Accepted = false,
                Message = "codec-incompatible",
                ProtocolVersion = VoiceProtocol.CurrentVersion
            }, player);
            return;
        }

        VoiceClientSession session = new(
            Random.Shared.Next(1, int.MaxValue),
            selectedCodec,
            config,
            now);
        sessionsByUid[player.PlayerUID] = session;
        onlinePlayersByUid[player.PlayerUID] = player;
        UpdateSpatialEntry(player);

        controlChannel?.SendPacket(new VoiceWelcomePacket
        {
            Accepted = true,
            Message = string.Empty,
            ProtocolVersion = VoiceProtocol.CurrentVersion,
            Codec = selectedCodec,
            SampleRate = VoiceConstants.SampleRate,
            FrameMilliseconds = VoiceConstants.FrameMilliseconds,
            Bitrate = selectedCodec == VoiceProtocol.CodecOpus ? 20_000 : 32_800,
            ConnectionEpoch = session.ConnectionEpoch,
            MaxStreamsPerListener = config.MaxStreamsPerListener,
            AllowContinuousTalk = config.AllowContinuousTalk,
            HasServerControl = player.HasPrivilege(Privilege.controlserver),
            ServerInstanceId = GetServerInstanceId()
        }, player);
        SendChannelSnapshot(player);
        SendTransmitAccessState(player, now);
    }

    private string GetServerInstanceId()
    {
        return config.ServerInstanceId;
    }

    private void OnVoicePing(IServerPlayer player, VoicePingPacket packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        long now = sapi.World.ElapsedMilliseconds;
        if (packet.Nonce <= 0
            || !sessionsByUid.TryGetValue(player.PlayerUID, out VoiceClientSession? session)
            || packet.ConnectionEpoch != session.ConnectionEpoch
            || !session.PingRate.TryConsume(1, now))
        {
            return;
        }

        voiceChannel?.SendPacket(new VoicePongPacket
        {
            ConnectionEpoch = session.ConnectionEpoch,
            Nonce = packet.Nonce
        }, player);
    }

    private void OnChannelCommand(IServerPlayer fromPlayer, ChannelCommandPacket packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        packet.ChannelId ??= string.Empty;
        packet.TargetPlayerUid ??= string.Empty;
        packet.Name ??= string.Empty;
        packet.Password ??= string.Empty;
        string action = (packet.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (action.Length == 0 || action.Length > 32
            || packet.ChannelId.Length > VoiceProtocol.MaxControlStringLength
            || packet.TargetPlayerUid.Length > VoiceProtocol.MaxControlStringLength
            || packet.Name.Length > VoiceProtocol.MaxControlStringLength
            || packet.Password.Length > VoiceProtocol.MaxControlStringLength)
        {
            SendFeedback(fromPlayer, "invalid-channel-command");
            return;
        }

        long now = sapi.World.ElapsedMilliseconds;
        if (!sessionsByUid.TryGetValue(fromPlayer.PlayerUID, out VoiceClientSession? commandSession)
            || !commandSession.ControlRate.TryConsume(1, now))
        {
            SendFeedback(fromPlayer, "control-rate-limited");
            return;
        }
        switch (action)
        {
            case "request":
                SendChannelSnapshot(fromPlayer);
                return;

            case "members":
                SendChannelMemberPage(fromPlayer, ResolveChannelId(fromPlayer.PlayerUID, packet.ChannelId), packet.Page, packet.PageSize);
                return;

            case "invite":
                {
                    if (!config.EnableChannels)
                    {
                        SendFeedback(fromPlayer, "channel-disabled");
                        return;
                    }

                    IServerPlayer? target = FindOnlinePlayer(packet.TargetPlayerUid);
                    if (target == null || target == fromPlayer)
                    {
                        SendFeedback(fromPlayer, "invalid-target");
                        return;
                    }

                    bool administrator = fromPlayer.HasPrivilege(Privilege.controlserver);
                    ChannelInviteResult invite = channels.Invite(
                        ResolveChannelId(fromPlayer.PlayerUID, packet.ChannelId),
                        fromPlayer.PlayerUID,
                        fromPlayer.PlayerName,
                        target.PlayerUID,
                        target.PlayerName,
                        now,
                        config.MaxChannelsPerPlayer,
                        administrator);
                    if (!invite.Succeeded)
                    {
                        SendFeedback(fromPlayer, invite.ErrorCode);
                        return;
                    }

                    SendFeedback(fromPlayer, "invite-sent", target.PlayerName);
                    SendFeedback(target, "invite-received", fromPlayer.PlayerName);
                    SendChannelSnapshot(target);
                    return;
                }

            case "accept":
                {
                    ChannelInviteResult accepted = channels.Accept(
                        fromPlayer.PlayerUID,
                        now,
                        config.MaxChannelsPerPlayer);
                    if (!accepted.Succeeded)
                    {
                        SendFeedback(fromPlayer, accepted.ErrorCode);
                        return;
                    }

                    if (sessionsByUid.TryGetValue(fromPlayer.PlayerUID, out VoiceClientSession? localSession))
                    {
                        localSession.SelectedChannelId = accepted.ChannelId;
                    }
                    if (channels.TryGet(accepted.ChannelId, out VoiceChannel channel))
                    {
                        foreach (string uid in channel.Members.Keys)
                        {
                            if (onlinePlayersByUid.TryGetValue(uid, out IServerPlayer? member))
                            {
                                if (sessionsByUid.TryGetValue(uid, out VoiceClientSession? memberSession)
                                    && (string.IsNullOrEmpty(memberSession.SelectedChannelId)
                                        || !channels.TryGet(memberSession.SelectedChannelId, out _)))
                                {
                                    memberSession.SelectedChannelId = channel.Id;
                                }
                                SendChannelSnapshot(member);
                            }
                        }
                    }
                    SendFeedback(fromPlayer, "invite-accepted");
                    return;
                }

            case "decline":
                if (channels.Decline(fromPlayer.PlayerUID))
                {
                    SendFeedback(fromPlayer, "invite-declined");
                }
                else
                {
                    SendFeedback(fromPlayer, "invite-missing");
                }
                SendChannelSnapshot(fromPlayer);
                return;

            case "leave":
                {
                    string channelId = ResolveChannelId(fromPlayer.PlayerUID, packet.ChannelId);
                    bool persistent = channels.TryGet(channelId, out VoiceChannel leavingChannel) && leavingChannel.Persistent;
                    if (!channels.Leave(fromPlayer.PlayerUID, channelId, out string[] affected))
                    {
                        SendFeedback(fromPlayer, "channel-missing");
                        return;
                    }
                    ClearUnavailableChannelSelections(channelId, affected);
                    foreach (string uid in affected)
                    {
                        if (onlinePlayersByUid.TryGetValue(uid, out IServerPlayer? member))
                        {
                            SendChannelSnapshot(member);
                        }
                    }
                    SendChannelSnapshot(fromPlayer);
                    if (persistent)
                    {
                        SavePersistentChannels();
                    }
                    SendFeedback(fromPlayer, "channel-left");
                    return;
                }

            case "disband":
                {
                    string channelId = ResolveChannelId(fromPlayer.PlayerUID, packet.ChannelId);
                    bool persistent = channels.TryGet(channelId, out VoiceChannel disbandingChannel) && disbandingChannel.Persistent;
                    bool admin = fromPlayer.HasPrivilege(Privilege.controlserver);
                    if (!channels.Disband(fromPlayer.PlayerUID, channelId, admin, out string[] affected))
                    {
                        SendFeedback(fromPlayer, "channel-disband-denied");
                        return;
                    }
                    ClearUnavailableChannelSelections(channelId, affected);
                    foreach (string uid in affected)
                    {
                        if (onlinePlayersByUid.TryGetValue(uid, out IServerPlayer? member))
                        {
                            SendChannelSnapshot(member);
                        }
                    }
                    if (persistent)
                    {
                        SavePersistentChannels();
                    }
                    SendFeedback(fromPlayer, "channel-disbanded");
                    return;
                }

            case "select":
                {
                    if (string.IsNullOrWhiteSpace(packet.ChannelId))
                    {
                        if (sessionsByUid.TryGetValue(fromPlayer.PlayerUID, out VoiceClientSession? emptySession))
                        {
                            emptySession.SelectedChannelId = string.Empty;
                        }
                        SendChannelSnapshot(fromPlayer);
                        return;
                    }
                    if (!channels.TryGet(packet.ChannelId, out VoiceChannel channel)
                        || !channel.Members.ContainsKey(fromPlayer.PlayerUID))
                    {
                        SendFeedback(fromPlayer, "channel-missing");
                        return;
                    }
                    if (sessionsByUid.TryGetValue(fromPlayer.PlayerUID, out VoiceClientSession? session))
                    {
                        session.SelectedChannelId = channel.Id;
                    }
                    SendChannelSnapshot(fromPlayer);
                    return;
                }

            case "diagnostics":
                controlChannel?.SendPacket(BuildDiagnosticsSnapshot(), fromPlayer);
                return;

            case "rename":
                {
                    bool administrator = fromPlayer.HasPrivilege(Privilege.controlserver);
                    if (!channels.TryGet(packet.ChannelId, out VoiceChannel channel)
                        || channel.ExternallyManaged
                        || !channel.Persistent
                        || string.IsNullOrWhiteSpace(packet.Name)
                        || (!administrator && channel.OwnerUid != fromPlayer.PlayerUID))
                    {
                        SendFeedback(fromPlayer, "channel-rename-denied");
                        return;
                    }

                    string previousName = channel.Name;
                    channel.SetName(packet.Name.Trim());
                    if (channel.Name == previousName)
                    {
                        SendFeedback(fromPlayer, "channel-renamed", channel.Name);
                        return;
                    }
                    SavePersistentChannels();
                    foreach (string uid in channel.Members.Keys.Append(fromPlayer.PlayerUID).Distinct(StringComparer.Ordinal))
                    {
                        if (onlinePlayersByUid.TryGetValue(uid, out IServerPlayer? member))
                        {
                            SendChannelSnapshot(member);
                        }
                    }
                    RecordAudit(fromPlayer, "channel-rename", channel.Id, previousName, channel.Name);
                    SendFeedback(fromPlayer, "channel-renamed", channel.Name);
                    return;
                }

            case "create":
                {
                    if (!fromPlayer.HasPrivilege(Privilege.controlserver)
                        || string.IsNullOrWhiteSpace(packet.Name)
                        || !config.EnableChannels
                        || channels.ChannelCount >= config.MaxChannels
                        || !channels.CanJoinChannel(fromPlayer.PlayerUID, string.Empty, config.MaxChannelsPerPlayer))
                    {
                        SendFeedback(fromPlayer, "channel-create-denied");
                        return;
                    }

                    VoiceChannel channel = channels.Create(
                        packet.Name.Trim(),
                        fromPlayer.PlayerUID,
                        maxMembers: config.MaxChannelMembers,
                        maxActiveTalkers: config.MaxChannelTalkers,
                        persistent: true,
                        password: packet.Password.Trim());
                    commandSession.SelectedChannelId = channel.Id;
                    SavePersistentChannels();
                    SendChannelSnapshot(fromPlayer);
                    RecordAudit(fromPlayer, "channel-create", channel.Id, "channel", channel.Name);
                    SendFeedback(fromPlayer, "channel-created", channel.Name, channel.Id);
                    return;
                }

            case "add":
                {
                    if (!fromPlayer.HasPrivilege(Privilege.controlserver)
                        || !channels.TryGet(packet.ChannelId, out VoiceChannel channel))
                    {
                        SendFeedback(fromPlayer, "channel-manage-denied");
                        return;
                    }

                    IServerPlayer? target = FindOnlinePlayer(packet.TargetPlayerUid);
                    string targetUid = target?.PlayerUID ?? packet.TargetPlayerUid;
                    if (string.IsNullOrWhiteSpace(targetUid)
                        || !channels.AddMember(channel.Id, targetUid, VoiceChannelRole.Member, config.MaxChannelsPerPlayer, bypassLock: true))
                    {
                        SendFeedback(fromPlayer, "channel-add-failed");
                        return;
                    }
                    SavePersistentChannels();
                    SendMemberDelta(channel, channel.Revision - 1, new[] { targetUid }, Array.Empty<string>(), channel.Members.Keys.Where(uid => uid != targetUid));
                    if (target != null)
                    {
                        SendChannelSnapshot(target);
                    }
                    RecordAudit(fromPlayer, "channel-add", targetUid, channel.Id);
                    SendFeedback(fromPlayer, "channel-member-added", target?.PlayerName ?? targetUid, channel.Name);
                    return;
                }

            case "remove":
                {
                    bool administrator = fromPlayer.HasPrivilege(Privilege.controlserver);
                    IServerPlayer? target = FindOnlinePlayer(packet.TargetPlayerUid);
                    string targetUid = target?.PlayerUID ?? packet.TargetPlayerUid;
                    if (!channels.RemoveMember(packet.ChannelId, fromPlayer.PlayerUID, targetUid, administrator, out string[] affected))
                    {
                        SendFeedback(fromPlayer, "channel-remove-failed");
                        return;
                    }
                    if (channels.TryGet(packet.ChannelId, out VoiceChannel remainingChannel))
                    {
                        SavePersistentChannels();
                        SendMemberDelta(remainingChannel, remainingChannel.Revision - 1, Array.Empty<string>(), new[] { targetUid }, affected);
                    }
                    ClearUnavailableChannelSelections(packet.ChannelId, affected.Append(targetUid));
                    if (target != null)
                    {
                        SendChannelSnapshot(target);
                    }
                    RecordAudit(fromPlayer, "channel-remove", targetUid, packet.ChannelId);
                    SendFeedback(fromPlayer, "channel-member-removed");
                    return;
                }

            case "role":
                {
                    bool administrator = fromPlayer.HasPrivilege(Privilege.controlserver);
                    IServerPlayer? target = FindOnlinePlayer(packet.TargetPlayerUid);
                    string targetUid = target?.PlayerUID ?? packet.TargetPlayerUid;
                    if (!Enum.TryParse(packet.Name, true, out VoiceChannelRole role)
                        || !channels.SetRole(packet.ChannelId, fromPlayer.PlayerUID, targetUid, role, administrator))
                    {
                        SendFeedback(fromPlayer, "channel-role-failed");
                        return;
                    }
                    if (channels.TryGet(packet.ChannelId, out VoiceChannel changedChannel))
                    {
                        SavePersistentChannels();
                        SendMemberDelta(changedChannel, changedChannel.Revision - 1, new[] { targetUid }, Array.Empty<string>());
                        RecordAudit(fromPlayer, "channel-role", targetUid, changedChannel.Id, role.ToString());
                    }
                    SendFeedback(fromPlayer, "channel-role-updated");
                    return;
                }

            case "lock":
            case "unlock":
                {
                    bool administrator = fromPlayer.HasPrivilege(Privilege.controlserver);
                    if (!channels.SetLocked(packet.ChannelId, fromPlayer.PlayerUID, action == "lock", administrator)
                        || !channels.TryGet(packet.ChannelId, out VoiceChannel changedChannel))
                    {
                        SendFeedback(fromPlayer, "channel-manage-denied");
                        return;
                    }
                    SavePersistentChannels();
                    SendMemberDelta(changedChannel, changedChannel.Revision - 1, Array.Empty<string>(), Array.Empty<string>());
                    RecordAudit(fromPlayer, action, string.Empty, changedChannel.Id);
                    SendFeedback(fromPlayer, action == "lock" ? "channel-locked" : "channel-unlocked");
                    return;
                }

            case "mute":
            case "unmute":
            case "ban":
            case "unban":
                {
                    bool administrator = fromPlayer.HasPrivilege(Privilege.controlserver);
                    IServerPlayer? target = FindOnlinePlayer(packet.TargetPlayerUid);
                    string targetUid = target?.PlayerUID ?? packet.TargetPlayerUid;
                    int baseRevision = channels.TryGet(packet.ChannelId, out VoiceChannel channelBeforeChange)
                        ? channelBeforeChange.Revision
                        : 0;
                    bool changed;
                    string[] affected = Array.Empty<string>();
                    if (action is "mute" or "unmute")
                    {
                        changed = channels.SetMuted(packet.ChannelId, fromPlayer.PlayerUID, targetUid, action == "mute", administrator);
                    }
                    else
                    {
                        changed = channels.SetBanned(packet.ChannelId, fromPlayer.PlayerUID, targetUid, action == "ban", administrator, out affected);
                    }
                    if (!changed || !channels.TryGet(packet.ChannelId, out VoiceChannel changedChannel))
                    {
                        SendFeedback(fromPlayer, "channel-manage-denied");
                        return;
                    }
                    SavePersistentChannels();
                    if (action == "ban")
                    {
                        ClearUnavailableChannelSelections(packet.ChannelId, affected.Append(targetUid));
                        SendMemberDelta(changedChannel, baseRevision, Array.Empty<string>(), new[] { targetUid }, affected);
                        if (target != null)
                        {
                            SendChannelSnapshot(target);
                        }
                    }
                    else
                    {
                        SendMemberDelta(changedChannel, baseRevision, changedChannel.Members.ContainsKey(targetUid) ? new[] { targetUid } : Array.Empty<string>(), Array.Empty<string>());
                    }
                    if (target != null && action is "mute" or "unmute")
                    {
                        SendFeedback(
                            target,
                            action == "mute" ? "channel-transmit-blocked" : "channel-transmit-restored",
                            changedChannel.Id);
                    }
                    RecordAudit(fromPlayer, $"channel-{action}", targetUid, changedChannel.Id);
                    SendFeedback(fromPlayer, $"channel-{action}-ok", target?.PlayerName ?? targetUid, changedChannel.Name);
                    return;
                }

            default:
                SendFeedback(fromPlayer, "invalid-channel-command");
                return;
        }
    }

    private void OnVoiceFrameV3(IServerPlayer fromPlayer, VoiceFrameV3Packet packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        long now = sapi.World.ElapsedMilliseconds;
        long routeStarted = Stopwatch.GetTimestamp();
        int candidateCount = 0;
        metrics.Received(now);
        if (!config.Enabled || fromPlayer.Entity == null || packet.Payload == null)
        {
            return;
        }

        if (!sessionsByUid.TryGetValue(fromPlayer.PlayerUID, out VoiceClientSession? session)
            || packet.ConnectionEpoch != session.ConnectionEpoch)
        {
            RecordInvalidFrame(fromPlayer, now);
            return;
        }

        if (!IsValidFrame(packet, session, now))
        {
            RecordInvalidFrame(fromPlayer, now);
            return;
        }

        if (!session.PacketRate.TryConsume(1, now)
            || !session.ByteRate.TryConsume(packet.Payload.Length, now))
        {
            metrics.DropRateLimit(now);
            if (session.ShouldSendFeedback("voice-rate-limited", now))
            {
                SendFeedback(fromPlayer, "voice-rate-limited");
            }
            return;
        }

        if (IsAdminSuppressedSpeaker(fromPlayer.PlayerUID)
            || !moderation.CanTransmit(fromPlayer.PlayerUID, now)
            || (statesByUid.TryGetValue(fromPlayer.PlayerUID, out ClientVoiceStatePacket? state)
                && (state.LocalMuted || state.GlobalMuted)))
        {
            if (session.ShouldSendFeedback("transmit-blocked", now))
            {
                SendTransmitAccessState(fromPlayer, now);
            }
            return;
        }

        UpdateSpatialEntry(fromPlayer);
        VoiceMode mode = NormalizeMode(packet.Mode);
        Vec3d position = fromPlayer.Entity.Pos.XYZ;
        List<VoiceSpatialCandidate> spatialCandidates = session.SpatialCandidates;
        Dictionary<string, RelayRecipient> routeRecipients = session.RouteRecipients;
        routeRecipients.Clear();

        if (packet.Target is VoiceTransmitTarget.Proximity or VoiceTransmitTarget.ProximityAndChannel)
        {
            float range = Math.Min(config.GetRange(mode), config.MaxRange);
            spatialIndex.Query(position.X, position.Y, position.Z, range + 1.0, spatialCandidates);
            candidateCount = spatialCandidates.Count;
            foreach (VoiceSpatialCandidate candidate in spatialCandidates)
            {
                AddRelayRecipient(
                    routeRecipients,
                    fromPlayer,
                    candidate.PlayerUid,
                    VoiceRelayKind.Proximity,
                    priority: 1,
                    candidate.DistanceSquared,
                    now);
            }
        }

        if (config.EnableChannels
            && packet.Target is VoiceTransmitTarget.SelectedChannel or VoiceTransmitTarget.ProximityAndChannel)
        {
            string channelId = packet.ChannelId ?? string.Empty;
            if (!string.IsNullOrEmpty(channelId)
                && channels.TryGet(channelId, out VoiceChannel channel)
                && channel.CanTransmit(fromPlayer.PlayerUID)
                && channel.TryAdmitTalker(fromPlayer.PlayerUID, now))
            {
                const int priority = 2;
                foreach (string memberUid in channel.Members.Keys)
                {
                    AddRelayRecipient(
                        routeRecipients,
                        fromPlayer,
                        memberUid,
                        VoiceRelayKind.Channel,
                        priority,
                        distanceSquared: 0,
                        now,
                        channelId);
                }
                NotifyTalkerStarted(channel, fromPlayer, now);
            }
            else if (!string.IsNullOrEmpty(channelId))
            {
                metrics.DropNoSlot(now);
                string feedbackCode = channels.TryGet(channelId, out VoiceChannel deniedChannel)
                    && deniedChannel.CanTransmit(fromPlayer.PlayerUID)
                        ? "channel-no-slot"
                        : "channel-not-authorized";
                if (session.ShouldSendFeedback(feedbackCode, now))
                {
                    SendFeedback(fromPlayer, feedbackCode);
                }
            }
        }

        SendV2Relays(fromPlayer, packet, mode, position, routeRecipients, now);
        metrics.RecordRoute(Stopwatch.GetElapsedTime(routeStarted).TotalMilliseconds, candidateCount, now);
    }

    private void RecordInvalidFrame(IServerPlayer player, long now)
    {
        metrics.DropInvalid(now);
        if (!moderation.AddInvalidPacketStrike(player.PlayerUID, now))
        {
            return;
        }
        RecordAudit(player, "automatic-protocol-suspension", player.PlayerUID, "global", "invalid packet strike threshold");
        SendFeedback(player, "protocol-suspended");
    }

    private bool IsValidFrame(VoiceFrameV3Packet packet, VoiceClientSession session, long now)
    {
        if (!VoiceProtocolValidation.IsValidFrameShape(
                packet,
                session.Codec,
                session.ConnectionEpoch,
                config.MaxVoicePayloadBytes))
        {
            return false;
        }

        return session.SequenceWindow.TryAccept(packet.SessionId, packet.Sequence, now, session.NewSessionRate);
    }

    private void AddRelayRecipient(
        Dictionary<string, RelayRecipient> recipients,
        IServerPlayer speaker,
        string recipientUid,
        VoiceRelayKind relayKind,
        int priority,
        double distanceSquared,
        long now,
        string channelId = "")
    {
        if (recipientUid == speaker.PlayerUID
            || !onlinePlayersByUid.TryGetValue(recipientUid, out IServerPlayer? recipient)
            || recipient.Entity == null
            || !sessionsByUid.ContainsKey(recipientUid))
        {
            return;
        }

        if (statesByUid.TryGetValue(recipientUid, out ClientVoiceStatePacket? recipientState)
            && recipientState.GlobalMuted)
        {
            return;
        }
        if (!moderation.CanReceive(recipientUid, now))
        {
            return;
        }
        if (mutedByListenerUid.TryGetValue(recipientUid, out HashSet<string>? muted)
            && muted.Contains(speaker.PlayerUID))
        {
            return;
        }

        if (!streamArbiter.TryAdmit(
                recipientUid,
                speaker.PlayerUID,
                priority,
                distanceSquared,
                config.MaxStreamsPerListener,
                now,
                proximity: relayKind == VoiceRelayKind.Proximity,
                config.MaxProximityStreams))
        {
            metrics.DropNoSlot(now);
            return;
        }

        if (recipients.TryGetValue(recipientUid, out RelayRecipient existing)
            && existing.Priority >= priority)
        {
            return;
        }

        recipients[recipientUid] = new RelayRecipient(recipient, relayKind, channelId, priority);
    }

    private void SendV2Relays(
        IServerPlayer speaker,
        VoiceFrameV3Packet frame,
        VoiceMode mode,
        Vec3d position,
        Dictionary<string, RelayRecipient> recipients,
        long now)
    {
        bool budgetDropped = false;
        foreach (IGrouping<RelayGroup, RelayRecipient> group in recipients.Values
                     .GroupBy(recipient => new RelayGroup(recipient.RelayKind, recipient.ChannelId))
                     .OrderByDescending(group => group.Max(recipient => recipient.Priority)))
        {
            IServerPlayer[] targets = group.Select(recipient => recipient.Player).ToArray();
            if (targets.Length == 0)
            {
                continue;
            }

            int estimatedPacketBytes = frame.Payload.Length + 64;
            List<IServerPlayer>? permittedTargets = null;
            for (int i = 0; i < targets.Length; i++)
            {
                string listenerUid = targets[i].PlayerUID;
                if (listenerEgressBudget.HasCapacity(listenerUid, estimatedPacketBytes, now)
                    && egressBudget.Available(now) + 0.0001d >= estimatedPacketBytes
                    && listenerEgressBudget.TryConsume(listenerUid, estimatedPacketBytes, now)
                    && egressBudget.TryConsume(estimatedPacketBytes, now))
                {
                    permittedTargets ??= new List<IServerPlayer>(targets.Length);
                    permittedTargets.Add(targets[i]);
                }
                else
                {
                    metrics.DropBudget(now);
                    budgetDropped = true;
                }
            }
            if (permittedTargets == null || permittedTargets.Count == 0)
            {
                continue;
            }

            VoiceRelayFrameV3Packet relay = new()
            {
                SenderUidHash = Audio.VoiceMath.StableUidHash(speaker.PlayerUID),
                SenderEntityId = speaker.Entity!.EntityId,
                SessionId = frame.SessionId,
                Sequence = frame.Sequence,
                Mode = mode,
                RelayKind = group.Key.RelayKind,
                ChannelId = group.Key.ChannelId,
                Level = frame.Level,
                Flags = frame.Flags,
                Payload = frame.Payload,
                X = (float)position.X,
                Y = (float)position.Y,
                Z = (float)position.Z,
                Codec = sessionsByUid.TryGetValue(speaker.PlayerUID, out VoiceClientSession? codecSession)
                    ? codecSession.Codec
                    : VoiceProtocol.CodecImaAdpcm
            };
            IServerPlayer[] finalTargets = permittedTargets.Count == targets.Length ? targets : permittedTargets.ToArray();
            voiceChannel?.SendPacket(relay, finalTargets);
            metrics.Relayed(finalTargets.Length, estimatedPacketBytes, now);
        }
        if (budgetDropped
            && sessionsByUid.TryGetValue(speaker.PlayerUID, out VoiceClientSession? feedbackSession)
            && feedbackSession.ShouldSendFeedback("server-egress-limited", now))
        {
            SendFeedback(speaker, "server-egress-limited");
        }
    }

    private void NotifyTalkerStarted(VoiceChannel channel, IServerPlayer speaker, long now)
    {
        string key = $"{channel.Id}\u001f{speaker.PlayerUID}";
        bool isNew = !activeTalkersByKey.ContainsKey(key);
        activeTalkersByKey[key] = new ActiveTalkerNotification(channel.Id, speaker.PlayerUID, speaker.PlayerName, now);
        if (!isNew)
        {
            return;
        }

        TalkerStateDeltaPacket packet = new()
        {
            ChannelId = channel.Id,
            SenderUidHash = Audio.VoiceMath.StableUidHash(speaker.PlayerUID),
            SenderName = speaker.PlayerName,
            Speaking = true
        };
        SendToOnlineChannelMembers(channel, packet);
    }

    private void SendChannelSnapshot(IServerPlayer player)
    {
        long now = sapi.World.ElapsedMilliseconds;
        IEnumerable<VoiceChannel> playerChannels = channels.GetForPlayer(player.PlayerUID);
        ChannelInfoPacket[] channelPackets = playerChannels.Select(channel => new ChannelInfoPacket
        {
            ChannelId = channel.Id,
            Name = channel.Name,
            Revision = channel.Revision,
            LocalRole = channel.Members[player.PlayerUID],
            MemberCount = channel.Members.Count,
            Locked = channel.Locked,
            ExternallyManaged = channel.ExternallyManaged,
            Members = channel.Members
                .OrderBy(member => member.Key, StringComparer.Ordinal)
                .Take(config.ChannelMemberPageSize)
                .Select(member => BuildChannelMemberPacket(member.Key, member.Value))
                .ToArray()
        }).ToArray();
        PendingChannelInvite? invite = channels.GetPendingInvite(player.PlayerUID, now);
        string selectedChannelId = sessionsByUid.TryGetValue(player.PlayerUID, out VoiceClientSession? session)
            ? session.SelectedChannelId
            : string.Empty;
        controlChannel?.SendPacket(new ChannelSnapshotPacket
        {
            Channels = channelPackets,
            SelectedChannelId = selectedChannelId,
            PendingInviteChannelIds = invite is { } pending && !string.IsNullOrEmpty(pending.ChannelId)
                ? new[] { pending.ChannelId }
                : Array.Empty<string>(),
            PendingInviteNames = invite is { } pendingInvite
                ? new[] { pendingInvite.InviterName }
                : Array.Empty<string>(),
            HasServerControl = player.HasPrivilege(Privilege.controlserver)
        }, player);
    }

    private void SendChannelMemberPage(IServerPlayer player, string channelId, int requestedPage, int requestedPageSize)
    {
        if (!channels.TryGet(channelId, out VoiceChannel channel)
            || !channel.Members.ContainsKey(player.PlayerUID))
        {
            SendFeedback(player, "channel-missing");
            return;
        }

        int pageSize = requestedPageSize <= 0
            ? config.ChannelMemberPageSize
            : Math.Clamp(requestedPageSize, 8, config.ChannelMemberPageSize);
        int pageCount = Math.Max(1, (channel.Members.Count + pageSize - 1) / pageSize);
        int page = Math.Clamp(requestedPage, 0, pageCount - 1);
        ChannelMemberPacket[] members = channel.Members
            .OrderByDescending(member => member.Value)
            .ThenBy(member => member.Key, StringComparer.Ordinal)
            .Skip(page * pageSize)
            .Take(pageSize)
            .Select(member => BuildChannelMemberPacket(member.Key, member.Value))
            .ToArray();
        controlChannel?.SendPacket(new ChannelMemberPagePacket
        {
            ChannelId = channel.Id,
            Revision = channel.Revision,
            Page = page,
            PageSize = pageSize,
            TotalMembers = channel.Members.Count,
            Members = members
        }, player);
    }

    private ChannelMemberPacket BuildChannelMemberPacket(string uid, VoiceChannelRole role)
    {
        return new ChannelMemberPacket
        {
            PlayerUid = uid,
            PlayerName = onlinePlayersByUid.TryGetValue(uid, out IServerPlayer? online) ? online.PlayerName : string.Empty,
            Role = role
        };
    }

    private void SendMemberDelta(
        VoiceChannel channel,
        int baseRevision,
        IEnumerable<string> upsertedUids,
        IEnumerable<string> removedUids,
        IEnumerable<string>? explicitRecipients = null)
    {
        ChannelMemberDeltaPacket packet = new()
        {
            ChannelId = channel.Id,
            BaseRevision = Math.Max(0, baseRevision),
            Revision = channel.Revision,
            MemberCount = channel.Members.Count,
            Locked = channel.Locked,
            UpsertedMembers = upsertedUids
                .Distinct(StringComparer.Ordinal)
                .Where(channel.Members.ContainsKey)
                .Select(uid => BuildChannelMemberPacket(uid, channel.Members[uid]))
                .ToArray(),
            RemovedPlayerUids = removedUids.Distinct(StringComparer.Ordinal).ToArray()
        };
        IEnumerable<string> recipients = explicitRecipients ?? channel.Members.Keys;
        IServerPlayer[] online = recipients
            .Distinct(StringComparer.Ordinal)
            .Where(onlinePlayersByUid.ContainsKey)
            .Select(uid => onlinePlayersByUid[uid])
            .ToArray();
        if (online.Length > 0)
        {
            controlChannel?.SendPacket(packet, online);
        }
    }

    private void SendSnapshots(IEnumerable<string> playerUids)
    {
        foreach (string uid in playerUids.Distinct(StringComparer.Ordinal))
        {
            if (onlinePlayersByUid.TryGetValue(uid, out IServerPlayer? player))
            {
                SendChannelSnapshot(player);
            }
        }
    }

    private void SendToOnlineChannelMembers<T>(VoiceChannel channel, T packet)
    {
        IServerPlayer[] recipients = channel.Members.Keys
            .Where(onlinePlayersByUid.ContainsKey)
            .Select(uid => onlinePlayersByUid[uid])
            .ToArray();
        if (recipients.Length > 0)
        {
            controlChannel?.SendPacket(packet, recipients);
        }
    }

    private void SendFeedback(IServerPlayer player, string code, params string[] arguments)
    {
        controlChannel?.SendPacket(new VoiceFeedbackPacket
        {
            Code = code,
            Arguments = arguments ?? Array.Empty<string>()
        }, player);
    }

    private VoiceDiagnosticsPacket BuildDiagnosticsSnapshot()
    {
        long now = sapi.World.ElapsedMilliseconds;
        return metrics.Snapshot(
            sessionsByUid.Count,
            activeTalkersByKey.Count,
            channels.ChannelCount,
            streamArbiter.ActiveSlotCount(now),
            channels.PendingInviteCount,
            now);
    }

    private string ResolveChannelId(string playerUid, string requestedChannelId)
    {
        if (!string.IsNullOrWhiteSpace(requestedChannelId))
        {
            return requestedChannelId;
        }
        return sessionsByUid.TryGetValue(playerUid, out VoiceClientSession? session)
            ? session.SelectedChannelId
            : string.Empty;
    }

    private void ClearUnavailableChannelSelections(string channelId, IEnumerable<string> candidateUids)
    {
        bool channelExists = channels.TryGet(channelId, out VoiceChannel channel);
        foreach (string uid in candidateUids.Distinct(StringComparer.Ordinal))
        {
            if (sessionsByUid.TryGetValue(uid, out VoiceClientSession? session)
                && string.Equals(session.SelectedChannelId, channelId, StringComparison.Ordinal)
                && (!channelExists || !channel.Members.ContainsKey(uid)))
            {
                session.SelectedChannelId = string.Empty;
            }
        }
    }

    private void OnSpatialTick(float dt)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        RefreshOnlinePlayerSnapshot();
    }

    private void RefreshOnlinePlayerSnapshot()
    {
        foreach (IServerPlayer player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            onlinePlayersByUid[player.PlayerUID] = player;
            UpdateSpatialEntry(player);
        }
    }

    private void UpdateSpatialEntry(IServerPlayer player)
    {
        if (player.Entity == null)
        {
            return;
        }
        Vec3d pos = player.Entity.Pos.XYZ;
        spatialIndex.Update(player.PlayerUID, pos.X, pos.Y, pos.Z);
    }

    private void RemoveActiveTalkerNotifications(string playerUid)
    {
        foreach (string key in activeTalkersByKey
                     .Where(pair => pair.Value.SenderUid == playerUid)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            activeTalkersByKey.Remove(key);
        }
    }

    private VoiceTokenBucket CreateEgressBudget(long now)
    {
        double bytesPerSecond = config.MaxServerEgressKbps * 1000d / 8d;
        return new VoiceTokenBucket(bytesPerSecond, bytesPerSecond * 1.25d, now);
    }

    private void RebuildRoutingLimits()
    {
        config.Normalize();
        spatialIndex = new VoiceSpatialIndex(config.SpatialCellSize);
        egressBudget = CreateEgressBudget(sapi.World.ElapsedMilliseconds);
        listenerEgressBudget.SetLimit(config.MaxListenerEgressKbps);
        StopAllTalkerNotifications();
        RestorePersistentChannels();
        RefreshOnlinePlayerSnapshot();
        long now = sapi.World.ElapsedMilliseconds;
        foreach (KeyValuePair<string, VoiceClientSession> pair in sessionsByUid)
        {
            pair.Value.ApplyRateLimits(config, now);
            if (!string.IsNullOrEmpty(pair.Value.SelectedChannelId)
                && (!channels.TryGet(pair.Value.SelectedChannelId, out VoiceChannel selected)
                    || !selected.Members.ContainsKey(pair.Key)))
            {
                pair.Value.SelectedChannelId = string.Empty;
            }
        }
        SendSnapshots(onlinePlayersByUid.Keys);
    }

    private void StopAllTalkerNotifications()
    {
        foreach (ActiveTalkerNotification notification in activeTalkersByKey.Values)
        {
            if (channels.TryGet(notification.ChannelId, out VoiceChannel channel))
            {
                SendToOnlineChannelMembers(channel, new TalkerStateDeltaPacket
                {
                    ChannelId = channel.Id,
                    SenderUidHash = Audio.VoiceMath.StableUidHash(notification.SenderUid),
                    SenderName = notification.SenderName,
                    Speaking = false
                });
            }
        }
        activeTalkersByKey.Clear();
    }

    private void RestorePersistentChannels()
    {
        channels.RemovePersistentChannels();
        foreach (PersistentVoiceChannelConfig stored in config.PersistentChannels)
        {
            channels.Restore(
                stored.Id,
                stored.Name,
                stored.OwnerUid,
                stored.MaxMembers,
                stored.MaxActiveTalkers,
                stored.Members,
                stored.Locked,
                stored.MutedPlayerUids,
                stored.BannedPlayerUids,
                config.MaxChannelsPerPlayer,
                stored.Password);
        }
    }

    private void SavePersistentChannels()
    {
        config.PersistentChannels = channels.Channels
            .Where(channel => channel.Persistent)
            .OrderBy(channel => channel.Id, StringComparer.Ordinal)
            .Select(channel => new PersistentVoiceChannelConfig
            {
                Id = channel.Id,
                Name = channel.Name,
                OwnerUid = channel.OwnerUid,
                MaxMembers = channel.MaxMembers,
                MaxActiveTalkers = channel.MaxActiveTalkers,
                Password = channel.Password,
                Members = new Dictionary<string, VoiceChannelRole>(channel.Members, StringComparer.Ordinal),
                Locked = channel.Locked,
                MutedPlayerUids = channel.MutedPlayerUids.OrderBy(uid => uid, StringComparer.Ordinal).ToList(),
                BannedPlayerUids = channel.BannedPlayerUids.OrderBy(uid => uid, StringComparer.Ordinal).ToList()
            })
            .ToList();
        SaveConfig();
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

    private TextCommandResult HandleAdminVoiceControl(string action, string targetNameOrUid, IServerPlayer? actor = null)
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
                if (target != null) SendTransmitAccessState(target, sapi.World.ElapsedMilliseconds);
                if (actor != null) RecordAudit(actor, action, uid);
                return TextCommandResult.Success(SVCLang.Get("server-adminmuted", display));

            case "adminunmute":
                SetListValue(config.GloballyMutedPlayerUids, uid, false);
                SaveConfig();
                if (target != null) SendFeedback(target, "transmit-restored");
                if (actor != null) RecordAudit(actor, action, uid);
                return TextCommandResult.Success(SVCLang.Get("server-adminunmuted", display));

            case "forceblock":
                SetListValue(config.ForceBlockedPlayerUids, uid, true);
                SaveConfig();
                if (target != null) SendTransmitAccessState(target, sapi.World.ElapsedMilliseconds);
                if (actor != null) RecordAudit(actor, action, uid);
                return TextCommandResult.Success(SVCLang.Get("server-forceblocked", display));

            case "unforceblock":
                SetListValue(config.ForceBlockedPlayerUids, uid, false);
                SaveConfig();
                if (target != null) SendFeedback(target, "transmit-restored");
                if (actor != null) RecordAudit(actor, action, uid);
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

    private void SendTransmitAccessState(IServerPlayer player, long now)
    {
        if (IsAdminSuppressedSpeaker(player.PlayerUID))
        {
            SendFeedback(player, "transmit-blocked");
            return;
        }

        ModerationPlayerSnapshot moderationSnapshot = moderation.Snapshot(player.PlayerUID, now);
        long remainingMilliseconds = Math.Max(
            moderationSnapshot.TemporaryMuteRemainingMilliseconds,
            moderationSnapshot.AutomaticSuspensionRemainingMilliseconds);
        if (remainingMilliseconds > 0)
        {
            long remainingSeconds = Math.Max(1, (long)Math.Ceiling(remainingMilliseconds / 1_000d));
            SendFeedback(player, "transmit-blocked", remainingSeconds.ToString());
        }
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

    private void SynchronizeChannelProviders()
    {
        HashSet<string> retainedIds = new(StringComparer.Ordinal);
        foreach (IVoiceChannelProvider provider in channelProviders)
        {
            string providerId;
            try
            {
                providerId = provider.ProviderId;
            }
            catch (Exception ex)
            {
                LogChannelProviderWarning(
                    "provider-id",
                    "SimpleVoiceChat: failed reading a voice channel provider id: {0}",
                    ex.Message);
                continue;
            }
            if (!VoiceChannelProviderId.IsValid(providerId))
            {
                continue;
            }
            IReadOnlyList<VoiceChannelSnapshot>? providerChannels;
            string error;
            bool synchronized;
            try
            {
                synchronized = provider.TryGetChannels(out providerChannels, out error);
            }
            catch (Exception ex)
            {
                providerChannels = null;
                error = ex.Message;
                synchronized = false;
            }
            if (!synchronized || providerChannels == null)
            {
                LogChannelProviderWarning(
                    providerId + ":sync",
                    "SimpleVoiceChat: channel provider {0} failed; retaining its last valid channel snapshot: {1}",
                    providerId,
                    error);
                foreach (VoiceChannel existing in channels.Channels
                             .Where(channel => channel.ExternallyManaged
                                 && channel.Id.StartsWith(providerId + ":", StringComparison.Ordinal)))
                {
                    retainedIds.Add(existing.Id);
                }
                continue;
            }

            VoiceChannelSnapshot[] channelSnapshot;
            try
            {
                channelSnapshot = providerChannels.Take(config.MaxChannels).Where(channel => channel != null).ToArray();
            }
            catch (Exception ex)
            {
                LogChannelProviderWarning(
                    providerId + ":snapshot",
                    "SimpleVoiceChat: channel provider {0} returned an unreadable snapshot; retaining its last valid channels: {1}",
                    providerId,
                    ex.Message);
                foreach (VoiceChannel existing in channels.Channels
                             .Where(channel => channel.ExternallyManaged
                                 && channel.Id.StartsWith(providerId + ":", StringComparison.Ordinal)))
                {
                    retainedIds.Add(existing.Id);
                }
                continue;
            }

            foreach (VoiceChannelSnapshot channelSnapshotItem in channelSnapshot)
            {
                if (string.IsNullOrWhiteSpace(channelSnapshotItem.ChannelId)
                    || !VoiceChannelProviderId.IsValid(channelSnapshotItem.ChannelId)
                    || string.IsNullOrWhiteSpace(channelSnapshotItem.OwnerUid)
                    || channelSnapshotItem.OwnerUid.Length > VoiceProtocol.MaxControlStringLength
                    || channelSnapshotItem.Members == null)
                {
                    continue;
                }
                string channelId = $"{providerId}:{channelSnapshotItem.ChannelId}";
                if (channelId.Length > VoiceProtocol.MaxControlStringLength)
                {
                    continue;
                }
                bool channelExists = channels.TryGet(channelId, out VoiceChannel existingChannel);
                if (!channelExists
                    && (channels.ChannelCount >= config.MaxChannels
                        || !channels.CanJoinChannel(channelSnapshotItem.OwnerUid, channelId, config.MaxChannelsPerPlayer)))
                {
                    LogChannelProviderWarning(
                        providerId + ":capacity",
                        "SimpleVoiceChat: skipped external voice channel {0}; channel capacity was reached.",
                        channelId);
                    continue;
                }
                retainedIds.Add(channelId);
                int previousRevision = channelExists ? existingChannel.Revision : -1;
                string[] previousMembers = channelExists ? existingChannel.Members.Keys.ToArray() : Array.Empty<string>();
                VoiceChannel channel;
                try
                {
            channel = channels.SynchronizeExternal(
                        channelId,
                        channelSnapshotItem.DisplayName,
                        channelSnapshotItem.OwnerUid,
                        Math.Clamp(channelSnapshotItem.MaxMembers, 2, 100),
                        Math.Clamp(channelSnapshotItem.MaxActiveTalkers, 1, 12),
                        channelSnapshotItem.Members,
                        config.MaxChannelsPerPlayer);
                }
                catch (Exception ex)
                {
                    LogChannelProviderWarning(
                        providerId + ":channel",
                        "SimpleVoiceChat: skipped unreadable external voice channel {0}; retaining its previous snapshot: {1}",
                        channelId,
                        ex.Message);
                    continue;
                }
                if (channel.Revision != previousRevision)
                {
                    SendSnapshots(previousMembers.Concat(channel.Members.Keys));
                }
            }
        }

        SendSnapshots(channels.RemoveExternalExcept(retainedIds));
    }

    private void LogChannelProviderWarning(string key, string message, params object[] args)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (channelProviderWarningMilliseconds.TryGetValue(key, out long previous)
            && now - previous < 60_000)
        {
            return;
        }

        channelProviderWarningMilliseconds[key] = now;
        sapi.Logger.Warning(message, args);
    }

    private static VoiceAuditLog LoadAuditLog(ICoreAPI api, int retention)
    {
        try
        {
            VoiceAuditConfig? stored = api.LoadModConfig<VoiceAuditConfig>(VoiceConstants.AuditConfigFileName);
            return new VoiceAuditLog(retention, stored?.Entries);
        }
        catch
        {
            return new VoiceAuditLog(retention);
        }
    }

    private void RecordAudit(
        IServerPlayer actor,
        string action,
        string target = "",
        string scope = "global",
        string reason = "")
    {
        auditLog.SetRetention(config.AuditRetention);
        auditLog.Add(actor.PlayerUID, actor.PlayerName, action, target, scope, reason);
        sapi.StoreModConfig(auditLog.ToConfig(), VoiceConstants.AuditConfigFileName);
    }

    public void Dispose()
    {
        if (!lifecycle.TryDispose())
        {
            return;
        }
        sapi.Event.PlayerJoin -= OnPlayerJoin;
        sapi.Event.PlayerLeave -= OnPlayerLeave;
        if (slowTickListenerId != 0)
        {
            sapi.Event.UnregisterGameTickListener(slowTickListenerId);
            slowTickListenerId = 0;
        }
        if (spatialTickListenerId != 0)
        {
            sapi.Event.UnregisterGameTickListener(spatialTickListenerId);
            spatialTickListenerId = 0;
        }

        onlinePlayersByUid.Clear();
        sessionsByUid.Clear();
        handshakeRatesByUid.Clear();
        activeTalkersByKey.Clear();
        channelProviderWarningMilliseconds.Clear();
        listenerEgressBudget.Clear();
        statesByUid.Clear();
        mutedByListenerUid.Clear();
        packetRates.Clear();
        spatialIndex = new VoiceSpatialIndex(config.SpatialCellSize);
        controlChannel = null;
        voiceChannel = null;
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

    private bool TryReloadConfig(out string error)
    {
        try
        {
            SimpleVoiceChatServerConfig? loaded = sapi.LoadModConfig<SimpleVoiceChatServerConfig>(VoiceConstants.ServerConfigFileName);
            if (loaded == null)
            {
                error = "configuration file was empty";
                return false;
            }
            if (string.IsNullOrWhiteSpace(loaded.ServerInstanceId))
            {
                loaded.ServerInstanceId = config.ServerInstanceId;
            }
            loaded.Normalize();
            config = loaded;
            auditLog.SetRetention(config.AuditRetention);
            sapi.StoreModConfig(config, VoiceConstants.ServerConfigFileName);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
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

    private sealed class VoiceClientSession
    {
        public VoiceClientSession(int connectionEpoch, int codec, SimpleVoiceChatServerConfig config, long nowMilliseconds)
        {
            ConnectionEpoch = connectionEpoch;
            Codec = codec;
            PacketRate = new VoiceTokenBucket(1, 1, nowMilliseconds);
            ByteRate = new VoiceTokenBucket(1, 1, nowMilliseconds);
            ApplyRateLimits(config, nowMilliseconds);
            NewSessionRate = new VoiceTokenBucket(5, 5, nowMilliseconds);
            ControlRate = new VoiceTokenBucket(5, 10, nowMilliseconds);
            StateRate = new VoiceTokenBucket(2, 5, nowMilliseconds);
            MuteRate = new VoiceTokenBucket(20, 256, nowMilliseconds);
            PingRate = new VoiceTokenBucket(1, 3, nowMilliseconds);
        }

        public int ConnectionEpoch { get; }
        public int Codec { get; }
        public VoiceTokenBucket PacketRate { get; private set; }
        public VoiceTokenBucket ByteRate { get; private set; }
        public VoiceTokenBucket NewSessionRate { get; }
        public VoiceTokenBucket ControlRate { get; }
        public VoiceTokenBucket StateRate { get; }
        public VoiceTokenBucket MuteRate { get; }
        public VoiceTokenBucket PingRate { get; }
        public VoiceSequenceWindow SequenceWindow { get; } = new();
        public List<VoiceSpatialCandidate> SpatialCandidates { get; } = new(128);
        public Dictionary<string, RelayRecipient> RouteRecipients { get; } = new(128, StringComparer.Ordinal);
        public string SelectedChannelId { get; set; } = string.Empty;
        private Dictionary<string, long> LastFeedbackMillisecondsByCode { get; } = new(StringComparer.Ordinal);

        public void ApplyRateLimits(SimpleVoiceChatServerConfig config, long nowMilliseconds)
        {
            PacketRate = new VoiceTokenBucket(config.MaxVoicePacketsPerSecond, config.MaxVoicePacketsPerSecond + 10, nowMilliseconds);
            ByteRate = new VoiceTokenBucket(config.MaxVoiceBytesPerSecond, config.MaxVoiceBytesPerSecond * 1.25d, nowMilliseconds);
        }

        public bool ShouldSendFeedback(string code, long nowMilliseconds)
        {
            if (LastFeedbackMillisecondsByCode.TryGetValue(code, out long previous)
                && nowMilliseconds - previous < 5_000)
            {
                return false;
            }
            LastFeedbackMillisecondsByCode[code] = nowMilliseconds;
            return true;
        }
    }

    private sealed class VoiceSequenceWindow
    {
        private int sessionId = int.MinValue;
        private bool initialized;
        private ushort latestSequence;
        private ulong receivedMask;

        public bool TryAccept(int nextSessionId, ushort sequence, long nowMilliseconds, VoiceTokenBucket newSessionRate)
        {
            if (nextSessionId <= 0)
            {
                return false;
            }

            if (nextSessionId != sessionId)
            {
                if (sessionId != int.MinValue && nextSessionId < sessionId)
                {
                    return false;
                }
                if (sessionId != int.MinValue && !newSessionRate.TryConsume(1, nowMilliseconds))
                {
                    return false;
                }

                sessionId = nextSessionId;
                initialized = true;
                latestSequence = sequence;
                receivedMask = 1;
                return true;
            }

            if (!initialized)
            {
                initialized = true;
                latestSequence = sequence;
                receivedMask = 1;
                return true;
            }

            short delta = unchecked((short)(sequence - latestSequence));
            if (delta > 0)
            {
                receivedMask = delta >= 64 ? 1UL : (receivedMask << delta) | 1UL;
                latestSequence = sequence;
                return true;
            }

            int behind = -delta;
            if (behind >= 64)
            {
                return false;
            }

            ulong bit = 1UL << behind;
            if ((receivedMask & bit) != 0)
            {
                return false;
            }
            receivedMask |= bit;
            return true;
        }
    }

    private readonly record struct RelayRecipient(IServerPlayer Player, VoiceRelayKind RelayKind, string ChannelId, int Priority);
    private readonly record struct RelayGroup(VoiceRelayKind RelayKind, string ChannelId);
    private readonly record struct ActiveTalkerNotification(string ChannelId, string SenderUid, string SenderName, long LastPacketMilliseconds);
}
