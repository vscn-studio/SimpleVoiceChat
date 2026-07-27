using SimpleVoiceChat.Audio;
using SimpleVoiceChat.Config;
using SimpleVoiceChat.Gui;
using SimpleVoiceChat.Networking;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace SimpleVoiceChat;

public sealed class ClientVoiceController : IDisposable
{
    private const int DebugRecordingMilliseconds = 3000;
    private const int SettingsMemberPageSize = 8;
    private const int MaxCaptureFramesPerTick = 8;
    private const long InitialDebugPlaybackEntityId = -900001;
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
    private VoiceHudSquadMember[] squadHudMembers = Array.Empty<VoiceHudSquadMember>();
    private ChannelInfoPacket[] channelInfos = Array.Empty<ChannelInfoPacket>();
    private readonly Dictionary<string, ChannelMemberPagePacket> memberPagesByChannel = new(StringComparer.Ordinal);
    private string[] pendingInviteNames = Array.Empty<string>();
    private readonly Dictionary<string, HashSet<int>> activeChannelTalkerHashesByChannel = new(StringComparer.Ordinal);
    private string lastDiagnostics = string.Empty;
    private readonly List<DebugVoiceFrame> debugRecordingFrames = new();
    private bool debugRecording;
    private bool debugCaptureStartedByTool;
    private bool debugPlaybackActive;
    private long debugRecordingStartMs;
    private long debugRecordingEndMs;
    private long debugPlaybackStartMs;
    private long debugPlaybackEntityId = InitialDebugPlaybackEntityId;
    private int debugPlaybackSessionId;
    private int debugPlaybackIndex;
    private ushort debugPlaybackSequence;
    private int nextSessionId = 1;
    private int nextVoiceProbeNonce = 1;
    private long lastVoiceProbeSentMs;
    private long voiceHandshakeAcceptedMs;
    private long lastCaptureRecoveryAttemptMs;

    public ClientVoiceController(ICoreClientAPI capi, SimpleVoiceChatClientConfig config)
    {
        this.capi = capi;
        this.config = config;
        sessionId = NextSessionId();
    }

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
        settingsDialog = new VoiceSettingsDialog(
            capi,
            config,
            BuildSettingsWindowSummary,
            BuildSquadStatusSummary,
            SaveConfig,
            () => hud?.Refresh(),
            ReinitializeCapture,
            StartDebugRecording,
            PlayDebugRecording,
            LeaveSquadFromWindow,
            DisbandSquadFromWindow,
            RequestSquadStatus,
            BuildChannelOptions,
            SelectChannel,
            BuildDiagnosticsSummary,
            AcceptPendingInvite,
            DeclinePendingInvite,
            () => serverConfig.ForceImmersive,
            BuildPlayerOptions,
            GetPlayerVolumePercent,
            SetPlayerVolume,
            SetPlayerMuted,
             BuildMemberPage,
             ManageSelectedChannel,
             enabled => playback?.SetAdaptiveJitter(enabled),
             () => localMuted,
             SetLocalMuted,
             () => globalMuted,
             SetGlobalMuted,
             () => toggleTalkEnabled,
             () => serverConfig.AllowContinuousTalk,
             SetContinuousTalk,
             () => hasServerControl);
        capi.Gui.RegisterDialog(settingsDialog);

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
            .RegisterMessageType<SquadBindPacket>()
            .RegisterMessageType<AdminVoiceControlPacket>()
            .RegisterMessageType<SquadHudPacket>()
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
            .SetMessageHandler<SquadHudPacket>(OnSquadHud)
            .SetMessageHandler<VoiceWelcomePacket>(OnVoiceWelcome)
            .SetMessageHandler<ChannelSnapshotPacket>(OnChannelSnapshot)
            .SetMessageHandler<ChannelMemberDeltaPacket>(OnChannelMemberDelta)
            .SetMessageHandler<ChannelMemberPagePacket>(OnChannelMemberPage)
            .SetMessageHandler<TalkerStateDeltaPacket>(OnTalkerStateDelta)
            .SetMessageHandler<VoiceFeedbackPacket>(OnVoiceFeedback)
            .SetMessageHandler<VoiceDiagnosticsPacket>(OnVoiceDiagnostics);

