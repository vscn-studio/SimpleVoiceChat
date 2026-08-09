using SimpleVoiceChat.Audio;
using SimpleVoiceChat.Config;
using SimpleVoiceChat.Networking;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SimpleVoiceChat.Gui;

internal static class VoiceSettingsActionPolicy
{
    public static bool RequiresTarget(string action)
    {
        return action is "invite" or "add" or "mute" or "unmute" or "remove" or "ban" or "unban"
            or "listenonly" or "member" or "moderator" or "role"
            or "tempmute" or "deafen" or "adminmute" or "adminunmute" or "forceblock" or "unforceblock";
    }

    public static bool RequiresChannel(string action)
    {
        return action is "add" or "mute" or "unmute" or "remove" or "ban" or "unban"
            or "listenonly" or "member" or "moderator" or "role"
            or "lock" or "unlock" or "leave" or "disband" or "rename";
    }
}

internal enum VoiceSettingsPage
{
    Audio,
    Channels,
    Admin
}

internal static class VoiceSettingsNavigation
{
    public static VoiceSettingsPage[] BuildPages(bool hasServerControl)
    {
        List<VoiceSettingsPage> pages = new()
        {
            VoiceSettingsPage.Audio,
            VoiceSettingsPage.Channels
        };
        if (hasServerControl)
        {
            pages.Add(VoiceSettingsPage.Admin);
        }
        return pages.ToArray();
    }
}

public sealed class VoiceSettingsDialog : GuiDialog
{
    private const double ContentLeft = 14;
    private const double ContentTop = 82;
    private const double ContentWidth = 900;
    private const double ViewportHeight = 548;

    private readonly ClientVoiceController controller;
    private readonly SimpleVoiceChatClientConfig config;

    private VoiceSettingsPage selectedPage = VoiceSettingsPage.Audio;
    private ElementBounds? contentBounds;
    private float scrollPosition;
    private double contentHeight = ViewportHeight;
    private bool suppressScrollCallback;
    private bool composeQueued;

    private string selectedPlayerUid = string.Empty;
    private string selectedChannelAction = "invite";
    private string selectedAdminChannelId = string.Empty;
    private string selectedAdminAction = "mute";
    private string renameText = string.Empty;
    private string createName = string.Empty;

    public VoiceSettingsDialog(ICoreClientAPI capi, ClientVoiceController controller)
        : base(capi)
    {
        this.controller = controller;
        config = controller.SettingsConfig;
    }

    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;
    public override bool DisableMouseGrab => true;
    public override EnumDialogType DialogType => EnumDialogType.Dialog;
    public override double DrawOrder => 0.48;
    public override double InputOrder => 0.3;

    public override bool TryOpen()
    {
        controller.RequestSettingsRefresh();
        Compose();
        return base.TryOpen();
    }

    public void RefreshData()
    {
        if (selectedPage == VoiceSettingsPage.Admin && !controller.HasServerControl)
        {
            selectedPage = VoiceSettingsPage.Audio;
            scrollPosition = 0;
        }
        if (IsOpened())
        {
            QueueCompose();
        }
    }

    public void RefreshConfiguration()
    {
        if (IsOpened())
        {
            QueueCompose();
        }
    }

