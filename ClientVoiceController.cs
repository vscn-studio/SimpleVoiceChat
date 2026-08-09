using SimpleVoiceChat.Audio;
using SimpleVoiceChat.Config;
using SimpleVoiceChat.Gui;
using SimpleVoiceChat.Networking;
using OpenTK.Audio.OpenAL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace SimpleVoiceChat;

public sealed class ClientVoiceController : IDisposable
{
    private const int SettingsMemberPageSize = 8;
    private const int MaxCaptureFramesPerTick = 8;
    private const long VoiceProbeIntervalMilliseconds = 2_000;
    private const long VoiceProbeTimeoutMilliseconds = 6_000;
    private const long CaptureRecoveryIntervalMilliseconds = 10_000;

    private readonly ICoreClientAPI capi;
    private readonly SimpleVoiceChatClientConfig config;
    private readonly ControllerLifecycle lifecycle = new();
    private IClientNetworkChannel? controlChannel;
    private IClientNetworkChannel? voiceChannel;
    private OpenAlCaptureService? capture;
    private OpenAlPlaybackService? playback;
    private VoiceHud? hud;
    private VoiceSettingsDialog? settingsDialog;
    private VoiceInviteDialog? inviteDialog;
    private readonly short[] captureBuffer = new short[VoiceConstants.SamplesPerFrame];
    private readonly VoiceCapturePreprocessor capturePreprocessor = new();
    private readonly VoiceProbeTracker voiceProbeTracker = new();
    private ServerVoiceConfigPacket serverConfig = new()
    {
        Enabled = true,
        AllowWhisper = true,
        AllowShout = true,
        MaxRange = 40,
        WhisperRange = 8,
        TalkRange = 18,
        ShoutRange = 35,
        EnableOcclusion = true,
        EnableHudIndicators = true
    };

    private VoiceMode mode = VoiceMode.Talk;
    private ushort sequence;
    private int sessionId;
    private bool localMuted;
    private bool globalMuted;
    private bool toggleTalkEnabled;
    private bool lastPressed;
    private bool lastSpeaking;
    private bool captureWarningShown;
    private bool localMutePressed;
    private bool globalMutePressed;
    private bool settingsPressed;
    private bool toggleTalkPressed;
    private bool voiceHandshakeAccepted;
    private bool selectedChannelRestorePending;
    private bool hasServerControl;
    private int connectionEpoch;
    private int negotiatedCodec = VoiceProtocol.CodecImaAdpcm;
    private IVoiceEncoder? voiceEncoder;
    private long fastTickListenerId;
    private long playbackTickListenerId;
    private long slowTickListenerId;
    private long lastStateSentMs;
    private long lastHelloSentMs;
    private float lastMicLevel;
    private float lastRemoteVoiceLevel;
    private long lastVoiceLevelMs;
    private VoiceHudChannelMember[] channelHudMembers = Array.Empty<VoiceHudChannelMember>();
    private ChannelInfoPacket[] channelInfos = Array.Empty<ChannelInfoPacket>();
    private readonly Dictionary<string, ChannelMemberPagePacket> memberPagesByChannel = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> activeChannelTalkerHashesByChannel = new(StringComparer.Ordinal);
    private string[] pendingInviteNames = Array.Empty<string>();
    private string pendingInviteKey = string.Empty;
    private long pendingInviteDeadlineMs;
    private string lastDiagnostics = string.Empty;
    private int nextSessionId = 1;
    private int nextVoiceProbeNonce = 1;
    private long lastVoiceProbeSentMs;
    private long voiceHandshakeAcceptedMs;
    private long lastCaptureRecoveryAttemptMs;
    private long transmitBlockedUntilMs;
    private bool serverTransmitBlocked;
    private bool channelTransmitBlocked;

    public ClientVoiceController(ICoreClientAPI capi, SimpleVoiceChatClientConfig config)
    {
        this.capi = capi;
        this.config = config;
        selectedChannelRestorePending = !string.IsNullOrEmpty(config.SelectedChannelId);
        sessionId = NextSessionId();
    }

    internal SimpleVoiceChatClientConfig SettingsConfig => config;
    internal bool LocalMuted => localMuted;
    internal bool GlobalMuted => globalMuted;
    internal bool ContinuousTalkEnabled => toggleTalkEnabled;
    internal bool ContinuousTalkAllowed => serverConfig.AllowContinuousTalk;
    internal bool OcclusionForced => serverConfig.ForceImmersive;

    public void Start()
    {
        if (!lifecycle.TryStart(this))
        {
            return;
        }
        config.EnableNoiseSuppression &= VoiceProcessingCapabilities.NoiseSuppressionAvailable;
        config.EnableEchoCancellation &= VoiceProcessingCapabilities.EchoCancellationAvailable;
        SaveConfig();
        RegisterChannels();
        RegisterHotkeys();
        RegisterCommands();

        capture = new OpenAlCaptureService(capi, config);
        capture.Initialize();

        playback = new OpenAlPlaybackService(capi, config);
        playback.Initialize();

        hud = new VoiceHud(capi, BuildHudSnapshot, ShouldShowHud);
        capi.Gui.RegisterDialog(hud);
        settingsDialog = new VoiceSettingsDialog(capi, this);
        inviteDialog = new VoiceInviteDialog(
            capi,
            () => capi.World.ElapsedMilliseconds,
            AcceptPendingInvite,
            DeclinePendingInvite,
            () => hud?.ReservedHeight ?? 0);
        hud.Refresh();

        capi.Event.KeyUp += OnKeyUp;
        fastTickListenerId = capi.Event.RegisterGameTickListener(OnFastTick, VoiceConstants.FrameMilliseconds);
        playbackTickListenerId = capi.Event.RegisterGameTickListener(OnPlaybackTick, VoiceConstants.FrameMilliseconds);
        slowTickListenerId = capi.Event.RegisterGameTickListener(OnSlowTick, 100);
        SendHello();
        SendState(force: true);
        SyncMutedPlayersToServer();
    }

    private void RegisterChannels()
    {
        controlChannel = capi.Network.RegisterChannel(VoiceConstants.ControlChannelName)
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
            .SetMessageHandler<ServerVoiceConfigPacket>(OnServerConfig)
            .SetMessageHandler<VoiceWelcomePacket>(OnVoiceWelcome)
            .SetMessageHandler<ChannelSnapshotPacket>(OnChannelSnapshot)
            .SetMessageHandler<ChannelMemberDeltaPacket>(OnChannelMemberDelta)
            .SetMessageHandler<ChannelMemberPagePacket>(OnChannelMemberPage)
            .SetMessageHandler<TalkerStateDeltaPacket>(OnTalkerStateDelta)
            .SetMessageHandler<VoiceFeedbackPacket>(OnVoiceFeedback)
            .SetMessageHandler<VoiceDiagnosticsPacket>(OnVoiceDiagnostics);

        voiceChannel = capi.Network.RegisterUdpChannel(VoiceConstants.VoiceChannelName)
            .RegisterMessageType<VoiceFrameV3Packet>()
            .RegisterMessageType<VoiceRelayFrameV3Packet>()
            .RegisterMessageType<VoicePingPacket>()
            .RegisterMessageType<VoicePongPacket>()
            .SetMessageHandler<VoiceRelayFrameV3Packet>(OnVoiceRelayFrameV3)
            .SetMessageHandler<VoicePongPacket>(OnVoicePong);
    }