        voiceChannel = capi.Network.RegisterUdpChannel(VoiceConstants.VoiceChannelName)
            .RegisterMessageType<VoiceFramePacket>()
            .RegisterMessageType<VoiceFrameV2Packet>()
            .RegisterMessageType<VoiceRelayFrameV2Packet>()
            .RegisterMessageType<VoicePingPacket>()
            .RegisterMessageType<VoicePongPacket>()
            .SetMessageHandler<VoiceFramePacket>(OnVoiceFrame)
            .SetMessageHandler<VoiceRelayFrameV2Packet>(OnVoiceRelayFrameV2)
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

            case "bind":
                {
                    IPlayer? target = GetSelectedPlayer();
                    if (target == null)
                    {
                        return TextCommandResult.Error(SVCLang.Get("command-bind-face-player"));
                    }

                    SendChannelCommand("invite", targetPlayerUid: target.PlayerUID);
                    return TextCommandResult.Success(SVCLang.Get("command-request-bind-squad", target.PlayerName));
                }

            case "unbind":
                SendChannelCommand("leave", channelId: config.SelectedChannelId);
                return TextCommandResult.Success(SVCLang.Get("command-request-leave-squad"));

            case "squad":
                SendChannelCommand("request");
                return TextCommandResult.Success(SVCLang.Get("command-request-squad-status"));

            case "accept":
                SendChannelCommand("accept");
                return TextCommandResult.Success(SVCLang.Get("command-invite-accepted"));

            case "decline":
                SendChannelCommand("decline");
                return TextCommandResult.Success(SVCLang.Get("command-invite-declined"));

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
            ModVersion = "0.2.0",
            SupportedCodecs = new[] { VoiceProtocol.CodecOpus, VoiceProtocol.CodecImaAdpcm },
            Capabilities = (int)(VoiceCapability.ProtocolV2
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
            && packet.ProtocolVersion == VoiceProtocol.CurrentVersion
            && packet.Codec is VoiceProtocol.CodecImaAdpcm or VoiceProtocol.CodecOpus;
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
            SendState(force: true);
            SyncMutedPlayersToServer();
        }
        hud?.Refresh();
        settingsDialog?.RefreshConfiguration();
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
        pendingInviteNames = packet.PendingInviteNames ?? Array.Empty<string>();
        string selected = packet.SelectedChannelId ?? string.Empty;
        if (!string.IsNullOrEmpty(selected) && channelInfos.Any(channel => channel.ChannelId == selected))
        {
            config.SelectedChannelId = selected;
        }
        else if (!channelInfos.Any(channel => channel.ChannelId == config.SelectedChannelId))
        {
            config.SelectedChannelId = channelInfos.FirstOrDefault(channel => channel.Kind == VoiceChannelKind.Squad)?.ChannelId ?? string.Empty;
        }