    private void Compose()
    {
        VoiceSettingsPage[] pages = VoiceSettingsNavigation.BuildPages(controller.HasServerControl);
        if (!pages.Contains(selectedPage))
        {
            selectedPage = VoiceSettingsPage.Audio;
            scrollPosition = 0;
        }

        SingleComposer?.Dispose();
        ElementBounds root = ElementStdBounds.AutosizedMainDialog;
        ElementBounds background = ElementStdBounds.DialogBackground();
        ElementBounds viewport = ElementBounds.Fixed(ContentLeft, ContentTop, ContentWidth, ViewportHeight);
        contentBounds = ElementBounds.Fixed(0, 0, ContentWidth, ViewportHeight);

        CairoFont titleFont = CairoFont.WhiteSmallText();

        GuiComposer composer = capi.Gui.CreateCompo("simplevoicechat-settings", root)
            .AddShadedDialogBG(background)
            .AddDialogTitleBar(SVCLang.Get("title"), OnClose, titleFont)
            .BeginChildElements(background);

        double navX = 14;
        foreach (VoiceSettingsPage page in pages)
        {
            VoiceSettingsPage captured = page;
            string key = "nav-" + page.ToString().ToLowerInvariant();
            double width = page == VoiceSettingsPage.Channels ? 156 : 100;
            composer.AddToggleButton(
                GetPageName(page),
                CairoFont.ButtonText(),
                active =>
                {
                    if (active)
                    {
                        SelectPage(captured);
                    }
                },
                ElementBounds.Fixed(navX, 40, width, 40),
                key);
            composer.GetToggleButton(key).SetValue(page == selectedPage);
            navX += width + 6;
        }

        composer
            .BeginClip(viewport)
            .BeginChildElements(contentBounds);

        contentHeight = selectedPage switch
        {
            VoiceSettingsPage.Channels => AddChannelsPage(composer),
            VoiceSettingsPage.Admin => AddAdminPage(composer),
            _ => AddAudioPage(composer)
        };
        contentHeight = Math.Max(ViewportHeight, contentHeight);
        scrollPosition = Math.Clamp(
            scrollPosition,
            0f,
            (float)Math.Max(0d, contentHeight - ViewportHeight));
        contentBounds.fixedY = -scrollPosition;

        composer
            .EndChildElements()
            .EndClip()
            .AddVerticalScrollbar(
                OnScroll,
                ElementBounds.Fixed(ContentLeft + ContentWidth + 6, ContentTop, 16, ViewportHeight),
                "pageScrollbar");

        SingleComposer = composer.EndChildElements().Compose();
        GuiElementScrollbar? scrollbar = SingleComposer.GetScrollbar("pageScrollbar");
        if (scrollbar != null)
        {
            float totalHeight = (float)contentHeight;
            suppressScrollCallback = true;
            try
            {
                scrollbar.SetHeights((float)ViewportHeight, totalHeight);
                scrollbar.CurrentYPosition = scrollPosition;
                scrollbar.TriggerChanged();
            }
            finally
            {
                suppressScrollCallback = false;
            }
        }
    }

    private double AddAudioPage(GuiComposer composer)
    {
        const double labelX = 18;
        const double controlX = 235;
        const double controlWidth = 220;
        double y = 0;
        CairoFont section = CairoFont.WhiteSmallishText();
        CairoFont label = CairoFont.WhiteSmallishText();

        string[] inputValues = controller.GetInputDeviceValues();
        string[] inputNames = ClientVoiceController.GetInputDeviceNames(inputValues);
        string selectedInput = config.InputDeviceName ?? string.Empty;

        composer
            .AddStaticText(SVCLang.Get("label-input-device"), label, ElementBounds.Fixed(labelX, y, 210, 26))
            .AddDropDown(inputValues, inputNames, Math.Max(0, Array.IndexOf(inputValues, selectedInput)), OnInputDeviceChanged, ElementBounds.Fixed(controlX, y, controlWidth, 26), "inputDevice")
            .AddStaticText(SVCLang.Get("label-output-volume"), label, ElementBounds.Fixed(labelX, y += 44, 210, 26))
            .AddSlider(value => { controller.SetOutputVolumeFromSettings(value); return true; }, ElementBounds.Fixed(controlX, y, controlWidth, 20), "outputVolume")
            .AddStaticText(SVCLang.Get("label-mic-gain"), label, ElementBounds.Fixed(labelX, y += 44, 210, 26))
            .AddSlider(value => { controller.SetMicGainFromSettings(value); return true; }, ElementBounds.Fixed(controlX, y, controlWidth, 20), "micGain")
            .AddStaticText(SVCLang.Get("label-noise-gate"), label, ElementBounds.Fixed(labelX, y += 44, 210, 26))
            .AddSlider(value => { controller.SetNoiseGateFromSettings(value); return true; }, ElementBounds.Fixed(controlX, y, controlWidth, 20), "noiseGate");

        ConfigureSlider(composer, "outputVolume", (int)Math.Round(config.OutputVolume * 100), 0, 200, "%");
        ConfigureSlider(composer, "micGain", (int)Math.Round(config.MicGain * 100), 10, 400, "%");
        ConfigureSlider(composer, "noiseGate", (int)Math.Round(config.NoiseGate * 1000), 0, 200);

        y += 54;
        composer.AddStaticText(SVCLang.Get("ui-section-behavior"), section, ElementBounds.Fixed(labelX, y += 42, 868, 24));
        y += 30;
        double leftY = y;
        double rightY = y;
        AddSwitchRow(composer, labelX, ref leftY, SVCLang.Get("label-mic-muted"), "localMute", controller.LocalMuted, controller.SetLocalMutedFromSettings);
        AddSwitchRow(composer, labelX, ref leftY, SVCLang.Get("label-deafened"), "globalMute", controller.GlobalMuted, controller.SetGlobalMutedFromSettings);
        AddSwitchRow(composer, labelX, ref leftY, SVCLang.Get("label-continuous-talk"), "continuous", controller.ContinuousTalkEnabled, controller.SetContinuousTalkFromSettings);
        AddSwitchRow(composer, labelX, ref leftY, SVCLang.Get("label-adaptive-jitter"), "jitter", config.AdaptiveJitterBuffer, controller.SetAdaptiveJitterFromSettings);
        AddSwitchRow(composer, labelX, ref leftY, SVCLang.Get("label-show-mic-hud"), "showHud", config.ShowMicrophoneHud, controller.SetHudVisibleFromSettings);

        const double rightX = 466;
        AddSwitchRow(composer, rightX, ref rightY, SVCLang.Get("label-noise-suppression"), "noiseSuppression", config.EnableNoiseSuppression, controller.SetNoiseSuppressionFromSettings);
        AddSwitchRow(composer, rightX, ref rightY, SVCLang.Get("label-echo-cancellation"), "echoCancellation", config.EnableEchoCancellation, controller.SetEchoCancellationFromSettings);
        AddSwitchRow(composer, rightX, ref rightY, SVCLang.Get("label-occlusion"), "occlusion", config.EnableOcclusionEffects, controller.SetOcclusionFromSettings);
        AddSwitchRow(composer, rightX, ref rightY, SVCLang.Get("label-performance-mode"), "performance", config.PerformanceMode, controller.SetPerformanceModeFromSettings);

        composer.GetSwitch("continuous").Enabled = controller.ContinuousTalkAllowed;
        composer.GetSwitch("noiseSuppression").Enabled = VoiceProcessingCapabilities.NoiseSuppressionAvailable;
        composer.GetSwitch("echoCancellation").Enabled = VoiceProcessingCapabilities.EchoCancellationAvailable;
        composer.GetSwitch("occlusion").Enabled = !controller.OcclusionForced;
        return Math.Max(leftY, rightY) + 18;
    }