    private void RegisterHotkeys()
    {
        capi.Input.RegisterHotKey(VoiceConstants.PushToTalkHotKey, SVCLang.Get("hotkey-push-to-talk"), GlKeys.N, HotkeyType.CharacterControls);
        capi.Input.RegisterHotKey(VoiceConstants.ToggleTalkHotKey, SVCLang.Get("hotkey-toggle-talk"), GlKeys.N, HotkeyType.GUIOrOtherControls, altPressed: true);
        capi.Input.RegisterHotKey(VoiceConstants.ModeCycleHotKey, SVCLang.Get("hotkey-cycle-mode"), GlKeys.LBracket, HotkeyType.CharacterControls);
        capi.Input.RegisterHotKey(VoiceConstants.ModeCycleAltHotKey, SVCLang.Get("hotkey-cycle-mode-alt"), GlKeys.RBracket, HotkeyType.CharacterControls);
        capi.Input.RegisterHotKey(VoiceConstants.LocalMuteHotKey, SVCLang.Get("hotkey-local-mute"), GlKeys.Minus, HotkeyType.GUIOrOtherControls, ctrlPressed: true);
        capi.Input.RegisterHotKey(VoiceConstants.GlobalMuteHotKey, SVCLang.Get("hotkey-global-mute"), GlKeys.Semicolon, HotkeyType.CharacterControls);
        capi.Input.RegisterHotKey(VoiceConstants.SettingsHotKey, SVCLang.Get("hotkey-settings"), GlKeys.Quote, HotkeyType.GUIOrOtherControls);

        capi.Input.SetHotKeyHandler(VoiceConstants.ModeCycleHotKey, _ =>
        {
            if (!lifecycle.IsStarted)
            {
                return false;
            }
            CycleMode();
            return true;
        });
        capi.Input.SetHotKeyHandler(VoiceConstants.ModeCycleAltHotKey, _ =>
        {
            if (!lifecycle.IsStarted)
            {
                return false;
            }
            CycleMode();
            return true;
        });
        capi.Input.SetHotKeyHandler(VoiceConstants.ToggleTalkHotKey, _ =>
        {
            if (!lifecycle.IsStarted)
            {
                return false;
            }
            if (!toggleTalkPressed)
            {
                toggleTalkPressed = true;
                ToggleContinuousTalk();
            }
            return true;
        });
        capi.Input.SetHotKeyHandler(VoiceConstants.LocalMuteHotKey, _ =>
        {
            if (!lifecycle.IsStarted)
            {
                return false;
            }
            if (!localMutePressed)
            {
                localMutePressed = true;
                ToggleLocalMute();
            }
            return true;
        });
        capi.Input.SetHotKeyHandler(VoiceConstants.GlobalMuteHotKey, _ =>
        {
            if (!lifecycle.IsStarted)
            {
                return false;
            }
            if (!globalMutePressed)
            {
                globalMutePressed = true;
                ToggleGlobalMute();
            }
            return true;
        });
        capi.Input.SetHotKeyHandler(VoiceConstants.SettingsHotKey, _ =>
        {
            if (!lifecycle.IsStarted)
            {
                return false;
            }
            if (!settingsPressed)
            {
                settingsPressed = true;
                settingsDialog?.Toggle();
            }
            return true;
        });
    }

    private void OnKeyUp(KeyEvent e)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        if (e.KeyCode == GetHotkeyCode(VoiceConstants.LocalMuteHotKey, GlKeys.Minus))
        {
            localMutePressed = false;
        }

        if (e.KeyCode == GetHotkeyCode(VoiceConstants.ToggleTalkHotKey, GlKeys.N))
        {
            toggleTalkPressed = false;
        }

        if (e.KeyCode == GetHotkeyCode(VoiceConstants.GlobalMuteHotKey, GlKeys.Semicolon))
        {
            globalMutePressed = false;
        }

