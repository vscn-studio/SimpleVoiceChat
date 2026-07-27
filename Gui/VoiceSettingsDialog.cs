using OpenTK.Audio.OpenAL;
using SimpleVoiceChat.Config;
using SimpleVoiceChat.Networking;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace SimpleVoiceChat.Gui;

public readonly record struct VoiceSettingsChannelOption(
    string Id,
    string Name,
    VoiceChannelRole LocalRole,
    VoiceChannelKind Kind,
    bool ExternallyManaged);
public readonly record struct VoiceSettingsPlayerOption(string Id, string Name);
public readonly record struct VoiceSettingsMemberOption(string Id, string Name, VoiceChannelRole Role);
public readonly record struct VoiceSettingsMemberPage(int TotalMembers, int Page, int PageSize, VoiceSettingsMemberOption[] Members)
{
    public static VoiceSettingsMemberPage Empty => new(0, 0, 8, Array.Empty<VoiceSettingsMemberOption>());
}

internal static class VoiceSettingsActionPolicy
{
    public static bool RequiresTarget(string action)
    {
        return action is "invite" or "add" or "mute" or "unmute" or "remove" or "ban" or "unban"
            or "listenonly" or "member" or "officer" or "role"
            or "tempmute" or "deafen" or "adminmute" or "adminunmute" or "forceblock" or "unforceblock";
    }

    public static bool RequiresChannel(string action)
    {
        return action is "add" or "mute" or "unmute" or "remove" or "ban" or "unban"
            or "listenonly" or "member" or "officer" or "role"
            or "lock" or "unlock" or "leave" or "disband";
    }
}

public sealed class VoiceSettingsDialog : GuiDialog
{
    private const string DefaultInputDeviceValue = "__default__";
    private const string InputDeviceKey = "inputDevice";
    private const string OutputVolumeKey = "outputVolume";
    private const string MicGainKey = "micGain";
    private const string NoiseGateKey = "noiseGate";
    private const string ShowMicrophoneHudKey = "showMicrophoneHud";
    private const string OcclusionKey = "occlusion";
    private const string PerformanceModeKey = "performanceMode";
    private const string SquadStatusKey = "squadStatus";
    private const string ChannelKey = "channel";
    private const string TransmitTargetKey = "transmitTarget";
    private const string ChannelVolumeKey = "channelVolume";
    private const string DiagnosticsKey = "diagnostics";
    private const string PlayerKey = "player";
    private const string PlayerVolumeKey = "playerVolume";
    private const string PlayerMuteKey = "playerMute";
    private const string MemberPageKey = "memberPage";
    private const string ChannelActionKey = "channelAction";
    private const string NoiseSuppressionKey = "noiseSuppression";
    private const string EchoCancellationKey = "echoCancellation";
    private const string AdaptiveJitterKey = "adaptiveJitter";
    private const string LocalMuteKey = "localMute";
    private const string GlobalMuteKey = "globalMute";
    private const string ContinuousTalkKey = "continuousTalk";
    private const string SummaryKey = "summary";

    private readonly SimpleVoiceChatClientConfig config;
    private readonly Func<string> summaryProvider;
    private readonly Func<string> squadStatusProvider;
    private readonly Action saveConfig;
    private readonly Action refreshHud;
    private readonly Action reinitializeCapture;
    private readonly Func<bool> startDebugRecording;
    private readonly Func<bool> playDebugRecording;
    private readonly Func<bool> leaveSquad;
    private readonly Func<bool> disbandSquad;
    private readonly Action requestSquadStatus;
    private readonly Func<VoiceSettingsChannelOption[]> channelOptionsProvider;
    private readonly Action<string> selectChannel;
    private readonly Func<string> diagnosticsProvider;
    private readonly Func<bool> acceptInvite;
    private readonly Func<bool> declineInvite;
    private readonly Func<bool> forceImmersiveProvider;
    private readonly Func<VoiceSettingsPlayerOption[]> playerOptionsProvider;
    private readonly Func<string, int> playerVolumeProvider;
    private readonly Action<string, int> setPlayerVolume;
    private readonly Action<string, bool> setPlayerMuted;
    private readonly Func<string, int, VoiceSettingsMemberPage> memberPageProvider;
    private readonly Action<string, string, string, VoiceChannelRole> manageChannel;
    private readonly Action<bool> setAdaptiveJitter;
    private readonly Func<bool> localMutedProvider;
    private readonly Action<bool> setLocalMuted;
    private readonly Func<bool> globalMutedProvider;
    private readonly Action<bool> setGlobalMuted;
    private readonly Func<bool> continuousTalkEnabledProvider;
    private readonly Func<bool> continuousTalkAllowedProvider;
    private readonly Action<bool> setContinuousTalk;
    private readonly Func<bool> hasServerControlProvider;
    private string selectedPlayerUid = string.Empty;
    private int memberPage;
    private string selectedChannelAction = "invite";
    private int selectedTab;