    private double AddChannelsPage(GuiComposer composer)
    {
        const double x = 18;
        const double rightX = 466;
        const double fieldWidth = 210;
        const double controlWidth = 203;
        double y = 0;
        CairoFont section = CairoFont.WhiteSmallishText();
        CairoFont label = CairoFont.WhiteSmallishText();
        CairoFont detail = CairoFont.WhiteSmallText();

        VoiceSettingsChannelOption[] channels = controller.BuildChannelOptions();
        VoiceSettingsPlayerOption[] players = controller.BuildPlayerOptions();
        NormalizePlayer(players);
        string[] channelValues = new[] { string.Empty }.Concat(channels.Select(channel => channel.Id)).ToArray();
        string[] channelNames = new[] { SVCLang.Get("channel-none") }
            .Concat(channels.Select(channel => channel.Name))
            .ToArray();
        string[] playerValues = players.Length == 0 ? new[] { string.Empty } : players.Select(player => player.Id).ToArray();
        string[] playerNames = players.Length == 0 ? new[] { SVCLang.Get("player-none") } : players.Select(player => player.Name).ToArray();
        string[] transmitValues = { "proximity", "channel", "both" };
        string[] transmitNames = transmitValues.Select(value => SVCLang.Get("transmit-" + value)).ToArray();

        composer
            .AddStaticText(SVCLang.Get("label-channel-select"), label, ElementBounds.Fixed(x, y, fieldWidth, 26))
            .AddStaticText(SVCLang.Get("label-transmit-target"), label, ElementBounds.Fixed(rightX, y, fieldWidth, 26))
            .AddDropDown(channelValues, channelNames, Math.Max(0, Array.IndexOf(channelValues, config.SelectedChannelId)), OnChannelChanged, ElementBounds.Fixed(x + 217, y, controlWidth, 26), "channel")
            .AddDropDown(transmitValues, transmitNames, Math.Max(0, Array.IndexOf(transmitValues, TransmitCode(config.TransmitTarget))), OnTransmitChanged, ElementBounds.Fixed(rightX + 217, y, controlWidth, 26), "transmit")
            .AddStaticText(SVCLang.Get("label-channel-volume"), label, ElementBounds.Fixed(x, y += 44, fieldWidth, 26))
            .AddSlider(value => { controller.SetChannelVolumeFromSettings(value); return true; }, ElementBounds.Fixed(x + 217, y, controlWidth, 20), "channelVolume")
            .AddStaticText(SVCLang.Get("ui-section-player"), section, ElementBounds.Fixed(x, y += 42, 868, 24))
            .AddStaticText(SVCLang.Get("ui-target-player"), label, ElementBounds.Fixed(x, y += 34, fieldWidth, 26))
            .AddDropDown(playerValues, playerNames, Math.Max(0, Array.IndexOf(playerValues, selectedPlayerUid)), OnPlayerChanged, ElementBounds.Fixed(x + 217, y, controlWidth, 26), "selectedPlayer")
            .AddStaticText(SVCLang.Get("label-player-volume"), label, ElementBounds.Fixed(x, y += 40, fieldWidth, 26))
            .AddSlider(OnSelectedPlayerVolumeChanged, ElementBounds.Fixed(x + 217, y, 164, 20), "selectedPlayerVolume")
            .AddSwitch(OnSelectedPlayerMuteChanged, ElementBounds.Fixed(x + 397, y, 28, 28), "selectedPlayerMute");

        composer.GetDropDown("channel").Enabled = true;
        ConfigureSlider(composer, "channelVolume", (int)Math.Round(config.ChannelOutputVolume * 100), 0, 200, "%");
        bool hasPlayer = !string.IsNullOrWhiteSpace(selectedPlayerUid);
        ConfigureSlider(composer, "selectedPlayerVolume", hasPlayer ? controller.GetPlayerVolumePercent(selectedPlayerUid) : 100, 0, 200, "%");
        composer.GetSlider("selectedPlayerVolume")!.Enabled = hasPlayer;
        composer.GetSwitch("selectedPlayerMute").SetValue(hasPlayer && controller.IsPlayerMuted(selectedPlayerUid));
        composer.GetSwitch("selectedPlayerMute").Enabled = hasPlayer;

        List<string> actions = BuildChannelActions(channels);
        if (!actions.Contains(selectedChannelAction, StringComparer.Ordinal))
        {
            selectedChannelAction = actions[0];
        }
        y += 52;
        composer
            .AddStaticText(SVCLang.Get("label-channel-manage"), section, ElementBounds.Fixed(x, y, 868, 24))
            .AddDropDown(actions.ToArray(), actions.Select(action => SVCLang.Get("channel-action-" + action)).ToArray(), Math.Max(0, actions.IndexOf(selectedChannelAction)), OnChannelActionChanged, ElementBounds.Fixed(x, y += 34, 720, 26), "channelAction")
            .AddSmallButton(SVCLang.Get("button-apply"), ExecuteChannelAction, ElementBounds.Fixed(750, y, 136, 26), EnumButtonStyle.Small, "applyChannelAction");
        composer.GetButton("applyChannelAction").Enabled = selectedChannelAction != "none";

        VoiceSettingsChannelOption? selectedChannel = channels.Cast<VoiceSettingsChannelOption?>().FirstOrDefault(channel => channel?.Id == config.SelectedChannelId);
        bool canRename = selectedChannel is { ExternallyManaged: false, LocalRole: VoiceChannelRole.Owner };
        if (canRename)
        {
            if (string.IsNullOrWhiteSpace(renameText))
            {
                renameText = selectedChannel?.Name ?? string.Empty;
            }
            composer
                .AddStaticText(SVCLang.Get("label-channel-name"), label, ElementBounds.Fixed(x, y += 42, fieldWidth, 26))
                .AddTextInput(ElementBounds.Fixed(x + 217, y, 320, 26), OnRenameTextChanged, CairoFont.TextInput(), "channelRename")
                .AddSmallButton(SVCLang.Get("button-rename-channel"), RenameSelectedChannel, ElementBounds.Fixed(x + 550, y, 86, 26), EnumButtonStyle.Small, "renameChannel");
            composer.GetTextInput("channelRename").SetValue(renameText);
            composer.GetTextInput("channelRename").SetMaxLength(VoiceProtocol.MaxControlStringLength);
        }

        y += 56;
        composer
            .AddStaticText(SVCLang.Get("setting-voice-players"), section, ElementBounds.Fixed(x, y, 868, 24))
            .AddStaticText(SVCLang.Get("setting-voice-player-column"), detail, ElementBounds.Fixed(x, y += 30, 250, 22))
            .AddStaticText(SVCLang.Get("setting-voice-volume-column"), detail, ElementBounds.Fixed(290, y, 540, 22))
            .AddStaticText(SVCLang.Get("setting-voice-mute-column"), detail, ElementBounds.Fixed(856, y, 30, 22));
        y += 26;

        if (players.Length == 0)
        {
            composer.AddStaticText(SVCLang.Get("player-none"), detail, ElementBounds.Fixed(x, y, 868, 30));
            return y + 48;
        }

        for (int index = 0; index < players.Length; index++)
        {
            VoiceSettingsPlayerOption player = players[index];
            string sliderKey = "playerVolume" + index;
            string muteKey = "playerMute" + index;
            composer
                .AddStaticText(Truncate(player.Name, 36), label, ElementBounds.Fixed(x, y, 250, 26), "playerName" + index)
                .AddSlider(value => SetPlayerVolume(player.Id, value), ElementBounds.Fixed(290, y, 540, 20), sliderKey)
                .AddSwitch(value => controller.SetPlayerMutedFromSettings(player.Id, value), ElementBounds.Fixed(856, y, 28, 28), muteKey);
            ConfigureSlider(composer, sliderKey, controller.GetPlayerVolumePercent(player.Id), 0, 200, "%");
            composer.GetSwitch(muteKey).SetValue(controller.IsPlayerMuted(player.Id));
            y += 40;
        }
        return y + 18;
    }

