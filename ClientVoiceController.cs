using SimpleVoiceChat.Audio;
using SimpleVoiceChat.Config;
using SimpleVoiceChat.Gui;
using SimpleVoiceChat.Integration;
using SimpleVoiceChat.Networking;
using SimpleVoiceChat.SpeechRecognition;
using OpenTK.Audio.OpenAL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace SimpleVoiceChat;

internal readonly record struct VoiceCurrentStatusSnapshot(
    bool ServerEnabled,
    bool ControlConnected,
    bool HandshakeAccepted,
    bool UdpResponsive,
    int ProtocolVersion,
    int Codec,
    int EncoderBitrate,
    int ConnectionEpoch,
    int MaxStreamsPerListener,
    double RoundTripMilliseconds,
    double ProbeLossPercent,
    VoiceDiagnosticsPacket? Diagnostics,
    bool CaptureAvailable,
    string CaptureFailure,
    string InputDevice,
    string OutputDevice,
    string PlaybackStatus,
    string ProcessingBackend,
    bool NoiseSuppressionAvailable,
    bool EchoCancellationAvailable,
    VoiceMode Mode,
    bool VoiceActivationEnabled,
    VoiceTransmitTarget TransmitTarget,
    string SelectedChannelName,
    string SelectedChannelId,
    bool LocalMuted,
    bool GlobalMuted,
    bool TransmitBlocked,
    bool IsRecording,
    VoiceRecordingMode? RecordingMode,
    long EncodedFrameAllocationCount,
    long EncodedFrameAllocatedBytes);

public sealed class ClientVoiceController : IDisposable
{
    private const int SettingsMemberPageSize = 8;
    private const int MaxCaptureFramesPerTick = 8;
    private const long VoiceProbeIntervalMilliseconds = 2_000;
    private const long RecorderClockProbeIntervalMilliseconds = 250;
    private const long RecorderClockControlFallbackDelayMilliseconds = 1_000;
    private const long VoiceProbeTimeoutMilliseconds = 6_000;
    private const long CaptureRecoveryIntervalMilliseconds = 10_000;

