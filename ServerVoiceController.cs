using SimpleVoiceChat.Config;
using SimpleVoiceChat.Audio;
using SimpleVoiceChat.Integration;
using SimpleVoiceChat.Networking;
using SimpleVoiceChat.Server;
using System.Diagnostics;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SimpleVoiceChat;

public sealed class ServerVoiceController : IDisposable
{
    private readonly ICoreServerAPI sapi;
    private readonly string runtimeInstanceId = Guid.NewGuid().ToString("N");
    private readonly ControllerLifecycle lifecycle = new();
    private SimpleVoiceChatServerConfig config;
    private IServerNetworkChannel? controlChannel;
    private IServerNetworkChannel? voiceChannel;
    private readonly Dictionary<string, ClientVoiceStatePacket> statesByUid = new();
    private readonly Dictionary<string, HashSet<string>> mutedByListenerUid = new();
    private readonly Dictionary<string, PacketRateWindow> packetRates = new();
    private readonly Dictionary<string, IServerPlayer> onlinePlayersByUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VoiceClientSession> sessionsByUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DirectorVoiceListener> directorListenersByUid = new(StringComparer.Ordinal);
    private readonly HashSet<string> recorderListeners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecorderParticipantState> recorderParticipants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecorderFileTransfer> recorderTransfers = new(StringComparer.Ordinal);
    private RecorderRecordingSession? recorderSession;
    private readonly Audio.ServerHostedRecordingService hostedRecorder;
    private readonly Dictionary<string, VoiceTokenBucket> handshakeRatesByUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveTalkerNotification> activeTalkersByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> channelProviderWarningMilliseconds = new(StringComparer.Ordinal);
    private readonly ChannelService channels;
    private readonly ListenerStreamArbiter streamArbiter = new();
    private readonly ListenerStreamArbiter directorStreamArbiter = new();
    private readonly VoiceMetrics metrics = new();
    private readonly VoiceModerationService moderation = new();
    private VoiceAuditLog auditLog;
    private IReadOnlyList<IVoiceChannelProvider> channelProviders;
    private VoiceSpatialIndex spatialIndex;
    private VoiceTokenBucket egressBudget;
    private readonly ListenerEgressBudget listenerEgressBudget;
    private readonly ListenerEgressBudget directorEgressBudget;
    private readonly ListenerEgressBudget recorderEgressBudget;
    private long slowTickListenerId;
    private long spatialTickListenerId;
    private long lastChannelProviderSyncMs;
    private long lastRecorderStatusMilliseconds;

    public ServerVoiceController(
        ICoreServerAPI sapi,
        SimpleVoiceChatServerConfig config,
        IReadOnlyList<IVoiceChannelProvider>? channelProviders = null)
    {
        this.sapi = sapi;
        this.config = config;
        this.channelProviders = channelProviders?.Take(32).ToArray() ?? Array.Empty<IVoiceChannelProvider>();
        config.Normalize();
        channels = new ChannelService(config.NextChannelNumber);
        auditLog = LoadAuditLog(sapi, config.AuditRetention);
        spatialIndex = new VoiceSpatialIndex(config.SpatialCellSize);
        egressBudget = CreateEgressBudget(sapi.World.ElapsedMilliseconds);
        listenerEgressBudget = new ListenerEgressBudget(config.MaxListenerEgressKbps);
        directorEgressBudget = new ListenerEgressBudget(config.MaxDirectorEgressKbps);
        recorderEgressBudget = new ListenerEgressBudget(config.MaxRecorderEgressKbps);
        hostedRecorder = new Audio.ServerHostedRecordingService(
            sapi.GetOrCreateDataPath(Path.Combine("ModData", "SimpleVoiceChat", "Recordings")),
            config.RecorderCheckpointSeconds);
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
        sapi.Event.PlayerChat += OnPlayerChat;
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
            .RegisterMessageType<AdminVoiceConfigPacket>()
            .RegisterMessageType<MutePlayerPacket>()
            .RegisterMessageType<AdminVoiceControlPacket>()
            .RegisterMessageType<VoiceHelloPacket>()
            .RegisterMessageType<VoiceWelcomePacket>()
            .RegisterMessageType<ChannelCommandPacket>()
            .RegisterMessageType<ChannelSnapshotPacket>()
            .RegisterMessageType<ChannelMemberDeltaPacket>()
            .RegisterMessageType<ChannelMemberPagePacket>()
            .RegisterMessageType<TalkerStateDeltaPacket>()
            .RegisterMessageType<DirectorVoiceListenerUpdatePacket>()
            .RegisterMessageType<RecorderVoiceListenerPacket>()
            .RegisterMessageType<RecorderVoiceTimelinePacket>()
            .RegisterMessageType<RecorderParticipantStatePacket>()
            .RegisterMessageType<RecorderCaptureStatePacket>()
            .RegisterMessageType<RecorderUploadFramePacket>()
            .RegisterMessageType<RecorderSessionStatusPacket>()
            .RegisterMessageType<RecorderFileRequestPacket>()
            .RegisterMessageType<RecorderFileChunkPacket>()
            .RegisterMessageType<VoiceFeedbackPacket>()
            .RegisterMessageType<VoiceDiagnosticsPacket>()
            .RegisterMessageType<VoicePingPacket>()
            .RegisterMessageType<VoicePongPacket>()
            .RegisterMessageType<VoiceNetworkQualityPacket>()
            .RegisterMessageType<VoiceBitrateControlPacket>()
            .SetMessageHandler<ClientVoiceStatePacket>(OnClientState)
            .SetMessageHandler<MutePlayerPacket>(OnMutePlayer)
            .SetMessageHandler<AdminVoiceControlPacket>(OnAdminVoiceControl)
            .SetMessageHandler<AdminVoiceConfigPacket>(OnAdminVoiceConfig)
            .SetMessageHandler<VoiceHelloPacket>(OnVoiceHello)
            .SetMessageHandler<DirectorVoiceListenerUpdatePacket>(OnDirectorVoiceListenerUpdate)
            .SetMessageHandler<RecorderVoiceListenerPacket>(OnRecorderVoiceListenerUpdate)
            .SetMessageHandler<RecorderParticipantStatePacket>(OnRecorderParticipantState)
            .SetMessageHandler<RecorderUploadFramePacket>(OnRecorderUploadFrame)
            .SetMessageHandler<RecorderFileRequestPacket>(OnRecorderFileRequest)
            .SetMessageHandler<ChannelCommandPacket>(OnChannelCommand)
            .SetMessageHandler<VoicePingPacket>(OnControlVoicePing)
            .SetMessageHandler<VoiceNetworkQualityPacket>(OnVoiceNetworkQuality);

        voiceChannel = sapi.Network.RegisterUdpChannel(VoiceConstants.VoiceChannelName)
            .RegisterMessageType<VoiceFrameV3Packet>()
            .RegisterMessageType<VoiceRelayFrameV3Packet>()
            .RegisterMessageType<DirectorVoiceRelayFrameV3Packet>()
            .RegisterMessageType<RecorderVoiceRelayFrameV3Packet>()
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

            case "channelallow":
                if (!HasServerControl(args))
                {
                    return NoServerControl();
                }
                string channelAllowValue = args.RawArgs.PopWord("").ToLowerInvariant();
                if (channelAllowValue is not ("on" or "off" or "true" or "false"))
                {
                    return TextCommandResult.Error("Usage: /svc channelallow <on|off>");
                }
                config.AllowPlayerChannelCreation = channelAllowValue is "on" or "true";
                SaveConfig();
                return TextCommandResult.Success(config.AllowPlayerChannelCreation
                    ? "玩家现在可以创建频道。"
                    : "已禁止普通玩家创建频道。");

            case "channelnamelength":
            case "setchannelnamelength":
                if (!HasServerControl(args))
                {
                    return NoServerControl();
                }
                int channelNameLength = args.RawArgs.PopInt(-1) ?? -1;
                if (channelNameLength < 1 || channelNameLength > VoiceProtocol.MaxControlStringLength)
                {
                    return TextCommandResult.Error(SVCLang.Get("server-channel-name-length-usage"));
                }
                config.MaxChannelNameLength = channelNameLength;
                config.Normalize();
                SaveConfig();
                BroadcastConfig();
                return TextCommandResult.Success(SVCLang.Get("server-channel-name-length-ok", config.MaxChannelNameLength));

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
                        SVCLang.Get("server-diagnostics", diag.HandshakenClients, diag.ActiveTalkers, diag.Channels, diag.ReceivedPackets, diag.RelayedPackets, diag.RelayedBytes, diag.EstimatedRelayedIpv4UdpBytes, diag.DroppedRateLimit, diag.DroppedInvalid, diag.DroppedNoSlot, diag.DroppedBudget, diag.P95FanOut.ToString("0.0"), diag.P95RouteMilliseconds.ToString("0.000"), diag.ActiveListenerStreams));
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