    private double AddAdminPage(GuiComposer composer)
    {
        const double leftX = 18;
        const double rightX = 466;
        const double columnWidth = 420;
        const double fieldWidth = 210;
        const double controlWidth = 203;
        double leftY = 0;
        double rightY = 0;
        CairoFont section = CairoFont.WhiteSmallishText();
        CairoFont label = CairoFont.WhiteSmallishText();
        CairoFont detail = CairoFont.WhiteSmallText();
        CairoFont input = CairoFont.TextInput();

        VoiceSettingsChannelOption[] channels = controller.BuildChannelOptions();
        VoiceSettingsPlayerOption[] players = controller.BuildPlayerOptions();
        NormalizePlayer(players);
        NormalizeAdminChannel(channels);
        string[] playerValues = players.Length == 0 ? new[] { string.Empty } : players.Select(player => player.Id).ToArray();
        string[] playerNames = players.Length == 0 ? new[] { SVCLang.Get("player-none") } : players.Select(player => player.Name).ToArray();
        string[] channelValues = channels.Length == 0 ? new[] { string.Empty } : channels.Select(channel => channel.Id).ToArray();
        string[] channelNames = channels.Length == 0 ? new[] { SVCLang.Get("channel-none") } : channels.Select(channel => channel.Name).ToArray();
        string[] adminActions = BuildAdminChannelActions(channels);

        composer
            .AddStaticText(SVCLang.Get("ui-section-admin-target"), section, ElementBounds.Fixed(leftX, leftY, columnWidth, 24))
            .AddStaticText(SVCLang.Get("ui-target-player"), label, ElementBounds.Fixed(leftX, leftY += 32, fieldWidth, 26))
            .AddDropDown(playerValues, playerNames, Math.Max(0, Array.IndexOf(playerValues, selectedPlayerUid)), OnPlayerChanged, ElementBounds.Fixed(leftX + 217, leftY, controlWidth, 26), "adminPlayer")
            .AddStaticText(SVCLang.Get("ui-section-temporary-actions"), section, ElementBounds.Fixed(leftX, leftY += 42, columnWidth, 24))
            .AddSmallButton(SVCLang.Get("channel-action-tempmute"), () => ExecuteModeration("tempmute"), ElementBounds.Fixed(leftX, leftY += 30, 196, 26), EnumButtonStyle.Small, "tempmute")
            .AddSmallButton(SVCLang.Get("channel-action-deafen"), () => ExecuteModeration("deafen"), ElementBounds.Fixed(leftX + 210, leftY, 196, 26), EnumButtonStyle.Small, "deafen")
            .AddStaticText(SVCLang.Get("ui-section-persistent-actions"), section, ElementBounds.Fixed(leftX, leftY += 40, columnWidth, 24))
            .AddSmallButton(SVCLang.Get("channel-action-adminmute"), () => ExecuteModeration("adminmute"), ElementBounds.Fixed(leftX, leftY += 30, 196, 26), EnumButtonStyle.Small, "adminmute")
            .AddSmallButton(SVCLang.Get("channel-action-adminunmute"), () => ExecuteModeration("adminunmute"), ElementBounds.Fixed(leftX + 210, leftY, 196, 26), EnumButtonStyle.Small, "adminunmute")
            .AddSmallButton(SVCLang.Get("channel-action-forceblock"), () => ExecuteModeration("forceblock"), ElementBounds.Fixed(leftX, leftY += 36, 196, 26), EnumButtonStyle.Small, "forceblock")
            .AddSmallButton(SVCLang.Get("channel-action-unforceblock"), () => ExecuteModeration("unforceblock"), ElementBounds.Fixed(leftX + 210, leftY, 196, 26), EnumButtonStyle.Small, "unforceblock")
            .AddStaticText(SVCLang.Get("ui-admin-warning"), detail, ElementBounds.Fixed(leftX, leftY += 38, columnWidth, 44));

        bool hasTarget = !string.IsNullOrWhiteSpace(selectedPlayerUid);
        foreach (string key in new[] { "tempmute", "deafen", "adminmute", "adminunmute", "forceblock", "unforceblock" })
        {
            composer.GetButton(key).Enabled = hasTarget;
        }

        composer
            .AddStaticText(SVCLang.Get("label-channel-manage"), section, ElementBounds.Fixed(rightX, rightY, columnWidth, 24))
            .AddStaticText(SVCLang.Get("label-current-channel"), label, ElementBounds.Fixed(rightX, rightY += 32, fieldWidth, 26))
            .AddDropDown(channelValues, channelNames, Math.Max(0, Array.IndexOf(channelValues, selectedAdminChannelId)), OnAdminChannelChanged, ElementBounds.Fixed(rightX + 217, rightY, controlWidth, 26), "adminChannel")
            .AddStaticText(SVCLang.Get("ui-action-select"), label, ElementBounds.Fixed(rightX, rightY += 42, fieldWidth, 26))
            .AddDropDown(adminActions, adminActions.Select(action => SVCLang.Get("channel-action-" + action)).ToArray(), Math.Max(0, Array.IndexOf(adminActions, selectedAdminAction)), OnAdminActionChanged, ElementBounds.Fixed(rightX + 217, rightY, 120, 26), "adminAction")
            .AddSmallButton(SVCLang.Get("button-apply"), ExecuteAdminChannelAction, ElementBounds.Fixed(rightX + 347, rightY, 76, 26), EnumButtonStyle.Small, "adminApply")
            .AddStaticText(SVCLang.Get("label-channel-name"), section, ElementBounds.Fixed(rightX, rightY += 46, columnWidth, 24))
            .AddTextInput(ElementBounds.Fixed(rightX + 217, rightY += 30, 120, 26), OnRenameTextChanged, input, "adminRenameInput")
            .AddSmallButton(SVCLang.Get("button-rename-channel"), RenameAdminChannel, ElementBounds.Fixed(rightX + 347, rightY, 76, 26), EnumButtonStyle.Small, "adminRename")
            .AddStaticText(SVCLang.Get("ui-section-create-channel"), section, ElementBounds.Fixed(rightX, rightY += 40, columnWidth, 24))
            .AddTextInput(ElementBounds.Fixed(rightX + 217, rightY += 30, controlWidth, 26), OnCreateNameChanged, input, "createName")
            .AddSmallButton(SVCLang.Get("channel-action-create"), CreateChannel, ElementBounds.Fixed(rightX + 217, rightY += 34, 120, 26), EnumButtonStyle.Small, "createChannel");

        composer.GetTextInput("adminRenameInput").SetValue(renameText);
        composer.GetTextInput("adminRenameInput").SetMaxLength(VoiceProtocol.MaxControlStringLength);
        composer.GetTextInput("createName").SetValue(createName);
        composer.GetTextInput("createName").SetMaxLength(VoiceProtocol.MaxControlStringLength);
        composer.GetTextInput("createName").SetPlaceHolderText(SVCLang.Get("placeholder-channel-name"));
        composer.GetButton("adminApply").Enabled = CanExecuteAdminAction();
        composer.GetButton("adminRename").Enabled = CanRenameAdminChannel(channels);
        composer.GetButton("createChannel").Enabled = !string.IsNullOrWhiteSpace(createName);
        return Math.Max(leftY, rightY) + 60;
    }

