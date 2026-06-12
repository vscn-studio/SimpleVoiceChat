using SimpleVoiceChat.Audio;
using SimpleVoiceChat.Config;
using SimpleVoiceChat.Gui;
using SimpleVoiceChat.Networking;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace SimpleVoiceChat;

public sealed class ClientVoiceController : IDisposable
{
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
        settingsDialog = new VoiceSettingsDialog(capi, config, BuildSettingsWindowSummary, SaveConfig, () => hud?.Refresh(), ReinitializeCapture);

        capi.Event.KeyUp += OnKeyUp;
        capi.Event.RegisterGameTickListener(OnFastTick, VoiceConstants.FrameMilliseconds);
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
            .SetMessageHandler<ServerVoiceConfigPacket>(OnServerConfig);

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

            default:
                return TextCommandResult.Error("用法：/svc status|volume|mute|unmute");
        }
    }

    private void OnServerConfig(ServerVoiceConfigPacket packet)
    {
        serverConfig = packet;
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

        playback?.Enqueue(packet);
        lastRemoteVoiceLevel = Math.Max(lastRemoteVoiceLevel, NormalizeRemoteVoiceLevel(packet));
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

        if (!canSpeak)
        {
            lastMicLevel *= 0.82f;
            if (lastMicLevel < 0.01f)
            {
                lastMicLevel = 0f;
            }
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
        playback?.Update(serverConfig);
        SendState(force: false);
        if (!lastSpeaking)
        {
            lastMicLevel *= 0.86f;
        }
        lastRemoteVoiceLevel *= 0.72f;
        if (lastRemoteVoiceLevel < 0.01f)
        {
            lastRemoteVoiceLevel = 0f;
        }
        hud?.Refresh();
    }

    private void CaptureAndSend()
    {
        if (capture?.TryReadFrame(captureBuffer) != true || voiceChannel?.Connected != true)
        {
            return;
        }

        VoiceFrameStats stats = AudioPreprocessor.Process(captureBuffer, config.MicGain, config.NoiseGate);
        lastMicLevel = NormalizeVoiceLevel(stats.Rms);
        if (!stats.Active)
        {
            return;
        }

        byte[] payload = ImaAdpcmCodec.Encode(captureBuffer);
        if (payload.Length + 64 > VoiceConstants.MaxUdpPacketBytes)
        {
            capi.Logger.Warning("SimpleVoiceChat: encoded voice frame too large ({0} bytes), skipping.", payload.Length);
            return;
        }

        Vec3d pos = capi.World.Player.Entity.Pos.XYZ;
        voiceChannel.SendPacket(new VoiceFramePacket
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
        return new VoiceHudSnapshot(micEnabled, lastSpeaking || lastRemoteVoiceLevel > 0.04f, voiceLevel, status, FormatMode(mode), detail);
    }

    private float NormalizeVoiceLevel(float rms)
    {
        float baseline = Math.Max(config.NoiseGate * 3f, 0.025f);
        return Math.Clamp((rms - baseline) / 0.22f, 0f, 1f);
    }

    private float NormalizeRemoteVoiceLevel(VoiceFramePacket packet)
    {
        Vec3d listener = capi.World.Player.Entity.Pos.XYZ;
        double distance = listener.DistanceTo(packet.X, packet.Y, packet.Z);
        float range = Math.Min(serverConfig.GetRange(packet.Mode), serverConfig.MaxRange);
        float distanceGain = VoiceMath.DistanceGain(distance, range);
        return Math.Clamp(NormalizeVoiceLevel(packet.Rms) * distanceGain * config.OutputVolume, 0f, 1f);
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
            $"命令：/svc volume <0-200>, /svc mute <玩家名>, /svc unmute <玩家名>";
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
            $"麦克风静音：{localMute}    全局开关：{globalMute}";
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
}