    private readonly ICoreClientAPI capi;
    private readonly SimpleVoiceChatClientConfig config;
    private readonly ControllerLifecycle lifecycle = new();
    private IClientNetworkChannel? controlChannel;
    private IClientNetworkChannel? voiceChannel;
    private OpenAlCaptureService? capture;
    private OpenAlPlaybackService? playback;
    private DirectorVoiceIntegration? directorVoice;
    private RecorderVoiceCapture? recorderVoice;
    private VoiceRecordingService? recording;
    private readonly AudioBusMixer audioBuses = new();
    private AudioBusPipeBridge? audioBusPipeBridge;
    private VoiceTestRecordingBuffer? microphoneTest;
    private VoiceHud? hud;
    private VoiceSettingsDialog? settingsDialog;
    private VoiceSetupWizardDialog? setupWizard;
    private VoiceInviteDialog? inviteDialog;
    private readonly short[] captureBuffer = new short[VoiceConstants.SamplesPerFrame];
    private readonly VoiceCapturePreprocessor capturePreprocessor = new();
    private readonly VoiceProbeTracker voiceProbeTracker = new();
    private readonly AdaptiveVoiceBitrateController adaptiveBitrate = new();
    private readonly VoiceProbeTracker recorderClockControlProbeTracker = new();
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
        EnableHudIndicators = true,
        AllowContinuousTalk = true
    };

    private VoiceMode mode = VoiceMode.Talk;
    private ushort sequence;
    private int sessionId;
    private bool localMuted;
    private bool globalMuted;
    private bool lastPressed;
    private bool lastSpeaking;
    private bool captureWarningShown;
    private bool localMutePressed;
    private bool globalMutePressed;
    private bool recorderListenerActive;
    private bool recorderListenerRequested;
    private readonly ServerClockEstimator recorderClock = new();
    private RecorderVoiceTimelinePacket? pendingRecorderTimeline;
    private RecorderCaptureStatePacket? recorderCaptureState;
    private RecorderSessionStatusPacket? recorderSessionStatus;
    private RecorderFileDownloadService? recorderDownloads;
    private bool multiTrackStartPending;
    private bool multiTrackSettingsPressed;
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
    private string runtimeServerInstanceId = string.Empty;
    private string pendingInviteKey = string.Empty;
    private long pendingInviteDeadlineMs;
    private string lastDiagnostics = string.Empty;
    private VoiceDiagnosticsPacket? lastDiagnosticsPacket;
    private int nextSessionId = 1;
    private int nextVoiceProbeNonce = 1;
    private long lastVoiceProbeSentMs;
    private long lastNetworkQualitySentMs;
    private long lastDiagnosticsRequestMs;
    private long lastRecorderClockControlProbeSentMs;
    private long lastRecorderParticipantStateSentMs;
    private long voiceHandshakeAcceptedMs;
    private long lastCaptureRecoveryAttemptMs;
    private long encodedFrameAllocationCount;
    private long encodedFrameAllocatedBytes;
    private long transmitBlockedUntilMs;
    private bool serverTransmitBlocked;
    private bool channelTransmitBlocked;
    private bool lastRecordingPlaybackActive;
    private float lastMicRms;
    private bool voiceActivationTriggered;
    private int voiceActivationHangoverFrames;
    private bool setupMicrophoneMonitoring;
    private readonly SpeechRecognitionAudioBuffer speechRecognitionBuffer = new();
    private ISpeechRecognitionClient? speechRecognitionClient;
    private CancellationTokenSource? speechRecognitionCancellation;
    private bool speechRecognitionActive;

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
    internal bool ContinuousTalkEnabled => config.PreferVoiceActivation;
    internal bool ContinuousTalkAllowed => serverConfig.AllowContinuousTalk;
    internal bool VoiceActivationEnabled => config.PreferVoiceActivation;
    internal float MicrophoneRms => lastMicRms;
    internal bool OcclusionForced => serverConfig.ForceImmersive;
    internal AudioBusMixer AudioBuses => audioBuses;

    internal bool RecorderListenerActive => recorderListenerActive;
    internal bool IsMultiTrackStartPending => multiTrackStartPending;
    internal bool CanStartMultiTrackRecording => hasServerControl && serverConfig.EnableRecorderCapture && recorderClock.IsStable;
    internal int RecorderClockSampleCount => recorderClock.SampleCount;
    internal double RecorderClockRoundTripMilliseconds => recorderClock.BestRoundTripMilliseconds;
    internal RecorderSessionStatusPacket? RecorderStatus => recorderSessionStatus;

    internal void SetRecorderListener(bool active)
    {
        if (!hasServerControl || controlChannel?.Connected != true)
        {
            recorderListenerRequested = false;
            recorderListenerActive = false;
            return;
        }

        recorderListenerRequested = active;
        if (!active)
        {
            recorderListenerActive = false;
        }
        controlChannel.SendPacket(new RecorderVoiceListenerPacket
        {
            Active = active,
            ClientTimestampMilliseconds = MonotonicClock.NowMilliseconds,
            SessionId = recording?.ActiveMultiTrackSession?.SessionId ?? string.Empty
        });
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

        recording = new VoiceRecordingService(capi);
        recorderDownloads = new RecorderFileDownloadService(recording.DirectoryPath);
        speechRecognitionClient = CreateSpeechRecognitionClient(config.SpeechRecognitionProvider);
        microphoneTest = new VoiceTestRecordingBuffer();
        playback = new OpenAlPlaybackService(capi, config);
        VoiceRecordingService recordingService = recording;
        playback.OutputFrameCaptured = samples => recordingService.AppendOutput(samples);
        playback.RemoteFrameCaptured = (entityId, uid, samples, timestamp) => CaptureMultiTrackRemote(recordingService, entityId, uid, samples, timestamp);
        playback.RemoteFrameCaptured += (_, _, samples, timestamp) =>
        {
            if (!recorderListenerActive)
            {
                audioBuses.SubmitAt(AudioBusKind.PlayerVoice, samples, ToLocalAudioTimestamp(timestamp));
            }
        };
        audioBusPipeBridge = new AudioBusPipeBridge(audioBuses);
        playback.Initialize();
        directorVoice = new DirectorVoiceIntegration(capi);
        recorderVoice = new RecorderVoiceCapture();
        recorderVoice.FrameCaptured = (uid, name, samples, timestamp) =>
        {
            if (recording?.Mode == VoiceRecordingMode.MultiTrack)
            {
                recording.AppendRemote(uid, name, samples, timestamp);
            }
            audioBuses.SubmitAt(AudioBusKind.PlayerVoice, samples, ToLocalAudioTimestamp(timestamp));
        };

        hud = new VoiceHud(capi, BuildHudSnapshot, ShouldShowHud);
        capi.Gui.RegisterDialog(hud);
        settingsDialog = new VoiceSettingsDialog(capi, this);
        setupWizard = new VoiceSetupWizardDialog(capi, this);
        inviteDialog = new VoiceInviteDialog(
            capi,
            () => capi.World.ElapsedMilliseconds,
            AcceptPendingInvite,
            DeclinePendingInvite,
            () => hud?.ReservedHeight ?? 0);
        hud.Refresh();
        ShowInitialSetupPrompt();

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
            .SetMessageHandler<ServerVoiceConfigPacket>(OnServerConfig)
            .SetMessageHandler<VoiceWelcomePacket>(OnVoiceWelcome)
            .SetMessageHandler<ChannelSnapshotPacket>(OnChannelSnapshot)
            .SetMessageHandler<ChannelMemberDeltaPacket>(OnChannelMemberDelta)
            .SetMessageHandler<ChannelMemberPagePacket>(OnChannelMemberPage)
            .SetMessageHandler<TalkerStateDeltaPacket>(OnTalkerStateDelta)
            .SetMessageHandler<VoiceFeedbackPacket>(OnVoiceFeedback)
            .SetMessageHandler<VoiceDiagnosticsPacket>(OnVoiceDiagnostics)
            .SetMessageHandler<VoiceBitrateControlPacket>(OnVoiceBitrateControl)
            .SetMessageHandler<RecorderVoiceTimelinePacket>(OnRecorderVoiceTimelinePacket)
            .SetMessageHandler<RecorderCaptureStatePacket>(OnRecorderCaptureStatePacket)
            .SetMessageHandler<RecorderSessionStatusPacket>(OnRecorderSessionStatusPacket)
            .SetMessageHandler<RecorderFileChunkPacket>(OnRecorderFileChunkPacket)
            .SetMessageHandler<VoicePongPacket>(OnControlVoicePong);

        voiceChannel = capi.Network.RegisterUdpChannel(VoiceConstants.VoiceChannelName)
            .RegisterMessageType<VoiceFrameV3Packet>()
            .RegisterMessageType<VoiceRelayFrameV3Packet>()
            .RegisterMessageType<DirectorVoiceRelayFrameV3Packet>()
            .RegisterMessageType<RecorderVoiceRelayFrameV3Packet>()
            .RegisterMessageType<VoicePingPacket>()
            .RegisterMessageType<VoicePongPacket>()
            .SetMessageHandler<VoiceRelayFrameV3Packet>(OnVoiceRelayFrameV3)
            .SetMessageHandler<DirectorVoiceRelayFrameV3Packet>(OnDirectorVoiceRelayFrameV3)
            .SetMessageHandler<RecorderVoiceRelayFrameV3Packet>(OnRecorderVoiceRelayFrameV3)
            .SetMessageHandler<VoicePongPacket>(OnVoicePong);
    }

    private void RegisterHotkeys()
    {
        capi.Input.RegisterHotKey(VoiceConstants.PushToTalkHotKey, SVCLang.Get("hotkey-push-to-talk"), GetConfiguredKey(config.PushToTalkKey, GlKeys.N), HotkeyType.CharacterControls);
        capi.Input.RegisterHotKey(VoiceConstants.ToggleTalkHotKey, SVCLang.Get("hotkey-toggle-talk"), GlKeys.N, HotkeyType.GUIOrOtherControls, altPressed: true);
        capi.Input.RegisterHotKey(VoiceConstants.ModeCycleHotKey, SVCLang.Get("hotkey-cycle-mode"), GetConfiguredKey(config.ModeCycleKey, GlKeys.LBracket), HotkeyType.CharacterControls);
        capi.Input.RegisterHotKey(VoiceConstants.ModeCycleAltHotKey, SVCLang.Get("hotkey-cycle-mode-alt"), GlKeys.RBracket, HotkeyType.CharacterControls);
        capi.Input.RegisterHotKey(VoiceConstants.LocalMuteHotKey, SVCLang.Get("hotkey-local-mute"), GlKeys.Minus, HotkeyType.GUIOrOtherControls, ctrlPressed: true);
        capi.Input.RegisterHotKey(VoiceConstants.GlobalMuteHotKey, SVCLang.Get("hotkey-global-mute"), GlKeys.Semicolon, HotkeyType.CharacterControls);
        capi.Input.RegisterHotKey(VoiceConstants.SettingsHotKey, SVCLang.Get("hotkey-settings"), GlKeys.Quote, HotkeyType.GUIOrOtherControls);
        capi.Input.RegisterHotKey(VoiceConstants.MultiTrackSettingsHotKey, SVCLang.Get("hotkey-multitrack-settings"), GlKeys.F9, HotkeyType.GUIOrOtherControls, ctrlPressed: true);
        capi.Input.RegisterHotKey(VoiceConstants.SpeechRecognitionHotKey, SVCLang.Get("hotkey-speech-recognition"), GlKeys.V, HotkeyType.CharacterControls);

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
                ToggleVoiceActivation();
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
                if (config.InitialSetupCompleted)
                {
                    settingsDialog?.Toggle();
                }
                else
                {
                    setupWizard?.Toggle();
                }
            }
            return true;
        });
        capi.Input.SetHotKeyHandler(VoiceConstants.MultiTrackSettingsHotKey, _ =>
        {
            if (!lifecycle.IsStarted || multiTrackSettingsPressed)
            {
                return false;
            }

            multiTrackSettingsPressed = true;
            if (!hasServerControl || !serverConfig.EnableRecorderCapture)
            {
                capi.ShowChatMessage(SVCLang.Get("chat-multitrack-admin-only"));
                return true;
            }

            settingsDialog?.OpenMultiTrackRecordingOverlay();
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

        if (e.KeyCode == GetHotkeyCode(VoiceConstants.MultiTrackSettingsHotKey, GlKeys.F9))
        {
            multiTrackSettingsPressed = false;
        }
    }

    private int GetHotkeyCode(string hotkeyCode, GlKeys fallback)
    {
        return capi.Input.GetHotKeyByCode(hotkeyCode)?.CurrentMapping?.KeyCode ?? (int)fallback;
    }

    private void ToggleLocalMute()
    {
        localMuted = !localMuted;
        capi.ShowChatMessage(SVCLang.Get("chat-local-mute", localMuted ? SVCLang.Get("chat-local-mute-on") : SVCLang.Get("chat-local-mute-off")));
        SendState(force: true);
        hud?.Refresh();
    }

    private void ToggleGlobalMute()
    {
        globalMuted = !globalMuted;
        capi.ShowChatMessage(SVCLang.Get("chat-global-mute", globalMuted ? SVCLang.Get("state-off") : SVCLang.Get("state-on")));
        SendState(force: true);
        hud?.Refresh();
    }

    private void ToggleVoiceActivation()
    {
        config.PreferVoiceActivation = !config.PreferVoiceActivation;
        SaveConfig();
        capi.ShowChatMessage(SVCLang.Get(
            "chat-voice-activation-mode",
            config.PreferVoiceActivation ? SVCLang.Get("mode-voice-activation") : SVCLang.Get("mode-push-to-talk")));
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
        if (config.PreferVoiceActivation != enabled)
        {
            ToggleVoiceActivation();
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
        if (!serverConfig.EnableRecorderCapture && (recorderListenerActive || recorderListenerRequested || multiTrackStartPending))
        {
            multiTrackStartPending = false;
            pendingRecorderTimeline = null;
            SetRecorderListener(false);
        }
        ActivateCurrentServerProfile(packet.ServerInstanceId);
        if (serverConfig.ForceImmersive)
        {
            config.EnableOcclusionEffects = true;
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
            ModVersion = "1.2.1",
            SupportedCodecs = new[] { VoiceProtocol.CodecOpus, VoiceProtocol.CodecImaAdpcm },
            Capabilities = (int)(VoiceCapability.ProtocolV4
                | VoiceCapability.ChannelDeltas
                | VoiceCapability.ChannelMemberPaging
                | VoiceCapability.AdaptiveJitter
                | VoiceCapability.Opus
                | VoiceCapability.Diagnostics
                | VoiceCapability.ProtocolV5
                | VoiceCapability.ServerHostedRecording
                | VoiceCapability.ProtocolV6
                | VoiceCapability.ServerGuidedBitrate),
            PreferredOpusBitrateKbps = config.PreferredOpusBitrateKbps
        });
    }

    private void OnVoiceWelcome(VoiceWelcomePacket packet)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        bool serverRestarted = !string.IsNullOrWhiteSpace(runtimeServerInstanceId)
            && !string.IsNullOrWhiteSpace(packet.RuntimeInstanceId)
            && !string.Equals(runtimeServerInstanceId, packet.RuntimeInstanceId, StringComparison.Ordinal);
        if (serverRestarted && recording?.Mode == VoiceRecordingMode.MultiTrack)
        {
            recording.Stop(MonotonicClock.NowMilliseconds, out _);
            multiTrackStartPending = false;
            pendingRecorderTimeline = null;
            capi.ShowChatMessage(SVCLang.Get("chat-recording-server-restarted"));
        }
        if (!string.IsNullOrWhiteSpace(packet.RuntimeInstanceId))
        {
            runtimeServerInstanceId = packet.RuntimeInstanceId;
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
        recorderClockControlProbeTracker.Reset();
        recorderClock.Reset();
        recorderListenerActive = false;
        recorderListenerRequested = false;
        pendingRecorderTimeline = null;
        recorderCaptureState = null;
        recorderSessionStatus = null;
        multiTrackStartPending = false;
        lastDiagnostics = string.Empty;
        lastDiagnosticsPacket = null;
        lastVoiceProbeSentMs = 0;
        lastNetworkQualitySentMs = 0;
        lastRecorderClockControlProbeSentMs = 0;
        lastRecorderParticipantStateSentMs = 0;
        voiceEncoder?.Dispose();
        int configuredMaximum = config.PreferredOpusBitrateKbps > 0
            ? config.PreferredOpusBitrateKbps * 1_000
            : serverConfig.MaxOpusBitrateKbps > 0 ? serverConfig.MaxOpusBitrateKbps * 1_000 : 32_000;
        int encoderBitrate = packet.Bitrate > 0 ? packet.Bitrate : 20_000;
        adaptiveBitrate.Reset(configuredMaximum, capi.World.ElapsedMilliseconds);
        if (packet.Codec == VoiceProtocol.CodecOpus && packet.Bitrate > 0)
        {
            adaptiveBitrate.SetServerGuidance(packet.Bitrate, 5, capi.World.ElapsedMilliseconds);
        }
        voiceEncoder = voiceHandshakeAccepted ? VoiceCodecFactory.CreateEncoder(negotiatedCodec, encoderBitrate) : null;
        if (voiceEncoder is INetworkAdaptiveVoiceEncoder adaptiveEncoder)
        {
            adaptiveEncoder.ConfigureNetwork(adaptiveBitrate.CurrentBitrate, adaptiveBitrate.PacketLossPercent);
        }
        if (!voiceHandshakeAccepted && !string.IsNullOrWhiteSpace(packet.Message))
        {
            capi.ShowChatMessage($"Simple Voice Chat: {SVCLang.Get("feedback-" + packet.Message)}");
        }
        if (voiceHandshakeAccepted)
        {
            selectedChannelRestorePending = !string.IsNullOrEmpty(config.SelectedChannelId);
            SendState(force: true);
            SendRecorderParticipantState(force: true);
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
        if (string.Equals(packet.Code, "channel-owner-leave-options", StringComparison.OrdinalIgnoreCase)
            && packet.Arguments is { Length: > 0 })
        {
            settingsDialog?.OpenOwnerLeaveOverlay(packet.Arguments[0]);
        }
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
        long rollingEstimatedBytes = packet.RollingEstimatedRelayedIpv4UdpBytes > 0
            ? packet.RollingEstimatedRelayedIpv4UdpBytes
            : packet.RollingRelayedBytes;
        lastDiagnosticsPacket = packet;
        lastDiagnostics = SVCLang.Get(
            "diagnostics-detail",
            packet.RollingRelayedPackets,
            packet.RollingRelayedBytes,
            rollingEstimatedBytes,
            packet.RollingDroppedPackets,
            packet.P95FanOut.ToString("0.0"),
            packet.P95RouteMilliseconds.ToString("0.000"),
            packet.ActiveListenerStreams,
            packet.ActiveTalkers);
        settingsDialog?.RefreshData();
    }

    private void OnVoicePong(VoicePongPacket packet)
    {
        AcceptRecorderClockSample(packet, voiceProbeTracker);
    }

    private void OnVoiceBitrateControl(VoiceBitrateControlPacket packet)
    {
        if (!lifecycle.IsStarted
            || !voiceHandshakeAccepted
            || packet.ConnectionEpoch != connectionEpoch
            || negotiatedCodec != VoiceProtocol.CodecOpus)
        {
            return;
        }

        adaptiveBitrate.SetServerGuidance(
            packet.TargetBitrate,
            packet.PacketLossPercent,
            capi.World.ElapsedMilliseconds);
        if (voiceEncoder is INetworkAdaptiveVoiceEncoder encoder)
        {
            encoder.ConfigureNetwork(adaptiveBitrate.CurrentBitrate, adaptiveBitrate.PacketLossPercent);
        }
        settingsDialog?.RefreshData();
    }

    private void OnControlVoicePong(VoicePongPacket packet)
    {
        AcceptRecorderClockSample(packet, recorderClockControlProbeTracker);
    }

    private void AcceptRecorderClockSample(VoicePongPacket packet, VoiceProbeTracker probeTracker)
    {
        if (!lifecycle.IsStarted
            || !voiceHandshakeAccepted
            || packet.ConnectionEpoch != connectionEpoch
            || packet.Nonce <= 0)
        {
            return;
        }

        if (probeTracker.MarkReply(packet.Nonce, capi.World.ElapsedMilliseconds))
        {
            recorderClock.AddSample(
                packet.ClientSendTimestampMilliseconds,
                packet.ServerTimestampMilliseconds,
                MonotonicClock.NowMilliseconds);
            SendRecorderParticipantState(force: recorderClock.SampleCount == 3);
            if (multiTrackStartPending && !recorderListenerRequested && recorderClock.IsStable)
            {
                SetRecorderListener(true);
                capi.ShowChatMessage(SVCLang.Get("chat-multitrack-waiting-anchor"));
            }
            TryStartPendingMultiTrackSession();
            hud?.Refresh();
        }
    }

    private void SendChannelCommand(
        string action,
        string channelId = "",
        string targetPlayerUid = "",
        string name = "",
        int page = 0,
        int pageSize = 0,
        string password = "",
        VoiceChannelVisibility visibility = VoiceChannelVisibility.Open)
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
            PageSize = pageSize,
            Password = password,
            Visibility = visibility
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
            .OrderByDescending(info => info.LocalRole != VoiceChannelRole.Banned)
            .ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
            .Select(info => new VoiceSettingsChannelOption(
                info.ChannelId,
                info.Name,
                info.LocalRole,
                info.ExternallyManaged,
                info.MemberCount,
                info.Locked,
                info.Visibility,
                info.OwnerUid))
            .ToArray();
    }

    internal void JoinChannelFromSettings(string channelId, string password)
    {
        SendChannelCommand("join", channelId: channelId, password: password);
    }

    internal void CreateChannelFromSettings(string name, string password, VoiceChannelVisibility visibility)
    {
        SendChannelCommand("create", name: name, password: password, visibility: visibility);
    }

    internal void LeaveChannelFromSettings(string channelId)
    {
        SendChannelCommand("leave", channelId: channelId);
    }

    internal void TransferChannelOwnerFromSettings(string channelId, string targetUid)
    {
        SendChannelCommand("transfer-owner", channelId: channelId, targetPlayerUid: targetUid);
    }

    internal void DeleteChannelFromSettings(string channelId)
    {
        SendChannelCommand("delete-owned-channel", channelId: channelId);
    }

    internal string[] BuildPlayerActions(string channelId)
    {
        VoiceSettingsChannelOption? selected = BuildChannelOptions()
            .Cast<VoiceSettingsChannelOption?>()
            .FirstOrDefault(channel => channel?.Id == channelId);
        if (!selected.HasValue)
        {
            return new[] { "none" };
        }

        VoiceSettingsChannelOption channel = selected.Value;
        List<string> actions = new();
        if (hasServerControl)
        {
            if (!channel.ExternallyManaged)
            {
                actions.AddRange(new[] { "invite", "add", "remove", "listenonly", "member", "moderator" });
            }
            actions.AddRange(new[] { "mute", "unmute", "ban", "unban" });
        }
        else if (channel.LocalRole == VoiceChannelRole.Owner)
        {
            if (!channel.ExternallyManaged)
            {
                actions.AddRange(new[] { "invite", "remove", "listenonly", "member", "moderator" });
            }
            actions.AddRange(new[] { "mute", "unmute", "ban", "unban" });
        }
        else if (channel.LocalRole == VoiceChannelRole.Moderator)
        {
            actions.Add("invite");
            actions.AddRange(new[] { "mute", "unmute", "ban", "unban" });
            if (!channel.ExternallyManaged)
            {
                actions.Add("remove");
            }
        }

        return actions.Count == 0 ? new[] { "none" } : actions.ToArray();
    }

    internal VoiceSettingsPlayerOption[] BuildPlayerOptions()
    {
        Dictionary<string, string> names = capi.World.AllOnlinePlayers
            .Where(player => player.PlayerUID != capi.World.Player.PlayerUID)
            .ToDictionary(player => player.PlayerUID, player => player.PlayerName, StringComparer.Ordinal);
        foreach (ChannelInfoPacket channel in channelInfos)
        {
            foreach (ChannelMemberPacket member in channel.Members ?? Array.Empty<ChannelMemberPacket>())
            {
                if (member.PlayerUid != capi.World.Player.PlayerUID && !names.ContainsKey(member.PlayerUid))
                {
                    names[member.PlayerUid] = DisplayPlayerName(member.PlayerUid, member.PlayerName);
                }
            }
        }
        return names.Select(pair => new VoiceSettingsPlayerOption(
                pair.Key,
                DisplayPlayerName(pair.Key, pair.Value),
                string.Join(", ", channelInfos
                    .Where(channel => (channel.Members ?? Array.Empty<ChannelMemberPacket>()).Any(member => member.PlayerUid == pair.Key))
                    .Select(channel => channel.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase))))
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

    internal VoiceSettingsMemberOption[] BuildChannelMembersForSettings(string channelId)
    {
        ChannelInfoPacket? channel = channelInfos.FirstOrDefault(info => info.ChannelId == channelId);
        if (channel == null) return Array.Empty<VoiceSettingsMemberOption>();
        return channel.Members
            .Where(member => member.PlayerUid != capi.World.Player.PlayerUID)
            .Select(member => new VoiceSettingsMemberOption(
                member.PlayerUid,
                DisplayPlayerName(member.PlayerUid, member.PlayerName),
                member.Role))
            .ToArray();
    }

    internal string[] GetOutputDeviceValues()
    {
        List<string> values = new() { string.Empty };
        try
        {
            IEnumerable<string> devices = ALC.GetString(AlcGetStringList.AllDevicesSpecifier);
            foreach (string device in devices)
            {
                if (!string.IsNullOrWhiteSpace(device) && !values.Contains(device, StringComparer.Ordinal))
                {
                    values.Add(device);
                }
            }
        }
        catch (Exception exception)
        {
            capi.Logger.Warning("SimpleVoiceChat: failed enumerating playback devices: {0}", exception.Message);
        }

        if (!string.IsNullOrWhiteSpace(config.OutputDeviceName)
            && !values.Contains(config.OutputDeviceName, StringComparer.Ordinal))
        {
            values.Add(config.OutputDeviceName);
        }
        return values.ToArray();
    }

    internal static string[] GetOutputDeviceNames(string[] values)
    {
        return values.Select(value => string.IsNullOrEmpty(value) ? SVCLang.Get("default-speaker") : value).ToArray();
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

    internal void SetOutputDeviceFromSettings(string value)
    {
        string next = value ?? string.Empty;
        if (config.OutputDeviceName == next)
        {
            return;
        }

        config.OutputDeviceName = next;
        SaveConfig();
        ReinitializePlayback();
    }

    internal void SetVoiceActivationFromSetup(bool voiceActivation)
    {
        config.PreferVoiceActivation = voiceActivation;
        config.PreferContinuousTalk = false;
        SaveConfig();
        SendState(force: true);
        hud?.Refresh();
    }

    internal void SetPushToTalkKeyFromSetup(string value)
    {
        GlKeys key = GetConfiguredKey(value, GlKeys.N);
        config.PushToTalkKey = key.ToString();
        HotKey? hotkey = capi.Input.GetHotKeyByCode(VoiceConstants.PushToTalkHotKey);
        if (hotkey?.CurrentMapping != null)
        {
            hotkey.CurrentMapping.KeyCode = (int)key;
            hotkey.CurrentMapping.SecondKeyCode = null;
            hotkey.CurrentMapping.Alt = false;
            hotkey.CurrentMapping.Ctrl = false;
            hotkey.CurrentMapping.Shift = false;
        }
        SaveConfig();
    }

    internal void CompleteInitialSetup()
    {
        config.InitialSetupCompleted = true;
        config.InitialSetupPromptShown = true;
        SaveConfig();
    }

    internal void SkipInitialSetupToSettings()
    {
        CompleteInitialSetup();
        setupWizard?.TryClose();
        settingsDialog?.TryOpen();
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

    internal void SetPreferredOpusBitrateFromSettings(string value)
    {
        if (!int.TryParse(value, out int kbps))
        {
            kbps = 0;
        }
        config.PreferredOpusBitrateKbps = SimpleVoiceChatClientConfig.NormalizePreferredOpusBitrate(kbps);
        SaveConfig();
        if (voiceEncoder is INetworkAdaptiveVoiceEncoder encoder && negotiatedCodec == VoiceProtocol.CodecOpus)
        {
            int maximum = config.PreferredOpusBitrateKbps > 0
                ? config.PreferredOpusBitrateKbps * 1_000
                : Math.Max(8_000, serverConfig.MaxOpusBitrateKbps * 1_000);
            adaptiveBitrate.SetMaximum(maximum, capi.World.ElapsedMilliseconds);
            encoder.ConfigureNetwork(adaptiveBitrate.CurrentBitrate, adaptiveBitrate.PacketLossPercent);
        }
    }

    internal void SetNoiseGateFromSettings(int value)
    {
        config.NoiseGate = Math.Clamp(value / 1000f, 0f, 0.2f);
        config.VoiceActivationThreshold = Math.Max(config.VoiceActivationThreshold, config.NoiseGate);
        SaveConfig();
    }

    internal void SetVoiceActivationThresholdFromSetup(int value)
    {
        SetVoiceActivationThresholdFromSettings(value);
    }

    internal void SetVoiceActivationThresholdFromSettings(int value)
    {
        config.VoiceActivationThreshold = Math.Clamp(value / 1000f, Math.Max(config.NoiseGate, 0.005f), 0.2f);
        SaveConfig();
    }

    internal void SetSetupMicrophoneMonitoring(bool enabled)
    {
        setupMicrophoneMonitoring = enabled;
        if (!enabled && microphoneTest?.IsRecording != true && recording?.IsRecording != true)
        {
            capture?.Stop();
            lastMicRms = 0f;
        }
    }

    internal bool IsRecording => recording?.IsRecording == true;
    internal bool IsRecordingPlaybackActive => playback?.IsRecordingPlaybackActive == true;
    internal bool HasRecording => recording?.HasRecording == true;
    internal string LastRecordingPath => recording?.LastRecordingPath ?? string.Empty;
    internal VoiceRecordingMode? RecordingMode => recording?.Mode;
    internal bool IsMicrophoneTestRecording => microphoneTest?.IsRecording == true;
    internal bool HasMicrophoneTestRecording => microphoneTest?.LastClip != null;
    internal bool IsMicrophoneTestPlaybackActive => playback?.IsRecordingPlaybackActive == true;

    internal bool ToggleMicrophoneTestRecording()
    {
        if (IsMicrophoneTestRecording)
        {
            return StopMicrophoneTestRecording();
        }

        if (microphoneTest == null || capture?.IsAvailable != true)
        {
            capi.ShowChatMessage(SVCLang.Get("chat-recording-unavailable", capture?.FailureReason ?? string.Empty));
            return false;
        }

        microphoneTest.Start();
        capture.Start();
        settingsDialog?.RefreshConfiguration();
        return true;
    }

    private bool StopMicrophoneTestRecording()
    {
        if (microphoneTest == null || !microphoneTest.IsRecording)
        {
            return false;
        }

        bool captured = microphoneTest.Stop();
        if (!lastPressed && recording?.IsRecording != true)
        {
            capture?.Stop();
        }

        settingsDialog?.RefreshConfiguration();
        if (!captured)
        {
            capi.ShowChatMessage(SVCLang.Get("chat-recording-empty"));
        }
        return captured;
    }

    internal bool ToggleMicrophoneTestPlayback()
    {
        if (IsMicrophoneTestPlaybackActive)
        {
            playback?.StopRecordingPlayback();
            lastRecordingPlaybackActive = false;
            settingsDialog?.RefreshConfiguration();
            return true;
        }

        RecordedAudioClip? clip = microphoneTest?.LastClip;
        if (clip == null || playback == null)
        {
            capi.ShowChatMessage(SVCLang.Get("chat-recording-playback-empty"));
            return false;
        }

        if (!playback.PlayRecording(clip, out string error))
        {
            capi.ShowChatMessage(SVCLang.Get("chat-recording-playback-failed", error));
            return false;
        }

        lastRecordingPlaybackActive = true;
        settingsDialog?.RefreshConfiguration();
        return true;
    }

    internal bool ToggleRecordingFromSettings()
    {
        if (IsRecording)
        {
            return StopRecordingFromSettings();
        }

        settingsDialog?.OpenRecordingModeOverlay();
        return true;
    }

    internal bool StartRecordingFromSettings(VoiceRecordingMode mode)
    {
        if (recording == null || capture?.IsAvailable != true)
        {
            capi.ShowChatMessage(SVCLang.Get("chat-recording-unavailable", capture?.FailureReason ?? string.Empty));
            return false;
        }

        if (mode == VoiceRecordingMode.MultiTrack)
        {
            if (!hasServerControl || !serverConfig.EnableRecorderCapture)
            {
                capi.ShowChatMessage(SVCLang.Get("chat-multitrack-admin-only"));
                return false;
            }

            if (!recorderClock.IsStable)
            {
                multiTrackStartPending = true;
                capi.ShowChatMessage(SVCLang.Get("chat-multitrack-syncing"));
                settingsDialog?.RefreshConfiguration();
                return true;
            }

            multiTrackStartPending = true;
            SetRecorderListener(true);
            capi.ShowChatMessage(SVCLang.Get("chat-multitrack-waiting-anchor"));
            settingsDialog?.RefreshConfiguration();
            return true;
        }

        if (!recording.Start(mode, MonotonicClock.NowMilliseconds, out string error))
        {
            capi.ShowChatMessage(SVCLang.Get("chat-recording-failed", error));
            return false;
        }

        capture.Start();
        settingsDialog?.RefreshConfiguration();
        capi.ShowChatMessage(SVCLang.Get("chat-recording-started", mode == VoiceRecordingMode.InputOnly
            ? SVCLang.Get("recording-mode-input")
                : SVCLang.Get("recording-mode-input-output")));
        return true;
    }

    private void CaptureMultiTrackRemote(VoiceRecordingService recordingService, long entityId, string uid, short[] samples, long timestamp)
    {
        if (recordingService.Mode != VoiceRecordingMode.MultiTrack || recorderListenerActive)
        {
            return;
        }

        IPlayer? player = capi.World.AllOnlinePlayers.FirstOrDefault(candidate => candidate.Entity?.EntityId == entityId);
        if (player != null || !string.IsNullOrWhiteSpace(uid))
        {
            recordingService.AppendRemote(
                string.IsNullOrWhiteSpace(uid) ? player!.PlayerUID : uid,
                player?.PlayerName ?? uid,
                samples,
                timestamp);
        }
    }

    internal bool StopRecordingFromSettings()
    {
        if (recording == null
            || (!recording.IsRecording && !multiTrackStartPending && recorderSessionStatus?.Active != true))
        {
            return false;
        }

        bool wasMultiTrack = recording.Mode == VoiceRecordingMode.MultiTrack
            || multiTrackStartPending
            || recorderSessionStatus?.Active == true;
        multiTrackStartPending = false;
        pendingRecorderTimeline = null;
        if (wasMultiTrack)
        {
            SetRecorderListener(false);
            capi.ShowChatMessage(SVCLang.Get("chat-recording-stop-requested"));
            settingsDialog?.RefreshConfiguration();
            return true;
        }
        string path = string.Empty;
        bool saved = recording.IsRecording && recording.Stop(MonotonicClock.NowMilliseconds, out path);
        SetRecorderListener(false);
        if (!lastPressed)
        {
            capture?.Stop();
        }

        settingsDialog?.RefreshConfiguration();
        if (saved)
        {
            capi.ShowChatMessage(SVCLang.Get("chat-recording-stopped", Path.GetFileName(path)));
        }
        else
        {
            capi.ShowChatMessage(SVCLang.Get("chat-recording-empty"));
        }
        return saved;
    }

    internal bool ToggleRecordingPlaybackFromSettings()
    {
        if (IsRecordingPlaybackActive)
        {
            playback?.StopRecordingPlayback();
            settingsDialog?.RefreshConfiguration();
            return true;
        }

        if (!HasRecording || recording?.CanPlayLastRecording != true || playback == null)
        {
            capi.ShowChatMessage(SVCLang.Get("chat-recording-playback-empty"));
            return false;
        }

        if (!playback.PlayRecording(LastRecordingPath, out string error))
        {
            capi.ShowChatMessage(SVCLang.Get("chat-recording-playback-failed", error));
            return false;
        }

        lastRecordingPlaybackActive = true;
        settingsDialog?.RefreshConfiguration();
        capi.ShowChatMessage(SVCLang.Get("chat-recording-playback-started"));
        return true;
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

    internal void SetSpeechRecognitionEnabledFromSettings(bool enabled)
    {
        config.EnableSpeechRecognition = enabled;
        if (!enabled && speechRecognitionActive)
        {
            speechRecognitionActive = false;
            speechRecognitionBuffer.Cancel();
            capture?.Stop();
        }
        SaveConfig();
    }

    internal void SetSpeechRecognitionApiKeyFromSettings(string value)
    {
        config.SpeechRecognitionApiKey = value.Trim();
        SaveConfig();
    }

    internal void SetSpeechRecognitionProviderFromSettings(string value)
    {
        if (!config.SelectSpeechRecognitionProvider(value))
        {
            return;
        }

        speechRecognitionCancellation?.Cancel();
        speechRecognitionClient?.Dispose();
        speechRecognitionClient = CreateSpeechRecognitionClient(config.SpeechRecognitionProvider);
        SaveConfig();
        settingsDialog?.RefreshConfiguration();
    }

    internal void SetSpeechRecognitionModelFromSettings(string value)
    {
        config.SpeechRecognitionModel = value.Trim();
        SaveConfig();
    }

    internal void SetSpeechRecognitionEndpointFromSettings(string value)
    {
        config.SpeechRecognitionEndpoint = value.Trim();
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

    private static GlKeys GetConfiguredKey(string? value, GlKeys fallback)
    {
        return Enum.TryParse(value, ignoreCase: true, out GlKeys key) && key != GlKeys.Unknown
            ? key
            : fallback;
    }

    private void ShowInitialSetupPrompt()
    {
        if (config.InitialSetupCompleted || config.InitialSetupPromptShown)
        {
            return;
        }

        capi.ShowChatMessage(SVCLang.Get("chat-initial-setup"));
        config.InitialSetupPromptShown = true;
        SaveConfig();
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
        VoiceChannelRole role = VoiceChannelRole.Member,
        string password = "")
    {
        if (action == "create-channel" || action == "create")
        {
            string channelName = string.IsNullOrWhiteSpace(name)
                ? $"{SVCLang.Get("channel-default-name")} - {capi.World.Player.PlayerName}"
                : name.Trim();
            SendChannelCommand("create", name: channelName, password: password);
            return;
        }

        if (action == "rename")
        {
            SendChannelCommand("rename", channelId: channelId, name: name.Trim());
            return;
        }

        if (action == "disband"
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

    internal void RequestSettingsRefresh()
    {
        SendChannelCommand("request");
    }

    internal void RequestDiagnosticsFromSettings()
    {
        lastDiagnosticsPacket = null;
        lastDiagnosticsRequestMs = capi.World.ElapsedMilliseconds;
        SendChannelCommand("diagnostics");
    }

    internal VoiceCurrentStatusSnapshot BuildCurrentStatusSnapshot()
    {
        long now = capi.World.ElapsedMilliseconds;
        ChannelInfoPacket? selectedChannel = channelInfos.FirstOrDefault(channel => channel.ChannelId == config.SelectedChannelId);
        return new VoiceCurrentStatusSnapshot(
            serverConfig.Enabled,
            controlChannel?.Connected == true,
            voiceHandshakeAccepted,
            voiceProbeTracker.IsResponsive(now, VoiceProbeTimeoutMilliseconds),
            VoiceProtocol.CurrentVersion,
            negotiatedCodec,
            !voiceHandshakeAccepted
                ? 0
                : voiceEncoder is INetworkAdaptiveVoiceEncoder adaptiveEncoder
                    ? adaptiveEncoder.Bitrate
                    : negotiatedCodec == VoiceProtocol.CodecImaAdpcm ? 32_800 : 0,
            connectionEpoch,
            serverConfig.MaxStreamsPerListener,
            voiceProbeTracker.SmoothedRttMilliseconds,
            voiceProbeTracker.LossPercent,
            lastDiagnosticsPacket,
            capture?.IsAvailable == true,
            capture?.FailureReason ?? string.Empty,
            string.IsNullOrWhiteSpace(config.InputDeviceName) ? SVCLang.Get("default-microphone") : config.InputDeviceName,
            string.IsNullOrWhiteSpace(config.OutputDeviceName) ? SVCLang.Get("default-speaker") : config.OutputDeviceName,
            playback?.BuildDebugStatus() ?? SVCLang.Get("summary-playback-uninitialized"),
            VoiceProcessingCapabilities.BackendName,
            VoiceProcessingCapabilities.NoiseSuppressionAvailable,
            VoiceProcessingCapabilities.EchoCancellationAvailable,
            mode,
            config.PreferVoiceActivation,
            config.TransmitTarget,
            selectedChannel?.Name ?? SVCLang.Get("channel-none"),
            config.SelectedChannelId,
            localMuted,
            globalMuted,
            serverTransmitBlocked || channelTransmitBlocked || now < transmitBlockedUntilMs,
            IsRecording,
            RecordingMode,
            encodedFrameAllocationCount,
            encodedFrameAllocatedBytes);
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

    private void OnDirectorVoiceRelayFrameV3(DirectorVoiceRelayFrameV3Packet packet)
    {
        if (!lifecycle.IsStarted
            || !voiceHandshakeAccepted
            || !serverConfig.Enabled
            || !serverConfig.EnableDirectorProximityCapture
            || globalMuted
            || !VoiceProtocolValidation.IsValidDirectorRelayShape(packet))
        {
            return;
        }

        directorVoice?.Enqueue(packet);
    }

    private void OnRecorderVoiceRelayFrameV3(RecorderVoiceRelayFrameV3Packet packet)
    {
        if (!lifecycle.IsStarted
            || !recorderListenerActive
            || !voiceHandshakeAccepted
            || !VoiceProtocolValidation.IsValidRecorderRelayShape(packet))
        {
            return;
        }

        recorderVoice?.Enqueue(packet, MonotonicClock.NowMilliseconds);
    }

    private void OnRecorderVoiceTimelinePacket(RecorderVoiceTimelinePacket packet)
    {
        if (!packet.Active)
        {
            if (recording?.Mode == VoiceRecordingMode.MultiTrack)
            {
                recording.Stop(packet.EndServerTimestampMilliseconds, out _);
            }
            recorderListenerActive = false;
            recorderListenerRequested = false;
            multiTrackStartPending = false;
            pendingRecorderTimeline = null;
            capi.ShowChatMessage(SVCLang.Get("chat-recording-server-finalized", packet.SessionId));
            settingsDialog?.RefreshConfiguration();
            hud?.Refresh();
            return;
        }

        if (!hasServerControl)
        {
            return;
        }

        if (recording?.Mode == VoiceRecordingMode.MultiTrack
            && recording.ActiveMultiTrackSession is AudioRecordingSession activeSession
            && string.Equals(activeSession.SessionId, packet.SessionId, StringComparison.Ordinal))
        {
            SetRecorderListener(true);
            recorderListenerActive = true;
            multiTrackStartPending = false;
            pendingRecorderTimeline = null;
            settingsDialog?.RefreshConfiguration();
            hud?.Refresh();
            return;
        }

        recorderListenerRequested = true;
        multiTrackStartPending = true;
        pendingRecorderTimeline = packet;
        TryStartPendingMultiTrackSession();
    }

    private void TryStartPendingMultiTrackSession()
    {
        if (pendingRecorderTimeline is not RecorderVoiceTimelinePacket timeline
            || recording == null
            || recording.IsRecording
            || !multiTrackStartPending
            || !recorderClock.IsStable)
        {
            return;
        }

        if (!recording.StartHostedMultiTrack(
                timeline.SessionId,
                timeline.StartServerTimestampMilliseconds,
                timeline.StartUtcUnixMilliseconds,
                recorderClock.OffsetMilliseconds,
                out string error))
        {
            multiTrackStartPending = false;
            SetRecorderListener(false);
            capi.ShowChatMessage(SVCLang.Get("chat-recording-failed", error));
            return;
        }

        recorderListenerActive = true;
        pendingRecorderTimeline = null;
        capture?.Start();
        if (recording.ActiveMultiTrackSession is AudioRecordingSession session)
        {
            audioBuses.NotifyRecordingSessionStarted(session);
        }
        capi.ShowChatMessage(SVCLang.Get("chat-recording-started", SVCLang.Get("recording-mode-multitrack")));
        settingsDialog?.RefreshConfiguration();
        hud?.Refresh();
    }

    private void OnRecorderCaptureStatePacket(RecorderCaptureStatePacket packet)
    {
        if (packet.Active
            && (!VoiceProtocolValidation.IsSafeRecorderSessionId(packet.RecordingSessionId)
                || packet.StartServerTimestampMilliseconds <= 0
                || packet.StartUtcUnixMilliseconds <= 0))
        {
            return;
        }
        recorderCaptureState = packet.Active ? packet : null;
        settingsDialog?.RefreshConfiguration();
        hud?.Refresh();
    }

    private void OnRecorderSessionStatusPacket(RecorderSessionStatusPacket packet)
    {
        recorderSessionStatus = packet;
        settingsDialog?.RefreshConfiguration();
    }

    private void OnRecorderFileChunkPacket(RecorderFileChunkPacket packet)
    {
        if (!string.IsNullOrWhiteSpace(packet.Error))
        {
            capi.ShowChatMessage(SVCLang.Get("chat-recording-download-failed", packet.Error));
            return;
        }
        if (recorderDownloads == null
            || recording == null
            || !VoiceProtocolValidation.IsSafeRecorderSessionId(packet.RecordingSessionId))
        {
            return;
        }
        if (!string.Equals(recorderDownloads.SessionId, packet.RecordingSessionId, StringComparison.Ordinal)
            && !recorderDownloads.Begin(packet.RecordingSessionId, out string beginError))
        {
            capi.ShowChatMessage(SVCLang.Get("chat-recording-download-failed", beginError));
            return;
        }

        bool completed = recorderDownloads.Accept(packet, out string error);
        if (!string.IsNullOrWhiteSpace(error))
        {
            capi.ShowChatMessage(SVCLang.Get("chat-recording-download-failed", error));
            return;
        }
        if (completed)
        {
            if (recording.CompleteHostedDownload(packet.RecordingSessionId))
            {
                capi.ShowChatMessage(SVCLang.Get("chat-recording-download-complete", packet.RecordingSessionId));
            }
            else
            {
                capi.ShowChatMessage(SVCLang.Get("chat-recording-download-failed", "session.core.json is unavailable"));
            }
            recorderDownloads.Reset();
            settingsDialog?.RefreshConfiguration();
        }
    }

    private void OnFastTick(float dt)
    {
        if (!lifecycle.IsStarted)
        {
            return;
        }
        bool pushToTalkPressed = IsPushToTalkPressed();
        bool activationMode = config.PreferVoiceActivation;
        bool pressed = activationMode || pushToTalkPressed;
        bool testRecordingActive = microphoneTest?.IsRecording == true;
        bool setupMonitoringActive = setupMicrophoneMonitoring;
        bool voiceReady = !testRecordingActive
            && !setupMonitoringActive
            && !localMuted
            && !globalMuted
            && serverConfig.Enabled
            && voiceHandshakeAccepted
            && capture?.IsAvailable == true
            && voiceChannel?.Connected == true;
        VoiceTransmitTarget transmitTarget = ResolveTransmitTarget(config.TransmitTarget, config.SelectedChannelId);
        bool directorCaptureReady = capture?.IsAvailable == true
            && directorVoice?.CanCaptureLocalFrame(transmitTarget, serverConfig) == true;
        bool captureReady = voiceReady || directorCaptureReady;
        bool speechCaptureReady = config.EnableSpeechRecognition
            && !localMuted
            && !testRecordingActive
            && !setupMonitoringActive
            && capi.Input.MouseGrabbed
            && capture?.IsAvailable == true;
        bool isRecording = recording?.IsRecording == true || testRecordingActive;
        bool canSpeak = false;
        if (pressed && capture?.IsAvailable != true && !captureWarningShown)
        {
            captureWarningShown = true;
            capi.ShowChatMessage(SVCLang.Get("chat-mic-unavailable", capture?.FailureReason ?? string.Empty));
        }

        bool speechPressed = speechCaptureReady && IsSpeechRecognitionPressed();
        if (speechPressed || speechRecognitionActive)
        {
            lastPressed = false;
            if (speechPressed)
            {
                if (!speechRecognitionActive)
                {
                    speechRecognitionActive = true;
                    speechRecognitionBuffer.Start();
                    capi.ShowChatMessage(SVCLang.Get("chat-speech-recognition-recording"));
                }
                capture?.Start();
                DrainCapturedFrames(sendVoice: false, captureSpeech: true);
            }
            else
            {
                DrainCapturedFrames(sendVoice: false, captureSpeech: true);
                capture?.Stop();
                StopSpeechRecognition();
            }
        }
        else if (testRecordingActive)
        {
            // A microphone test must stay local and must never transmit voice.
            lastPressed = false;
            capture?.Start();
            DrainCapturedFrames(sendVoice: false);
        }
        else if (setupMonitoringActive)
        {
            lastPressed = false;
            capture?.Start();
            DrainCapturedFrames(sendVoice: false);
        }
        else if (captureReady && activationMode)
        {
            if (!lastPressed)
            {
                BeginVoiceSession();
            }
            capture?.Start();
            bool triggered = DrainCapturedFrames(
                sendVoice: voiceReady,
                captureDirectorAudio: directorCaptureReady,
                requireVoiceActivation: true);
            canSpeak = triggered;
        }
        else if (captureReady && pushToTalkPressed)
        {
            if (!lastPressed)
            {
                BeginVoiceSession();
                capture?.Start();
            }
            DrainCapturedFrames(
                sendVoice: voiceReady,
                captureDirectorAudio: directorCaptureReady);
            canSpeak = true;
        }
        else if (lastPressed)
        {
            DrainCapturedFrames(
                sendVoice: voiceReady,
                captureDirectorAudio: directorCaptureReady);
            if (!isRecording)
            {
                capture?.Stop();
            }
        }
        else if (isRecording)
        {
            capture?.Start();
            DrainCapturedFrames(sendVoice: false);
        }

        if (!canSpeak && !setupMonitoringActive)
        {
            lastMicLevel = 0f;
            lastMicRms = 0f;
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
        long now = capi.World.ElapsedMilliseconds;
        UpdateAdaptiveBitrate(now);
        UpdateSettingsDiagnostics(now);
        UpdateRecorderClockControlFallback();
        SendRecorderParticipantState(force: false);
        directorVoice?.UpdateListener(controlChannel);
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

    private void UpdateAdaptiveBitrate(long nowMilliseconds)
    {
        if (voiceEncoder is not INetworkAdaptiveVoiceEncoder encoder)
        {
            return;
        }

        if (controlChannel?.Connected == true
            && nowMilliseconds - lastNetworkQualitySentMs >= VoiceProbeIntervalMilliseconds)
        {
            lastNetworkQualitySentMs = nowMilliseconds;
            controlChannel.SendPacket(new VoiceNetworkQualityPacket
            {
                ConnectionEpoch = connectionEpoch,
                RoundTripMilliseconds = voiceProbeTracker.SmoothedRttMilliseconds,
                ProbeLossPercent = voiceProbeTracker.LossPercent
            });
        }

        if (!adaptiveBitrate.IsEvaluationDue(nowMilliseconds))
        {
            return;
        }

        bool udpResponsive = voiceProbeTracker.IsResponsive(nowMilliseconds, VoiceProbeTimeoutMilliseconds);
        double roundTripMilliseconds = voiceProbeTracker.SmoothedRttMilliseconds;
        if (roundTripMilliseconds < 0
            && !udpResponsive
            && nowMilliseconds - voiceHandshakeAcceptedMs >= VoiceProbeTimeoutMilliseconds)
        {
            roundTripMilliseconds = VoiceProbeTimeoutMilliseconds;
        }
        if (adaptiveBitrate.Update(
                nowMilliseconds,
                udpResponsive,
                roundTripMilliseconds,
                voiceProbeTracker.LossPercent))
        {
            encoder.ConfigureNetwork(adaptiveBitrate.CurrentBitrate, adaptiveBitrate.PacketLossPercent);
        }
    }

    private void UpdateSettingsDiagnostics(long nowMilliseconds)
    {
        if (settingsDialog?.IsCurrentStatusOpen != true
            || nowMilliseconds - lastDiagnosticsRequestMs < 1_000)
        {
            return;
        }

        lastDiagnosticsRequestMs = nowMilliseconds;
        SendChannelCommand("diagnostics");
        settingsDialog.RefreshData();
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
            || now - lastVoiceProbeSentMs < (!recorderClock.IsStable
                ? RecorderClockProbeIntervalMilliseconds
                : VoiceProbeIntervalMilliseconds))
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
            Nonce = nonce,
            ClientSendTimestampMilliseconds = MonotonicClock.NowMilliseconds
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
        directorVoice?.Update(serverConfig);
        recorderVoice?.Update(MonotonicClock.NowMilliseconds);
        audioBuses.Flush(MonotonicClock.NowMilliseconds);
        bool playbackActive = playback?.IsRecordingPlaybackActive == true;
        if (playbackActive != lastRecordingPlaybackActive)
        {
            lastRecordingPlaybackActive = playbackActive;
            settingsDialog?.RefreshConfiguration();
        }
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

    private bool IsSpeechRecognitionPressed()
    {
        HotKey? hotKey = capi.Input.GetHotKeyByCode(VoiceConstants.SpeechRecognitionHotKey);
        int keyCode = hotKey?.CurrentMapping?.KeyCode ?? (int)GlKeys.V;
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

    private void ReinitializePlayback()
    {
        playback?.Dispose();
        playback = new OpenAlPlaybackService(capi, config);
        if (recording != null)
        {
            VoiceRecordingService recordingService = recording;
            playback.OutputFrameCaptured = samples => recordingService.AppendOutput(samples);
            playback.RemoteFrameCaptured = (entityId, uid, samples, timestamp) => CaptureMultiTrackRemote(recordingService, entityId, uid, samples, timestamp);
            playback.RemoteFrameCaptured += (_, _, samples, timestamp) =>
            {
                if (!recorderListenerActive)
                {
                    audioBuses.SubmitAt(AudioBusKind.PlayerVoice, samples, ToLocalAudioTimestamp(timestamp));
                }
            };
        }
        playback.Initialize();
        lastRecordingPlaybackActive = false;
        settingsDialog?.RefreshConfiguration();
        capi.ShowChatMessage(SVCLang.Get("chat-output-device-switched", string.IsNullOrWhiteSpace(config.OutputDeviceName) ? SVCLang.Get("default-speaker") : config.OutputDeviceName));
    }

    private bool DrainCapturedFrames(
        bool sendVoice,
        bool captureDirectorAudio = false,
        bool requireVoiceActivation = false,
        bool captureSpeech = false)
    {
        int processedFrames = 0;
        bool hadFrame = false;
        float peakMicLevel = 0f;
        bool activationDetected = false;
        if (!requireVoiceActivation)
        {
            voiceActivationHangoverFrames = 0;
            voiceActivationTriggered = false;
        }

        while (processedFrames < MaxCaptureFramesPerTick
            && capture is not null
            && capture.TryReadFrame(captureBuffer, out long captureTimestampMilliseconds))
        {
            hadFrame = true;
            processedFrames++;

            VoiceFrameStats stats = capturePreprocessor.Process(captureBuffer, config.MicGain, config.NoiseGate);
            lastMicRms = stats.Rms;
            if (requireVoiceActivation && stats.Active && stats.Rms >= config.VoiceActivationThreshold)
            {
                activationDetected = true;
                voiceActivationHangoverFrames = 8;
            }
            else if (requireVoiceActivation && voiceActivationHangoverFrames > 0)
            {
                voiceActivationHangoverFrames--;
            }
            if (recording?.IsRecording == true)
            {
                long recordingTimestamp = recording.Mode == VoiceRecordingMode.MultiTrack
                    ? recorderClock.ToServerTime(captureTimestampMilliseconds)
                    : captureTimestampMilliseconds;
                recording.AppendInput(captureBuffer, recordingTimestamp);
            }
            if (microphoneTest?.IsRecording == true)
            {
                microphoneTest.AppendInput(captureBuffer);
            }
            if (captureSpeech)
            {
                speechRecognitionBuffer.Append(captureBuffer);
            }
            if (!sendVoice && !captureDirectorAudio)
            {
                continue;
            }
            bool active = stats.Active && (!requireVoiceActivation || activationDetected || voiceActivationHangoverFrames > 0);
            VoiceTransmitTarget transmitTarget = ResolveTransmitTarget(config.TransmitTarget, config.SelectedChannelId);
            // Director records the local proximity microphone independently of
            // VAD. VAD still controls network transmission below.
            if (captureDirectorAudio)
            {
                directorVoice?.SubmitLocalFrame(
                    captureBuffer,
                    captureTimestampMilliseconds,
                    mode,
                    transmitTarget,
                    serverConfig);
            }
            if (!active)
            {
                continue;
            }

            peakMicLevel = Math.Max(peakMicLevel, NormalizeVoiceLevel(stats.Rms, mode));
            lastVoiceLevelMs = capi.World.ElapsedMilliseconds;

            long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            byte[] payload = voiceEncoder?.Encode(captureBuffer) ?? Array.Empty<byte>();
            encodedFrameAllocationCount++;
            encodedFrameAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
            if (payload.Length == 0)
            {
                continue;
            }
            if (payload.Length + 64 > VoiceConstants.MaxUdpPacketBytes)
            {
                capi.Logger.Warning("SimpleVoiceChat: encoded voice frame too large ({0} bytes), skipping.", payload.Length);
                continue;
            }

            SendCapturedFrame(payload, stats, captureTimestampMilliseconds);
        }

        if (hadFrame)
        {
            lastMicLevel = peakMicLevel;
        }

        if (requireVoiceActivation)
        {
            voiceActivationTriggered = activationDetected || voiceActivationHangoverFrames > 0;
        }
        return !requireVoiceActivation || voiceActivationTriggered;
    }

    private void SendCapturedFrame(byte[] payload, VoiceFrameStats stats, long captureTimestampMilliseconds)
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

        long captureServerTimestamp = recorderClock.IsStable
            ? recorderClock.ToServerTime(captureTimestampMilliseconds)
            : 0L;
        ushort frameSequence = sequence++;
        voiceChannel?.SendPacket(new VoiceFrameV3Packet
        {
            ConnectionEpoch = connectionEpoch,
            SessionId = sessionId,
            Sequence = frameSequence,
            Mode = mode,
            Target = transmitTarget,
            ChannelId = config.SelectedChannelId,
            Level = (byte)Math.Clamp((int)Math.Round(stats.Rms * byte.MaxValue), 0, byte.MaxValue),
            Flags = 0,
            Payload = payload,
            CaptureServerTimestampMilliseconds = captureServerTimestamp
        });
        if (recorderCaptureState?.Active == true && controlChannel?.Connected == true)
        {
            controlChannel.SendPacket(new RecorderUploadFramePacket
            {
                RecordingSessionId = recorderCaptureState.RecordingSessionId,
                ConnectionEpoch = connectionEpoch,
                VoiceSessionId = sessionId,
                Sequence = frameSequence,
                Payload = payload,
                CaptureServerTimestampMilliseconds = captureServerTimestamp
            });
        }
    }

    private void UpdateRecorderClockControlFallback()
    {
        long now = capi.World.ElapsedMilliseconds;
        recorderClockControlProbeTracker.Expire(now, VoiceProbeTimeoutMilliseconds);
        if (recorderClock.IsStable
            || !voiceHandshakeAccepted
            || controlChannel?.Connected != true
            || voiceProbeTracker.IsResponsive(now, VoiceProbeTimeoutMilliseconds)
            || now - voiceHandshakeAcceptedMs < RecorderClockControlFallbackDelayMilliseconds
            || now - lastRecorderClockControlProbeSentMs < RecorderClockProbeIntervalMilliseconds)
        {
            return;
        }

        if (nextVoiceProbeNonce == int.MaxValue)
        {
            nextVoiceProbeNonce = 1;
        }
        int nonce = nextVoiceProbeNonce++;
        lastRecorderClockControlProbeSentMs = now;
        recorderClockControlProbeTracker.MarkSent(nonce, now);
        controlChannel.SendPacket(new VoicePingPacket
        {
            ConnectionEpoch = connectionEpoch,
            Nonce = nonce,
            ClientSendTimestampMilliseconds = MonotonicClock.NowMilliseconds
        });
    }

    private void SendRecorderParticipantState(bool force)
    {
        long now = capi.World.ElapsedMilliseconds;
        if (!voiceHandshakeAccepted
            || controlChannel?.Connected != true
            || (!force && now - lastRecorderParticipantStateSentMs < 1_000L))
        {
            return;
        }
        lastRecorderParticipantStateSentMs = now;
        controlChannel.SendPacket(new RecorderParticipantStatePacket
        {
            ConnectionEpoch = connectionEpoch,
            ClockReady = recorderClock.IsStable,
            ClockSampleCount = recorderClock.SampleCount,
            BestRoundTripMilliseconds = double.IsFinite(recorderClock.BestRoundTripMilliseconds)
                ? recorderClock.BestRoundTripMilliseconds
                : 10_000d,
            ClientUtcUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    private long ToLocalAudioTimestamp(long serverTimestampMilliseconds)
    {
        return serverTimestampMilliseconds > 0 && recorderClock.HasEstimate
            ? recorderClock.ToClientTime(serverTimestampMilliseconds)
            : MonotonicClock.NowMilliseconds;
    }

    private void StopSpeechRecognition()
    {
        if (!speechRecognitionActive)
        {
            return;
        }

        speechRecognitionActive = false;
        if (!speechRecognitionBuffer.Stop(out byte[] wavAudio))
        {
            capi.ShowChatMessage(SVCLang.Get("chat-speech-recognition-too-short"));
            return;
        }

        if (speechRecognitionClient == null)
        {
            return;
        }

        speechRecognitionCancellation?.Cancel();
        speechRecognitionCancellation?.Dispose();
        speechRecognitionCancellation = new CancellationTokenSource();
        capi.ShowChatMessage(SVCLang.Get("chat-speech-recognition-recognizing"));
        _ = CompleteSpeechRecognitionAsync(wavAudio, speechRecognitionCancellation.Token);
    }

    private async Task CompleteSpeechRecognitionAsync(byte[] wavAudio, CancellationToken cancellationToken)
    {
        ISpeechRecognitionClient? client = speechRecognitionClient;
        if (client == null)
        {
            return;
        }

        SpeechRecognitionResult result = await client.TranscribeAsync(wavAudio, config, cancellationToken).ConfigureAwait(false);
        capi.Event.EnqueueMainThreadTask(() =>
        {
            if (!lifecycle.IsStarted || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!result.Succeeded)
            {
                capi.ShowChatMessage(SVCLang.Get("chat-speech-recognition-failed", result.Error));
                return;
            }

            string text = result.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.StartsWith('/'))
            {
                text = text.TrimStart('/').TrimStart();
            }
            if (text.Length == 0)
            {
                capi.ShowChatMessage(SVCLang.Get("speech-recognition-error-empty"));
                return;
            }

            if (text.Length > 512)
            {
                text = text[..512];
            }
            capi.SendChatMessage(text, string.Empty);
        }, "simplevoicechat-speech-recognition");
    }

    private static ISpeechRecognitionClient CreateSpeechRecognitionClient(string provider)
        => provider switch
        {
            SimpleVoiceChatClientConfig.SiliconFlowSpeechRecognitionProvider => new SiliconFlowSpeechRecognitionClient(),
            SimpleVoiceChatClientConfig.DeepgramSpeechRecognitionProvider => new DeepgramSpeechRecognitionClient(),
            SimpleVoiceChatClientConfig.WhisperSpeechRecognitionProvider => new WhisperSpeechRecognitionClient(),
            _ => new AlibabaSpeechRecognitionClient()
        };

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
        if (multiTrackStartPending)
        {
            return SVCLang.Get("hud-status-multitrack-syncing");
        }
        if (recording?.Mode == VoiceRecordingMode.MultiTrack)
        {
            return SVCLang.Get("hud-status-multitrack-recording");
        }
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
            return config.PreferVoiceActivation ? SVCLang.Get("hud-status-always-talking") : SVCLang.Get("hud-status-speaking");
        }
        return config.PreferVoiceActivation ? SVCLang.Get("hud-status-always-standby") : SVCLang.Get("hud-status-mic-ready");
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
            $"{SVCLang.Get("summary-line-toggle-talk", toggleTalk, config.PreferVoiceActivation ? SVCLang.Get("state-on") : SVCLang.Get("state-off"))}\n" +
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
        speechRecognitionCancellation?.Cancel();
        speechRecognitionCancellation?.Dispose();
        speechRecognitionCancellation = null;
        speechRecognitionBuffer.Cancel();
        speechRecognitionClient?.Dispose();
        speechRecognitionClient = null;
        if (fastTickListenerId != 0)
        {
            capi.Event.UnregisterGameTickListener(fastTickListenerId);
            fastTickListenerId = 0;
        }
        recorderListenerActive = false;
        recorderListenerRequested = false;
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
        directorVoice?.Dispose();
        directorVoice = null;
        recorderVoice?.Dispose();
        recorderVoice = null;
        recorderDownloads?.Dispose();
        recorderDownloads = null;
        recording?.Dispose();
        recording = null;
        audioBusPipeBridge?.Dispose();
        audioBusPipeBridge = null;
        audioBuses.Dispose();
        microphoneTest = null;
        hud?.TryClose();
        hud?.Dispose();
        hud = null;
        settingsDialog?.TryClose();
        settingsDialog?.Dispose();
        settingsDialog = null;
        setupWizard?.TryClose();
        setupWizard?.Dispose();
        setupWizard = null;
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