    private static void AddSwitchRow(GuiComposer composer, double x, ref double y, string text, string key, bool value, Action<bool> changed)
    {
        composer
            .AddStaticText(text, CairoFont.WhiteSmallishText(), ElementBounds.Fixed(x, y, 210, 26))
            .AddSwitch(changed, ElementBounds.Fixed(x + 217, y, 28, 28), key);
        composer.GetSwitch(key).SetValue(value);
        y += 36;
    }

    private static void ConfigureSlider(
        GuiComposer composer,
        string key,
        int value,
        int minimum,
        int maximum,
        string suffix = "")
    {
        GuiElementSlider slider = composer.GetSlider(key)
            ?? throw new InvalidOperationException($"Settings slider '{key}' was not found.");
        slider.SetValues(value, minimum, maximum, 1, suffix);
    }

    private List<string> BuildChannelActions(VoiceSettingsChannelOption[] channels)
    {
        bool hasPlayer = !string.IsNullOrWhiteSpace(selectedPlayerUid);
        VoiceSettingsChannelOption? selected = channels.Cast<VoiceSettingsChannelOption?>().FirstOrDefault(channel => channel?.Id == config.SelectedChannelId);
        bool hasChannel = selected.HasValue;
        VoiceChannelRole role = selected?.LocalRole ?? VoiceChannelRole.Banned;
        bool external = selected?.ExternallyManaged ?? false;
        bool canInvite = controller.HasServerControl
            || selected is { LocalRole: >= VoiceChannelRole.Moderator };
        List<string> actions = new();
        if (canInvite && hasPlayer) actions.Add("invite");
        if (hasChannel && !external) actions.Add("leave");
        if (hasChannel && role >= VoiceChannelRole.Moderator && hasPlayer)
        {
            actions.AddRange(new[] { "mute", "unmute", "ban", "unban" });
            if (!external) actions.Add("remove");
        }
        if (hasChannel && role == VoiceChannelRole.Owner)
        {
            if (hasPlayer && !external) actions.AddRange(new[] { "listenonly", "member", "moderator" });
            actions.AddRange(new[] { "lock", "unlock" });
            if (!external) actions.Add("disband");
        }
        if (actions.Count == 0) actions.Add("none");
        return actions;
    }