    public VoiceSettingsDialog(
        ICoreClientAPI capi,
        SimpleVoiceChatClientConfig config,
        Func<string> summaryProvider,
        Func<string> squadStatusProvider,
        Action saveConfig,
        Action refreshHud,
        Action reinitializeCapture,
        Func<bool> startDebugRecording,
        Func<bool> playDebugRecording,
        Func<bool> leaveSquad,
        Func<bool> disbandSquad,
        Action requestSquadStatus,
        Func<VoiceSettingsChannelOption[]> channelOptionsProvider,
        Action<string> selectChannel,
        Func<string> diagnosticsProvider,
        Func<bool> acceptInvite,
        Func<bool> declineInvite,
        Func<bool> forceImmersiveProvider,
        Func<VoiceSettingsPlayerOption[]> playerOptionsProvider,
        Func<string, int> playerVolumeProvider,
        Action<string, int> setPlayerVolume,
        Action<string, bool> setPlayerMuted,
        Func<string, int, VoiceSettingsMemberPage> memberPageProvider,
        Action<string, string, string, VoiceChannelRole> manageChannel,
        Action<bool> setAdaptiveJitter,
        Func<bool> localMutedProvider,
        Action<bool> setLocalMuted,
        Func<bool> globalMutedProvider,
        Action<bool> setGlobalMuted,
        Func<bool> continuousTalkEnabledProvider,
        Func<bool> continuousTalkAllowedProvider,
        Action<bool> setContinuousTalk,
        Func<bool> hasServerControlProvider)
        : base(capi)
    {
        this.config = config;
        this.summaryProvider = summaryProvider;
        this.squadStatusProvider = squadStatusProvider;
        this.saveConfig = saveConfig;
        this.refreshHud = refreshHud;
        this.reinitializeCapture = reinitializeCapture;
        this.startDebugRecording = startDebugRecording;
        this.playDebugRecording = playDebugRecording;
        this.leaveSquad = leaveSquad;
        this.disbandSquad = disbandSquad;
        this.requestSquadStatus = requestSquadStatus;
        this.channelOptionsProvider = channelOptionsProvider;
        this.selectChannel = selectChannel;
        this.diagnosticsProvider = diagnosticsProvider;
        this.acceptInvite = acceptInvite;
        this.declineInvite = declineInvite;
        this.forceImmersiveProvider = forceImmersiveProvider;
        this.playerOptionsProvider = playerOptionsProvider;
        this.playerVolumeProvider = playerVolumeProvider;
        this.setPlayerVolume = setPlayerVolume;
        this.setPlayerMuted = setPlayerMuted;
        this.memberPageProvider = memberPageProvider;
        this.manageChannel = manageChannel;
        this.setAdaptiveJitter = setAdaptiveJitter;
        this.localMutedProvider = localMutedProvider;
        this.setLocalMuted = setLocalMuted;
        this.globalMutedProvider = globalMutedProvider;
        this.setGlobalMuted = setGlobalMuted;
        this.continuousTalkEnabledProvider = continuousTalkEnabledProvider;
        this.continuousTalkAllowedProvider = continuousTalkAllowedProvider;
        this.setContinuousTalk = setContinuousTalk;
        this.hasServerControlProvider = hasServerControlProvider;
        Compose();
    }

    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;

    public override bool TryOpen()
    {
        requestSquadStatus();
        Compose();
        return base.TryOpen();
    }