            case "recording":
                {
                    if (!HasServerControl(args))
                    {
                        return NoServerControl();
                    }

                    string recordingAction = args.RawArgs.PopWord("status").ToLowerInvariant();
                    if (recordingAction == "status")
                    {
                        return TextCommandResult.Success(BuildRecorderStatus());
                    }

                    if (recordingAction == "list")
                    {
                        string[] sessions = hostedRecorder.ListCompletedSessionIds();
                        return TextCommandResult.Success(sessions.Length == 0
                            ? SVCLang.Get("server-list-none")
                            : string.Join(", ", sessions));
                    }

                    if (recordingAction == "download" && GetCommandPlayer(args) is { } downloader)
                    {
                        string requestedSessionId = args.RawArgs.PopWord("");
                        if (string.IsNullOrWhiteSpace(requestedSessionId))
                        {
                            return TextCommandResult.Error(SVCLang.Get("server-recording-usage"));
                        }
                        return QueueRecorderTransfer(downloader, requestedSessionId)
                            ? TextCommandResult.Success(SVCLang.Get("server-recording-download-started", requestedSessionId))
                            : TextCommandResult.Error(SVCLang.Get("server-recording-download-missing", requestedSessionId));
                    }

                    if (recordingAction is not ("start" or "stop") || GetCommandPlayer(args) is not { } recorder)
                    {
                        return TextCommandResult.Error(SVCLang.Get("server-recording-usage"));
                    }
                    if (recordingAction == "start"
                        && recorderSession == null
                        && !AreRecorderParticipantsReady(out string readinessError))
                    {
                        return TextCommandResult.Error(SVCLang.Get("feedback-recording-not-ready", readinessError));
                    }

                    OnRecorderVoiceListenerUpdate(recorder, new RecorderVoiceListenerPacket
                    {
                        Active = recordingAction == "start",
                        ClientTimestampMilliseconds = MonotonicClock.NowMilliseconds
                    });
                    if (recordingAction == "start" && recorderSession?.OwnerUid != recorder.PlayerUID)
                    {
                        return TextCommandResult.Error(BuildRecorderStatus());
                    }
                    if (recordingAction == "stop" && recorderSession != null)
                    {
                        return TextCommandResult.Error(BuildRecorderStatus());
                    }
                    return TextCommandResult.Success(recordingAction == "start"
                        ? SVCLang.Get("server-recording-started")
                        : SVCLang.Get("server-recording-stopped"));
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
                            $"{channel.Id} {channel.Name} type={channel.Visibility} owner={channel.OwnerUid} members={channel.Members.Count} talkers={channel.ActiveTalkerCount}/{channel.MaxActiveTalkers} locked={channel.Locked}"));
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
        // Refresh existing channel members so a reconnect replaces any stale
        // offline label cached by their clients. The joining player receives
        // its initial snapshot after VoiceHello creates a control session.
        SendSnapshots(channels.GetForPlayer(player.PlayerUID)
            .SelectMany(channel => channel.Members.Keys)
            .Where(uid => !string.Equals(uid, player.PlayerUID, StringComparison.Ordinal)));
    }

    private void OnPlayerLeave(IServerPlayer player)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        // Persistent channel membership survives a disconnect. Snapshot every
        // peer before removing the player from the online index so clients
        // receive the updated online state instead of retaining their name.
        string[] channelObservers = channels.GetForPlayer(player.PlayerUID)
            .SelectMany(channel => channel.Members.Keys)
            .Append(player.PlayerUID)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
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
        directorStreamArbiter.RemovePlayer(player.PlayerUID);
        directorListenersByUid.Remove(player.PlayerUID);
        recorderListeners.Remove(player.PlayerUID);
        recorderParticipants.Remove(player.PlayerUID);
        if (recorderSession != null)
        {
            hostedRecorder.ObserveParticipant(player.PlayerUID, player.PlayerName, false, MonotonicClock.NowMilliseconds, "player-left");
            SendRecorderStatus();
        }
        channels.RemoveOnlineState(player.PlayerUID);
        RemoveActiveTalkerNotifications(player.PlayerUID);
        SendSnapshots(channelObservers.Concat(affectedChannelMembers));
    }

    private void OnPlayerChat(IServerPlayer byPlayer, int channelId, ref string message, ref string data, BoolRef consumed)
    {
        if (!lifecycle.IsStarted
            || !config.EnableProximityChatText
            || channelId != GlobalConstants.GeneralChatGroup
            || byPlayer.Entity == null)
        {
            return;
        }

        Vec3d speakerPosition = byPlayer.Entity.Pos.XYZ;
        int speakerDimension = byPlayer.Entity.Pos.Dimension;
        double rangeSquared = (double)config.ProximityChatRange * config.ProximityChatRange;
        string chatMessage = message ?? string.Empty;
        string chatData = data ?? string.Empty;

        // Replace the default global broadcast with explicit nearby delivery.
        consumed.value = true;
        byPlayer.SendMessage(GlobalConstants.GeneralChatGroup, chatMessage, EnumChatType.OwnMessage, chatData);
        sapi.Logger.Chat($"{GlobalConstants.GeneralChatGroup} | {byPlayer.PlayerName}: {chatMessage.Replace("{", "{{").Replace("}", "}}")}");
        foreach (IServerPlayer recipient in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (recipient.PlayerUID == byPlayer.PlayerUID
                || recipient.Entity == null
                || recipient.Entity.Pos.Dimension != speakerDimension)
            {
                continue;
            }

            Vec3d recipientPosition = recipient.Entity.Pos.XYZ;
            double dx = recipientPosition.X - speakerPosition.X;
            double dy = recipientPosition.Y - speakerPosition.Y;
            double dz = recipientPosition.Z - speakerPosition.Z;
            double distanceSquared = dx * dx + dy * dy + dz * dz;
            if (double.IsFinite(distanceSquared) && distanceSquared <= rangeSquared)
            {
                recipient.SendMessage(GlobalConstants.GeneralChatGroup, chatMessage, EnumChatType.OthersMessage, chatData);
            }
        }
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
        bool visibilityStateChanged = !statesByUid.TryGetValue(fromPlayer.PlayerUID, out ClientVoiceStatePacket? previous)
            || previous.HideSelfFromPlayerLists != packet.HideSelfFromPlayerLists
            || previous.RejectChannelInvites != packet.RejectChannelInvites;
        statesByUid[fromPlayer.PlayerUID] = packet;
        if (visibilityStateChanged)
        {
            SendSnapshots(onlinePlayersByUid.Keys);
        }
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
        long recorderNow = MonotonicClock.NowMilliseconds;
        channels.Prune(now);
        moderation.Prune(now);
        hostedRecorder.Checkpoint(recorderNow);
        if (recorderSession is { } activeRecording)
        {
            if (recorderNow - activeRecording.StartServerTimestampMilliseconds
                >= config.MaxRecorderSessionMinutes * 60_000L)
            {
                StopRecorderSession(null, recorderNow, "maximum-duration");
            }
        }
        SendRecorderStatus();
        PumpRecorderTransfers();
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

    private void OnAdminVoiceConfig(IServerPlayer fromPlayer, AdminVoiceConfigPacket packet)
    {
        if (!lifecycle.IsStarted
            || packet == null
            || !sessionsByUid.TryGetValue(fromPlayer.PlayerUID, out VoiceClientSession? session)
            || !session.ControlRate.TryConsume(1, sapi.World.ElapsedMilliseconds))
        {
            return;
        }
        if (!fromPlayer.HasPrivilege(Privilege.controlserver))
        {
            SendPlayerMessage(fromPlayer, SVCLang.Get("server-no-voice-admin-permission"));
            return;
        }

        if (packet.Reload)
        {
            if (!TryReloadConfig(out string reloadError))
            {
                SendPlayerMessage(fromPlayer, SVCLang.Get("server-config-reload-failed", reloadError));
                return;
            }
            RebuildRoutingLimits();
            BroadcastConfig();
            RecordAudit(fromPlayer, "config-reload", reason: "administrator requested server configuration reload");
            SendPlayerMessage(fromPlayer, SVCLang.Get("server-config-reloaded"));
            return;
        }

        if (!packet.Apply || packet.Config == null)
        {
            return;
        }
        SimpleVoiceChatServerConfig updated = BuildConfigFromPacket(packet.Config);
        updated.Normalize();
        config = updated;
        auditLog.SetRetention(config.AuditRetention);
        SaveConfig();
        RebuildRoutingLimits();
        BroadcastConfig();
        RecordAudit(fromPlayer, "config-apply", reason: "administrator applied server configuration");
        SendPlayerMessage(fromPlayer, SVCLang.Get("server-config-applied"));
    }

    private SimpleVoiceChatServerConfig BuildConfigFromPacket(ServerVoiceConfigPacket packet)
    {
        SimpleVoiceChatServerConfig updated = new()
        {
            ConfigVersion = config.ConfigVersion,
            ServerInstanceId = config.ServerInstanceId,
            NextChannelNumber = config.NextChannelNumber,
            GloballyMutedPlayerUids = config.GloballyMutedPlayerUids,
            ForceBlockedPlayerUids = config.ForceBlockedPlayerUids,
            PersistentChannels = config.PersistentChannels,
            Enabled = packet.Enabled,
            AllowAdpcmFallback = packet.AllowAdpcmFallback,
            DefaultOpusBitrateKbps = packet.DefaultOpusBitrateKbps,
            MaxOpusBitrateKbps = packet.MaxOpusBitrateKbps,
            EnableAdaptiveBitrate = packet.EnableAdaptiveBitrate,
            AllowWhisper = packet.AllowWhisper,
            AllowShout = packet.AllowShout,
            ForceImmersive = packet.ForceImmersive,
            MaxRange = packet.MaxRange,
            WhisperRange = packet.WhisperRange,
            TalkRange = packet.TalkRange,
            ShoutRange = packet.ShoutRange,
            EnableProximityChatText = packet.EnableProximityChatText,
            ProximityChatRange = packet.ProximityChatRange,
            EnableOcclusion = packet.EnableOcclusion,
            EnableWeatherEffects = packet.EnableWeatherEffects,
            EnableEnvironmentalVoiceEffects = packet.EnableEnvironmentalVoiceEffects,
            ApplyUnderwaterEffectsToChannels = packet.ApplyUnderwaterEffectsToChannels,
            EquipmentVoiceEffectRules = config.EquipmentVoiceEffectRules
                .Select(rule => new VoiceEquipmentEffectRule
                {
                    Slot = rule.Slot,
                    ItemCodePattern = rule.ItemCodePattern,
                    Effect = rule.Effect
                })
                .ToList(),
            EnableHudIndicators = packet.EnableHudIndicators,
            MaxVoicePacketsPerSecond = packet.MaxVoicePacketsPerSecond,
            MaxVoiceBytesPerSecond = packet.MaxVoiceBytesPerSecond,
            MaxVoicePayloadBytes = packet.MaxVoicePayloadBytes,
            MaxServerEgressKbps = packet.MaxServerEgressKbps,
            MaxListenerEgressKbps = packet.MaxListenerEgressKbps,
            MaxDirectorEgressKbps = packet.MaxDirectorEgressKbps,
            SpatialCellSize = packet.SpatialCellSize,
            MaxStreamsPerListener = packet.MaxStreamsPerListener,
            MaxProximityStreams = packet.MaxProximityStreams,
            MaxChannelTalkers = packet.MaxChannelTalkers,
            MaxChannelMembers = packet.MaxChannelMembers,
            MaxChannelsPerPlayer = packet.MaxChannelsPerPlayer,
            MaxChannels = packet.MaxChannels,
            MaxChannelNameLength = packet.MaxChannelNameLength,
            ChannelMemberPageSize = packet.ChannelMemberPageSize,
            AuditRetention = packet.AuditRetention,
            AllowContinuousTalk = packet.AllowContinuousTalk,
            EnableChannels = packet.EnableChannels,
            AllowPlayerChannelCreation = packet.AllowPlayerChannelCreation,
            EnableDirectorProximityCapture = packet.EnableDirectorProximityCapture,
            MaxDirectorListeners = packet.MaxDirectorListeners,
            MaxDirectorStreamsPerListener = packet.MaxDirectorStreamsPerListener,
            EnableRecorderCapture = packet.EnableRecorderCapture,
            MaxRecorderListeners = packet.MaxRecorderListeners,
            MaxRecorderEgressKbps = packet.MaxRecorderEgressKbps,
            RecorderCheckpointSeconds = packet.RecorderCheckpointSeconds,
            MaxRecorderSessionMinutes = packet.MaxRecorderSessionMinutes,
            MaxRecorderClockSkewMilliseconds = packet.MaxRecorderClockSkewMilliseconds,
            MaxRecorderDownloadKbps = packet.MaxRecorderDownloadKbps
        };
        return updated;
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

        int preferredMaximum = packet.PreferredOpusBitrateKbps is >= 12 and <= 48
            ? packet.PreferredOpusBitrateKbps
            : config.MaxOpusBitrateKbps;
        VoiceClientSession session = new(
            Random.Shared.Next(1, int.MaxValue),
            selectedCodec,
            config,
            now,
            preferredMaximum,
            (packet.Capabilities & (int)VoiceCapability.ServerGuidedBitrate) != 0);
        sessionsByUid[player.PlayerUID] = session;
        onlinePlayersByUid[player.PlayerUID] = player;
        UpdateSpatialEntry(player);
        if (recorderSession != null)
        {
            hostedRecorder.ObserveParticipant(player.PlayerUID, player.PlayerName, true, MonotonicClock.NowMilliseconds, "handshake");
        }

        controlChannel?.SendPacket(new VoiceWelcomePacket
        {
            Accepted = true,
            Message = string.Empty,
            ProtocolVersion = VoiceProtocol.CurrentVersion,
            Codec = selectedCodec,
            SampleRate = VoiceConstants.SampleRate,
            FrameMilliseconds = VoiceConstants.FrameMilliseconds,
            Bitrate = config.DefaultOpusBitrateKbps * 1_000,
            ConnectionEpoch = session.ConnectionEpoch,
            MaxStreamsPerListener = config.MaxStreamsPerListener,
            AllowContinuousTalk = config.AllowContinuousTalk,
            HasServerControl = player.HasPrivilege(Privilege.controlserver),
            ServerInstanceId = GetServerInstanceId(),
            EnableRecorderCapture = config.EnableRecorderCapture,
            RuntimeInstanceId = runtimeInstanceId
        }, player);
        SendChannelSnapshot(player);
        SendTransmitAccessState(player, now);
        SendRecorderCaptureState(player);
        if (recorderSession is { } activeRecording && activeRecording.OwnerUid == player.PlayerUID)
        {
            controlChannel?.SendPacket(new RecorderVoiceTimelinePacket
            {
                Active = true,
                ServerTimestampMilliseconds = MonotonicClock.NowMilliseconds,
                SessionId = activeRecording.SessionId,
                StartServerTimestampMilliseconds = activeRecording.StartServerTimestampMilliseconds,
                StartUtcUnixMilliseconds = activeRecording.StartUtcUnixMilliseconds
            }, player);
        }
    }

    private string GetServerInstanceId()
    {
        return config.ServerInstanceId;
    }

    private void OnVoicePing(IServerPlayer player, VoicePingPacket packet)
    {
        if (TryCreateVoicePong(player, packet, out VoicePongPacket pong))
        {
            voiceChannel?.SendPacket(pong, player);
        }
    }

    private void OnControlVoicePing(IServerPlayer player, VoicePingPacket packet)
    {
        if (TryCreateVoicePong(player, packet, out VoicePongPacket pong))
        {
            controlChannel?.SendPacket(pong, player);
        }
    }

    private void OnVoiceNetworkQuality(IServerPlayer player, VoiceNetworkQualityPacket packet)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (!lifecycle.IsStarted
            || !sessionsByUid.TryGetValue(player.PlayerUID, out VoiceClientSession? session)
            || !session.QualityRate.TryConsume(1, now)
            || packet.ConnectionEpoch != session.ConnectionEpoch)
        {
            return;
        }

        session.ReportedRoundTripMilliseconds = double.IsFinite(packet.RoundTripMilliseconds)
            ? Math.Clamp(packet.RoundTripMilliseconds, -1d, 60_000d)
            : -1d;
        session.ReportedProbeLossPercent = double.IsFinite(packet.ProbeLossPercent)
            ? Math.Clamp(packet.ProbeLossPercent, 0d, 100d)
            : 0d;
        session.LastQualityMilliseconds = now;
    }

    private bool TryCreateVoicePong(IServerPlayer player, VoicePingPacket packet, out VoicePongPacket pong)
    {
        pong = null!;
        if (!lifecycle.IsStarted)
        {
            return false;
        }
        long now = sapi.World.ElapsedMilliseconds;
        if (packet.Nonce <= 0
            || !sessionsByUid.TryGetValue(player.PlayerUID, out VoiceClientSession? session)
            || packet.ConnectionEpoch != session.ConnectionEpoch
            || !session.PingRate.TryConsume(1, now))
        {
            return false;
        }

        pong = new VoicePongPacket
        {
            ConnectionEpoch = session.ConnectionEpoch,
            Nonce = packet.Nonce,
            ClientSendTimestampMilliseconds = packet.ClientSendTimestampMilliseconds,
            ServerTimestampMilliseconds = MonotonicClock.NowMilliseconds
        };
        return true;
    }

    private void OnDirectorVoiceListenerUpdate(
        IServerPlayer player,
        DirectorVoiceListenerUpdatePacket packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }

        long now = sapi.World.ElapsedMilliseconds;
        if (!sessionsByUid.TryGetValue(player.PlayerUID, out VoiceClientSession? session)
            || !session.DirectorListenerRate.TryConsume(1, now))
        {
            return;
        }

        if (!config.EnableDirectorProximityCapture
            || !player.HasPrivilege(Privilege.controlserver)
            || !packet.Active)
        {
            directorListenersByUid.Remove(player.PlayerUID);
            return;
        }

        if (!double.IsFinite(packet.X)
            || !double.IsFinite(packet.Y)
            || !double.IsFinite(packet.Z)
            || packet.Dimension is < -1024 or > 1024)
        {
            return;
        }

        bool captureRegion = packet.CaptureRegionActive
            && double.IsFinite(packet.CaptureRegionCenterX)
            && double.IsFinite(packet.CaptureRegionCenterZ)
            && packet.CaptureRegionDimension is >= -1024 and <= 1024
            && packet.CaptureRegionRadiusChunks is >= 0 and <= 16;
        directorListenersByUid[player.PlayerUID] = new DirectorVoiceListener(
            new Vec3d(packet.X, packet.Y, packet.Z),
            packet.Dimension,
            now + 750L,
            captureRegion,
            new Vec3d(packet.CaptureRegionCenterX, 0d, packet.CaptureRegionCenterZ),
            packet.CaptureRegionDimension,
            Math.Clamp(packet.CaptureRegionRadiusChunks, 0, 16));
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
        if (!sessionsByUid.TryGetValue(fromPlayer.PlayerUID, out VoiceClientSession? commandSession))
        {
            return;
        }
        if (!commandSession.ControlRate.TryConsume(1, now))
        {
            SendFeedback(fromPlayer, "control-rate-limited");
            return;
        }
        switch (action)
        {
            case "request":
                SendChannelSnapshot(fromPlayer);
                return;

            case "join":
                {
                    if (!config.EnableChannels)
                    {
                        SendFeedback(fromPlayer, "channel-disabled");
                        return;
                    }
                    ChannelInviteResult joined = channels.Join(
                        fromPlayer.PlayerUID,
                        packet.ChannelId,
                        packet.Password,
                        config.MaxChannelsPerPlayer,
                        fromPlayer.HasPrivilege(Privilege.controlserver));
                    if (!joined.Succeeded)
                    {
                        SendFeedback(fromPlayer, joined.ErrorCode);
                        return;
                    }
                    commandSession.SelectedChannelId = joined.ChannelId;
                    if (channels.TryGet(joined.ChannelId, out VoiceChannel joinedChannel))
                    {
                        SendSnapshots(joinedChannel.Members.Keys);
                    }
                    SendFeedback(fromPlayer, "channel-joined");
                    return;
                }

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

                    if (statesByUid.TryGetValue(target.PlayerUID, out ClientVoiceStatePacket? targetState)
                        && targetState.RejectChannelInvites)
                    {
                        SendFeedback(fromPlayer, "invite-declined");
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
                    if (leavingChannel != null
                        && leavingChannel.OwnerUid == fromPlayer.PlayerUID
                        && leavingChannel.Members.Count > 1)
                    {
                        SendFeedback(fromPlayer, "channel-owner-leave-options", channelId);
                        return;
                    }
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
                    SendSnapshots(onlinePlayersByUid.Keys);
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

                    if (packet.Name.Trim().Length > config.MaxChannelNameLength)
                    {
                        SendFeedback(fromPlayer, "channel-name-too-long", config.MaxChannelNameLength.ToString());
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
                    SendSnapshots(onlinePlayersByUid.Keys);
                    RecordAudit(fromPlayer, "channel-rename", channel.Id, previousName, channel.Name);
                    SendFeedback(fromPlayer, "channel-renamed", channel.Name);
                    return;
                }

            case "transfer-owner":
                {
                    string channelId = ResolveChannelId(fromPlayer.PlayerUID, packet.ChannelId);
                    bool administrator = fromPlayer.HasPrivilege(Privilege.controlserver);
                    bool persistent = channels.TryGet(channelId, out VoiceChannel transferChannel) && transferChannel.Persistent;
                    if (!channels.TransferOwnership(fromPlayer.PlayerUID, channelId, packet.TargetPlayerUid, administrator, out string[] affected))
                    {
                        SendFeedback(fromPlayer, "channel-owner-transfer-denied");
                        return;
                    }
                    SendSnapshots(affected);
                    if (persistent) SavePersistentChannels();
                    SendFeedback(fromPlayer, "channel-owner-transferred");
                    return;
                }

            case "delete-owned-channel":
                {
                    string channelId = ResolveChannelId(fromPlayer.PlayerUID, packet.ChannelId);
                    bool persistent = channels.TryGet(channelId, out VoiceChannel deleteChannel) && deleteChannel.Persistent;
                    if (!channels.Disband(fromPlayer.PlayerUID, channelId, administrator: fromPlayer.HasPrivilege(Privilege.controlserver), out string[] affected))
                    {
                        SendFeedback(fromPlayer, "channel-disband-denied");
                        return;
                    }
                    ClearUnavailableChannelSelections(channelId, affected);
                    SendSnapshots(affected.Append(fromPlayer.PlayerUID));
                    if (persistent) SavePersistentChannels();
                    SendFeedback(fromPlayer, "channel-disbanded");
                    return;
                }

            case "create":
                {
                    if ((!config.AllowPlayerChannelCreation && !fromPlayer.HasPrivilege(Privilege.controlserver))
                        || string.IsNullOrWhiteSpace(packet.Name)
                        || !config.EnableChannels
                        || channels.ChannelCount >= config.MaxChannels
                        || !channels.CanJoinChannel(fromPlayer.PlayerUID, string.Empty, config.MaxChannelsPerPlayer))
                    {
                        SendFeedback(fromPlayer, "channel-create-denied");
                        return;
                    }

                    VoiceChannelVisibility visibility = packet.Visibility;
                    if (!Enum.IsDefined(visibility)) visibility = string.IsNullOrWhiteSpace(packet.Password)
                        ? VoiceChannelVisibility.Open : VoiceChannelVisibility.Password;
                    if (visibility == VoiceChannelVisibility.Password && string.IsNullOrWhiteSpace(packet.Password))
                    {
                        SendFeedback(fromPlayer, "channel-password-required");
                        return;
                    }

                    if (packet.Name.Trim().Length > config.MaxChannelNameLength)
                    {
                        SendFeedback(fromPlayer, "channel-name-too-long", config.MaxChannelNameLength.ToString());
                        return;
                    }
                    VoiceChannel channel = channels.Create(
                        packet.Name.Trim(),
                        fromPlayer.PlayerUID,
                        maxMembers: config.MaxChannelMembers,
                        maxActiveTalkers: config.MaxChannelTalkers,
                        persistent: true,
                        password: packet.Password.Trim(),
                        visibility: visibility);
                    commandSession.SelectedChannelId = channel.Id;
                    SavePersistentChannels();
                    SendSnapshots(onlinePlayersByUid.Keys);
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

        long serverTimestamp = MonotonicClock.NowMilliseconds;
        // Capture time is supplied in the server clock domain. Invalid or stale
        // client estimates must never move a multi-track WAV timeline backward.
        if (packet.CaptureServerTimestampMilliseconds > 0
            && (packet.CaptureServerTimestampMilliseconds < serverTimestamp - 10_000L
            || packet.CaptureServerTimestampMilliseconds > serverTimestamp + 2_000L)
        )
        {
            packet.CaptureServerTimestampMilliseconds = serverTimestamp;
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

        SendBitrateControl(fromPlayer, session, routeRecipients, now);
        SendV2Relays(fromPlayer, packet, mode, position, session, routeRecipients, now);
        SendRecorderRelay(fromPlayer, packet, session.Codec, now);
        SendDirectorProximityRelays(fromPlayer, packet, session.Codec, mode, position, now);
        metrics.RecordRoute(Stopwatch.GetElapsedTime(routeStarted).TotalMilliseconds, candidateCount, now);
    }

    private void OnRecorderParticipantState(IServerPlayer player, RecorderParticipantStatePacket packet)
    {
        long now = MonotonicClock.NowMilliseconds;
        if (!lifecycle.IsStarted
            || !sessionsByUid.TryGetValue(player.PlayerUID, out VoiceClientSession? session)
            || !session.RecorderStateRate.TryConsume(1, sapi.World.ElapsedMilliseconds)
            || !VoiceProtocolValidation.IsValidRecorderParticipantState(packet, session.ConnectionEpoch))
        {
            return;
        }

        long utcSkew = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - packet.ClientUtcUnixMilliseconds);
        recorderParticipants[player.PlayerUID] = new RecorderParticipantState(
            packet.ConnectionEpoch,
            packet.ClockReady && packet.ClockSampleCount >= 3 && packet.BestRoundTripMilliseconds <= 1_000d,
            packet.ClockSampleCount,
            packet.BestRoundTripMilliseconds,
            utcSkew,
            now);
    }

    private void OnRecorderUploadFrame(IServerPlayer player, RecorderUploadFramePacket packet)
    {
        long rateNow = sapi.World.ElapsedMilliseconds;
        long serverNow = MonotonicClock.NowMilliseconds;
        if (!lifecycle.IsStarted
            || !config.EnableRecorderCapture
            || recorderSession is not RecorderRecordingSession recordingSession
            || !sessionsByUid.TryGetValue(player.PlayerUID, out VoiceClientSession? voiceSession)
            || !VoiceProtocolValidation.IsValidRecorderUploadShape(packet, voiceSession.Codec, voiceSession.ConnectionEpoch)
            || !string.Equals(packet.RecordingSessionId, recordingSession.SessionId, StringComparison.Ordinal)
            || !voiceSession.RecorderPacketRate.TryConsume(1, rateNow)
            || !voiceSession.RecorderByteRate.TryConsume(packet.Payload.Length, rateNow)
            || !voiceSession.RecorderSequenceWindow.TryAccept(packet.VoiceSessionId, packet.Sequence, rateNow, voiceSession.NewSessionRate)
            || IsAdminSuppressedSpeaker(player.PlayerUID)
            || !moderation.CanTransmit(player.PlayerUID, rateNow)
            || (statesByUid.TryGetValue(player.PlayerUID, out ClientVoiceStatePacket? state)
                && (state.LocalMuted || state.GlobalMuted)))
        {
            return;
        }

        long captureTimestamp = packet.CaptureServerTimestampMilliseconds;
        if (captureTimestamp > 0
            && (captureTimestamp < serverNow - 10_000L || captureTimestamp > serverNow + 2_000L))
        {
            captureTimestamp = 0L;
        }
        hostedRecorder.Append(
            player.PlayerUID,
            player.PlayerName,
            packet.ConnectionEpoch,
            packet.VoiceSessionId,
            packet.Sequence,
            voiceSession.Codec,
            packet.Payload,
            captureTimestamp,
            serverNow);
    }

    private void OnRecorderFileRequest(IServerPlayer player, RecorderFileRequestPacket packet)
    {
        if (!lifecycle.IsStarted
            || !player.HasPrivilege(Privilege.controlserver)
            || !sessionsByUid.TryGetValue(player.PlayerUID, out VoiceClientSession? session)
            || !session.ControlRate.TryConsume(1, sapi.World.ElapsedMilliseconds))
        {
            return;
        }
        QueueRecorderTransfer(player, packet?.RecordingSessionId);
    }

    private bool AreRecorderParticipantsReady(out string error)
    {
        long now = MonotonicClock.NowMilliseconds;
        List<string> unavailable = new();
        foreach ((string uid, VoiceClientSession session) in sessionsByUid)
        {
            if (!onlinePlayersByUid.TryGetValue(uid, out IServerPlayer? player)
                || !recorderParticipants.TryGetValue(uid, out RecorderParticipantState? state)
                || state.ConnectionEpoch != session.ConnectionEpoch
                || now - state.UpdatedServerTimestampMilliseconds > 3_000L
                || !state.ClockReady
                || state.UtcSkewMilliseconds > config.MaxRecorderClockSkewMilliseconds)
            {
                unavailable.Add(player?.PlayerName ?? uid);
            }
        }

        error = unavailable.Count == 0 ? string.Empty : string.Join(", ", unavailable.Take(8));
        return unavailable.Count == 0 && sessionsByUid.Count > 0;
    }

    private void SendRecorderCaptureState(IServerPlayer player)
    {
        RecorderRecordingSession? active = recorderSession;
        controlChannel?.SendPacket(new RecorderCaptureStatePacket
        {
            Active = active != null,
            RecordingSessionId = active?.SessionId ?? string.Empty,
            StartServerTimestampMilliseconds = active?.StartServerTimestampMilliseconds ?? 0L,
            StartUtcUnixMilliseconds = active?.StartUtcUnixMilliseconds ?? 0L,
            OwnerUid = active?.OwnerUid ?? string.Empty,
            OwnerName = active?.OwnerName ?? string.Empty
        }, player);
    }

    private void BroadcastRecorderCaptureState()
    {
        foreach ((string uid, VoiceClientSession _) in sessionsByUid)
        {
            if (onlinePlayersByUid.TryGetValue(uid, out IServerPlayer? player))
            {
                SendRecorderCaptureState(player);
            }
        }
    }

    private void SendRecorderStatus(IServerPlayer? target = null)
    {
        long now = MonotonicClock.NowMilliseconds;
        if (target == null && now - lastRecorderStatusMilliseconds < 1_000L)
        {
            return;
        }
        lastRecorderStatusMilliseconds = now;
        HostedRecordingSnapshot snapshot = hostedRecorder.Snapshot;
        int total = sessionsByUid.Count;
        int ready = sessionsByUid.Count(pair =>
            recorderParticipants.TryGetValue(pair.Key, out RecorderParticipantState? state)
            && state.ConnectionEpoch == pair.Value.ConnectionEpoch
            && state.ClockReady
            && state.UtcSkewMilliseconds <= config.MaxRecorderClockSkewMilliseconds
            && now - state.UpdatedServerTimestampMilliseconds <= 3_000L);
        RecorderSessionStatusPacket packet = new()
        {
            Active = recorderSession != null,
            RecordingSessionId = recorderSession?.SessionId ?? string.Empty,
            ReadyParticipants = ready,
            TotalParticipants = total,
            TrackCount = snapshot.TrackCount,
            PacketCount = snapshot.PacketCount,
            MissingPackets = snapshot.MissingPackets,
            FallbackTimestampFrames = snapshot.FallbackTimestampFrames,
            StoredPcmBytes = snapshot.StoredPcmBytes,
            OwnerConnected = recorderSession != null && onlinePlayersByUid.ContainsKey(recorderSession.Value.OwnerUid),
            HostedState = recorderSession != null ? "active" : "idle"
        };
        if (target != null)
        {
            controlChannel?.SendPacket(packet, target);
            return;
        }
        foreach (IServerPlayer player in onlinePlayersByUid.Values.Where(value => value.HasPrivilege(Privilege.controlserver)))
        {
            controlChannel?.SendPacket(packet, player);
        }
    }

    private void OnRecorderVoiceListenerUpdate(IServerPlayer player, RecorderVoiceListenerPacket packet)
    {
        if (!lifecycle.IsStarted || !config.EnableRecorderCapture || !player.HasPrivilege(Privilege.controlserver) || packet == null)
        {
            return;
        }

        long now = MonotonicClock.NowMilliseconds;

        if (packet.Active)
        {
            if (recorderSession is { } existing && existing.OwnerUid != player.PlayerUID)
            {
                SendFeedback(player, "recording-already-active", existing.OwnerName);
                return;
            }

            if (recorderSession == null)
            {
                if (!AreRecorderParticipantsReady(out string readinessError))
                {
                    SendFeedback(player, "recording-not-ready", readinessError);
                    SendRecorderStatus(player);
                    return;
                }

                long start = now + 1_500L;
                RecorderRecordingSession newSession = new(
                    player.PlayerUID,
                    player.PlayerName,
                    $"multitrack-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}",
                    start,
                    DateTimeOffset.UtcNow.AddMilliseconds(1_500).ToUnixTimeMilliseconds());
                if (!hostedRecorder.Start(
                        newSession.SessionId,
                        newSession.OwnerUid,
                        newSession.OwnerName,
                        newSession.StartServerTimestampMilliseconds,
                        newSession.StartUtcUnixMilliseconds,
                        out string error))
                {
                    SendFeedback(player, "recording-start-failed", error);
                    return;
                }
                recorderSession = newSession;
                hostedRecorder.ObserveParticipant(player.PlayerUID, player.PlayerName, true, now, "recording-owner");
                RecordAudit(player, "recording-start", newSession.SessionId, "multitrack", "server-hosted-clock-anchor");
                BroadcastRecorderCaptureState();
            }

            RecorderRecordingSession session = recorderSession!.Value;
            recorderListeners.Add(player.PlayerUID);
            controlChannel?.SendPacket(new RecorderVoiceTimelinePacket
            {
                Active = true,
                ServerTimestampMilliseconds = now,
                ClientTimestampMilliseconds = packet.ClientTimestampMilliseconds,
                SessionId = session.SessionId,
                StartServerTimestampMilliseconds = session.StartServerTimestampMilliseconds,
                StartUtcUnixMilliseconds = session.StartUtcUnixMilliseconds
            }, player);
        }
        else
        {
            if (recorderSession != null)
            {
                StopRecorderSession(player, now, "requested");
            }
        }
    }

    private void StopRecorderSession(IServerPlayer? actor, long endServerTimestampMilliseconds, string reason)
    {
        if (recorderSession is not RecorderRecordingSession session)
        {
            if (actor != null)
            {
                recorderListeners.Remove(actor.PlayerUID);
            }
            return;
        }

        long end = Math.Max(session.StartServerTimestampMilliseconds, endServerTimestampMilliseconds);
        if (!hostedRecorder.Stop(end, reason, out HostedRecordingSessionResult result, out string error))
        {
            if (actor != null)
            {
                SendFeedback(actor, "recording-stop-failed", error);
            }
            if (!hostedRecorder.IsActive)
            {
                recorderListeners.Clear();
                recorderSession = null;
                BroadcastRecorderCaptureState();
                SendRecorderStatus();
            }
            return;
        }
        if (onlinePlayersByUid.TryGetValue(session.OwnerUid, out IServerPlayer? owner))
        {
            controlChannel?.SendPacket(new RecorderVoiceTimelinePacket
            {
                Active = false,
                ServerTimestampMilliseconds = end,
                SessionId = session.SessionId,
                StartServerTimestampMilliseconds = session.StartServerTimestampMilliseconds,
                StartUtcUnixMilliseconds = session.StartUtcUnixMilliseconds,
                EndServerTimestampMilliseconds = end
            }, owner);
        }

        recorderListeners.Clear();
        recorderSession = null;
        BroadcastRecorderCaptureState();
        HashSet<string> transferRecipients = new(StringComparer.Ordinal) { session.OwnerUid };
        if (actor != null)
        {
            transferRecipients.Add(actor.PlayerUID);
        }
        foreach (string uid in transferRecipients)
        {
            if (onlinePlayersByUid.TryGetValue(uid, out IServerPlayer? online)
                && online.HasPrivilege(Privilege.controlserver))
            {
                QueueRecorderTransfer(online, result.SessionId);
            }
        }
        RecordAudit(actor, "recording-stop", session.SessionId, "multitrack", reason);
        SendRecorderStatus();
    }

    private string BuildRecorderStatus()
    {
        if (recorderSession is not RecorderRecordingSession session)
        {
            return SVCLang.Get("server-recording-status-idle");
        }

        HostedRecordingSnapshot snapshot = hostedRecorder.Snapshot;
        return SVCLang.Get("server-recording-status-active", session.OwnerName, session.SessionId)
            + $" tracks={snapshot.TrackCount} packets={snapshot.PacketCount} missing={snapshot.MissingPackets}";
    }

    private bool QueueRecorderTransfer(IServerPlayer player, string? sessionId)
    {
        string requestedSessionId = sessionId ?? string.Empty;
        if (!VoiceProtocolValidation.IsSafeRecorderSessionId(requestedSessionId)
            || !hostedRecorder.TryGetCompletedSession(requestedSessionId, out HostedRecordingSessionFiles session))
        {
            controlChannel?.SendPacket(new RecorderFileChunkPacket
            {
                RecordingSessionId = requestedSessionId,
                Error = "Recording session is unavailable."
            }, player);
            return false;
        }

        recorderTransfers[player.PlayerUID] = new RecorderFileTransfer(session);
        return true;
    }

    private void PumpRecorderTransfers()
    {
        int transferBudget = Math.Max(
            VoiceProtocol.MaxRecorderFileChunkBytes,
            config.MaxRecorderDownloadKbps * 1000 / 8 / 4);
        foreach ((string uid, RecorderFileTransfer transfer) in recorderTransfers.ToArray())
        {
            if (!onlinePlayersByUid.TryGetValue(uid, out IServerPlayer? player))
            {
                recorderTransfers.Remove(uid);
                continue;
            }

            int remaining = transferBudget;
            int packetCount = 0;
            while (remaining > 0 && packetCount++ < 32)
            {
                int maximumBytes = Math.Min(remaining, VoiceProtocol.MaxRecorderFileChunkBytes);
                if (!transfer.TryRead(maximumBytes, out RecorderFileChunkPacket packet, out string error))
                {
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        controlChannel?.SendPacket(new RecorderFileChunkPacket
                        {
                            RecordingSessionId = transfer.SessionId,
                            Error = error
                        }, player);
                    }
                    recorderTransfers.Remove(uid);
                    break;
                }

                controlChannel?.SendPacket(packet, player);
                remaining -= Math.Max(1, packet.Data.Length);
                if (packet.TransferCompleted)
                {
                    recorderTransfers.Remove(uid);
                    break;
                }
            }
        }
    }

    private void SendRecorderRelay(IServerPlayer speaker, VoiceFrameV3Packet frame, int codec, long now)
    {
        if (!config.EnableRecorderCapture
            || recorderSession == null
            || recorderListeners.Count == 0
            || speaker.Entity is null
            || frame.CaptureServerTimestampMilliseconds <= 0)
        {
            return;
        }

        RecorderRecordingSession session = recorderSession.Value;
        int listenerCount = 0;
        foreach (string listenerUid in recorderListeners.ToArray())
        {
            // The owner records their microphone locally. Do not loop that
            // stream back, but keep their recorder subscription alive.
            if (listenerUid == speaker.PlayerUID)
            {
                continue;
            }

            if (listenerUid != session.OwnerUid
                || !onlinePlayersByUid.TryGetValue(listenerUid, out IServerPlayer? listener)
                || !listener.HasPrivilege(Privilege.controlserver)
                || !sessionsByUid.ContainsKey(listenerUid))
            {
                recorderListeners.Remove(listenerUid);
                continue;
            }

            if (listenerCount >= config.MaxRecorderListeners)
            {
                break;
            }

            int estimatedPacketBytes = frame.Payload.Length + 96;
            if (!recorderEgressBudget.HasCapacity(listenerUid, estimatedPacketBytes, now)
                || egressBudget.Available(now) + 0.0001d < estimatedPacketBytes
                || !recorderEgressBudget.TryConsume(listenerUid, estimatedPacketBytes, now)
                || !egressBudget.TryConsume(estimatedPacketBytes, now))
            {
                continue;
            }

            long packetAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
            RecorderVoiceRelayFrameV3Packet relay = new()
            {
                SpeakerUid = speaker.PlayerUID,
                SpeakerEntityId = speaker.Entity.EntityId,
                SessionId = frame.SessionId,
                Sequence = frame.Sequence,
                Payload = frame.Payload,
                Codec = codec,
                SpeakerName = speaker.PlayerName,
                ServerTimestampMilliseconds = MonotonicClock.NowMilliseconds,
                CaptureServerTimestampMilliseconds = frame.CaptureServerTimestampMilliseconds
            };
            long packetAllocationBytes = GC.GetAllocatedBytesForCurrentThread() - packetAllocationBefore;
            long serializationAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
            voiceChannel?.SendPacket(relay, listener);
            metrics.RecordRelayAllocation(
                packetAllocationBytes > 0 ? 1 : 0,
                GC.GetAllocatedBytesForCurrentThread() - serializationAllocationBefore,
                now);
            listenerCount++;
        }
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

    private void SendBitrateControl(
        IServerPlayer speaker,
        VoiceClientSession session,
        Dictionary<string, RelayRecipient> recipients,
        long now)
    {
        if (!config.EnableAdaptiveBitrate
            || session.Codec != VoiceProtocol.CodecOpus
            || !session.SupportsServerGuidedBitrate
            || controlChannel == null)
        {
            return;
        }

        List<double> losses = session.ListenerLossSamples;
        losses.Clear();
        foreach (RelayRecipient recipient in recipients.Values)
        {
            if (sessionsByUid.TryGetValue(recipient.Player.PlayerUID, out VoiceClientSession? listener)
                && now - listener.LastQualityMilliseconds <= 5_000L)
            {
                losses.Add(listener.ReportedProbeLossPercent);
            }
        }

        double p75Loss = 0d;
        if (losses.Count > 0)
        {
            losses.Sort();
            int index = Math.Clamp((int)Math.Ceiling(losses.Count * 0.75d) - 1, 0, losses.Count - 1);
            p75Loss = losses[index];
        }

        ServerBitrateDecision decision = ServerAdaptiveBitrateController.Evaluate(
            session.MaximumOpusBitrateKbps * 1_000,
            recipients.Count,
            p75Loss,
            egressBudget.Pressure(now));
        bool lower = session.LastGuidedBitrate <= 0 || decision.TargetBitrate < session.LastGuidedBitrate;
        long interval = lower ? 1_000L : 5_000L;
        if (session.LastGuidedBitrate == decision.TargetBitrate
            && now - session.LastBitrateControlMilliseconds < interval)
        {
            return;
        }

        session.LastGuidedBitrate = decision.TargetBitrate;
        session.LastBitrateControlMilliseconds = now;
        controlChannel.SendPacket(new VoiceBitrateControlPacket
        {
            ConnectionEpoch = session.ConnectionEpoch,
            TargetBitrate = decision.TargetBitrate,
            PacketLossPercent = decision.PacketLossPercent,
            FanOut = recipients.Count,
            ListenerLossP75 = p75Loss,
            EgressBudgetPressure = egressBudget.Pressure(now)
        }, speaker);
    }

    private void SendV2Relays(
        IServerPlayer speaker,
        VoiceFrameV3Packet frame,
        VoiceMode mode,
        Vec3d position,
        VoiceClientSession session,
        Dictionary<string, RelayRecipient> recipients,
        long now)
    {
        bool budgetDropped = false;
        RelayDispatchWorkspace dispatch = session.RelayDispatch;
        dispatch.GroupRecipients(recipients);
        for (int groupIndex = 0; groupIndex < dispatch.Count; groupIndex++)
        {
            RelayDispatchGroup group = dispatch[groupIndex];
            int estimatedPacketBytes = frame.Payload.Length + 64;
            group.ClearPermittedTargets();
            foreach (RelayRecipient recipient in group.Recipients)
            {
                string listenerUid = recipient.Player.PlayerUID;
                if (listenerEgressBudget.HasCapacity(listenerUid, estimatedPacketBytes, now)
                    && egressBudget.Available(now) + 0.0001d >= estimatedPacketBytes
                    && listenerEgressBudget.TryConsume(listenerUid, estimatedPacketBytes, now)
                    && egressBudget.TryConsume(estimatedPacketBytes, now))
                {
                    group.AddPermittedTarget(recipient.Player);
                }
                else
                {
                    metrics.DropBudget(now);
                    budgetDropped = true;
                }
            }
            if (group.PermittedTargetCount == 0)
            {
                continue;
            }

            long packetAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
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
                Codec = session.Codec,
                SenderUid = speaker.PlayerUID,
                CaptureServerTimestampMilliseconds = frame.CaptureServerTimestampMilliseconds,
                SourceEffects = ResolveSourceEffects(speaker, session, group.Key.RelayKind, now)
            };
            long packetAllocationBytes = GC.GetAllocatedBytesForCurrentThread() - packetAllocationBefore;
            IServerPlayer[] finalTargets = group.PreparePermittedTargets();
            long serializationAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
            voiceChannel?.SendPacket(relay, finalTargets);
            long serializationAllocationBytes = GC.GetAllocatedBytesForCurrentThread() - serializationAllocationBefore;
            metrics.RecordRelayAllocation(packetAllocationBytes > 0 ? 1 : 0, serializationAllocationBytes, now);
            metrics.Relayed(
                finalTargets.Length,
                estimatedPacketBytes,
                VoicePacketSizeEstimator.EstimateIpv4UdpBytes(relay),
                now);
        }
        if (budgetDropped
            && sessionsByUid.TryGetValue(speaker.PlayerUID, out VoiceClientSession? feedbackSession)
            && feedbackSession.ShouldSendFeedback("server-egress-limited", now))
        {
            SendFeedback(speaker, "server-egress-limited");
        }
    }

    private void SendDirectorProximityRelays(
        IServerPlayer speaker,
        VoiceFrameV3Packet frame,
        int codec,
        VoiceMode mode,
        Vec3d position,
        long now)
    {
        if (!config.EnableDirectorProximityCapture || speaker.Entity is null)
        {
            return;
        }

        bool hasCaptureRegionListener = directorListenersByUid.Values.Any(listener => listener.CaptureRegionActive);
        if (frame.Target is not (VoiceTransmitTarget.Proximity or VoiceTransmitTarget.ProximityAndChannel)
            && !hasCaptureRegionListener)
        {
            return;
        }

        float range = Math.Min(config.GetRange(mode), config.MaxRange);
        double rangeSquared = range * range;
        int speakerDimension = speaker.Entity.Pos.Dimension;
        float referenceDistance = CalculateReferenceDistance(range);
        float rolloffFactor = CalculateRolloff(range);
        int listenerCount = 0;

        foreach (KeyValuePair<string, DirectorVoiceListener> entry in directorListenersByUid.ToArray())
        {
            string listenerUid = entry.Key;
            DirectorVoiceListener listener = entry.Value;
            if (listener.ExpiresAtMilliseconds < now)
            {
                directorListenersByUid.Remove(listenerUid);
                directorStreamArbiter.RemovePlayer(listenerUid);
                continue;
            }
            int listenerDimension = listener.CaptureRegionActive
                ? listener.CaptureRegionDimension
                : listener.Dimension;
            if (listenerCount >= config.MaxDirectorListeners
                || listenerDimension != speakerDimension
                || listenerUid == speaker.PlayerUID
                || !onlinePlayersByUid.TryGetValue(listenerUid, out IServerPlayer? target)
                || !sessionsByUid.ContainsKey(listenerUid)
                || (statesByUid.TryGetValue(listenerUid, out ClientVoiceStatePacket? state) && state.GlobalMuted)
                || !moderation.CanReceive(listenerUid, now)
                || (mutedByListenerUid.TryGetValue(listenerUid, out HashSet<string>? muted) && muted.Contains(speaker.PlayerUID)))
            {
                continue;
            }

            bool inCaptureRegion = listener.CaptureRegionActive
                && IsWithinCaptureRegion(
                    position,
                    speakerDimension,
                    listener.CaptureRegionCenter,
                    listener.CaptureRegionDimension,
                    listener.CaptureRegionRadiusChunks);
            double distanceSquared = (position.X - listener.Position.X) * (position.X - listener.Position.X)
                + (position.Y - listener.Position.Y) * (position.Y - listener.Position.Y)
                + (position.Z - listener.Position.Z) * (position.Z - listener.Position.Z);
            if (!inCaptureRegion && (!double.IsFinite(distanceSquared) || distanceSquared > rangeSquared))
            {
                continue;
            }
            if (!directorStreamArbiter.TryAdmit(
                    listenerUid,
                    speaker.PlayerUID,
                    priority: 1,
                    distanceSquared,
                    config.MaxDirectorStreamsPerListener,
                    now,
                    proximity: true,
                    maxProximityStreams: config.MaxDirectorStreamsPerListener))
            {
                metrics.DropNoSlot(now);
                continue;
            }

            int estimatedPacketBytes = frame.Payload.Length + 128;
            if (!directorEgressBudget.HasCapacity(listenerUid, estimatedPacketBytes, now)
                || egressBudget.Available(now) + 0.0001d < estimatedPacketBytes
                || !directorEgressBudget.TryConsume(listenerUid, estimatedPacketBytes, now)
                || !egressBudget.TryConsume(estimatedPacketBytes, now))
            {
                metrics.DropBudget(now);
                continue;
            }

            long packetAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
            DirectorVoiceRelayFrameV3Packet relay = new()
            {
                SpeakerUid = speaker.PlayerUID,
                SpeakerEntityId = speaker.Entity.EntityId,
                SessionId = frame.SessionId,
                Sequence = frame.Sequence,
                Mode = mode,
                Payload = frame.Payload,
                X = (float)position.X,
                Y = (float)position.Y,
                Z = (float)position.Z,
                Dimension = speakerDimension,
                Codec = codec,
                MaxDistance = range,
                ReferenceDistance = referenceDistance,
                RolloffFactor = rolloffFactor,
                SpeakerName = speaker.PlayerName
            };
            long packetAllocationBytes = GC.GetAllocatedBytesForCurrentThread() - packetAllocationBefore;
            long serializationAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
            voiceChannel?.SendPacket(relay, target);
            metrics.RecordRelayAllocation(
                packetAllocationBytes > 0 ? 1 : 0,
                GC.GetAllocatedBytesForCurrentThread() - serializationAllocationBefore,
                now);
            metrics.Relayed(1, estimatedPacketBytes, VoicePacketSizeEstimator.EstimateIpv4UdpBytes(relay), now);
            listenerCount++;
        }
    }

    private static float CalculateRolloff(float range)
        => range > 1f ? (float)-Math.Log(0.01d) / (float)Math.Log(range) : 1f;

    private static float CalculateReferenceDistance(float range)
        => (float)Math.Max(3d, Math.Sqrt(Math.Max(range, 1f)) - 2d);

    private VoiceSourceEffectFlags ResolveSourceEffects(
        IServerPlayer speaker,
        VoiceClientSession session,
        VoiceRelayKind relayKind,
        long now)
    {
        if (!config.EnableEnvironmentalVoiceEffects || speaker.Entity == null)
        {
            return VoiceSourceEffectFlags.None;
        }

        if (session.LastSourceEffectMilliseconds < 0 || now - session.LastSourceEffectMilliseconds >= 150)
        {
            session.CachedEquipmentEffects = ResolveEquipmentEffects(speaker, session.EquippedSlots);
            session.CachedEyeInLiquid = IsEyeInLiquid(speaker.Entity);
            session.LastSourceEffectMilliseconds = now;
        }

        VoiceSourceEffectFlags effects = session.CachedEquipmentEffects;
        if (session.CachedEyeInLiquid
            && (relayKind == VoiceRelayKind.Proximity || config.ApplyUnderwaterEffectsToChannels))
        {
            effects |= VoiceSourceEffectFlags.Underwater;
        }
        return effects;
    }

    private VoiceSourceEffectFlags ResolveEquipmentEffects(IServerPlayer player, List<ItemSlotCharacter> equippedSlots)
    {
        if (config.EquipmentVoiceEffectRules.Count == 0)
        {
            return VoiceSourceEffectFlags.None;
        }

        equippedSlots.Clear();
        foreach (InventoryBase inventory in player.InventoryManager.InventoriesOrdered)
        {
            foreach (ItemSlot slot in inventory)
            {
                if (slot is ItemSlotCharacter characterSlot && !slot.Empty && slot.Itemstack?.Collectible?.Code != null)
                {
                    equippedSlots.Add(characterSlot);
                }
            }
        }

        foreach (VoiceEquipmentEffectRule rule in config.EquipmentVoiceEffectRules)
        {
            foreach (ItemSlotCharacter slot in equippedSlots)
            {
                if (!MatchesSlot(slot.Type, rule.Slot))
                {
                    continue;
                }

                AssetLocation? code = slot.Itemstack?.Collectible?.Code;
                if (code == null)
                {
                    continue;
                }
                string fullCode = code.ToString().ToLowerInvariant();
                string path = code.Path.ToLowerInvariant();
                if (!MatchesWildcard(rule.ItemCodePattern, fullCode)
                    && !MatchesWildcard(rule.ItemCodePattern, path))
                {
                    continue;
                }

                return rule.Effect == VoiceEquipmentVoiceEffect.Helmet
                    ? VoiceSourceEffectFlags.Helmet
                    : VoiceSourceEffectFlags.Mask;
            }
        }

        return VoiceSourceEffectFlags.None;
    }

    private bool IsEyeInLiquid(Vintagestory.API.Common.Entities.Entity entity)
    {
        Vec3d eye = new(
            entity.Pos.X + entity.LocalEyePos.X,
            entity.Pos.Y + entity.LocalEyePos.Y,
            entity.Pos.Z + entity.LocalEyePos.Z);
        BlockPos blockPos = new((int)Math.Floor(eye.X), (int)Math.Floor(eye.Y), (int)Math.Floor(eye.Z));
        return sapi.World.BlockAccessor.GetBlock(blockPos, 2).IsLiquid();
    }

    private static bool MatchesSlot(EnumCharacterDressType slot, VoiceEquipmentSlot configuredSlot)
    {
        return configuredSlot switch
        {
            VoiceEquipmentSlot.Head => slot == EnumCharacterDressType.Head,
            VoiceEquipmentSlot.Face => slot == EnumCharacterDressType.Face,
            VoiceEquipmentSlot.ArmorHead => slot == EnumCharacterDressType.ArmorHead,
            _ => false
        };
    }

    internal static bool MatchesWildcard(string pattern, string value)
    {
        if (!pattern.Contains(':', StringComparison.Ordinal))
        {
            int domainSeparator = value.IndexOf(':');
            if (domainSeparator >= 0 && domainSeparator + 1 < value.Length)
            {
                value = value[(domainSeparator + 1)..];
            }
        }

        int patternIndex = 0;
        int valueIndex = 0;
        int starIndex = -1;
        int retryValueIndex = 0;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length
                && (pattern[patternIndex] == value[valueIndex] || pattern[patternIndex] == '?'))
            {
                patternIndex++;
                valueIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                retryValueIndex = valueIndex;
            }
            else if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++retryValueIndex;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }
        return patternIndex == pattern.Length;
    }

    private static bool IsWithinCaptureRegion(
        Vec3d position,
        int dimension,
        Vec3d center,
        int centerDimension,
        int radiusChunks)
        => DirectorVoiceCaptureRegion.Contains(
            position.X,
            position.Z,
            dimension,
            center.X,
            center.Z,
            centerDimension,
            radiusChunks);

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
        bool administrator = player.HasPrivilege(Privilege.controlserver);
        IEnumerable<VoiceChannel> playerChannels = channels.GetVisibleForPlayer(player.PlayerUID, administrator)
            .OrderBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase);
        ChannelInfoPacket[] channelPackets = playerChannels.Select(channel => new ChannelInfoPacket
        {
            ChannelId = channel.Id,
            Name = channel.Name,
            Revision = channel.Revision,
            LocalRole = channel.Members.TryGetValue(player.PlayerUID, out VoiceChannelRole localRole)
                ? localRole
                : VoiceChannelRole.Banned,
            MemberCount = channel.Members.Count,
            Locked = channel.Locked,
            ExternallyManaged = channel.ExternallyManaged,
            Visibility = channel.Visibility,
            OwnerUid = channel.OwnerUid,
            Members = (administrator || channel.Members.ContainsKey(player.PlayerUID))
                ? channel.Members
                    .OrderBy(member => member.Key, StringComparer.Ordinal)
                    .Take(config.ChannelMemberPageSize)
                    .Select(member => BuildChannelMemberPacket(member.Key, member.Value))
                    .ToArray()
                : Array.Empty<ChannelMemberPacket>()
        }).ToArray();
        PendingChannelInvite? invite = channels.GetPendingInvite(player.PlayerUID, now);
        VoiceChannel? inviteChannel = invite is { } pendingInviteChannel
            && channels.TryGet(pendingInviteChannel.ChannelId, out VoiceChannel resolvedInviteChannel)
                ? resolvedInviteChannel
                : null;
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
            HasServerControl = administrator,
            HiddenPlayerUids = statesByUid
                .Where(entry => entry.Value.HideSelfFromPlayerLists && onlinePlayersByUid.ContainsKey(entry.Key))
                .Select(entry => entry.Key)
                .OrderBy(uid => uid, StringComparer.Ordinal)
                .ToArray(),
            PendingInviteChannelName = inviteChannel?.Name ?? string.Empty,
            PendingInviteChannelMemberCount = inviteChannel?.Members.Count ?? 0,
            PendingInviteChannelMaxMembers = inviteChannel?.MaxMembers ?? 0,
            PendingInviteChannelVisibility = inviteChannel?.Visibility ?? VoiceChannelVisibility.Open,
            PendingInviteChannelLocked = inviteChannel?.Locked == true
        }, player);
    }

    private void SendChannelMemberPage(IServerPlayer player, string channelId, int requestedPage, int requestedPageSize)
    {
        if (!channels.TryGet(channelId, out VoiceChannel channel)
            || (!channel.Members.ContainsKey(player.PlayerUID)
                && !player.HasPrivilege(Privilege.controlserver)))
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
        IServerPlayer? online = onlinePlayersByUid.TryGetValue(uid, out IServerPlayer? cached)
            ? cached
            : onlinePlayersByUid.Values.FirstOrDefault(player =>
                string.Equals(player.PlayerUID, uid, StringComparison.OrdinalIgnoreCase));
        online ??= sapi.World.AllOnlinePlayers
            .OfType<IServerPlayer>()
            .FirstOrDefault(player => string.Equals(player.PlayerUID, uid, StringComparison.OrdinalIgnoreCase));
        string playerName = online?.PlayerName?.Trim() ?? string.Empty;
        return new ChannelMemberPacket
        {
            PlayerUid = uid,
            PlayerName = playerName,
            Role = role,
            Online = online != null
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
        foreach (IServerPlayer player in onlinePlayersByUid.Values)
        {
            UpdateSpatialEntry(player);
        }
    }

    private void RefreshOnlinePlayerSnapshot()
    {
        foreach (IPlayer onlinePlayer in sapi.World.AllOnlinePlayers)
        {
            if (onlinePlayer is not IServerPlayer player)
            {
                continue;
            }
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
        directorEgressBudget.SetLimit(config.MaxDirectorEgressKbps);
        recorderEgressBudget.SetLimit(config.MaxRecorderEgressKbps);
        hostedRecorder.SetCheckpointInterval(config.RecorderCheckpointSeconds);
        StopAllTalkerNotifications();
        RestorePersistentChannels();
        SynchronizeChannelProviders();
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
        channels.EnsureNextChannelNumber(config.NextChannelNumber);
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
                stored.Password,
                stored.Visibility);
        }
    }

    private void SavePersistentChannels()
    {
        config.NextChannelNumber = channels.NextChannelNumber;
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
                Visibility = channel.Visibility,
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
        IServerPlayer? actor,
        string action,
        string target = "",
        string scope = "global",
        string reason = "")
    {
        if (actor == null)
        {
            return;
        }
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
        if (recorderSession != null)
        {
            StopRecorderSession(null, MonotonicClock.NowMilliseconds, "server-shutdown");
        }
        hostedRecorder.Dispose();
        sapi.Event.PlayerJoin -= OnPlayerJoin;
        sapi.Event.PlayerLeave -= OnPlayerLeave;
        sapi.Event.PlayerChat -= OnPlayerChat;
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
        directorEgressBudget.Clear();
        recorderEgressBudget.Clear();
        recorderListeners.Clear();
        recorderParticipants.Clear();
        recorderTransfers.Clear();
        recorderSession = null;
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
        public VoiceClientSession(
            int connectionEpoch,
            int codec,
            SimpleVoiceChatServerConfig config,
            long nowMilliseconds,
            int preferredMaximumOpusBitrateKbps,
            bool supportsServerGuidedBitrate)
        {
            ConnectionEpoch = connectionEpoch;
            Codec = codec;
            MaximumOpusBitrateKbps = Math.Clamp(preferredMaximumOpusBitrateKbps, 12, config.MaxOpusBitrateKbps);
            SupportsServerGuidedBitrate = supportsServerGuidedBitrate;
            PacketRate = new VoiceTokenBucket(1, 1, nowMilliseconds);
            ByteRate = new VoiceTokenBucket(1, 1, nowMilliseconds);
            ApplyRateLimits(config, nowMilliseconds);
            NewSessionRate = new VoiceTokenBucket(5, 5, nowMilliseconds);
            ControlRate = new VoiceTokenBucket(5, 10, nowMilliseconds);
            DirectorListenerRate = new VoiceTokenBucket(10, 10, nowMilliseconds);
            StateRate = new VoiceTokenBucket(2, 5, nowMilliseconds);
            MuteRate = new VoiceTokenBucket(20, 256, nowMilliseconds);
            PingRate = new VoiceTokenBucket(8, 8, nowMilliseconds);
            QualityRate = new VoiceTokenBucket(1, 2, nowMilliseconds);
            RecorderStateRate = new VoiceTokenBucket(2, 4, nowMilliseconds);
            RecorderPacketRate = new VoiceTokenBucket(100, 120, nowMilliseconds);
            RecorderByteRate = new VoiceTokenBucket(65_536, 81_920, nowMilliseconds);
        }

        public int ConnectionEpoch { get; }
        public int Codec { get; }
        public int MaximumOpusBitrateKbps { get; }
        public bool SupportsServerGuidedBitrate { get; }
        public double ReportedRoundTripMilliseconds { get; set; } = -1d;
        public double ReportedProbeLossPercent { get; set; }
        public long LastQualityMilliseconds { get; set; }
        public long LastBitrateControlMilliseconds { get; set; }
        public int LastGuidedBitrate { get; set; }
        public long LastSourceEffectMilliseconds { get; set; } = -1;
        public VoiceSourceEffectFlags CachedEquipmentEffects { get; set; }
        public bool CachedEyeInLiquid { get; set; }
        public List<ItemSlotCharacter> EquippedSlots { get; } = new(8);
        public VoiceTokenBucket PacketRate { get; private set; }
        public VoiceTokenBucket ByteRate { get; private set; }
        public VoiceTokenBucket NewSessionRate { get; }
        public VoiceTokenBucket ControlRate { get; }
        public VoiceTokenBucket DirectorListenerRate { get; }
        public VoiceTokenBucket StateRate { get; }
        public VoiceTokenBucket MuteRate { get; }
        public VoiceTokenBucket PingRate { get; }
        public VoiceTokenBucket QualityRate { get; }
        public VoiceTokenBucket RecorderStateRate { get; }
        public VoiceTokenBucket RecorderPacketRate { get; }
        public VoiceTokenBucket RecorderByteRate { get; }
        public VoiceSequenceWindow SequenceWindow { get; } = new();
        public VoiceSequenceWindow RecorderSequenceWindow { get; } = new();
        public List<VoiceSpatialCandidate> SpatialCandidates { get; } = new(128);
        public List<double> ListenerLossSamples { get; } = new(128);
        public Dictionary<string, RelayRecipient> RouteRecipients { get; } = new(128, StringComparer.Ordinal);
        public RelayDispatchWorkspace RelayDispatch { get; } = new();
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

    internal sealed class RelayDispatchWorkspace
    {
        private readonly List<RelayDispatchGroup> groups = new(2);

        public int Count { get; private set; }

        public RelayDispatchGroup this[int index] => groups[index];

        public void GroupRecipients(Dictionary<string, RelayRecipient> recipients)
        {
            Count = 0;
            foreach (RelayRecipient recipient in recipients.Values)
            {
                RelayGroup key = new(recipient.RelayKind, recipient.ChannelId);
                RelayDispatchGroup? group = null;
                for (int i = 0; i < Count; i++)
                {
                    if (groups[i].Key == key)
                    {
                        group = groups[i];
                        break;
                    }
                }

                if (group == null)
                {
                    if (Count == groups.Count)
                    {
                        groups.Add(new RelayDispatchGroup());
                    }
                    group = groups[Count++];
                    group.Reset(key);
                }
                group.Add(recipient);
            }

            // Match the previous stable OrderByDescending priority without its
            // grouping, iterator, and sorting allocations.
            for (int i = 1; i < Count; i++)
            {
                RelayDispatchGroup current = groups[i];
                int insertAt = i;
                while (insertAt > 0 && groups[insertAt - 1].Priority < current.Priority)
                {
                    groups[insertAt] = groups[insertAt - 1];
                    insertAt--;
                }
                groups[insertAt] = current;
            }
        }
    }

    internal sealed class RelayDispatchGroup
    {
        private readonly List<IServerPlayer> permittedTargets = new(128);
        private IServerPlayer[] exactTargets = Array.Empty<IServerPlayer>();
        private IServerPlayer[] previousExactTargets = Array.Empty<IServerPlayer>();

        public RelayGroup Key { get; private set; }
        public int Priority { get; private set; }
        public List<RelayRecipient> Recipients { get; } = new(128);
        public int PermittedTargetCount => permittedTargets.Count;

        public void Reset(RelayGroup key)
        {
            Key = key;
            Priority = int.MinValue;
            Recipients.Clear();
            permittedTargets.Clear();
        }

        public void Add(RelayRecipient recipient)
        {
            Recipients.Add(recipient);
            Priority = Math.Max(Priority, recipient.Priority);
        }

        public void ClearPermittedTargets() => permittedTargets.Clear();

        public void AddPermittedTarget(IServerPlayer player) => permittedTargets.Add(player);

        public IServerPlayer[] PreparePermittedTargets()
        {
            if (exactTargets.Length != permittedTargets.Count)
            {
                if (previousExactTargets.Length == permittedTargets.Count)
                {
                    (exactTargets, previousExactTargets) = (previousExactTargets, exactTargets);
                }
                else
                {
                    previousExactTargets = exactTargets;
                    exactTargets = new IServerPlayer[permittedTargets.Count];
                }
            }
            permittedTargets.CopyTo(exactTargets, 0);
            return exactTargets;
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

    internal readonly record struct RelayRecipient(IServerPlayer Player, VoiceRelayKind RelayKind, string ChannelId, int Priority);
    internal readonly record struct RelayGroup(VoiceRelayKind RelayKind, string ChannelId);
    private readonly record struct RecorderRecordingSession(
        string OwnerUid,
        string OwnerName,
        string SessionId,
        long StartServerTimestampMilliseconds,
        long StartUtcUnixMilliseconds);
    private sealed record RecorderParticipantState(
        int ConnectionEpoch,
        bool ClockReady,
        int ClockSampleCount,
        double BestRoundTripMilliseconds,
        long UtcSkewMilliseconds,
        long UpdatedServerTimestampMilliseconds);

    private sealed class RecorderFileTransfer
    {
        private readonly HostedRecordingSessionFiles files;
        private int fileIndex;
        private long offset;

        internal RecorderFileTransfer(HostedRecordingSessionFiles files)
        {
            this.files = files;
        }

        internal string SessionId => files.SessionId;

        internal bool TryRead(int maximumBytes, out RecorderFileChunkPacket packet, out string error)
        {
            packet = null!;
            error = string.Empty;
            if (fileIndex >= files.Files.Length)
            {
                error = "Recording transfer is already complete.";
                return false;
            }

            string path = files.Files[fileIndex];
            try
            {
                FileInfo info = new(path);
                if (!info.Exists || offset > info.Length)
                {
                    error = "Recording file changed during transfer.";
                    return false;
                }

                int count = (int)Math.Min(Math.Min(maximumBytes, VoiceProtocol.MaxRecorderFileChunkBytes), info.Length - offset);
                if (count <= 0)
                {
                    error = "Recording file is empty or unreadable.";
                    return false;
                }
                byte[] data = new byte[count];
                using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    stream.Seek(offset, SeekOrigin.Begin);
                    int read = stream.Read(data, 0, data.Length);
                    if (read != data.Length)
                    {
                        error = "Recording file read was incomplete.";
                        return false;
                    }
                }

                long currentOffset = offset;
                offset += count;
                bool fileCompleted = offset >= info.Length;
                bool transferCompleted = fileCompleted && fileIndex == files.Files.Length - 1;
                packet = new RecorderFileChunkPacket
                {
                    RecordingSessionId = files.SessionId,
                    RelativeFileName = Path.GetFileName(path),
                    Offset = currentOffset,
                    FileLength = info.Length,
                    TotalTransferBytes = files.TotalBytes,
                    Data = data,
                    FileCompleted = fileCompleted,
                    TransferCompleted = transferCompleted
                };
                if (fileCompleted)
                {
                    fileIndex++;
                    offset = 0L;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
    private readonly record struct ActiveTalkerNotification(string ChannelId, string SenderUid, string SenderName, long LastPacketMilliseconds);
    private readonly record struct DirectorVoiceListener(
        Vec3d Position,
        int Dimension,
        long ExpiresAtMilliseconds,
        bool CaptureRegionActive,
        Vec3d CaptureRegionCenter,
        int CaptureRegionDimension,
        int CaptureRegionRadiusChunks);
}