    private string[] BuildAdminChannelActions(VoiceSettingsChannelOption[] channels)
    {
        VoiceSettingsChannelOption? selected = channels.Cast<VoiceSettingsChannelOption?>().FirstOrDefault(channel => channel?.Id == selectedAdminChannelId);
        List<string> actions = new() { "mute", "unmute", "ban", "unban", "lock", "unlock" };
        if (selected is { ExternallyManaged: false })
        {
            actions.AddRange(new[] { "add", "remove", "listenonly", "member", "moderator", "disband" });
        }
        if (!actions.Contains(selectedAdminAction, StringComparer.Ordinal))
        {
            selectedAdminAction = actions[0];
        }
        return actions.ToArray();
    }

    private bool SelectPage(VoiceSettingsPage page)
    {
        if (page == VoiceSettingsPage.Admin && !controller.HasServerControl)
        {
            return false;
        }
        selectedPage = page;
        scrollPosition = 0;
        QueueCompose();
        return true;
    }

    private void OnInputDeviceChanged(string value, bool selected)
    {
        if (selected) controller.SetInputDeviceFromSettings(value);
    }

    private void OnChannelChanged(string value, bool selected)
    {
        if (!selected) return;
        config.SelectedChannelId = value;
        renameText = string.Empty;
        selectedChannelAction = "invite";
        controller.SelectChannelFromSettings(value);
        QueueCompose();
    }