    public void Compose()
    {
        const double dialogWidth = 700;
        const double dialogHeight = 660;
        ElementBounds dialogBounds = ElementBounds.Fixed(EnumDialogArea.CenterMiddle, -dialogWidth / 2, -dialogHeight / 2, dialogWidth, dialogHeight);
        ElementBounds bgBounds = ElementBounds.Fixed(0, 0, dialogWidth, dialogHeight);
        ElementBounds closeBounds = ElementBounds.Fixed(295, 608, 110, 32);

        GuiComposer composer = capi.Gui.CreateCompo("simplevoicechat-settings", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(SVCLang.Get("title"), () => TryClose())
            .BeginChildElements(bgBounds)
            .AddSmallButton(TabLabel(0, "tab-audio"), () => SelectTab(0), ElementBounds.Fixed(28, 52, 196, 32))
            .AddSmallButton(TabLabel(1, "tab-channels"), () => SelectTab(1), ElementBounds.Fixed(252, 52, 196, 32))
            .AddSmallButton(TabLabel(2, "tab-status"), () => SelectTab(2), ElementBounds.Fixed(476, 52, 196, 32));

        switch (selectedTab)
        {
            case 1:
                ComposeChannelPage(composer);
                break;
            case 2:
                ComposeStatusPage(composer);
                break;
            default:
                ComposeAudioPage(composer);
                break;
        }

        SingleComposer = composer
            .AddSmallButton(SVCLang.Get("button-close"), () => TryClose(), closeBounds)
            .EndChildElements()
            .Compose();

        switch (selectedTab)
        {
            case 1:
                InitializeChannelPage();
                break;
            case 2:
                RefreshStatusTexts();
                break;
            default:
                InitializeAudioPage();
                break;
        }
    }

    private void ComposeAudioPage(GuiComposer composer)
    {
        string[] inputDeviceValues = GetInputDeviceValues();
        string[] inputDeviceNames = GetInputDeviceNames(inputDeviceValues);
        int selectedInputDeviceIndex = GetSelectedInputDeviceIndex(inputDeviceValues);
        double labelX = 28;
        double controlX = 260;
        double labelWidth = 210;
        double controlWidth = 390;
        double y = 104;
        double row = 42;

        composer
            .AddStaticText(SVCLang.Get("label-input-device"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y + 4, labelWidth, 24))
            .AddDropDown(inputDeviceValues, inputDeviceNames, selectedInputDeviceIndex, OnInputDeviceChanged, ElementBounds.Fixed(controlX, y, controlWidth, 32), InputDeviceKey)
            .AddStaticText(SVCLang.Get("label-output-volume"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSlider(OnOutputVolumeChanged, ElementBounds.Fixed(controlX, y, controlWidth, 24), OutputVolumeKey)
            .AddStaticText(SVCLang.Get("label-mic-gain"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSlider(OnMicGainChanged, ElementBounds.Fixed(controlX, y, controlWidth, 24), MicGainKey)
            .AddStaticText(SVCLang.Get("label-noise-gate"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSlider(OnNoiseGateChanged, ElementBounds.Fixed(controlX, y, controlWidth, 24), NoiseGateKey)
            .AddStaticText(SVCLang.Get("label-audio-processing"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddStaticText("NS", CairoFont.WhiteSmallText(), ElementBounds.Fixed(controlX, y + 2, 28, 24))
            .AddSwitch(OnNoiseSuppressionChanged, ElementBounds.Fixed(controlX + 32, y - 6, 36, 32), NoiseSuppressionKey, 26, 3)
            .AddStaticText("AEC", CairoFont.WhiteSmallText(), ElementBounds.Fixed(controlX + 92, y + 2, 38, 24))
            .AddSwitch(OnEchoCancellationChanged, ElementBounds.Fixed(controlX + 134, y - 6, 36, 32), EchoCancellationKey, 26, 3)
            .AddStaticText(SVCLang.Get("label-adaptive-jitter"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(controlX + 190, y + 2, 72, 24))
            .AddSwitch(OnAdaptiveJitterChanged, ElementBounds.Fixed(controlX + 266, y - 6, 36, 32), AdaptiveJitterKey, 26, 3)
            .AddStaticText(Audio.VoiceProcessingCapabilities.BackendName, CairoFont.WhiteSmallText(), ElementBounds.Fixed(controlX + 316, y + 2, 72, 24))
            .AddStaticText(SVCLang.Get("label-voice-state"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row + 8, labelWidth, 24))
            .AddStaticText(SVCLang.Get("label-mic-muted"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(controlX, y + 2, 64, 24))
            .AddSwitch(OnLocalMuteChanged, ElementBounds.Fixed(controlX + 66, y - 6, 36, 32), LocalMuteKey, 26, 3)
            .AddStaticText(SVCLang.Get("label-deafened"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(controlX + 118, y + 2, 72, 24))
            .AddSwitch(OnGlobalMuteChanged, ElementBounds.Fixed(controlX + 192, y - 6, 36, 32), GlobalMuteKey, 26, 3)
            .AddStaticText(SVCLang.Get("label-continuous-talk"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(controlX + 244, y + 2, 94, 24))
            .AddSwitch(OnContinuousTalkChanged, ElementBounds.Fixed(controlX + 344, y - 6, 36, 32), ContinuousTalkKey, 26, 3)
            .AddStaticText(SVCLang.Get("label-show-mic-hud"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSwitch(OnShowMicrophoneHudChanged, ElementBounds.Fixed(controlX, y - 6, 36, 32), ShowMicrophoneHudKey, 26, 3)
            .AddStaticText(SVCLang.Get("label-occlusion"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSwitch(OnOcclusionChanged, ElementBounds.Fixed(controlX, y - 6, 36, 32), OcclusionKey, 26, 3)
            .AddStaticText(SVCLang.Get("label-performance-mode"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSwitch(OnPerformanceModeChanged, ElementBounds.Fixed(controlX, y - 6, 36, 32), PerformanceModeKey, 26, 3)
            .AddStaticText(SVCLang.Get("label-debug-recording"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSmallButton(SVCLang.Get("button-record-3s"), OnDebugRecordClicked, ElementBounds.Fixed(controlX, y - 6, 104, 32))
            .AddSmallButton(SVCLang.Get("button-play-recording"), OnDebugPlayClicked, ElementBounds.Fixed(controlX + 124, y - 6, 150, 32));
    }

    private void ComposeChannelPage(GuiComposer composer)
    {
        VoiceSettingsChannelOption[] channelOptions = channelOptionsProvider();
        string[] channelValues = channelOptions.Length == 0 ? new[] { string.Empty } : channelOptions.Select(option => option.Id).ToArray();
        string[] channelNames = channelOptions.Length == 0 ? new[] { SVCLang.Get("channel-none") } : channelOptions.Select(option => option.Name).ToArray();
        int selectedChannelIndex = Math.Max(0, Array.IndexOf(channelValues, config.SelectedChannelId));
        string[] transmitValues = { "proximity", "channel", "both" };
        string[] transmitNames = { SVCLang.Get("transmit-proximity"), SVCLang.Get("transmit-channel"), SVCLang.Get("transmit-both") };
        int selectedTransmitIndex = config.TransmitTarget switch
        {
            VoiceTransmitTarget.SelectedChannel => 1,
            VoiceTransmitTarget.ProximityAndChannel => 2,
            _ => 0
        };
        VoiceSettingsPlayerOption[] playerOptions = playerOptionsProvider();
        if (!playerOptions.Any(player => player.Id == selectedPlayerUid))
        {
            selectedPlayerUid = playerOptions.FirstOrDefault().Id ?? string.Empty;
        }
        string[] playerValues = playerOptions.Length == 0 ? new[] { string.Empty } : playerOptions.Select(player => player.Id).ToArray();
        string[] playerNames = playerOptions.Length == 0 ? new[] { SVCLang.Get("player-none") } : playerOptions.Select(player => player.Name).ToArray();
        int selectedPlayerIndex = Math.Max(0, Array.IndexOf(playerValues, selectedPlayerUid));
        bool hasSelectedPlayer = !string.IsNullOrWhiteSpace(selectedPlayerUid);
        int selectedChannelOptionIndex = Array.FindIndex(channelOptions, option => option.Id == config.SelectedChannelId);
        bool hasSelectedChannel = selectedChannelOptionIndex >= 0;
        VoiceChannelRole selectedRole = hasSelectedChannel
            ? channelOptions[selectedChannelOptionIndex].LocalRole
            : VoiceChannelRole.Banned;
        bool externallyManaged = hasSelectedChannel && channelOptions[selectedChannelOptionIndex].ExternallyManaged;
        VoiceSettingsChannelOption[] squadChannels = channelOptions.Where(option => option.Kind == VoiceChannelKind.Squad).ToArray();
        bool canInvite = squadChannels.Length == 0 || squadChannels.Any(option => option.LocalRole >= VoiceChannelRole.Officer);
        List<string> actionValues = new();
        if (canInvite && hasSelectedPlayer)
        {
            actionValues.Add("invite");
        }
        if (hasSelectedChannel && !externallyManaged)
        {
            actionValues.Add("leave");
        }
        if (hasSelectedChannel && selectedRole >= VoiceChannelRole.Officer)
        {
            if (hasSelectedPlayer)
            {
                actionValues.AddRange(new[] { "mute", "unmute", "ban", "unban" });
                if (!externallyManaged)
                {
                    actionValues.Add("remove");
                }
            }
        }
        if (hasSelectedChannel && selectedRole == VoiceChannelRole.Owner)
        {
            if (hasSelectedPlayer && !externallyManaged)
            {
                actionValues.AddRange(new[] { "listenonly", "member", "officer" });
            }
            actionValues.AddRange(new[] { "lock", "unlock" });
            if (!externallyManaged)
            {
                actionValues.Add("disband");
            }
        }
        if (hasServerControlProvider())
        {
            if (hasSelectedChannel)
            {
                if (hasSelectedPlayer)
                {
                    actionValues.AddRange(new[] { "mute", "unmute", "ban", "unban" });
                    if (!externallyManaged)
                    {
                        actionValues.AddRange(new[] { "add", "remove", "listenonly", "member", "officer" });
                    }
                }
                actionValues.AddRange(new[] { "lock", "unlock" });
                if (!externallyManaged)
                {
                    actionValues.Add("disband");
                }
            }
            actionValues.AddRange(new[]
            {
                "create-civilization", "create-command", "create-diplomacy", "create-staff", "create-broadcast", "create-radio"
            });
            if (hasSelectedPlayer)
            {
                actionValues.AddRange(new[] { "tempmute", "deafen", "adminmute", "adminunmute", "forceblock", "unforceblock" });
            }
        }
        actionValues = actionValues.Distinct(StringComparer.Ordinal).ToList();
        if (actionValues.Count == 0)
        {
            actionValues.Add("none");
        }
        if (!actionValues.Contains(selectedChannelAction))
        {
            selectedChannelAction = actionValues[0];
        }
        string[] actionNames = actionValues.Select(value => SVCLang.Get("channel-action-" + value)).ToArray();
        int selectedActionIndex = Math.Max(0, actionValues.IndexOf(selectedChannelAction));

        double labelX = 28;
        double controlX = 260;
        double labelWidth = 210;
        double controlWidth = 390;
        double y = 104;
        double row = 44;

        composer
            .AddStaticText(SVCLang.Get("label-channel-select"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y + 4, labelWidth, 24))
            .AddDropDown(channelValues, channelNames, selectedChannelIndex, OnChannelChanged, ElementBounds.Fixed(controlX, y - 4, controlWidth, 32), ChannelKey)
            .AddStaticText(SVCLang.Get("label-transmit-target"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddDropDown(transmitValues, transmitNames, selectedTransmitIndex, OnTransmitTargetChanged, ElementBounds.Fixed(controlX, y - 4, controlWidth, 32), TransmitTargetKey)
            .AddStaticText(SVCLang.Get("label-channel-volume"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSlider(OnChannelVolumeChanged, ElementBounds.Fixed(controlX, y - 4, controlWidth, 24), ChannelVolumeKey)
            .AddStaticText(SVCLang.Get("label-pending-invite"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddDynamicText("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(controlX, y - 4, controlWidth, 38), SquadStatusKey)
            .AddSmallButton(SVCLang.Get("button-accept-invite"), OnAcceptInviteClicked, ElementBounds.Fixed(controlX, y += row, 140, 32))
            .AddSmallButton(SVCLang.Get("button-decline-invite"), OnDeclineInviteClicked, ElementBounds.Fixed(controlX + 156, y, 140, 32))
            .AddStaticText(SVCLang.Get("label-player-volume"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row + 4, labelWidth, 24))
            .AddDropDown(playerValues, playerNames, selectedPlayerIndex, OnPlayerChanged, ElementBounds.Fixed(controlX, y - 4, 150, 32), PlayerKey)
            .AddSlider(OnPlayerVolumeChanged, ElementBounds.Fixed(controlX + 160, y, 176, 24), PlayerVolumeKey)
            .AddSwitch(OnPlayerMuteChanged, ElementBounds.Fixed(controlX + 350, y - 6, 36, 32), PlayerMuteKey, 26, 3)
            .AddStaticText(SVCLang.Get("label-channel-members"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddDynamicText("", CairoFont.WhiteSmallText().WithFontSize(13), ElementBounds.Fixed(controlX, y - 4, 270, 150), MemberPageKey)
            .AddSmallButton("<", OnPreviousMemberPage, ElementBounds.Fixed(controlX + 282, y - 6, 46, 32))
            .AddSmallButton(">", OnNextMemberPage, ElementBounds.Fixed(controlX + 338, y - 6, 46, 32))
            .AddStaticText(SVCLang.Get("label-channel-manage"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += 160, labelWidth, 24))
            .AddDropDown(actionValues.ToArray(), actionNames, selectedActionIndex, OnChannelActionChanged, ElementBounds.Fixed(controlX, y - 4, 245, 32), ChannelActionKey)
            .AddSmallButton(SVCLang.Get("button-apply"), OnApplyChannelAction, ElementBounds.Fixed(controlX + 260, y - 6, 124, 32));
    }

    private void ComposeStatusPage(GuiComposer composer)
    {
        composer
            .AddStaticText(SVCLang.Get("label-current-status"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(28, 104, 180, 24))
            .AddDynamicText("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28, 136, 644, 214), SummaryKey)
            .AddStaticText(SVCLang.Get("label-diagnostics"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(28, 366, 180, 24))
            .AddDynamicText("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28, 398, 644, 140), DiagnosticsKey)
            .AddSmallButton(SVCLang.Get("button-refresh-status"), OnRefreshSquadClicked, ElementBounds.Fixed(28, 552, 180, 32));
    }

    private void InitializeAudioPage()
    {
        if (SingleComposer == null)
        {
            return;
        }

        SingleComposer.GetSlider(OutputVolumeKey).SetValues((int)Math.Round(config.OutputVolume * 100f), 0, 200, 5, "%");
        SingleComposer.GetSlider(MicGainKey).SetValues((int)Math.Round(config.MicGain * 100f), 10, 400, 5, "%");
        SingleComposer.GetSlider(NoiseGateKey).SetValues((int)Math.Round(config.NoiseGate * 1000f), 0, 200, 1, " /1000");
        SingleComposer.GetSwitch(ShowMicrophoneHudKey).SetValue(config.ShowMicrophoneHud);
        SingleComposer.GetSwitch(OcclusionKey).SetValue(config.EnableOcclusionEffects);
        SingleComposer.GetSwitch(PerformanceModeKey).SetValue(config.PerformanceMode);
        SingleComposer.GetSwitch(NoiseSuppressionKey).SetValue(config.EnableNoiseSuppression && Audio.VoiceProcessingCapabilities.NoiseSuppressionAvailable);
        SingleComposer.GetSwitch(NoiseSuppressionKey).Enabled = Audio.VoiceProcessingCapabilities.NoiseSuppressionAvailable;
        SingleComposer.GetSwitch(EchoCancellationKey).SetValue(config.EnableEchoCancellation && Audio.VoiceProcessingCapabilities.EchoCancellationAvailable);
        SingleComposer.GetSwitch(EchoCancellationKey).Enabled = Audio.VoiceProcessingCapabilities.EchoCancellationAvailable;
        SingleComposer.GetSwitch(AdaptiveJitterKey).SetValue(config.AdaptiveJitterBuffer);
        SingleComposer.GetSwitch(LocalMuteKey).SetValue(localMutedProvider());
        SingleComposer.GetSwitch(GlobalMuteKey).SetValue(globalMutedProvider());
        SingleComposer.GetSwitch(ContinuousTalkKey).SetValue(continuousTalkEnabledProvider());
        SingleComposer.GetSwitch(ContinuousTalkKey).Enabled = continuousTalkAllowedProvider();
    }

    private void InitializeChannelPage()
    {
        if (SingleComposer == null)
        {
            return;
        }

        SingleComposer.GetSlider(ChannelVolumeKey).SetValues((int)Math.Round(config.ChannelOutputVolume * 100f), 0, 200, 5, "%");
        SingleComposer.GetSlider(PlayerVolumeKey).SetValues(playerVolumeProvider(selectedPlayerUid), 0, 200, 5, "%");
        SingleComposer.GetSwitch(PlayerMuteKey).SetValue(config.MutedPlayerUids.Contains(selectedPlayerUid));
        RefreshMemberPage();
        SingleComposer.GetDynamicText(SquadStatusKey).SetNewText(squadStatusProvider(), true, true, true);
    }

    private string TabLabel(int tab, string localizationKey)
    {
        string label = SVCLang.Get(localizationKey);
        return selectedTab == tab ? $"[{label}]" : label;
    }

    private bool SelectTab(int tab)
    {
        if (selectedTab == tab)
        {
            return true;
        }

        selectedTab = tab;
        Compose();
        return true;
    }

    private void OnInputDeviceChanged(string value, bool selected)
    {
        if (!selected)
        {
            return;
        }

        string nextDevice = value == DefaultInputDeviceValue ? string.Empty : value;
        if (config.InputDeviceName == nextDevice)
        {
            return;
        }

        config.InputDeviceName = nextDevice;
        ApplyConfig();
        reinitializeCapture();
    }

    private bool OnOutputVolumeChanged(int value)
    {
        config.OutputVolume = value / 100f;
        ApplyConfig();
        return true;
    }

    private bool OnMicGainChanged(int value)
    {
        config.MicGain = value / 100f;
        ApplyConfig();
        return true;
    }

    private bool OnNoiseGateChanged(int value)
    {
        config.NoiseGate = value / 1000f;
        ApplyConfig();
        return true;
    }

    private void OnNoiseSuppressionChanged(bool enabled)
    {
        config.EnableNoiseSuppression = enabled && Audio.VoiceProcessingCapabilities.NoiseSuppressionAvailable;
        ApplyConfig();
    }

    private void OnEchoCancellationChanged(bool enabled)
    {
        config.EnableEchoCancellation = enabled && Audio.VoiceProcessingCapabilities.EchoCancellationAvailable;
        ApplyConfig();
    }

    private void OnAdaptiveJitterChanged(bool enabled)
    {
        config.AdaptiveJitterBuffer = enabled;
        ApplyConfig();
        setAdaptiveJitter(enabled);
    }

    private void OnLocalMuteChanged(bool muted)
    {
        setLocalMuted(muted);
        RefreshStatusTexts();
    }

    private void OnGlobalMuteChanged(bool muted)
    {
        setGlobalMuted(muted);
        RefreshStatusTexts();
    }

    private void OnContinuousTalkChanged(bool enabled)
    {
        setContinuousTalk(enabled);
        SingleComposer?.GetSwitch(ContinuousTalkKey)?.SetValue(continuousTalkEnabledProvider());
        RefreshStatusTexts();
    }

    private void OnShowMicrophoneHudChanged(bool enabled)
    {
        config.ShowMicrophoneHud = enabled;
        config.ShowHudIndicator = enabled;
        ApplyConfig();
    }

    private void OnOcclusionChanged(bool enabled)
    {
        if (!enabled && forceImmersiveProvider())
        {
            config.EnableOcclusionEffects = true;
            SingleComposer?.GetSwitch(OcclusionKey)?.SetValue(true);
            return;
        }
        config.EnableOcclusionEffects = enabled;
        ApplyConfig();
    }

    private void OnPerformanceModeChanged(bool enabled)
    {
        config.PerformanceMode = enabled;
        ApplyConfig();
    }

    private void OnChannelChanged(string value, bool selected)
    {
        if (!selected || config.SelectedChannelId == value)
        {
            return;
        }
        config.SelectedChannelId = value;
        memberPage = 0;
        selectChannel(value);
        Compose();
    }

    private void OnTransmitTargetChanged(string value, bool selected)
    {
        if (!selected)
        {
            return;
        }
        config.TransmitTarget = value switch
        {
            "channel" => VoiceTransmitTarget.SelectedChannel,
            "both" => VoiceTransmitTarget.ProximityAndChannel,
            _ => VoiceTransmitTarget.Proximity
        };
        ApplyConfig();
    }

    private bool OnChannelVolumeChanged(int value)
    {
        config.ChannelOutputVolume = value / 100f;
        ApplyConfig();
        return true;
    }

    private void OnPlayerChanged(string value, bool selected)
    {
        if (!selected)
        {
            return;
        }
        selectedPlayerUid = value;
        SingleComposer?.GetSlider(PlayerVolumeKey)?.SetValues(playerVolumeProvider(value), 0, 200, 5, "%");
        SingleComposer?.GetSwitch(PlayerMuteKey)?.SetValue(config.MutedPlayerUids.Contains(value));
    }

    private bool OnPlayerVolumeChanged(int value)
    {
        setPlayerVolume(selectedPlayerUid, value);
        return true;
    }

    private void OnPlayerMuteChanged(bool muted)
    {
        setPlayerMuted(selectedPlayerUid, muted);
    }

    private void OnChannelActionChanged(string value, bool selected)
    {
        if (selected)
        {
            selectedChannelAction = value;
        }
    }

    private bool OnPreviousMemberPage()
    {
        memberPage = Math.Max(0, memberPage - 1);
        RefreshMemberPage();
        return true;
    }

    private bool OnNextMemberPage()
    {
        VoiceSettingsMemberPage current = memberPageProvider(config.SelectedChannelId, memberPage);
        int pageSize = Math.Max(1, current.PageSize);
        int maxPage = Math.Max(0, (current.TotalMembers + pageSize - 1) / pageSize - 1);
        memberPage = Math.Min(maxPage, memberPage + 1);
        RefreshMemberPage();
        return true;
    }

    private bool OnApplyChannelAction()
    {
        if (selectedChannelAction == "none")
        {
            return true;
        }
        if (VoiceSettingsActionPolicy.RequiresTarget(selectedChannelAction)
            && string.IsNullOrWhiteSpace(selectedPlayerUid))
        {
            capi.ShowChatMessage(SVCLang.Get("chat-channel-action-requires-player"));
            return true;
        }
        if (VoiceSettingsActionPolicy.RequiresChannel(selectedChannelAction)
            && string.IsNullOrWhiteSpace(config.SelectedChannelId))
        {
            capi.ShowChatMessage(SVCLang.Get("chat-channel-action-requires-channel"));
            return true;
        }
        VoiceChannelRole role = selectedChannelAction switch
        {
            "listenonly" => VoiceChannelRole.ListenOnly,
            "officer" => VoiceChannelRole.Officer,
            _ => VoiceChannelRole.Member
        };
        string action = selectedChannelAction is "listenonly" or "member" or "officer" ? "role" : selectedChannelAction;
        manageChannel(action, config.SelectedChannelId, selectedPlayerUid, role);
        return true;
    }

    private bool OnDebugRecordClicked()
    {
        return startDebugRecording();
    }

    private bool OnDebugPlayClicked()
    {
        return playDebugRecording();
    }

    private bool OnLeaveSquadClicked()
    {
        return leaveSquad();
    }

    private bool OnDisbandSquadClicked()
    {
        return disbandSquad();
    }

    private bool OnRefreshSquadClicked()
    {
        requestSquadStatus();
        RefreshStatusTexts();
        return true;
    }

    private bool OnAcceptInviteClicked()
    {
        return acceptInvite();
    }

    private bool OnDeclineInviteClicked()
    {
        return declineInvite();
    }

    private void ApplyConfig()
    {
        saveConfig();
        refreshHud();
        RefreshStatusTexts();
    }

    public void RefreshStatusTexts()
    {
        if (SingleComposer == null)
        {
            return;
        }

        SingleComposer.GetDynamicText(SummaryKey)?.SetNewText(summaryProvider(), true, true, true);
        SingleComposer.GetDynamicText(SquadStatusKey)?.SetNewText(squadStatusProvider(), true, true, true);
        SingleComposer.GetDynamicText(DiagnosticsKey)?.SetNewText(diagnosticsProvider(), true, true, true);
        SingleComposer.GetSwitch(LocalMuteKey)?.SetValue(localMutedProvider());
        SingleComposer.GetSwitch(GlobalMuteKey)?.SetValue(globalMutedProvider());
        SingleComposer.GetSwitch(ContinuousTalkKey)?.SetValue(continuousTalkEnabledProvider());
        if (selectedTab == 1)
        {
            RefreshMemberPage();
        }
    }

    public void RefreshChannelData()
    {
        if (IsOpened() && selectedTab == 1)
        {
            Compose();
            return;
        }

        RefreshStatusTexts();
    }

    public void RefreshConfiguration()
    {
        if (IsOpened())
        {
            Compose();
            return;
        }

        RefreshStatusTexts();
    }

    private void RefreshMemberPage()
    {
        if (SingleComposer == null)
        {
            return;
        }
        VoiceSettingsMemberPage page = memberPageProvider(config.SelectedChannelId, memberPage);
        string names = page.Members.Length == 0
            ? SVCLang.Get("channel-members-loading")
            : string.Join("\n", page.Members.Select(member => $"{Truncate(member.Name, 24)} [{FormatRole(member.Role)}]"));
        int pageSize = Math.Max(1, page.PageSize);
        int pageCount = Math.Max(1, (page.TotalMembers + pageSize - 1) / pageSize);
        SingleComposer.GetDynamicText(MemberPageKey)?.SetNewText(
            SVCLang.Get("channel-members-page", page.TotalMembers, page.Page + 1, pageCount, names),
            true,
            true,
            true);
    }

    private static string FormatRole(VoiceChannelRole role)
    {
        return role switch
        {
            VoiceChannelRole.Owner => SVCLang.Get("channel-role-owner"),
            VoiceChannelRole.Officer => SVCLang.Get("channel-role-officer"),
            VoiceChannelRole.Member => SVCLang.Get("channel-role-member"),
            VoiceChannelRole.ListenOnly => SVCLang.Get("channel-role-listenonly"),
            _ => SVCLang.Get("channel-role-banned")
        };
    }

    private static string Truncate(string value, int maximumLength)
    {
        return value.Length <= maximumLength ? value : value[..Math.Max(1, maximumLength - 3)] + "...";
    }

    private string[] GetInputDeviceValues()
    {
        List<string> values = new() { DefaultInputDeviceValue };
        try
        {
            foreach (string device in ALC.GetString(AlcGetStringList.CaptureDeviceSpecifier))
            {
                if (!string.IsNullOrWhiteSpace(device) && !values.Contains(device))
                {
                    values.Add(device);
                }
            }
        }
        catch (Exception ex)
        {
            capi.Logger.Warning("SimpleVoiceChat: failed enumerating capture devices: {0}", ex.Message);
        }

        if (!string.IsNullOrWhiteSpace(config.InputDeviceName) && !values.Contains(config.InputDeviceName))
        {
            values.Add(config.InputDeviceName);
        }

        return values.ToArray();
    }

    private static string[] GetInputDeviceNames(string[] values)
    {
        string[] names = new string[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            names[i] = values[i] == DefaultInputDeviceValue ? SVCLang.Get("default-microphone") : values[i];
        }

        return names;
    }

    private int GetSelectedInputDeviceIndex(string[] values)
    {
        string current = string.IsNullOrWhiteSpace(config.InputDeviceName) ? DefaultInputDeviceValue : config.InputDeviceName;
        int index = Array.IndexOf(values, current);
        return index >= 0 ? index : 0;
    }
}
