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
    private int debugPlaybackIndex;
    private ushort debugPlaybackSequence;

    public ClientVoiceController(ICoreClientAPI capi, SimpleVoiceChatClientConfig config)
    {
        this.capi = capi;
        this.config = config;
        sessionId = Random.Shared.Next(1, int.MaxValue);
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
            SaveConfig,
            () => hud?.Refresh(),
            ReinitializeCapture,
            StartDebugRecording,
            PlayDebugRecording);

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
        capi.Input.RegisterHotKey(VoiceConstants.PushToTalkHotKey, "简单语音对话：按住说话", GlKeys.N, HotkeyType.CharacterControls);
        capi.Input.RegisterHotKey(VoiceConstants.ToggleTalkHotKey, "简单语音对话：持续说话开关", GlKeys.N, HotkeyType.GUIOrOtherControls, altPressed: true);
        capi.Input.RegisterHotKey(VoiceConstants.ModeCycleHotKey, "简单语音对话：切换模式", GlKeys.LBracket, HotkeyType.CharacterControls);
        capi.Input.RegisterHotKey(VoiceConstants.ModeCycleAltHotKey, "简单语音对话：切换模式备用", GlKeys.RBracket, HotkeyType.CharacterControls);
        capi.Input.RegisterHotKey(VoiceConstants.LocalMuteHotKey, "简单语音对话：麦克风静音", GlKeys.Minus, HotkeyType.GUIOrOtherControls, ctrlPressed: true);
        capi.Input.RegisterHotKey(VoiceConstants.GlobalMuteHotKey, "简单语音对话：全局语音开关", GlKeys.Semicolon, HotkeyType.CharacterControls);
        capi.Input.RegisterHotKey(VoiceConstants.SettingsHotKey, "简单语音对话：状态/设置窗口", GlKeys.Quote, HotkeyType.GUIOrOtherControls);

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
        capi.ShowChatMessage($"简单语音对话：麦克风已{(localMuted ? "静音" : "取消静音")}。");
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
        capi.ShowChatMessage($"简单语音对话：全局语音已{(globalMuted ? "关闭" : "开启")}。");
        SendState(force: true);
        hud?.Refresh();
    }

    private void ToggleContinuousTalk()
    {
        toggleTalkEnabled = !toggleTalkEnabled;
        capi.ShowChatMessage($"简单语音对话：持续说话已{(toggleTalkEnabled ? "开启" : "关闭")}。");
        SendState(force: true);
        hud?.Refresh();
    }

    private void RegisterCommands()
    {
        capi.ChatCommands.Create("svc")
            .WithDescription("SimpleVoiceChat client controls")
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
                    return TextCommandResult.Error("用法：/svc volume <0-200>");
                }
                config.OutputVolume = value / 100f;
                SaveConfig();
                return TextCommandResult.Success($"简单语音对话：播放音量已设为 {value}%。");
            }

            case "mute":
            case "unmute":
            {
                string name = args.RawArgs.PopWord("");
                if (string.IsNullOrWhiteSpace(name))
                {
                    return TextCommandResult.Error($"用法：/svc {sub} <玩家名>");
                }

                IPlayer? player = capi.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (player == null)
                {
                    return TextCommandResult.Error($"找不到在线玩家：{name}。");
                }

                bool muted = sub == "mute";
                SetMuted(player.PlayerUID, muted);
                return TextCommandResult.Success($"已{(muted ? "屏蔽" : "取消屏蔽")} {player.PlayerName}。");
            }

            case "bind":
            {
                IPlayer? target = GetSelectedPlayer();
                if (target == null)
                {
                    return TextCommandResult.Error("请面对近处玩家后输入 /svc bind。");
                }

                controlChannel?.SendPacket(new SquadBindPacket { TargetPlayerUid = target.PlayerUID });
                return TextCommandResult.Success($"已请求与 {target.PlayerName} 绑定小队频道。");
            }

            case "unbind":
                controlChannel?.SendPacket(new SquadBindPacket { LeaveSquad = true });
                return TextCommandResult.Success("已请求离开当前小队频道。");

            case "squad":
                controlChannel?.SendPacket(new SquadBindPacket { RequestStatus = true });
                return TextCommandResult.Success("已请求小队频道状态。");

            case "adminmute":
            case "adminunmute":
            case "forceblock":
            case "unforceblock":
            {
                string nameOrUid = args.RawArgs.PopWord("");
                if (string.IsNullOrWhiteSpace(nameOrUid))
                {
                    return TextCommandResult.Error($"用法：/svc {sub} <玩家名或UID>");
                }

                controlChannel?.SendPacket(new AdminVoiceControlPacket { Action = sub, TargetNameOrUid = nameOrUid });
                return TextCommandResult.Success("已发送管理员语音控制请求。");
            }

            case "adminmutes":
                controlChannel?.SendPacket(new AdminVoiceControlPacket { Action = sub });
                return TextCommandResult.Success("已请求管理员语音屏蔽列表。");

            default:
                return TextCommandResult.Error("用法：/svc status|volume|mute|unmute|bind|unbind|squad");
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
            capi.ShowChatMessage($"简单语音对话：麦克风不可用。{capture?.FailureReason}");
        }

        if (canSpeak)
        {
            if (!lastPressed)
            {
                capture?.Start();
            }
            CaptureAndSend();
        }
        else if (lastPressed)
        {
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
        if (!TryReadActiveEncodedFrame(out byte[] payload, out VoiceFrameStats stats))
        {
            return;
        }

        RecordDebugFrame(payload, stats.Rms, mode);

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

    private void CaptureDebugFrameOnly()
    {
        if (!EnsureDebugCaptureRunning() || !TryReadActiveEncodedFrame(out byte[] payload, out VoiceFrameStats stats))
        {
            return;
        }

        RecordDebugFrame(payload, stats.Rms, mode);
    }

    private bool TryReadActiveEncodedFrame(out byte[] payload, out VoiceFrameStats stats)
    {
        payload = Array.Empty<byte>();
        stats = default;
        if (capture?.TryReadFrame(captureBuffer) != true)
        {
            return false;
        }

        stats = AudioPreprocessor.Process(captureBuffer, config.MicGain, config.NoiseGate);
        if (!stats.Active)
        {
            lastMicLevel = 0f;
            return false;
        }

        lastMicLevel = NormalizeVoiceLevel(stats.Rms, mode);
        lastVoiceLevelMs = capi.World.ElapsedMilliseconds;

        payload = ImaAdpcmCodec.Encode(captureBuffer);
        if (payload.Length + 64 > VoiceConstants.MaxUdpPacketBytes)
        {
            capi.Logger.Warning("SimpleVoiceChat: encoded voice frame too large ({0} bytes), skipping.", payload.Length);
            payload = Array.Empty<byte>();
            return false;
        }

        return true;
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

        capi.ShowChatMessage($"简单语音对话：语音模式已切换为 {FormatMode(mode)}。");
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
        capi.ShowChatMessage($"简单语音对话：麦克风输入设备已切换为 {(string.IsNullOrWhiteSpace(config.InputDeviceName) ? "默认麦克风" : config.InputDeviceName)}。");
    }

    private bool StartDebugRecording()
    {
        if (capture?.IsAvailable != true)
        {
            capi.ShowChatMessage($"简单语音对话：无法开始调试录音，麦克风不可用。{capture?.FailureReason}");
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

        capi.ShowChatMessage("简单语音对话：开始调试录音 3 秒。本地录制将经过发送前处理与 ADPCM 编码，但不会发给服务器。");
        return true;
    }

    private bool PlayDebugRecording()
    {
        if (debugRecording)
        {
            capi.ShowChatMessage("简单语音对话：调试录音仍在进行，请录制完成后再播放。");
            return true;
        }

        if (debugRecordingFrames.Count == 0)
        {
            capi.ShowChatMessage("简单语音对话：还没有可播放的调试录音，或录音期间没有超过噪声门的麦克风输入。");
            return true;
        }

        if (playback == null)
        {
            capi.ShowChatMessage("简单语音对话：播放服务未初始化，无法播放调试录音。");
            return true;
        }

        debugPlaybackActive = true;
        debugPlaybackIndex = 0;
        debugPlaybackSequence = 0;
        debugPlaybackStartMs = capi.World.ElapsedMilliseconds;
        debugPlaybackEntityId--;
        if (debugPlaybackEntityId >= 0)
        {
            debugPlaybackEntityId = InitialDebugPlaybackEntityId;
        }

        capi.ShowChatMessage($"简单语音对话：开始播放调试录音（{debugRecordingFrames.Count} 帧）。播放会使用游戏 OpenAL 3D 声场，并叠加语音传播所需的距离/遮挡/水下低通修正。");
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
        string suffix = debugRecordingFrames.Count == 0 ? "没有捕获到超过噪声门的声音。" : $"捕获 {debugRecordingFrames.Count} 帧，可在状态窗口点击播放录音。";
        capi.ShowChatMessage($"简单语音对话：调试录音完成，{suffix}");
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
            capi.ShowChatMessage("简单语音对话：调试录音播放结束。");
        }
    }

    private void EnqueueDebugPlaybackFrame(DebugVoiceFrame frame)
    {
        VoiceFramePacket packet = new()
        {
            SenderUidHash = VoiceMath.StableUidHash(capi.World.Player.PlayerUID + ":debug"),
            SenderEntityId = debugPlaybackEntityId,
            SessionId = sessionId,
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

    private VoiceHudSnapshot BuildHudSnapshot()
    {
        bool captureAvailable = capture?.IsAvailable == true;
        bool micEnabled = !localMuted && !globalMuted && serverConfig.Enabled && captureAvailable;
        string status = BuildHudStatus(captureAvailable);
        string detail = voiceChannel?.Connected == true ? "UDP OK" : "UDP WAIT";
        if (!captureAvailable)
        {
            detail = "麦克风不可用";
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
        Vec3d listener = capi.World.Player.Entity.Pos.XYZ;
        double distance = listener.DistanceTo(packet.X, packet.Y, packet.Z);
        float range = Math.Min(serverConfig.GetRange(packet.Mode), serverConfig.MaxRange);
        float distanceGain = EstimateOpenAlDistanceGain(distance, range);
        if (packet.SquadRelay && distance > range)
        {
            distanceGain = 0.62f;
        }
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
            return "语音关闭";
        }

        if (!captureAvailable)
        {
            return "麦克风不可用";
        }

        if (localMuted)
        {
            return "麦克风静音";
        }

        if (lastSpeaking)
        {
            return toggleTalkEnabled ? "持续说话" : "正在说话";
        }

        return toggleTalkEnabled ? "持续待机" : "麦克风就绪";
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
            $"语音模式：{FormatMode(mode)}\n" +
            $"语音总开关：{(serverConfig.Enabled && !globalMuted ? "开启" : "关闭")}\n" +
            $"麦克风：{(capture?.IsAvailable == true ? (localMuted ? "静音" : "就绪") : "不可用")}\n" +
            $"播放音量：{(int)(config.OutputVolume * 100)}%\n" +
            $"按住说话：{ptt}\n" +
            $"持续说话：{toggleTalk}（当前：{(toggleTalkEnabled ? "开启" : "关闭")}）\n" +
            $"切换模式：{cycle} / {cycleAlt}\n" +
            $"麦克风静音：{localMute}    全局开关：{globalMute}\n" +
            $"打开状态/设置窗口：{settings}\n" +
            $"调试录音：{BuildDebugRecordingStatus()}\n" +
            $"{VoiceEnvironment.BuildDebugSummary(capi, config, serverConfig)}\n" +
            $"命令：/svc volume <0-200>, /svc mute <玩家名>, /svc bind, /svc unbind";
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
            $"模式：{FormatMode(mode)}    总开关：{(serverConfig.Enabled && !globalMuted ? "开启" : "关闭")}\n" +
            $"麦克风：{(capture?.IsAvailable == true ? (localMuted ? "静音" : "就绪") : "不可用")}\n" +
            $"按住说话：{ptt}    持续说话：{toggleTalk}（{(toggleTalkEnabled ? "开" : "关")}）\n" +
            $"切换模式：{cycle} / {cycleAlt}\n" +
            $"麦克风静音：{localMute}    全局开关：{globalMute}\n" +
            $"调试录音：{BuildDebugRecordingStatus()}\n" +
            VoiceEnvironment.BuildDebugSummary(capi, config, serverConfig);
    }

    private string BuildDebugRecordingStatus()
    {
        if (debugRecording)
        {
            float remaining = Math.Max(0, debugRecordingEndMs - capi.World.ElapsedMilliseconds) / 1000f;
            return $"录制中，剩余 {remaining:0.0}s，已捕获 {debugRecordingFrames.Count} 帧";
        }

        if (debugPlaybackActive)
        {
            return $"播放中 {debugPlaybackIndex}/{debugRecordingFrames.Count} 帧";
        }

        if (debugRecordingFrames.Count > 0)
        {
            int duration = Math.Min(DebugRecordingMilliseconds, debugRecordingFrames[^1].OffsetMilliseconds + VoiceConstants.FrameMilliseconds);
            return $"已录制 {debugRecordingFrames.Count} 帧，约 {duration / 1000f:0.0}s";
        }

        return "未录制";
    }

    private static string FormatMode(VoiceMode voiceMode)
    {
        return voiceMode switch
        {
            VoiceMode.Whisper => "耳语",
            VoiceMode.Shout => "大喊",
            _ => "正常说话"
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