    private void OnTransmitChanged(string value, bool selected)
    {
        if (selected) controller.SetTransmitTargetFromSettings(value);
    }

    private void OnPlayerChanged(string value, bool selected)
    {
        if (!selected) return;
        selectedPlayerUid = value;
        QueueCompose();
    }

    private bool OnSelectedPlayerVolumeChanged(int value)
    {
        if (!string.IsNullOrWhiteSpace(selectedPlayerUid)) controller.SetPlayerVolumeFromSettings(selectedPlayerUid, value);
        return true;
    }

    private void OnSelectedPlayerMuteChanged(bool value)
    {
        if (!string.IsNullOrWhiteSpace(selectedPlayerUid)) controller.SetPlayerMutedFromSettings(selectedPlayerUid, value);
    }

    private bool SetPlayerVolume(string playerUid, int value)
    {
        controller.SetPlayerVolumeFromSettings(playerUid, value);
        return true;
    }

    private void OnChannelActionChanged(string value, bool selected)
    {
        if (selected) selectedChannelAction = value;
    }

    private bool ExecuteChannelAction()
    {
        if (selectedChannelAction == "none") return false;
        VoiceChannelRole role = selectedChannelAction switch
        {
            "listenonly" => VoiceChannelRole.ListenOnly,
            "moderator" => VoiceChannelRole.Moderator,
            _ => VoiceChannelRole.Member
        };
        string action = selectedChannelAction is "listenonly" or "member" or "moderator" ? "role" : selectedChannelAction;
        controller.ManageSelectedChannel(action, config.SelectedChannelId, selectedPlayerUid, string.Empty, role);
        return true;
    }

    private bool RenameSelectedChannel()
    {
        if (string.IsNullOrWhiteSpace(renameText) || string.IsNullOrWhiteSpace(config.SelectedChannelId)) return false;
        controller.ManageSelectedChannel("rename", config.SelectedChannelId, name: renameText);
        return true;
    }

    private bool ExecuteModeration(string action)
    {
        if (!controller.HasServerControl || string.IsNullOrWhiteSpace(selectedPlayerUid)) return false;
        controller.ManageSelectedChannel(action, string.Empty, selectedPlayerUid);
        return true;
    }

    private void OnAdminChannelChanged(string value, bool selected)
    {
        if (!selected) return;
        selectedAdminChannelId = value;
        renameText = controller.BuildChannelOptions().FirstOrDefault(channel => channel.Id == value).Name ?? string.Empty;
        QueueCompose();
    }

    private void OnAdminActionChanged(string value, bool selected)
    {
        if (selected)
        {
            selectedAdminAction = value;
            SetButtonEnabled("adminApply", CanExecuteAdminAction());
        }
    }

