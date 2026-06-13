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
    private const int MaxCaptureFramesPerTick = 8;
    private const long InitialDebugPlaybackEntityId = -900001;

    private readonly ICoreClientAPI capi;
    private readonly SimpleVoiceChatClientConfig config;
    private IClientNetworkChannel? controlChannel;
    private IClientNetworkChannel? voiceChannel;
    private OpenAlCaptureService? capture;
    private OpenAlPlaybackService? playback;
    private VoiceHud? hud;
    private VoiceSettingsDialog? settingsDialog;
    private readonly short[] captureBuffer = new short[VoiceConstants.SamplesPerFrame];
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
    private bool disposed;
    private bool localMutePressed;
    private bool globalMutePressed;
    private bool settingsPressed;
    private bool toggleTalkPressed;
    private long lastStateSentMs;
    private float lastMicLevel;
    private float lastRemoteVoiceLevel;
    private long lastVoiceLevelMs;
    private VoiceHudSquadMember[] squadHudMembers = Array.Empty<VoiceHudSquadMember>();
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

    public ClientVoiceController(ICoreClientAPI capi, SimpleVoiceChatClientConfig config)
    {
        this.capi = capi;
        this.config = config;
        sessionId = NextSessionId();
    }

    public void Start()
    {
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
            RequestSquadStatus);

        capi.Event.KeyUp += OnKeyUp;
        capi.Event.RegisterGameTickListener(OnFastTick, VoiceConstants.FrameMilliseconds);
        capi.Event.RegisterGameTickListener(OnPlaybackTick, VoiceConstants.FrameMilliseconds);
        capi.Event.RegisterGameTickListener(OnSlowTick, 100);
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
            .SetMessageHandler<ServerVoiceConfigPacket>(OnServerConfig)
            .SetMessageHandler<SquadHudPacket>(OnSquadHud);

        voiceChannel = capi.Network.RegisterUdpChannel(VoiceConstants.VoiceChannelName)
            .RegisterMessageType<VoiceFramePacket>()
            .SetMessageHandler<VoiceFramePacket>(OnVoiceFrame);
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
            CycleMode();
            return true;
        });
        capi.Input.SetHotKeyHandler(VoiceConstants.ModeCycleAltHotKey, _ =>
        {
            CycleMode();
            return true;
        });
        capi.Input.SetHotKeyHandler(VoiceConstants.ToggleTalkHotKey, _ =>
        {
            if (!toggleTalkPressed)
            {
                toggleTalkPressed = true;
                ToggleContinuousTalk();
            }
            return true;
        });
        capi.Input.SetHotKeyHandler(VoiceConstants.LocalMuteHotKey, _ =>
        {
            if (!localMutePressed)
            {
                localMutePressed = true;
                ToggleLocalMute();
            }
            return true;
        });
        capi.Input.SetHotKeyHandler(VoiceConstants.GlobalMuteHotKey, _ =>
        {
            if (!globalMutePressed)
            {
                globalMutePressed = true;
                ToggleGlobalMute();
            }
            return true;
        });
        capi.Input.SetHotKeyHandler(VoiceConstants.SettingsHotKey, _ =>
        {
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
        toggleTalkEnabled = !toggleTalkEnabled;
        capi.ShowChatMessage(SVCLang.Get("chat-continuous-talk", toggleTalkEnabled ? SVCLang.Get("state-on") : SVCLang.Get("state-off")));
        SendState(force: true);
        hud?.Refresh();
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

                controlChannel?.SendPacket(new SquadBindPacket { TargetPlayerUid = target.PlayerUID });
                return TextCommandResult.Success(SVCLang.Get("command-request-bind-squad", target.PlayerName));
            }

            case "unbind":
                controlChannel?.SendPacket(new SquadBindPacket { LeaveSquad = true });
                return TextCommandResult.Success(SVCLang.Get("command-request-leave-squad"));

            case "squad":
                controlChannel?.SendPacket(new SquadBindPacket { RequestStatus = true });
                return TextCommandResult.Success(SVCLang.Get("command-request-squad-status"));

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

                controlChannel?.SendPacket(new AdminVoiceControlPacket { Action = sub, TargetNameOrUid = nameOrUid });
                return TextCommandResult.Success(SVCLang.Get("command-request-admin-control"));
            }

            case "adminmutes":
                controlChannel?.SendPacket(new AdminVoiceControlPacket { Action = sub });
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
        serverConfig = packet;
        hud?.Refresh();
    }

    private void OnSquadHud(SquadHudPacket packet)
    {
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
        if (globalMuted || packet.SenderEntityId == capi.World.Player.Entity.EntityId)
        {
            return;
        }

        IPlayer? sender = capi.World.AllOnlinePlayers.FirstOrDefault(p => p.Entity?.EntityId == packet.SenderEntityId);
        if (sender != null && config.MutedPlayerUids.Contains(sender.PlayerUID))
        {
            return;
        }

        playback?.Enqueue(packet, serverConfig);
        lastRemoteVoiceLevel = Math.Max(lastRemoteVoiceLevel, NormalizeRemoteVoiceLevel(packet));
        lastVoiceLevelMs = capi.World.ElapsedMilliseconds;
        hud?.Refresh();
    }

    private void OnFastTick(float dt)
    {
        bool pressed = toggleTalkEnabled || IsPushToTalkPressed();
        bool canSpeak = pressed && !localMuted && !globalMuted && serverConfig.Enabled && capture?.IsAvailable == true && voiceChannel?.Connected == true;

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

    private void OnPlaybackTick(float dt)
    {
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
        controlChannel?.SendPacket(new MutePlayerPacket { PlayerUid = playerUid, Muted = muted });
    }

    private void SendState(bool force)
    {
        long now = capi.World.ElapsedMilliseconds;
        if (!force && now - lastStateSentMs < 1000)
        {
            return;
        }

        lastStateSentMs = now;
        controlChannel?.SendPacket(new ClientVoiceStatePacket
        {
            Mode = mode,
            LocalMuted = localMuted,
            GlobalMuted = globalMuted,
            IsSpeaking = lastSpeaking
        });
    }

    private void SyncMutedPlayersToServer()
    {
        if (controlChannel == null)
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

        controlChannel.SendPacket(new SquadBindPacket { LeaveSquad = true });
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

        controlChannel.SendPacket(new SquadBindPacket { DisbandSquad = true });
        squadHudMembers = Array.Empty<VoiceHudSquadMember>();
        hud?.Refresh();
        settingsDialog?.RefreshStatusTexts();
        capi.ShowChatMessage(SVCLang.Get("chat-requested-disband-squad"));
        return true;
    }

    private void RequestSquadStatus()
    {
        controlChannel?.SendPacket(new SquadBindPacket { RequestStatus = true });
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

            VoiceFrameStats stats = AudioPreprocessor.Process(captureBuffer, config.MicGain, config.NoiseGate);
            if (!stats.Active)
            {
                continue;
            }

            peakMicLevel = Math.Max(peakMicLevel, NormalizeVoiceLevel(stats.Rms, mode));
            lastVoiceLevelMs = capi.World.ElapsedMilliseconds;

            byte[] payload = ImaAdpcmCodec.Encode(captureBuffer);
            if (payload.Length + 64 > VoiceConstants.MaxUdpPacketBytes)
            {
                capi.Logger.Warning("SimpleVoiceChat: encoded voice frame too large ({0} bytes), skipping.", payload.Length);
                continue;
            }

            RecordDebugFrame(payload, stats.Rms, mode);
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
        Vec3d pos = capi.World.Player.Entity.Pos.XYZ;
        voiceChannel?.SendPacket(new VoiceFramePacket
        {
            SenderUidHash = VoiceMath.StableUidHash(capi.World.Player.PlayerUID),
            SenderEntityId = capi.World.Player.Entity.EntityId,
            SessionId = sessionId,
            Sequence = sequence++,
            Mode = mode,
            Rms = stats.Rms,
            Flags = 0,
            Payload = payload,
            X = (float)pos.X,
            Y = (float)pos.Y,
            Z = (float)pos.Z
        });
    }

    private void BeginVoiceSession()
    {
        sessionId = NextSessionId();
        sequence = 0;
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
        bool micEnabled = !localMuted && !globalMuted && serverConfig.Enabled && captureAvailable;
        string status = BuildHudStatus(captureAvailable);
        string detail = voiceChannel?.Connected == true ? SVCLang.Get("hud-detail-udp-ok") : SVCLang.Get("hud-detail-udp-wait");
        if (!captureAvailable)
        {
            detail = SVCLang.Get("hud-detail-mic-unavailable");
        }

        float voiceLevel = Math.Max(lastMicLevel, lastRemoteVoiceLevel);
        if (voiceLevel < 0.015f)
        {
            voiceLevel = 0f;
        }

        return new VoiceHudSnapshot(micEnabled, voiceLevel > 0f, voiceLevel, status, FormatMode(mode), detail, squadHudMembers);
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
        if (!serverConfig.Enabled || globalMuted)
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
        return config.ShowMicrophoneHud;
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
            $"{SVCLang.Get("summary-line-debug-recording", BuildDebugRecordingStatus())}\n" +
            VoiceEnvironment.BuildDebugSummary(capi, config, serverConfig);
    }

    private string BuildSquadStatusSummary()
    {
        if (!serverConfig.Enabled)
        {
            return SVCLang.Get("squad-status-voice-off");
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
        if (disposed)
        {
            return;
        }

        disposed = true;
        capi.Event.KeyUp -= OnKeyUp;
        capture?.Dispose();
        playback?.Dispose();
        hud?.TryClose();
        settingsDialog?.TryClose();
    }

    private readonly record struct DebugVoiceFrame(byte[] Payload, float Rms, VoiceMode Mode, int OffsetMilliseconds, Vec3f Position);
}