        UpdateSquadHudMembers();
        SaveConfig();
        hud?.Refresh();
        settingsDialog?.RefreshChannelData();
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
        UpdateSquadHudMembers();
        hud?.Refresh();
        settingsDialog?.RefreshChannelData();
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
        settingsDialog?.RefreshChannelData();
    }

    private void OnTalkerStateDelta(TalkerStateDeltaPacket packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        if (!channelInfos.Any(info => info.ChannelId == packet.ChannelId))
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

        UpdateSquadHudMembers();
        hud?.Refresh();
    }

    private void OnVoiceFeedback(VoiceFeedbackPacket packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        string message = LocalizeFeedback(packet);
        if (!string.IsNullOrWhiteSpace(message))
        {
            capi.ShowChatMessage($"Simple Voice Chat: {message}");
        }
        settingsDialog?.RefreshStatusTexts();
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
        settingsDialog?.RefreshStatusTexts();
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
            settingsDialog?.RefreshStatusTexts();
            hud?.Refresh();
        }
    }

    private void SendChannelCommand(
        string action,
        string channelId = "",
        string targetPlayerUid = "",
        string name = "",
        VoiceChannelKind kind = VoiceChannelKind.Squad,
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
            Kind = kind,
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

    private void UpdateSquadHudMembers()
    {
        ChannelInfoPacket? channel = channelInfos.FirstOrDefault(info => info.ChannelId == config.SelectedChannelId)
            ?? channelInfos.FirstOrDefault(info => info.Kind == VoiceChannelKind.Squad);
        if (channel == null)
        {
            squadHudMembers = Array.Empty<VoiceHudSquadMember>();
            return;
        }
        activeChannelTalkerHashesByChannel.TryGetValue(channel.ChannelId, out HashSet<int>? activeTalkers);

        squadHudMembers = (channel.Members ?? Array.Empty<ChannelMemberPacket>())
            .Where(member => member.PlayerUid != capi.World.Player.PlayerUID)
            .Take(12)
            .Select(member => new VoiceHudSquadMember(
                member.PlayerName,
                activeTalkers?.Contains(VoiceMath.StableUidHash(member.PlayerUid)) == true))
            .ToArray();
    }

    private VoiceSettingsChannelOption[] BuildChannelOptions()
    {
        return channelInfos
            .OrderBy(info => info.Kind)
            .ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
            .Select(info => new VoiceSettingsChannelOption(info.ChannelId, info.Name, info.LocalRole, info.Kind, info.ExternallyManaged))
            .ToArray();
    }

    private VoiceSettingsPlayerOption[] BuildPlayerOptions()
    {
        IEnumerable<VoiceSettingsPlayerOption> online = capi.World.AllOnlinePlayers
            .Where(player => player.PlayerUID != capi.World.Player.PlayerUID)
            .Select(player => new VoiceSettingsPlayerOption(player.PlayerUID, player.PlayerName));
        IEnumerable<VoiceSettingsPlayerOption> members = channelInfos
            .Where(channel => channel.ChannelId == config.SelectedChannelId)
            .SelectMany(channel => channel.Members ?? Array.Empty<ChannelMemberPacket>())
            .Where(member => member.PlayerUid != capi.World.Player.PlayerUID)
            .Select(member => new VoiceSettingsPlayerOption(member.PlayerUid, member.PlayerName));
        IEnumerable<VoiceSettingsPlayerOption> currentPage = memberPagesByChannel.TryGetValue(config.SelectedChannelId, out ChannelMemberPagePacket? page)
            ? page.Members
                .Where(member => member.PlayerUid != capi.World.Player.PlayerUID)
                .Select(member => new VoiceSettingsPlayerOption(member.PlayerUid, member.PlayerName))
            : Array.Empty<VoiceSettingsPlayerOption>();
        return online.Concat(members).Concat(currentPage)
            .GroupBy(player => player.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
    }

    private int GetPlayerVolumePercent(string playerUid)
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
            cached.Members.Select(member => new VoiceSettingsMemberOption(member.PlayerUid, member.PlayerName, member.Role)).ToArray());
    }

    private void ManageSelectedChannel(
        string action,
        string channelId,
        string targetUid = "",
        string name = "",
        VoiceChannelRole role = VoiceChannelRole.Member)
    {
        if (action.StartsWith("create-", StringComparison.Ordinal))
        {
            string kindText = action["create-".Length..];
            if (Enum.TryParse(kindText, true, out VoiceChannelKind kind)
                && kind is >= VoiceChannelKind.Civilization and <= VoiceChannelKind.Radio)
            {
                string channelName = string.IsNullOrWhiteSpace(name)
                    ? $"{FormatChannelKind(kind)} - {capi.World.Player.PlayerName}"
                    : name.Trim();
                SendChannelCommand("create", name: channelName, kind: kind);
            }
            return;
        }

        if (action == "rename")
        {
            SendChannelCommand("rename", channelId: channelId, name: name.Trim());
            return;
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
        SaveConfig();
        SendChannelCommand("select", channelId: channelId);
        UpdateSquadHudMembers();
        hud?.Refresh();
    }

    private bool AcceptPendingInvite()
    {
        SendChannelCommand("accept");
        return true;
    }

    private bool DeclinePendingInvite()
    {
        SendChannelCommand("decline");
        return true;
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

    private void OnSquadHud(SquadHudPacket packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        int count = Math.Min(packet.MemberNames.Length, packet.Speaking.Length);
        VoiceHudSquadMember[] members = new VoiceHudSquadMember[count];
        for (int i = 0; i < count; i++)
        {
            members[i] = new VoiceHudSquadMember(packet.MemberNames[i], packet.Speaking[i]);
        }

        squadHudMembers = members;
        hud?.Refresh();
        settingsDialog?.RefreshStatusTexts();
    }

    private void OnVoiceFrame(VoiceFramePacket packet)
    {
        if (!lifecycle.IsStarted
            || !serverConfig.Enabled
            || globalMuted
            || packet.Payload == null
            || packet.SenderEntityId == capi.World.Player.Entity.EntityId)
        {
            return;
        }

        IPlayer? sender = capi.World.AllOnlinePlayers.FirstOrDefault(p => p.Entity?.EntityId == packet.SenderEntityId);
        if (sender != null && config.MutedPlayerUids.Contains(sender.PlayerUID))
        {
            return;
        }

        playback?.Enqueue(packet, serverConfig, GetPlaybackGain(sender, packet.SquadRelay));
        lastRemoteVoiceLevel = Math.Max(lastRemoteVoiceLevel, NormalizeRemoteVoiceLevel(packet));
        lastVoiceLevelMs = capi.World.ElapsedMilliseconds;
        hud?.Refresh();
    }

    private void OnVoiceRelayFrameV2(VoiceRelayFrameV2Packet packet)
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
        VoiceFramePacket levelPacket = new()
        {
            SenderEntityId = packet.SenderEntityId,
            Mode = packet.Mode,
            Rms = packet.Level / 255f,
            X = packet.X,
            Y = packet.Y,
            Z = packet.Z,
            SquadRelay = channelRelay
        };
        lastRemoteVoiceLevel = Math.Max(lastRemoteVoiceLevel, NormalizeRemoteVoiceLevel(levelPacket));
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

        if (debugRecording && !canSpeak)
        {
            CaptureDebugFrameOnly();
        }

        if (!canSpeak)
        {
            lastMicLevel = 0f;
        }

        lastPressed = canSpeak;
        UpdateDebugRecording();
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
        SendState(force: false);
        if (!lastSpeaking)
        {
            lastMicLevel = 0f;
        }
        lastRemoteVoiceLevel *= 0.72f;
        if (lastRemoteVoiceLevel < 0.01f)
        {
            lastRemoteVoiceLevel = 0f;
        }
        if (capi.World.ElapsedMilliseconds - lastVoiceLevelMs > 350)
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
        SendState(force: true);
        settingsDialog?.RefreshStatusTexts();
        hud?.Refresh();
        capi.ShowChatMessage(SVCLang.Get("chat-device-recovered", string.IsNullOrWhiteSpace(config.InputDeviceName) ? SVCLang.Get("default-microphone") : config.InputDeviceName));
    }

    private void OnPlaybackTick(float dt)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        UpdateDebugPlayback();
        playback?.Update(serverConfig);
    }

    private void CaptureAndSend()
    {
        DrainCapturedFrames(sendFrames: true);
    }

    private void CaptureDebugFrameOnly()
    {
        if (!EnsureDebugCaptureRunning())
        {
            return;
        }

        DrainCapturedFrames(sendFrames: false);
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

    private bool LeaveSquadFromWindow()
    {
        if (controlChannel == null)
        {
            capi.ShowChatMessage(SVCLang.Get("chat-control-not-connected-leave"));
            return true;
        }

        SendChannelCommand("leave", channelId: config.SelectedChannelId);
        squadHudMembers = Array.Empty<VoiceHudSquadMember>();
        hud?.Refresh();
        settingsDialog?.RefreshStatusTexts();
        capi.ShowChatMessage(SVCLang.Get("chat-requested-leave-squad"));
        return true;
    }

    private bool DisbandSquadFromWindow()
    {
        if (controlChannel == null)
        {
            capi.ShowChatMessage(SVCLang.Get("chat-control-not-connected-disband"));
            return true;
        }

        SendChannelCommand("disband", channelId: config.SelectedChannelId);
        squadHudMembers = Array.Empty<VoiceHudSquadMember>();
        hud?.Refresh();
        settingsDialog?.RefreshStatusTexts();
        capi.ShowChatMessage(SVCLang.Get("chat-requested-disband-squad"));
        return true;
    }

    private void RequestSquadStatus()
    {
        SendChannelCommand("request");
        SendChannelCommand("diagnostics");
    }

    private bool StartDebugRecording()
    {
        if (capture?.IsAvailable != true)
        {
            capi.ShowChatMessage(SVCLang.Get("chat-debug-recording-unavailable", capture?.FailureReason ?? string.Empty));
            return true;
        }

        debugPlaybackActive = false;
        debugRecordingFrames.Clear();
        debugRecording = true;
        debugCaptureStartedByTool = !lastPressed;
        debugRecordingStartMs = capi.World.ElapsedMilliseconds;
        debugRecordingEndMs = debugRecordingStartMs + DebugRecordingMilliseconds;
        if (debugCaptureStartedByTool)
        {
            capture.Start();
        }

        capi.ShowChatMessage(SVCLang.Get("chat-debug-recording-started"));
        return true;
    }

    private bool PlayDebugRecording()
    {
        if (debugRecording)
        {
            capi.ShowChatMessage(SVCLang.Get("chat-debug-recording-busy"));
            return true;
        }

        if (debugRecordingFrames.Count == 0)
        {
            capi.ShowChatMessage(SVCLang.Get("chat-debug-recording-empty"));
            return true;
        }

        if (playback == null)
        {
            capi.ShowChatMessage(SVCLang.Get("chat-debug-playback-uninitialized"));
            return true;
        }

        debugPlaybackActive = true;
        debugPlaybackIndex = 0;
        debugPlaybackSessionId = NextSessionId();
        debugPlaybackSequence = 0;
        debugPlaybackStartMs = capi.World.ElapsedMilliseconds;
        debugPlaybackEntityId--;
        if (debugPlaybackEntityId >= 0)
        {
            debugPlaybackEntityId = InitialDebugPlaybackEntityId;
        }

        capi.ShowChatMessage(SVCLang.Get("chat-debug-playback-started", debugRecordingFrames.Count));
        return true;
    }

    private bool EnsureDebugCaptureRunning()
    {
        if (capture?.IsAvailable != true)
        {
            return false;
        }

        if (!lastPressed && !debugCaptureStartedByTool)
        {
            debugCaptureStartedByTool = true;
            capture.Start();
        }

        return true;
    }

    private void RecordDebugFrame(byte[] payload, float rms, VoiceMode frameMode)
    {
        if (!debugRecording || payload.Length == 0)
        {
            return;
        }

        Vec3d pos = capi.World.Player.Entity.Pos.XYZ;
        Vec3f speakerPosition = new((float)pos.X, (float)pos.Y, (float)pos.Z);
        int offsetMs = (int)Math.Clamp(capi.World.ElapsedMilliseconds - debugRecordingStartMs, 0, DebugRecordingMilliseconds);
        debugRecordingFrames.Add(new DebugVoiceFrame(payload, rms, frameMode, offsetMs, speakerPosition));
    }

    private void UpdateDebugRecording()
    {
        if (!debugRecording || capi.World.ElapsedMilliseconds < debugRecordingEndMs)
        {
            return;
        }

        debugRecording = false;
        if (debugCaptureStartedByTool && !lastPressed)
        {
            capture?.Stop();
        }

        debugCaptureStartedByTool = false;
        string suffix = debugRecordingFrames.Count == 0
            ? SVCLang.Get("chat-debug-recording-finished-empty")
            : SVCLang.Get("chat-debug-recording-finished-frames", debugRecordingFrames.Count);
        capi.ShowChatMessage(SVCLang.Get("chat-debug-recording-finished", suffix));
    }

    private void UpdateDebugPlayback()
    {
        if (!debugPlaybackActive || playback == null)
        {
            return;
        }

        long elapsed = capi.World.ElapsedMilliseconds - debugPlaybackStartMs;
        while (debugPlaybackIndex < debugRecordingFrames.Count && debugRecordingFrames[debugPlaybackIndex].OffsetMilliseconds <= elapsed)
        {
            EnqueueDebugPlaybackFrame(debugRecordingFrames[debugPlaybackIndex]);
            debugPlaybackIndex++;
        }

        int lastOffset = debugRecordingFrames.Count == 0 ? 0 : debugRecordingFrames[^1].OffsetMilliseconds;
        if (debugPlaybackIndex >= debugRecordingFrames.Count && elapsed > lastOffset + 500)
        {
            debugPlaybackActive = false;
            capi.ShowChatMessage(SVCLang.Get("chat-debug-playback-finished"));
        }
    }

    private void EnqueueDebugPlaybackFrame(DebugVoiceFrame frame)
    {
        VoiceFramePacket packet = new()
        {
            SenderUidHash = VoiceMath.StableUidHash(capi.World.Player.PlayerUID + ":debug"),
            SenderEntityId = debugPlaybackEntityId,
            SessionId = debugPlaybackSessionId,
            Sequence = debugPlaybackSequence++,
            Mode = frame.Mode,
            Rms = frame.Rms,
            Flags = 0,
            Payload = frame.Payload,
            X = frame.Position.X,
            Y = frame.Position.Y,
            Z = frame.Position.Z
        };

        playback?.Enqueue(packet, serverConfig);
        lastRemoteVoiceLevel = Math.Max(lastRemoteVoiceLevel, NormalizeRemoteVoiceLevel(packet));
        lastVoiceLevelMs = capi.World.ElapsedMilliseconds;
        hud?.Refresh();
    }

    private void DrainCapturedFrames(bool sendFrames)
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

            if (debugRecording)
            {
                byte[] debugPayload = negotiatedCodec == VoiceProtocol.CodecImaAdpcm
                    ? payload
                    : ImaAdpcmCodec.Encode(captureBuffer);
                RecordDebugFrame(debugPayload, stats.Rms, mode);
            }
            if (sendFrames)
            {
                SendCapturedFrame(payload, stats);
            }
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

        voiceChannel?.SendPacket(new VoiceFrameV2Packet
        {
            ConnectionEpoch = connectionEpoch,
            SessionId = sessionId,
            Sequence = sequence++,
            Mode = mode,
            Target = config.TransmitTarget,
            ChannelId = config.SelectedChannelId,
            Level = (byte)Math.Clamp((int)Math.Round(stats.Rms * byte.MaxValue), 0, byte.MaxValue),
            Flags = 0,
            Payload = payload
        });
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
        bool micEnabled = !localMuted && !globalMuted && serverConfig.Enabled && voiceHandshakeAccepted && captureAvailable;
        string status = BuildHudStatus(captureAvailable);
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
            micEnabled,
            voiceLevel > 0f,
            voiceLevel,
            status,
            $"{FormatMode(mode)} | {BuildTransmitTargetLabel()}",
            detail,
            squadHudMembers);
    }

    private float NormalizeVoiceLevel(float rms, VoiceMode voiceMode)
    {
        float baseline = Math.Max(config.NoiseGate * 3f, 0.025f);
        float raw = Math.Clamp((rms - baseline) / 0.22f, 0f, 1f);
        return Math.Clamp(raw * ModeLevelMultiplier(voiceMode), 0f, 1f);
    }

    private float NormalizeRemoteVoiceLevel(VoiceFramePacket packet)
    {
        if (packet.SquadRelay)
        {
            return Math.Clamp(NormalizeVoiceLevel(packet.Rms, packet.Mode) * 0.82f, 0f, 1f);
        }

        Vec3d listener = capi.World.Player.Entity.Pos.XYZ;
        double distance = listener.DistanceTo(packet.X, packet.Y, packet.Z);
        float range = Math.Min(serverConfig.GetRange(packet.Mode), serverConfig.MaxRange);
        float distanceGain = EstimateOpenAlDistanceGain(distance, range);
        return Math.Clamp(NormalizeVoiceLevel(packet.Rms, packet.Mode) * distanceGain, 0f, 1f);
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
        return config.ShowMicrophoneHud
            && serverConfig.EnableHudIndicators;
    }

    private string BuildSettingsSummary()
    {
        string ptt = FormatHotkey(VoiceConstants.PushToTalkHotKey, "N");
        string toggleTalk = FormatHotkey(VoiceConstants.ToggleTalkHotKey, "Alt + N");
        string cycle = FormatHotkey(VoiceConstants.ModeCycleHotKey, "[");
        string cycleAlt = FormatHotkey(VoiceConstants.ModeCycleAltHotKey, "]");
        string localMute = FormatHotkey(VoiceConstants.LocalMuteHotKey, "Ctrl + -");
        string globalMute = FormatHotkey(VoiceConstants.GlobalMuteHotKey, ";");
        string settings = FormatHotkey(VoiceConstants.SettingsHotKey, "'");
        return
            $"{SVCLang.Get("summary-line-voice-mode", FormatMode(mode))}\n" +
            $"{SVCLang.Get("summary-line-voice-master", serverConfig.Enabled && !globalMuted ? SVCLang.Get("state-on") : SVCLang.Get("state-off"))}\n" +
            $"{SVCLang.Get("summary-line-mic", capture?.IsAvailable == true ? (localMuted ? SVCLang.Get("state-muted") : SVCLang.Get("state-ready")) : SVCLang.Get("state-unavailable"))}\n" +
            $"{SVCLang.Get("summary-line-playback-volume", (int)(config.OutputVolume * 100))}\n" +
            $"{SVCLang.Get("summary-line-push-to-talk", ptt)}\n" +
            $"{SVCLang.Get("summary-line-toggle-talk", toggleTalk, toggleTalkEnabled ? SVCLang.Get("state-on") : SVCLang.Get("state-off"))}\n" +
            $"{SVCLang.Get("summary-line-cycle-mode", cycle, cycleAlt)}\n" +
            $"{SVCLang.Get("summary-line-local-global", localMute, globalMute)}\n" +
            $"{SVCLang.Get("summary-line-open-settings", settings)}\n" +
            $"{SVCLang.Get("summary-line-debug-recording", BuildDebugRecordingStatus())}\n" +
            $"{playback?.BuildDebugStatus() ?? SVCLang.Get("summary-playback-uninitialized")}\n" +
            $"{VoiceEnvironment.BuildDebugSummary(capi, config, serverConfig)}\n" +
            $"{SVCLang.Get("summary-line-commands")}";
    }

    private string BuildSettingsWindowSummary()
    {
        string ptt = FormatHotkey(VoiceConstants.PushToTalkHotKey, "N");
        string toggleTalk = FormatHotkey(VoiceConstants.ToggleTalkHotKey, "Alt + N");
        string cycle = FormatHotkey(VoiceConstants.ModeCycleHotKey, "[");
        string cycleAlt = FormatHotkey(VoiceConstants.ModeCycleAltHotKey, "]");
        string localMute = FormatHotkey(VoiceConstants.LocalMuteHotKey, "Ctrl + -");
        string globalMute = FormatHotkey(VoiceConstants.GlobalMuteHotKey, ";");
        return
            $"{SVCLang.Get("summary-line-window-header", FormatMode(mode), serverConfig.Enabled && !globalMuted ? SVCLang.Get("state-on") : SVCLang.Get("state-off"))}\n" +
            $"{SVCLang.Get("summary-line-window-mic", capture?.IsAvailable == true ? (localMuted ? SVCLang.Get("state-muted") : SVCLang.Get("state-ready")) : SVCLang.Get("state-unavailable"))}\n" +
            $"{SVCLang.Get("summary-line-window-talk", ptt, toggleTalk, toggleTalkEnabled ? SVCLang.Get("state-on-short") : SVCLang.Get("state-off-short"))}\n" +
            $"{SVCLang.Get("summary-line-window-cycle", cycle, cycleAlt)}\n" +
            $"{SVCLang.Get("summary-line-window-local-global", localMute, globalMute)}\n" +
            $"{SVCLang.Get("summary-line-window-target", BuildTransmitTargetLabel())}\n" +
            $"{SVCLang.Get("summary-line-debug-recording", BuildDebugRecordingStatus())}\n" +
            VoiceEnvironment.BuildDebugSummary(capi, config, serverConfig);
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

    private string BuildSquadStatusSummary()
    {
        if (!serverConfig.Enabled)
        {
            return SVCLang.Get("squad-status-voice-off");
        }

        if (pendingInviteNames.Length > 0)
        {
            return SVCLang.Get("squad-status-invite", string.Join(", ", pendingInviteNames));
        }

        if (squadHudMembers.Length == 0)
        {
            return SVCLang.Get("squad-status-none");
        }

        string names = string.Join("、", squadHudMembers.Select(member => member.Name));
        return SVCLang.Get("squad-status-members", names);
    }

    private string BuildDebugRecordingStatus()
    {
        if (debugRecording)
        {
            float remaining = Math.Max(0, debugRecordingEndMs - capi.World.ElapsedMilliseconds) / 1000f;
            return SVCLang.Get("debug-status-recording", remaining.ToString("0.0"), debugRecordingFrames.Count);
        }

        if (debugPlaybackActive)
        {
            return SVCLang.Get("debug-status-playing", debugPlaybackIndex, debugRecordingFrames.Count);
        }

        if (debugRecordingFrames.Count > 0)
        {
            int duration = Math.Min(DebugRecordingMilliseconds, debugRecordingFrames[^1].OffsetMilliseconds + VoiceConstants.FrameMilliseconds);
            return SVCLang.Get("debug-status-recorded", debugRecordingFrames.Count, (duration / 1000f).ToString("0.0"));
        }

        return SVCLang.Get("debug-status-none");
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

    private static string FormatChannelKind(VoiceChannelKind kind)
    {
        return kind switch
        {
            VoiceChannelKind.Civilization => SVCLang.Get("channel-kind-civilization"),
            VoiceChannelKind.Command => SVCLang.Get("channel-kind-command"),
            VoiceChannelKind.Diplomacy => SVCLang.Get("channel-kind-diplomacy"),
            VoiceChannelKind.Staff => SVCLang.Get("channel-kind-staff"),
            VoiceChannelKind.Broadcast => SVCLang.Get("channel-kind-broadcast"),
            VoiceChannelKind.Radio => SVCLang.Get("channel-kind-radio"),
            _ => SVCLang.Get("channel-kind-squad")
        };
    }

    private string FormatHotkey(string hotkeyCode, string fallback)
    {
        string value = capi.Input.GetHotKeyByCode(hotkeyCode)?.CurrentMapping?.ToString() ?? fallback;
        return value == "Quote" ? "'" : value;
    }

    private void SaveConfig()
    {
        config.Normalize();
        capi.StoreModConfig(config, VoiceConstants.ClientConfigFileName);
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
        controlChannel = null;
        voiceChannel = null;
        channelInfos = Array.Empty<ChannelInfoPacket>();
        memberPagesByChannel.Clear();
        activeChannelTalkerHashesByChannel.Clear();
    }

    private readonly record struct DebugVoiceFrame(byte[] Payload, float Rms, VoiceMode Mode, int OffsetMilliseconds, Vec3f Position);
}