        if (e.KeyCode == GetHotkeyCode(VoiceConstants.SettingsHotKey, GlKeys.Quote))
        {
            settingsPressed = false;
        }
    }

    private int GetHotkeyCode(string hotkeyCode, GlKeys fallback)
    {
        return capi.Input.GetHotKeyByCode(hotkeyCode)?.CurrentMapping?.KeyCode ?? (int)fallback;
    }

    private void ToggleLocalMute()
    {
        localMuted = !localMuted;
        if (localMuted)
        {
            toggleTalkEnabled = false;
        }
        capi.ShowChatMessage(SVCLang.Get("chat-local-mute", localMuted ? SVCLang.Get("chat-local-mute-on") : SVCLang.Get("chat-local-mute-off")));
        SendState(force: true);
        hud?.Refresh();
    }

    private void ToggleGlobalMute()
    {
        globalMuted = !globalMuted;
        if (globalMuted)
        {
            toggleTalkEnabled = false;
        }
        capi.ShowChatMessage(SVCLang.Get("chat-global-mute", globalMuted ? SVCLang.Get("state-off") : SVCLang.Get("state-on")));
        SendState(force: true);
        hud?.Refresh();
    }

    private void ToggleContinuousTalk()
    {
        if (!serverConfig.AllowContinuousTalk)
        {
            toggleTalkEnabled = false;
            capi.ShowChatMessage(SVCLang.Get("chat-continuous-disabled"));
            hud?.Refresh();
            return;
        }
        toggleTalkEnabled = !toggleTalkEnabled;
        capi.ShowChatMessage(SVCLang.Get("chat-continuous-talk", toggleTalkEnabled ? SVCLang.Get("state-on") : SVCLang.Get("state-off")));
        SendState(force: true);
        hud?.Refresh();
    }

    private void SetLocalMuted(bool muted)
    {
        if (localMuted != muted)
        {
            ToggleLocalMute();
        }
    }

    private void SetGlobalMuted(bool muted)
    {
        if (globalMuted != muted)
        {
            ToggleGlobalMute();
        }
    }

    private void SetContinuousTalk(bool enabled)
    {
        if (toggleTalkEnabled != enabled)
        {
            ToggleContinuousTalk();
        }
    }

    private void RegisterCommands()
    {
        capi.ChatCommands.Create("svc")
            .WithDescription(SVCLang.Get("command-description-client"))
            .IgnoreAdditionalArgs()
            .HandleWith(HandleClientCommand);
    }

    private TextCommandResult HandleClientCommand(TextCommandCallingArgs args)
    {
        if (!lifecycle.IsStarted)
        {
            return TextCommandResult.Error(SVCLang.Get("command-controller-unavailable"));
        }
        string sub = args.RawArgs.PopWord("status").ToLowerInvariant();
        switch (sub)
        {
            case "status":
                return TextCommandResult.Success(BuildSettingsSummary());

            case "volume":
                {
                    int value = args.RawArgs.PopInt(-1) ?? -1;
                    if (value < 0 || value > 200)
                    {
                        return TextCommandResult.Error(SVCLang.Get("command-usage-volume"));
                    }
                    config.OutputVolume = value / 100f;
                    SaveConfig();
                    return TextCommandResult.Success(SVCLang.Get("command-set-volume-ok", value));
                }

            case "volumeplayer":
                {
                    string name = args.RawArgs.PopWord("");
                    int value = args.RawArgs.PopInt(-1) ?? -1;
                    if (string.IsNullOrWhiteSpace(name) || value < 0 || value > 200)
                    {
                        return TextCommandResult.Error(SVCLang.Get("command-usage-player-volume"));
                    }

                    IPlayer? player = capi.World.AllOnlinePlayers.FirstOrDefault(candidate =>
                        candidate.PlayerName.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (player == null)
                    {
                        return TextCommandResult.Error(SVCLang.Get("command-player-not-found", name));
                    }

                    if (value == 100)
                    {
                        config.PlayerVolumeOverrides.Remove(player.PlayerUID);
                    }
                    else
                    {
                        config.PlayerVolumeOverrides[player.PlayerUID] = value / 100f;
                    }
                    SaveConfig();
                    return TextCommandResult.Success(SVCLang.Get("command-set-player-volume-ok", player.PlayerName, value));
                }

            case "mute":
            case "unmute":
                {
                    string name = args.RawArgs.PopWord("");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return TextCommandResult.Error(SVCLang.Get("command-usage-player", sub));
                    }

                    IPlayer? player = capi.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerName.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (player == null)
                    {
                        return TextCommandResult.Error(SVCLang.Get("command-player-not-found", name));
                    }

                    bool muted = sub == "mute";
                    SetMuted(player.PlayerUID, muted);
                    return TextCommandResult.Success(muted ? SVCLang.Get("command-mute-player", player.PlayerName) : SVCLang.Get("command-unmute-player", player.PlayerName));
                }

            case "channelinvite":
                {
                    string name = args.RawArgs.PopWord("");
                    IPlayer? target = capi.World.AllOnlinePlayers.FirstOrDefault(player =>
                        player.PlayerName.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (target == null)
                    {
                        return TextCommandResult.Error(SVCLang.Get("command-usage-channel-invite"));
                    }
                    if (string.IsNullOrWhiteSpace(config.SelectedChannelId))
                    {
                        return TextCommandResult.Error(SVCLang.Get("chat-channel-action-requires-channel"));
                    }
                    SendChannelCommand("invite", config.SelectedChannelId, target.PlayerUID);
                    return TextCommandResult.Success(SVCLang.Get("command-request-channel-invite", target.PlayerName));
                }

            case "channelleave":
                SendChannelCommand("leave", channelId: args.RawArgs.PopWord(config.SelectedChannelId));
                return TextCommandResult.Success(SVCLang.Get("command-request-leave-channel"));

            case "channel":
                SendChannelCommand("request");
                return TextCommandResult.Success(SVCLang.Get("command-request-channel-status"));

            case "diag":
                SendChannelCommand("diagnostics");
                return TextCommandResult.Success(SVCLang.Get("command-diagnostics-requested"));

            case "adminmute":
            case "adminunmute":
            case "forceblock":
            case "unforceblock":
                {
                    string nameOrUid = args.RawArgs.PopWord("");
                    if (string.IsNullOrWhiteSpace(nameOrUid))
                    {
                        return TextCommandResult.Error(SVCLang.Get("command-usage-player-or-uid", sub));
                    }

                    if (controlChannel?.Connected == true)
                    {
                        controlChannel.SendPacket(new AdminVoiceControlPacket { Action = sub, TargetNameOrUid = nameOrUid });
                    }
                    return TextCommandResult.Success(SVCLang.Get("command-request-admin-control"));
                }

            case "adminmutes":
                if (controlChannel?.Connected == true)
                {
                    controlChannel.SendPacket(new AdminVoiceControlPacket { Action = sub });
                }
                return TextCommandResult.Success(SVCLang.Get("command-request-admin-list"));

            default:
                return TextCommandResult.Error(SVCLang.Get("command-usage-client-root"));
        }
    }

    private IPlayer? GetSelectedPlayer()
    {
        Entity? selected = capi.World.Player.CurrentEntitySelection?.Entity;
        if (selected == null)
        {
            return null;
        }

        return capi.World.AllOnlinePlayers.FirstOrDefault(p =>
            p.Entity != null
            && p.Entity.EntityId == selected.EntityId
            && p.PlayerUID != capi.World.Player.PlayerUID);
    }

    private void OnServerConfig(ServerVoiceConfigPacket packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        serverConfig = packet;
        ActivateCurrentServerProfile(packet.ServerInstanceId);
        if (serverConfig.ForceImmersive)
        {
            config.EnableOcclusionEffects = true;
        }
        if (!serverConfig.AllowContinuousTalk)
        {
            toggleTalkEnabled = false;
        }
        SaveConfig();
        hud?.Refresh();
        settingsDialog?.RefreshConfiguration();
    }

    private void SendHello()
    {
        if (controlChannel?.Connected != true)
        {
            return;
        }
        lastHelloSentMs = capi.World.ElapsedMilliseconds;
        controlChannel.SendPacket(new VoiceHelloPacket
        {
            ProtocolVersion = VoiceProtocol.CurrentVersion,
            ModVersion = "1.0.0",
            SupportedCodecs = new[] { VoiceProtocol.CodecOpus, VoiceProtocol.CodecImaAdpcm },
            Capabilities = (int)(VoiceCapability.ProtocolV3
                | VoiceCapability.ChannelDeltas
                | VoiceCapability.ChannelMemberPaging
                | VoiceCapability.AdaptiveJitter
                | VoiceCapability.Opus
                | VoiceCapability.Diagnostics)
        });
    }

    private void OnVoiceWelcome(VoiceWelcomePacket packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        voiceHandshakeAccepted = packet.Accepted
            && VoiceProtocol.IsCompatible(packet.ProtocolVersion)
            && packet.Codec is VoiceProtocol.CodecImaAdpcm or VoiceProtocol.CodecOpus;
        if (voiceHandshakeAccepted)
        {
            transmitBlockedUntilMs = 0;
            serverTransmitBlocked = false;
            channelTransmitBlocked = false;
        }
        ActivateCurrentServerProfile(packet.ServerInstanceId);
        connectionEpoch = voiceHandshakeAccepted ? packet.ConnectionEpoch : 0;
        voiceHandshakeAcceptedMs = voiceHandshakeAccepted ? capi.World.ElapsedMilliseconds : 0;
        negotiatedCodec = packet.Codec;
        hasServerControl = voiceHandshakeAccepted && packet.HasServerControl;
        voiceProbeTracker.Reset();
        lastVoiceProbeSentMs = 0;
        voiceEncoder?.Dispose();
        voiceEncoder = voiceHandshakeAccepted ? VoiceCodecFactory.CreateEncoder(negotiatedCodec, packet.Bitrate) : null;
        if (!voiceHandshakeAccepted && !string.IsNullOrWhiteSpace(packet.Message))
        {
            capi.ShowChatMessage($"Simple Voice Chat: {SVCLang.Get("feedback-" + packet.Message)}");
        }
        if (voiceHandshakeAccepted)
        {
            selectedChannelRestorePending = !string.IsNullOrEmpty(config.SelectedChannelId);
            SendState(force: true);
            SyncMutedPlayersToServer();
        }
        hud?.Refresh();
        settingsDialog?.RefreshData();
    }

    private void OnChannelSnapshot(ChannelSnapshotPacket packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        hasServerControl = packet.HasServerControl;
        channelInfos = packet.Channels ?? Array.Empty<ChannelInfoPacket>();
        HashSet<string> currentChannelIds = channelInfos.Select(channel => channel.ChannelId).ToHashSet(StringComparer.Ordinal);
        foreach (string removedChannelId in memberPagesByChannel.Keys.Where(id => !currentChannelIds.Contains(id)).ToArray())
        {
            memberPagesByChannel.Remove(removedChannelId);
        }
        foreach (string removedChannelId in activeChannelTalkerHashesByChannel.Keys.Where(id => !currentChannelIds.Contains(id)).ToArray())
        {
            activeChannelTalkerHashesByChannel.Remove(removedChannelId);
        }
        (string channelId, bool restoreOnServer) = ResolveChannelSelection(
            channelInfos,
            config.SelectedChannelId,
            packet.SelectedChannelId,
            selectedChannelRestorePending);
        config.SelectedChannelId = channelId;
        selectedChannelRestorePending = false;

        UpdateChannelHudMembers();
        UpdatePendingInvite(packet);
        SaveConfig();
        if (restoreOnServer)
        {
            SendChannelCommand("select", channelId: config.SelectedChannelId);
        }
        hud?.Refresh();
        settingsDialog?.RefreshData();
    }

    private void OnChannelMemberDelta(ChannelMemberDeltaPacket packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        int channelIndex = Array.FindIndex(channelInfos, channel => channel.ChannelId == packet.ChannelId);
        if (channelIndex < 0)
        {
            SendChannelCommand("request");
            return;
        }

        ChannelInfoPacket current = channelInfos[channelIndex];
        if (current.Revision != packet.BaseRevision || packet.Revision <= current.Revision)
        {
            SendChannelCommand("request");
            return;
        }

        Dictionary<string, ChannelMemberPacket> members = current.Members
            .ToDictionary(member => member.PlayerUid, member => member, StringComparer.Ordinal);
        foreach (string uid in packet.RemovedPlayerUids ?? Array.Empty<string>())
        {
            members.Remove(uid);
        }
        foreach (ChannelMemberPacket member in packet.UpsertedMembers ?? Array.Empty<ChannelMemberPacket>())
        {
            members[member.PlayerUid] = member;
        }
        current.Members = members.Values
            .OrderByDescending(member => member.Role)
            .ThenBy(member => member.PlayerName, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToArray();
        current.MemberCount = packet.MemberCount;
        current.Locked = packet.Locked;
        current.Revision = packet.Revision;
        channelInfos[channelIndex] = current;
        if (memberPagesByChannel.TryGetValue(packet.ChannelId, out ChannelMemberPagePacket? cachedPage))
        {
            memberPagesByChannel.Remove(packet.ChannelId);
            SendChannelCommand("members", channelId: packet.ChannelId, page: cachedPage.Page, pageSize: SettingsMemberPageSize);
        }
        UpdateChannelHudMembers();
        hud?.Refresh();
        settingsDialog?.RefreshData();
    }

    private void OnChannelMemberPage(ChannelMemberPagePacket packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        ChannelInfoPacket? channel = channelInfos.FirstOrDefault(info => info.ChannelId == packet.ChannelId);
        if (channel == null || channel.Revision != packet.Revision)
        {
            SendChannelCommand("request");
            return;
        }
        memberPagesByChannel[packet.ChannelId] = packet;
        settingsDialog?.RefreshData();
    }

    private void OnTalkerStateDelta(TalkerStateDeltaPacket packet)
    {
        if (!lifecycle.IsStarted || !channelInfos.Any(info => info.ChannelId == packet.ChannelId))
        {
            return;
        }

        if (packet.Speaking)
        {
            if (!activeChannelTalkerHashesByChannel.TryGetValue(packet.ChannelId, out HashSet<int>? activeTalkers))
            {
                activeTalkers = new HashSet<int>();
                activeChannelTalkerHashesByChannel[packet.ChannelId] = activeTalkers;
            }
            activeTalkers.Add(packet.SenderUidHash);
        }
        else if (activeChannelTalkerHashesByChannel.TryGetValue(packet.ChannelId, out HashSet<int>? activeTalkers))
        {
            activeTalkers.Remove(packet.SenderUidHash);
            if (activeTalkers.Count == 0)
            {
                activeChannelTalkerHashesByChannel.Remove(packet.ChannelId);
            }
        }

        UpdateChannelHudMembers();
        hud?.Refresh();
    }

    private void OnVoiceFeedback(VoiceFeedbackPacket packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        UpdateHudAccessState(packet);
        string message = LocalizeFeedback(packet);
        if (!string.IsNullOrWhiteSpace(message))
        {
            capi.ShowChatMessage($"Simple Voice Chat: {message}");
        }
    }

    private static string LocalizeFeedback(VoiceFeedbackPacket packet)
    {
        string code = (packet.Code ?? string.Empty).Trim().ToLowerInvariant();
        if (code.Length > 0)
        {
            string localized = SVCLang.Get("feedback-" + code, packet.Arguments?.Cast<object>().ToArray() ?? Array.Empty<object>());
            string unresolved = $"simplevoicechat:feedback-{code}";
            if (!localized.Equals(unresolved, StringComparison.OrdinalIgnoreCase))
            {
                return localized;
            }
        }
        return packet.Message ?? string.Empty;
    }

    private void OnVoiceDiagnostics(VoiceDiagnosticsPacket packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        lastDiagnostics = SVCLang.Get(
            "diagnostics-detail",
            packet.RollingRelayedPackets,
            packet.RollingRelayedBytes,
            packet.RollingDroppedPackets,
            packet.P95FanOut.ToString("0.0"),
            packet.P95RouteMilliseconds.ToString("0.000"),
            packet.ActiveListenerStreams,
            packet.ActiveTalkers);
        settingsDialog?.RefreshData();
    }

    private void OnVoicePong(VoicePongPacket packet)
    {
        if (!lifecycle.IsStarted
            || !voiceHandshakeAccepted
            || packet.ConnectionEpoch != connectionEpoch
            || packet.Nonce <= 0)
        {
            return;
        }

        if (voiceProbeTracker.MarkReply(packet.Nonce, capi.World.ElapsedMilliseconds))
        {
            hud?.Refresh();
        }
    }

    private void SendChannelCommand(
        string action,
        string channelId = "",
        string targetPlayerUid = "",
        string name = "",
        int page = 0,
        int pageSize = 0)
    {
        if (controlChannel?.Connected != true)
        {
            return;
        }
        controlChannel.SendPacket(new ChannelCommandPacket
        {
            Action = action,
            ChannelId = channelId,
            TargetPlayerUid = targetPlayerUid,
            Name = name,
            Page = page,
            PageSize = pageSize
        });
    }

    private float GetPlaybackGain(IPlayer? sender, bool channelRelay)
    {
        float playerGain = sender != null && config.PlayerVolumeOverrides.TryGetValue(sender.PlayerUID, out float configured)
            ? configured
            : 1f;
        float channelGain = channelRelay ? config.ChannelOutputVolume : 1f;
        return Math.Clamp(playerGain * channelGain, 0f, 2f);
    }

    private void UpdateChannelHudMembers()
    {
        ChannelInfoPacket? channel = channelInfos.FirstOrDefault(info => info.ChannelId == config.SelectedChannelId)
            ?? channelInfos.FirstOrDefault();
        if (channel == null)
        {
            channelHudMembers = Array.Empty<VoiceHudChannelMember>();
            return;
        }
        activeChannelTalkerHashesByChannel.TryGetValue(channel.ChannelId, out HashSet<int>? activeTalkers);

        channelHudMembers = (channel.Members ?? Array.Empty<ChannelMemberPacket>())
            .Where(member => member.PlayerUid != capi.World.Player.PlayerUID)
            .Take(12)
            .Select(member => new VoiceHudChannelMember(
                DisplayPlayerName(member.PlayerUid, member.PlayerName),
                activeTalkers?.Contains(VoiceMath.StableUidHash(member.PlayerUid)) == true))
            .ToArray();
    }

    internal VoiceSettingsChannelOption[] BuildChannelOptions()
    {
        return channelInfos
            .OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
            .Select(info => new VoiceSettingsChannelOption(info.ChannelId, info.Name, info.LocalRole, info.ExternallyManaged))
            .ToArray();
    }

    internal VoiceSettingsPlayerOption[] BuildPlayerOptions()
    {
        IEnumerable<VoiceSettingsPlayerOption> online = capi.World.AllOnlinePlayers
            .Where(player => player.PlayerUID != capi.World.Player.PlayerUID)
            .Select(player => new VoiceSettingsPlayerOption(player.PlayerUID, player.PlayerName));
        IEnumerable<VoiceSettingsPlayerOption> members = channelInfos
            .Where(channel => channel.ChannelId == config.SelectedChannelId)
            .SelectMany(channel => channel.Members ?? Array.Empty<ChannelMemberPacket>())
            .Where(member => member.PlayerUid != capi.World.Player.PlayerUID)
            .Select(member => new VoiceSettingsPlayerOption(
                member.PlayerUid,
                DisplayPlayerName(member.PlayerUid, member.PlayerName)));
        IEnumerable<VoiceSettingsPlayerOption> currentPage = memberPagesByChannel.TryGetValue(config.SelectedChannelId, out ChannelMemberPagePacket? page)
            ? page.Members
                .Where(member => member.PlayerUid != capi.World.Player.PlayerUID)
                .Select(member => new VoiceSettingsPlayerOption(
                    member.PlayerUid,
                    DisplayPlayerName(member.PlayerUid, member.PlayerName)))
            : Array.Empty<VoiceSettingsPlayerOption>();
        return online.Concat(members).Concat(currentPage)
            .GroupBy(player => player.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
    }

    internal int GetPlayerVolumePercent(string playerUid)
    {
        return config.PlayerVolumeOverrides.TryGetValue(playerUid, out float value)
            ? (int)Math.Round(value * 100f)
            : 100;
    }

    private void SetPlayerVolume(string playerUid, int value)
    {
        if (string.IsNullOrWhiteSpace(playerUid))
        {
            return;
        }
        float normalized = Math.Clamp(value / 100f, 0f, 2f);
        if (Math.Abs(normalized - 1f) < 0.001f)
        {
            config.PlayerVolumeOverrides.Remove(playerUid);
        }
        else
        {
            config.PlayerVolumeOverrides[playerUid] = normalized;
        }
        SaveConfig();
    }

    private void SetPlayerMuted(string playerUid, bool muted)
    {
        if (string.IsNullOrWhiteSpace(playerUid))
        {
            return;
        }
        SetMuted(playerUid, muted);
    }

    internal string[] GetInputDeviceValues()
    {
        List<string> values = new() { string.Empty };
        try
        {
            foreach (string device in ALC.GetString(AlcGetStringList.CaptureDeviceSpecifier))
            {
                if (!string.IsNullOrWhiteSpace(device) && !values.Contains(device, StringComparer.Ordinal))
                {
                    values.Add(device);
                }
            }
        }
        catch (Exception exception)
        {
            capi.Logger.Warning("SimpleVoiceChat: failed enumerating capture devices: {0}", exception.Message);
        }

        if (!string.IsNullOrWhiteSpace(config.InputDeviceName)
            && !values.Contains(config.InputDeviceName, StringComparer.Ordinal))
        {
            values.Add(config.InputDeviceName);
        }
        return values.ToArray();
    }

    internal static string[] GetInputDeviceNames(string[] values)
    {
        return values.Select(value => string.IsNullOrEmpty(value) ? SVCLang.Get("default-microphone") : value).ToArray();
    }

    internal bool IsPlayerMuted(string playerUid)
    {
        return config.MutedPlayerUids.Contains(playerUid, StringComparer.Ordinal);
    }

    internal void SetInputDeviceFromSettings(string value)
    {
        string next = value ?? string.Empty;
        if (config.InputDeviceName == next)
        {
            return;
        }
        config.InputDeviceName = next;
        SaveConfig();
        ReinitializeCapture();
    }

    internal void SetOutputVolumeFromSettings(int value)
    {
        config.OutputVolume = Math.Clamp(value / 100f, 0f, 2f);
        SaveConfig();
    }

    internal void SetChannelVolumeFromSettings(int value)
    {
        config.ChannelOutputVolume = Math.Clamp(value / 100f, 0f, 2f);
        SaveConfig();
    }

    internal void SetMicGainFromSettings(int value)
    {
        config.MicGain = Math.Clamp(value / 100f, 0.1f, 4f);
        SaveConfig();
    }

    internal void SetNoiseGateFromSettings(int value)
    {
        config.NoiseGate = Math.Clamp(value / 1000f, 0f, 0.2f);
        SaveConfig();
    }

    internal void SetNoiseSuppressionFromSettings(bool enabled)
    {
        config.EnableNoiseSuppression = enabled && VoiceProcessingCapabilities.NoiseSuppressionAvailable;
        SaveConfig();
    }

    internal void SetEchoCancellationFromSettings(bool enabled)
    {
        config.EnableEchoCancellation = enabled && VoiceProcessingCapabilities.EchoCancellationAvailable;
        SaveConfig();
    }

    internal void SetAdaptiveJitterFromSettings(bool enabled)
    {
        config.AdaptiveJitterBuffer = enabled;
        playback?.SetAdaptiveJitter(enabled);
        SaveConfig();
    }

    internal void SetHudVisibleFromSettings(bool visible)
    {
        config.ShowMicrophoneHud = visible;
        config.ShowHudIndicator = visible;
        SaveConfig();
        hud?.Refresh();
    }

    internal void SetOcclusionFromSettings(bool enabled)
    {
        config.EnableOcclusionEffects = serverConfig.ForceImmersive || enabled;
        SaveConfig();
    }

    internal void SetPerformanceModeFromSettings(bool enabled)
    {
        config.PerformanceMode = enabled;
        SaveConfig();
    }

    internal void SetLocalMutedFromSettings(bool muted) => SetLocalMuted(muted);
    internal void SetGlobalMutedFromSettings(bool muted) => SetGlobalMuted(muted);
    internal void SetContinuousTalkFromSettings(bool enabled) => SetContinuousTalk(enabled);
    internal void SetPlayerVolumeFromSettings(string playerUid, int value) => SetPlayerVolume(playerUid, value);
    internal void SetPlayerMutedFromSettings(string playerUid, bool muted) => SetPlayerMuted(playerUid, muted);
    internal void SelectChannelFromSettings(string channelId) => SelectChannel(channelId);

    internal void SetTransmitTargetFromSettings(string value)
    {
        config.TransmitTarget = value switch
        {
            "channel" => VoiceTransmitTarget.SelectedChannel,
            "both" => VoiceTransmitTarget.ProximityAndChannel,
            _ => VoiceTransmitTarget.Proximity
        };
        SaveConfig();
        hud?.Refresh();
    }

    private VoiceSettingsMemberPage BuildMemberPage(string channelId, int page)
    {
        ChannelInfoPacket? channel = channelInfos.FirstOrDefault(info => info.ChannelId == channelId);
        if (channel == null)
        {
            return VoiceSettingsMemberPage.Empty;
        }
        if (!memberPagesByChannel.TryGetValue(channelId, out ChannelMemberPagePacket? cached)
            || cached.Page != page
            || cached.Revision != channel.Revision)
        {
            SendChannelCommand("members", channelId: channelId, page: page, pageSize: SettingsMemberPageSize);
            return new VoiceSettingsMemberPage(channel.MemberCount, page, SettingsMemberPageSize, Array.Empty<VoiceSettingsMemberOption>());
        }
        return new VoiceSettingsMemberPage(
            cached.TotalMembers,
            cached.Page,
            cached.PageSize,
            cached.Members.Select(member => new VoiceSettingsMemberOption(
                member.PlayerUid,
                DisplayPlayerName(member.PlayerUid, member.PlayerName),
                member.Role)).ToArray());
    }

    private void UpdateHudAccessState(VoiceFeedbackPacket packet)
    {
        string code = (packet.Code ?? string.Empty).Trim().ToLowerInvariant();
        long now = capi.World.ElapsedMilliseconds;
        switch (code)
        {
            case "transmit-blocked":
                if (packet.Arguments is { Length: > 0 }
                    && long.TryParse(packet.Arguments[0], out long seconds)
                    && seconds > 0)
                {
                    transmitBlockedUntilMs = now + Math.Min(seconds, 86_400) * 1_000;
                }
                else
                {
                    serverTransmitBlocked = true;
                }
                break;

            case "transmit-restored":
                serverTransmitBlocked = false;
                transmitBlockedUntilMs = 0;
                break;

            case "channel-not-authorized":
                channelTransmitBlocked = true;
                break;

            case "channel-transmit-blocked":
                if (packet.Arguments is { Length: > 0 }
                    && string.Equals(packet.Arguments[0], config.SelectedChannelId, StringComparison.Ordinal))
                {
                    channelTransmitBlocked = true;
                }
                break;

            case "channel-transmit-restored":
                if (packet.Arguments is { Length: > 0 }
                    && string.Equals(packet.Arguments[0], config.SelectedChannelId, StringComparison.Ordinal))
                {
                    channelTransmitBlocked = false;
                }
                break;

            case "protocol-suspended":
                transmitBlockedUntilMs = now + 60_000;
                break;
        }
        hud?.Refresh();
    }

    private string DisplayPlayerName(string playerUid, string? playerName)
    {
        string safeName = (playerName ?? string.Empty).Trim();
        if (safeName.Length > 0 && !safeName.Equals(playerUid, StringComparison.Ordinal))
        {
            return safeName;
        }

        IPlayer? online = capi.World.AllOnlinePlayers.FirstOrDefault(player =>
            player.PlayerUID.Equals(playerUid, StringComparison.Ordinal));
        safeName = (online?.PlayerName ?? string.Empty).Trim();
        return safeName.Length > 0 && !safeName.Equals(playerUid, StringComparison.Ordinal)
            ? safeName
            : SVCLang.Get("player-offline");
    }

    internal void ManageSelectedChannel(
        string action,
        string channelId,
        string targetUid = "",
        string name = "",
        VoiceChannelRole role = VoiceChannelRole.Member)
    {
        if (action == "create-channel" || action == "create")
        {
            string channelName = string.IsNullOrWhiteSpace(name)
                ? $"{SVCLang.Get("channel-default-name")} - {capi.World.Player.PlayerName}"
                : name.Trim();
            SendChannelCommand("create", name: channelName);
            return;
        }

        if (action == "rename")
        {
            SendChannelCommand("rename", channelId: channelId, name: name.Trim());
            return;
        }

        if ((action is "leave" or "disband")
            && string.Equals(config.SelectedChannelId, channelId, StringComparison.Ordinal))
        {
            config.SelectedChannelId = string.Empty;
            channelTransmitBlocked = false;
            SaveConfig();
            UpdateChannelHudMembers();
            hud?.Refresh();
        }

        if (action is "tempmute" or "deafen" or "adminmute" or "adminunmute" or "forceblock" or "unforceblock")
        {
            if (controlChannel?.Connected == true)
            {
                controlChannel.SendPacket(new AdminVoiceControlPacket
                {
                    Action = action,
                    TargetNameOrUid = targetUid,
                    DurationSeconds = action is "tempmute" or "deafen" ? 60 : 0
                });
            }
            return;
        }

        string roleName = action == "role" ? role.ToString() : string.Empty;
        SendChannelCommand(action, channelId: channelId, targetPlayerUid: targetUid, name: roleName);
    }

    private void SelectChannel(string channelId)
    {
        config.SelectedChannelId = channelId;
        channelTransmitBlocked = false;
        SaveConfig();
        SendChannelCommand("select", channelId: channelId);
        UpdateChannelHudMembers();
        hud?.Refresh();
    }

    internal static (string ChannelId, bool RestoreOnServer) ResolveChannelSelection(
        ChannelInfoPacket[] channels,
        string? savedChannelId,
        string? serverChannelId,
        bool restorePending)
    {
        string serverSelected = serverChannelId ?? string.Empty;
        if (!string.IsNullOrEmpty(serverSelected)
            && channels.Any(channel => channel.ChannelId == serverSelected))
        {
            return (serverSelected, false);
        }

        string savedSelected = savedChannelId ?? string.Empty;
        if (!string.IsNullOrEmpty(savedSelected)
            && channels.Any(channel => channel.ChannelId == savedSelected))
        {
            return (savedSelected, restorePending);
        }

        if (string.IsNullOrEmpty(savedSelected))
        {
            return (string.Empty, false);
        }

        return (channels.FirstOrDefault()?.ChannelId ?? string.Empty, false);
    }

    private bool AcceptPendingInvite()
    {
        SendChannelCommand("accept");
        ClearPendingInvite();
        return true;
    }

    private bool DeclinePendingInvite()
    {
        SendChannelCommand("decline");
        ClearPendingInvite();
        return true;
    }

    private void UpdatePendingInvite(ChannelSnapshotPacket packet)
    {
        string[] names = packet.PendingInviteNames ?? Array.Empty<string>();
        string[] channelIds = packet.PendingInviteChannelIds ?? Array.Empty<string>();
        if (names.Length == 0)
        {
            ClearPendingInvite();
            return;
        }

        string key = string.Join("\u001f", channelIds) + "\u001e" + string.Join("\u001f", names);
        pendingInviteNames = names;
        if (key == pendingInviteKey)
        {
            return;
        }

        pendingInviteKey = key;
        pendingInviteDeadlineMs = capi.World.ElapsedMilliseconds + VoiceInvitePolicy.ResponseTimeoutMilliseconds;
        inviteDialog?.ShowInvite(string.Join(", ", names), pendingInviteDeadlineMs);
    }

    private void UpdatePendingInviteTimeout()
    {
        if (!VoiceInvitePolicy.HasExpired(capi.World.ElapsedMilliseconds, pendingInviteDeadlineMs))
        {
            return;
        }

        DeclinePendingInvite();
    }

    private void ClearPendingInvite()
    {
        pendingInviteNames = Array.Empty<string>();
        pendingInviteKey = string.Empty;
        pendingInviteDeadlineMs = 0;
        inviteDialog?.Dismiss();
    }

    private string BuildDiagnosticsSummary()
    {
        if (!voiceHandshakeAccepted)
        {
            return SVCLang.Get("diagnostics-waiting");
        }

        string codec = negotiatedCodec == VoiceProtocol.CodecOpus ? "Opus" : "ADPCM";
        bool udpResponsive = voiceProbeTracker.IsResponsive(capi.World.ElapsedMilliseconds, VoiceProbeTimeoutMilliseconds);
        double rtt = voiceProbeTracker.SmoothedRttMilliseconds;
        string network = SVCLang.Get(
            "diagnostics-network",
            udpResponsive ? SVCLang.Get("state-ready") : SVCLang.Get("state-unavailable"),
            rtt < 0 ? "--" : rtt.ToString("0"),
            voiceProbeTracker.LossPercent.ToString("0"));
        string local = SVCLang.Get(
            "diagnostics-local-processing",
            playback?.BuildDebugStatus() ?? SVCLang.Get("summary-playback-uninitialized"),
            VoiceProcessingCapabilities.BackendName,
            VoiceProcessingCapabilities.NoiseSuppressionAvailable,
            VoiceProcessingCapabilities.EchoCancellationAvailable);
        return string.IsNullOrWhiteSpace(lastDiagnostics)
            ? SVCLang.Get("diagnostics-summary", VoiceProtocol.CurrentVersion, codec, connectionEpoch, serverConfig.MaxStreamsPerListener, $"{network}\n{local}")
            : SVCLang.Get("diagnostics-summary", VoiceProtocol.CurrentVersion, codec, connectionEpoch, serverConfig.MaxStreamsPerListener, $"{network}\n{lastDiagnostics}\n{local}");
    }

    internal bool HasServerControl => hasServerControl;

    internal string BuildSettingsStatus() => BuildSettingsSummary();

    internal string BuildSettingsDiagnostics() => BuildDiagnosticsSummary();

    internal void RequestSettingsRefresh()
    {
        SendChannelCommand("request");
        SendChannelCommand("diagnostics");
    }

    private void OnVoiceRelayFrameV3(VoiceRelayFrameV3Packet packet)
    {
        if (!lifecycle.IsStarted
            || !voiceHandshakeAccepted
            || !serverConfig.Enabled
            || globalMuted
            || !VoiceProtocolValidation.IsValidRelayShape(packet)
            || packet.SenderEntityId == capi.World.Player.Entity.EntityId)
        {
            return;
        }

        IPlayer? sender = capi.World.AllOnlinePlayers.FirstOrDefault(player => player.Entity?.EntityId == packet.SenderEntityId);
        if (sender != null && config.MutedPlayerUids.Contains(sender.PlayerUID))
        {
            return;
        }

        bool channelRelay = packet.RelayKind != VoiceRelayKind.Proximity;
        playback?.Enqueue(packet, packet.Codec, GetPlaybackGain(sender, channelRelay));
        lastRemoteVoiceLevel = Math.Max(lastRemoteVoiceLevel, NormalizeRemoteVoiceLevel(
            packet.Level / 255f,
            packet.Mode,
            channelRelay,
            packet.X,
            packet.Y,
            packet.Z));
        lastVoiceLevelMs = capi.World.ElapsedMilliseconds;
        hud?.Refresh();
    }

    private void OnFastTick(float dt)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        bool pressed = toggleTalkEnabled || IsPushToTalkPressed();
        bool canSpeak = pressed
            && !localMuted
            && !globalMuted
            && serverConfig.Enabled
            && voiceHandshakeAccepted
            && capture?.IsAvailable == true
            && voiceChannel?.Connected == true;

        if (pressed && capture?.IsAvailable != true && !captureWarningShown)
        {
            captureWarningShown = true;
            capi.ShowChatMessage(SVCLang.Get("chat-mic-unavailable", capture?.FailureReason ?? string.Empty));
        }

        if (canSpeak)
        {
            if (!lastPressed)
            {
                BeginVoiceSession();
                capture?.Start();
            }
            CaptureAndSend();
        }
        else if (lastPressed)
        {
            CaptureAndSend();
            capture?.Stop();
        }

        if (!canSpeak)
        {
            lastMicLevel = 0f;
        }

        lastPressed = canSpeak;
        bool speaking = canSpeak;
        if (speaking != lastSpeaking)
        {
            lastSpeaking = speaking;
            SendState(force: true);
            hud?.Refresh();
        }
    }

    private void OnSlowTick(float dt)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        if (!voiceHandshakeAccepted
            && controlChannel?.Connected == true
            && capi.World.ElapsedMilliseconds - lastHelloSentMs >= 3000)
        {
            SendHello();
        }
        UpdateVoiceProbe();
        TryRecoverCapture();
        UpdatePendingInviteTimeout();
        SendState(force: false);
        if (!lastSpeaking)
        {
            lastMicLevel = 0f;
        }
        lastRemoteVoiceLevel *= 0.72f;
        if (lastRemoteVoiceLevel < 0.01f
            || capi.World.ElapsedMilliseconds - lastVoiceLevelMs > 350)
        {
            lastRemoteVoiceLevel = 0f;
        }
        hud?.Refresh();
    }

    private void UpdateVoiceProbe()
    {
        long now = capi.World.ElapsedMilliseconds;
        voiceProbeTracker.Expire(now, VoiceProbeTimeoutMilliseconds);
        if (voiceHandshakeAccepted
            && voiceChannel?.Connected == true
            && lastVoiceProbeSentMs > 0
            && !voiceProbeTracker.IsResponsive(now, VoiceProbeTimeoutMilliseconds)
            && now - voiceHandshakeAcceptedMs >= VoiceProbeTimeoutMilliseconds
            && controlChannel?.Connected == true
            && now - lastHelloSentMs >= 3_000)
        {
            SendHello();
            return;
        }
        if (!voiceHandshakeAccepted
            || voiceChannel?.Connected != true
            || now - lastVoiceProbeSentMs < VoiceProbeIntervalMilliseconds)
        {
            return;
        }

        if (nextVoiceProbeNonce == int.MaxValue)
        {
            nextVoiceProbeNonce = 1;
        }
        int nonce = nextVoiceProbeNonce++;
        lastVoiceProbeSentMs = now;
        voiceProbeTracker.MarkSent(nonce, now);
        voiceChannel.SendPacket(new VoicePingPacket
        {
            ConnectionEpoch = connectionEpoch,
            Nonce = nonce
        });
    }

    private void TryRecoverCapture()
    {
        long now = capi.World.ElapsedMilliseconds;
        if (capture?.IsAvailable == true
            || now - lastCaptureRecoveryAttemptMs < CaptureRecoveryIntervalMilliseconds)
        {
            return;
        }

        lastCaptureRecoveryAttemptMs = now;
        capture?.Dispose();
        OpenAlCaptureService replacement = new(capi, config);
        capture = replacement;
        if (!replacement.Initialize(logFailure: false))
        {
            return;
        }

        captureWarningShown = false;
        lastPressed = false;
        lastSpeaking = false;
        lastMicLevel = 0f;
        SendState(force: true);
        hud?.Refresh();
        capi.ShowChatMessage(SVCLang.Get("chat-device-recovered", string.IsNullOrWhiteSpace(config.InputDeviceName) ? SVCLang.Get("default-microphone") : config.InputDeviceName));
    }

    private void OnPlaybackTick(float dt)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        playback?.Update(serverConfig);
    }

    private void CaptureAndSend()
    {
        DrainCapturedFrames();
    }

    private bool IsPushToTalkPressed()
    {
        HotKey? hotKey = capi.Input.GetHotKeyByCode(VoiceConstants.PushToTalkHotKey);
        int keyCode = hotKey?.CurrentMapping?.KeyCode ?? (int)GlKeys.N;
        if (capi.Input.KeyboardKeyState[(int)GlKeys.LControl]
            || capi.Input.KeyboardKeyState[(int)GlKeys.RControl]
            || capi.Input.KeyboardKeyState[(int)GlKeys.AltLeft]
            || capi.Input.KeyboardKeyState[(int)GlKeys.AltRight]
            || capi.Input.KeyboardKeyState[(int)GlKeys.LShift]
            || capi.Input.KeyboardKeyState[(int)GlKeys.RShift])
        {
            return false;
        }

        return keyCode >= 0
            && keyCode < capi.Input.KeyboardKeyState.Length
            && capi.Input.KeyboardKeyState[keyCode];
    }

    private void CycleMode()
    {
        mode = mode switch
        {
            VoiceMode.Whisper => VoiceMode.Talk,
            VoiceMode.Talk => serverConfig.AllowShout ? VoiceMode.Shout : VoiceMode.Whisper,
            _ => serverConfig.AllowWhisper ? VoiceMode.Whisper : VoiceMode.Talk
        };

        if (mode == VoiceMode.Whisper && !serverConfig.AllowWhisper)
        {
            mode = VoiceMode.Talk;
        }

        if (mode == VoiceMode.Shout && !serverConfig.AllowShout)
        {
            mode = VoiceMode.Talk;
        }

        capi.ShowChatMessage(SVCLang.Get("chat-switched-mode", FormatMode(mode)));
        SendState(force: true);
        hud?.Refresh();
    }

    private void SetMuted(string playerUid, bool muted)
    {
        if (muted)
        {
            if (!config.MutedPlayerUids.Contains(playerUid))
            {
                config.MutedPlayerUids.Add(playerUid);
            }
        }
        else
        {
            config.MutedPlayerUids.Remove(playerUid);
        }

        SaveConfig();
        if (controlChannel?.Connected == true)
        {
            controlChannel.SendPacket(new MutePlayerPacket { PlayerUid = playerUid, Muted = muted });
        }
    }

    private void SendState(bool force)
    {
        if (controlChannel?.Connected != true)
        {
            return;
        }
        long now = capi.World.ElapsedMilliseconds;
        if (!force && now - lastStateSentMs < 1000)
        {
            return;
        }

        lastStateSentMs = now;
        controlChannel.SendPacket(new ClientVoiceStatePacket
        {
            Mode = mode,
            LocalMuted = localMuted,
            GlobalMuted = globalMuted,
            IsSpeaking = lastSpeaking
        });
    }

    private void SyncMutedPlayersToServer()
    {
        if (controlChannel?.Connected != true)
        {
            return;
        }

        foreach (string uid in config.MutedPlayerUids)
        {
            controlChannel.SendPacket(new MutePlayerPacket { PlayerUid = uid, Muted = true });
        }
    }

    private void ReinitializeCapture()
    {
        lastCaptureRecoveryAttemptMs = capi.World.ElapsedMilliseconds;
        capture?.Dispose();
        capture = new OpenAlCaptureService(capi, config);
        capture.Initialize();
        captureWarningShown = false;
        lastPressed = false;
        lastSpeaking = false;
        lastMicLevel = 0f;
        lastRemoteVoiceLevel = 0f;
        SendState(force: true);
        hud?.Refresh();
        capi.ShowChatMessage(SVCLang.Get("chat-device-switched", string.IsNullOrWhiteSpace(config.InputDeviceName) ? SVCLang.Get("default-microphone") : config.InputDeviceName));
    }

    private void DrainCapturedFrames()
    {
        int processedFrames = 0;
        bool hadFrame = false;
        float peakMicLevel = 0f;

        while (processedFrames < MaxCaptureFramesPerTick && capture?.TryReadFrame(captureBuffer) == true)
        {
            hadFrame = true;
            processedFrames++;

            VoiceFrameStats stats = capturePreprocessor.Process(captureBuffer, config.MicGain, config.NoiseGate);
            if (!stats.Active)
            {
                continue;
            }

            peakMicLevel = Math.Max(peakMicLevel, NormalizeVoiceLevel(stats.Rms, mode));
            lastVoiceLevelMs = capi.World.ElapsedMilliseconds;

            byte[] payload = voiceEncoder?.Encode(captureBuffer) ?? Array.Empty<byte>();
            if (payload.Length == 0)
            {
                continue;
            }
            if (payload.Length + 64 > VoiceConstants.MaxUdpPacketBytes)
            {
                capi.Logger.Warning("SimpleVoiceChat: encoded voice frame too large ({0} bytes), skipping.", payload.Length);
                continue;
            }

            SendCapturedFrame(payload, stats);
        }

        if (hadFrame)
        {
            lastMicLevel = peakMicLevel;
        }
    }

    private void SendCapturedFrame(byte[] payload, VoiceFrameStats stats)
    {
        if (!voiceHandshakeAccepted || voiceEncoder == null)
        {
            return;
        }

        VoiceTransmitTarget transmitTarget = ResolveTransmitTarget(config.TransmitTarget, config.SelectedChannelId);
        if (transmitTarget == VoiceTransmitTarget.SelectedChannel
            && string.IsNullOrEmpty(config.SelectedChannelId))
        {
            return;
        }

        voiceChannel?.SendPacket(new VoiceFrameV3Packet
        {
            ConnectionEpoch = connectionEpoch,
            SessionId = sessionId,
            Sequence = sequence++,
            Mode = mode,
            Target = transmitTarget,
            ChannelId = config.SelectedChannelId,
            Level = (byte)Math.Clamp((int)Math.Round(stats.Rms * byte.MaxValue), 0, byte.MaxValue),
            Flags = 0,
            Payload = payload
        });
    }

    internal static VoiceTransmitTarget ResolveTransmitTarget(VoiceTransmitTarget configuredTarget, string? selectedChannelId)
    {
        return configuredTarget == VoiceTransmitTarget.ProximityAndChannel
            && string.IsNullOrEmpty(selectedChannelId)
                ? VoiceTransmitTarget.Proximity
                : configuredTarget;
    }

    private void BeginVoiceSession()
    {
        sessionId = NextSessionId();
        sequence = 0;
        voiceEncoder?.Reset();
        capturePreprocessor.Reset();
    }

    private int NextSessionId()
    {
        if (nextSessionId == int.MaxValue)
        {
            nextSessionId = 1;
        }

        return nextSessionId++;
    }

    private VoiceHudSnapshot BuildHudSnapshot()
    {
        bool captureAvailable = capture?.IsAvailable == true;
        VoiceHudIconState iconState = GetHudIconState(captureAvailable);
        bool microphoneEnabled = iconState is VoiceHudIconState.Whispering or VoiceHudIconState.Talking;
        bool udpResponsive = voiceProbeTracker.IsResponsive(capi.World.ElapsedMilliseconds, VoiceProbeTimeoutMilliseconds);
        string detail = udpResponsive ? SVCLang.Get("hud-detail-udp-ok") : SVCLang.Get("hud-detail-udp-wait");
        if (!captureAvailable)
        {
            detail = SVCLang.Get("hud-detail-mic-unavailable");
        }

        float voiceLevel = Math.Max(lastMicLevel, lastRemoteVoiceLevel);
        if (voiceLevel < 0.015f)
        {
            voiceLevel = 0f;
        }

        return new VoiceHudSnapshot(
            microphoneEnabled,
            iconState,
            voiceLevel > 0f,
            voiceLevel,
            BuildHudStatus(captureAvailable),
            $"{FormatMode(mode)} | {BuildTransmitTargetLabel()}",
            detail,
            channelHudMembers);
    }

    private VoiceHudIconState GetHudIconState(bool captureAvailable)
    {
        if (!serverConfig.Enabled || globalMuted || !voiceHandshakeAccepted)
        {
            return VoiceHudIconState.VoiceDisabled;
        }

        long now = capi.World.ElapsedMilliseconds;
        if (transmitBlockedUntilMs > 0 && transmitBlockedUntilMs <= now)
        {
            transmitBlockedUntilMs = 0;
        }

        bool sendsToChannel = config.TransmitTarget is VoiceTransmitTarget.SelectedChannel or VoiceTransmitTarget.ProximityAndChannel
            && !string.IsNullOrEmpty(config.SelectedChannelId);
        if (localMuted
            || !captureAvailable
            || serverTransmitBlocked
            || transmitBlockedUntilMs > now
            || (sendsToChannel && channelTransmitBlocked))
        {
            return VoiceHudIconState.Muted;
        }

        return mode == VoiceMode.Whisper
            ? VoiceHudIconState.Whispering
            : VoiceHudIconState.Talking;
    }

    private float NormalizeVoiceLevel(float rms, VoiceMode voiceMode)
    {
        float baseline = Math.Max(config.NoiseGate * 3f, 0.025f);
        float raw = Math.Clamp((rms - baseline) / 0.22f, 0f, 1f);
        return Math.Clamp(raw * ModeLevelMultiplier(voiceMode), 0f, 1f);
    }

    private float NormalizeRemoteVoiceLevel(float rms, VoiceMode mode, bool channelRelay, float x, float y, float z)
    {
        if (channelRelay)
        {
            return Math.Clamp(NormalizeVoiceLevel(rms, mode) * 0.82f, 0f, 1f);
        }

        Vec3d listener = capi.World.Player.Entity.Pos.XYZ;
        double distance = listener.DistanceTo(x, y, z);
        float range = Math.Min(serverConfig.GetRange(mode), serverConfig.MaxRange);
        float distanceGain = EstimateOpenAlDistanceGain(distance, range);
        return Math.Clamp(NormalizeVoiceLevel(rms, mode) * distanceGain, 0f, 1f);
    }

    private static float EstimateOpenAlDistanceGain(double distance, float range)
    {
        float referenceDistance = (float)Math.Max(3.0, Math.Pow(Math.Max(range, 1f), 0.5) - 2.0);
        if (distance <= referenceDistance)
        {
            return 1f;
        }

        float rolloff = range > 1f ? (float)(0.0 - Math.Log(0.01) / Math.Log(range)) : 1f;
        return (float)Math.Clamp(Math.Pow(distance / referenceDistance, -rolloff), 0f, 1f);
    }

    private static float ModeLevelMultiplier(VoiceMode voiceMode)
    {
        return voiceMode switch
        {
            VoiceMode.Whisper => 0.42f,
            VoiceMode.Shout => 1f,
            _ => 0.72f
        };
    }

    private string BuildHudStatus(bool captureAvailable)
    {
        if (!serverConfig.Enabled || globalMuted || !voiceHandshakeAccepted)
        {
            return SVCLang.Get("hud-status-voice-off");
        }
        if (!captureAvailable)
        {
            return SVCLang.Get("hud-status-mic-unavailable");
        }
        if (localMuted)
        {
            return SVCLang.Get("hud-status-mic-muted");
        }
        if (lastSpeaking)
        {
            return toggleTalkEnabled ? SVCLang.Get("hud-status-always-talking") : SVCLang.Get("hud-status-speaking");
        }
        return toggleTalkEnabled ? SVCLang.Get("hud-status-always-standby") : SVCLang.Get("hud-status-mic-ready");
    }

    private bool ShouldShowHud()
    {
        return config.ShowMicrophoneHud && serverConfig.EnableHudIndicators;
    }

    private string BuildTransmitTargetLabel()
    {
        ChannelInfoPacket? selected = channelInfos.FirstOrDefault(channel => channel.ChannelId == config.SelectedChannelId);
        string channelName = selected == null ? SVCLang.Get("channel-none") : Truncate(selected.Name, 14);
        return config.TransmitTarget switch
        {
            VoiceTransmitTarget.SelectedChannel => SVCLang.Get("hud-transmit-channel", channelName),
            VoiceTransmitTarget.ProximityAndChannel when selected != null => SVCLang.Get("hud-transmit-both", channelName),
            _ => SVCLang.Get("hud-transmit-proximity")
        };
    }

    private static string Truncate(string value, int maximumLength)
    {
        return value.Length <= maximumLength ? value : value[..Math.Max(1, maximumLength - 3)] + "...";
    }

    private string BuildSettingsSummary()
    {
        string ptt = FormatHotkey(VoiceConstants.PushToTalkHotKey, "N");
        string toggleTalk = FormatHotkey(VoiceConstants.ToggleTalkHotKey, "Alt + N");
        string cycle = FormatHotkey(VoiceConstants.ModeCycleHotKey, "[");
        string cycleAlt = FormatHotkey(VoiceConstants.ModeCycleAltHotKey, "]");
        string localMute = FormatHotkey(VoiceConstants.LocalMuteHotKey, "Ctrl + -");
        string globalMute = FormatHotkey(VoiceConstants.GlobalMuteHotKey, ";");
        string settingsLine = SVCLang.Get("summary-line-open-settings", FormatHotkey(VoiceConstants.SettingsHotKey, "'"));
        return
            $"{SVCLang.Get("summary-line-voice-mode", FormatMode(mode))}\n" +
            $"{SVCLang.Get("summary-line-voice-master", serverConfig.Enabled && !globalMuted ? SVCLang.Get("state-on") : SVCLang.Get("state-off"))}\n" +
            $"{SVCLang.Get("summary-line-mic", capture?.IsAvailable == true ? (localMuted ? SVCLang.Get("state-muted") : SVCLang.Get("state-ready")) : SVCLang.Get("state-unavailable"))}\n" +
            $"{SVCLang.Get("summary-line-playback-volume", (int)(config.OutputVolume * 100))}\n" +
            $"{SVCLang.Get("summary-line-push-to-talk", ptt)}\n" +
            $"{SVCLang.Get("summary-line-toggle-talk", toggleTalk, toggleTalkEnabled ? SVCLang.Get("state-on") : SVCLang.Get("state-off"))}\n" +
            $"{SVCLang.Get("summary-line-cycle-mode", cycle, cycleAlt)}\n" +
            $"{SVCLang.Get("summary-line-local-global", localMute, globalMute)}\n" +
            $"{settingsLine}\n" +
            $"{playback?.BuildDebugStatus() ?? SVCLang.Get("summary-playback-uninitialized")}\n" +
            $"{VoiceEnvironment.BuildDebugSummary(capi, config, serverConfig)}\n" +
            $"{SVCLang.Get("summary-line-commands")}";
    }

    private static string FormatMode(VoiceMode voiceMode)
    {
        return voiceMode switch
        {
            VoiceMode.Whisper => SVCLang.Get("mode-whisper"),
            VoiceMode.Shout => SVCLang.Get("mode-shout"),
            _ => SVCLang.Get("mode-talk")
        };
    }

    private string FormatHotkey(string hotkeyCode, string fallback)
    {
        string value = capi.Input.GetHotKeyByCode(hotkeyCode)?.CurrentMapping?.ToString() ?? fallback;
        return value == "Quote" ? "'" : value;
    }

    private void SaveConfig()
    {
        config.StoreActiveServerProfile();
        config.Normalize();
        capi.StoreModConfig(config, VoiceConstants.ClientConfigFileName);
    }

    private void ActivateCurrentServerProfile(string? serverIdentifier = null)
    {
        string identifier = string.IsNullOrWhiteSpace(serverIdentifier)
            ? capi.World.SavegameIdentifier
            : serverIdentifier;
        if (string.IsNullOrWhiteSpace(identifier)
            || config.ActiveServerId == identifier)
        {
            return;
        }

        bool previousAdaptiveJitter = config.AdaptiveJitterBuffer;
        if (!config.ActivateServer(identifier))
        {
            return;
        }

        if (serverConfig.ForceImmersive)
        {
            config.EnableOcclusionEffects = true;
        }
        SaveConfig();

        if (previousAdaptiveJitter != config.AdaptiveJitterBuffer)
        {
            playback?.SetAdaptiveJitter(config.AdaptiveJitterBuffer);
        }
        hud?.Refresh();
        settingsDialog?.RefreshData();
    }

    public void Dispose()
    {
        if (!lifecycle.TryDispose())
        {
            return;
        }
        capi.Event.KeyUp -= OnKeyUp;
        if (fastTickListenerId != 0)
        {
            capi.Event.UnregisterGameTickListener(fastTickListenerId);
            fastTickListenerId = 0;
        }
        if (playbackTickListenerId != 0)
        {
            capi.Event.UnregisterGameTickListener(playbackTickListenerId);
            playbackTickListenerId = 0;
        }
        if (slowTickListenerId != 0)
        {
            capi.Event.UnregisterGameTickListener(slowTickListenerId);
            slowTickListenerId = 0;
        }
        capture?.Dispose();
        capture = null;
        voiceEncoder?.Dispose();
        voiceEncoder = null;
        playback?.Dispose();
        playback = null;
        hud?.TryClose();
        hud?.Dispose();
        hud = null;
        settingsDialog?.TryClose();
        settingsDialog?.Dispose();
        settingsDialog = null;
        inviteDialog?.Dismiss();
        inviteDialog?.Dispose();
        inviteDialog = null;
        controlChannel = null;
        voiceChannel = null;
        channelInfos = Array.Empty<ChannelInfoPacket>();
        memberPagesByChannel.Clear();
        activeChannelTalkerHashesByChannel.Clear();
        channelHudMembers = Array.Empty<VoiceHudChannelMember>();
    }

}