    private bool ExecuteAdminChannelAction()
    {
        if (!controller.HasServerControl || !CanExecuteAdminAction()) return false;
        VoiceChannelRole role = selectedAdminAction switch
        {
            "listenonly" => VoiceChannelRole.ListenOnly,
            "moderator" => VoiceChannelRole.Moderator,
            _ => VoiceChannelRole.Member
        };
        string action = selectedAdminAction is "listenonly" or "member" or "moderator" ? "role" : selectedAdminAction;
        controller.ManageSelectedChannel(action, selectedAdminChannelId, selectedPlayerUid, string.Empty, role);
        return true;
    }

    private bool RenameAdminChannel()
    {
        if (!controller.HasServerControl || string.IsNullOrWhiteSpace(renameText)) return false;
        controller.ManageSelectedChannel("rename", selectedAdminChannelId, name: renameText);
        return true;
    }

    private void OnRenameTextChanged(string value)
    {
        renameText = value;
        SetButtonEnabled("adminRename", CanRenameAdminChannel(controller.BuildChannelOptions()));
    }

    private void OnCreateNameChanged(string value)
    {
        createName = value;
        SetButtonEnabled("createChannel", controller.HasServerControl && !string.IsNullOrWhiteSpace(value));
    }

    private bool CreateChannel()
    {
        if (!controller.HasServerControl || string.IsNullOrWhiteSpace(createName)) return false;
        controller.ManageSelectedChannel("create-channel", string.Empty, name: createName);
        createName = string.Empty;
        SingleComposer?.GetTextInput("createName")?.SetValue(string.Empty);
        SetButtonEnabled("createChannel", false);
        return true;
    }

    private bool CanExecuteAdminAction()
    {
        return controller.HasServerControl
            && !string.IsNullOrWhiteSpace(selectedAdminChannelId)
            && (!VoiceSettingsActionPolicy.RequiresTarget(selectedAdminAction) || !string.IsNullOrWhiteSpace(selectedPlayerUid));
    }

    private bool CanRenameAdminChannel(VoiceSettingsChannelOption[] channels)
    {
        return controller.HasServerControl
            && !string.IsNullOrWhiteSpace(renameText)
            && channels.Any(channel => channel.Id == selectedAdminChannelId && !channel.ExternallyManaged);
    }

    private void NormalizePlayer(VoiceSettingsPlayerOption[] players)
    {
        if (!players.Any(player => player.Id == selectedPlayerUid))
        {
            selectedPlayerUid = players.FirstOrDefault().Id ?? string.Empty;
        }
    }

    private void NormalizeAdminChannel(VoiceSettingsChannelOption[] channels)
    {
        if (channels.Any(channel => channel.Id == selectedAdminChannelId)) return;
        selectedAdminChannelId = channels.FirstOrDefault(channel => channel.Id == config.SelectedChannelId).Id
            ?? channels.FirstOrDefault().Id
            ?? string.Empty;
        renameText = channels.FirstOrDefault(channel => channel.Id == selectedAdminChannelId).Name ?? string.Empty;
    }

    private void SetButtonEnabled(string key, bool enabled)
    {
        if (SingleComposer?.GetButton(key) is { } button)
        {
            button.Enabled = enabled;
        }
    }

    private void OnScroll(float value)
    {
        scrollPosition = Math.Clamp(
            value,
            0f,
            (float)Math.Max(0d, contentHeight - ViewportHeight));
        if (contentBounds == null) return;
        contentBounds.fixedY = -scrollPosition;
        contentBounds.CalcWorldBounds();
        if (!suppressScrollCallback)
        {
            SingleComposer?.ReCompose();
        }
    }

    private void QueueCompose()
    {
        if (composeQueued) return;
        composeQueued = true;
        capi.Event.EnqueueMainThreadTask(() =>
        {
            composeQueued = false;
            if (IsOpened()) Compose();
        }, "simplevoicechat-settings-recompose");
    }

    private void OnClose()
    {
        TryClose();
    }

    private static string GetPageName(VoiceSettingsPage page)
    {
        return page switch
        {
            VoiceSettingsPage.Channels => SVCLang.Get("tab-channels"),
            VoiceSettingsPage.Admin => SVCLang.Get("tab-admin"),
            _ => SVCLang.Get("tab-audio")
        };
    }

    private static string TransmitCode(VoiceTransmitTarget target)
    {
        return target switch
        {
            VoiceTransmitTarget.SelectedChannel => "channel",
            VoiceTransmitTarget.ProximityAndChannel => "both",
            _ => "proximity"
        };
    }

    private static string Truncate(string value, int maximumLength)
    {
        return value.Length <= maximumLength ? value : value[..Math.Max(1, maximumLength - 3)] + "...";
    }
}
