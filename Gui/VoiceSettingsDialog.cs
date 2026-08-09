using Cairo;
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
    private const double WindowWidth = 940;
    private const double WindowHeight = 650;
    private const double ContentLeft = 14;
    private const double ContentTop = 56;
    private const double ContentWidth = 900;
    private const double ViewportHeight = 580;
    private const string FontAwesomeCheckIcon = "svc-fa-check";
    private const string FontAwesomeCloseIcon = "svc-fa-xmark";

    private static readonly AssetLocation FontAwesomeCheckAsset = new("simplevoicechat", "icons/fontawesome/check.svg");
    private static readonly AssetLocation FontAwesomeCloseAsset = new("simplevoicechat", "icons/fontawesome/xmark.svg");

    private readonly ClientVoiceController controller;
    private readonly SimpleVoiceChatClientConfig config;

    private VoiceSettingsPage selectedPage = VoiceSettingsPage.Audio;
    private ElementBounds? contentBounds;
    private float scrollPosition;
    private double contentHeight = ViewportHeight;
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
        RegisterFontAwesomeIcons();
        VoiceSettingsPage[] pages = VoiceSettingsNavigation.BuildPages(controller.HasServerControl);
        if (!pages.Contains(selectedPage))
        {
            selectedPage = VoiceSettingsPage.Audio;
            scrollPosition = 0;
        }

        SingleComposer?.Dispose();
        ElementBounds root = ElementBounds.Fixed(EnumDialogArea.CenterMiddle, 0, 0, WindowWidth, WindowHeight);
        ElementBounds background = ElementBounds.Fixed(0, 0, WindowWidth, WindowHeight);
        ElementBounds viewport = ElementBounds.Fixed(ContentLeft, ContentTop, ContentWidth, ViewportHeight);
        contentBounds = ElementBounds.Fixed(0, 0, ContentWidth, ViewportHeight);

        GuiComposer composer = capi.Gui.CreateCompo("simplevoicechat-settings", root)
            .AddStaticCustomDraw(background, DrawWindowBackground)
            .BeginChildElements(background);

        composer.AddInteractiveElement(
            new VoiceSettingsIconButton(
                capi,
                ElementBounds.Fixed(WindowWidth - 42, 10, 28, 28),
                FontAwesomeCloseIcon,
                _ => OnClose()),
            "close");

        double navX = ContentLeft;
        foreach (VoiceSettingsPage page in pages)
        {
            VoiceSettingsPage captured = page;
            string key = "nav-" + page.ToString().ToLowerInvariant();
            double width = page == VoiceSettingsPage.Channels ? 178 : 118;
            AddFlatButton(
                composer,
                GetPageName(page),
                () => SelectPage(captured),
                ElementBounds.Fixed(navX, 10, width, 32),
                key,
                page == selectedPage);
            navX += width + 3;
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

        SingleComposer = composer.EndChildElements().EndClip().EndChildElements().Compose();
    }

    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        base.OnMouseWheel(args);
        if (args.IsHandled)
        {
            return;
        }

        if (!IsOpened() || SingleComposer == null
            || !SingleComposer.Bounds.PointInside(capi.Input.MouseX, capi.Input.MouseY))
        {
            return;
        }

        float next = Math.Clamp(
            scrollPosition - (float)(args.delta * GuiElement.scaled(36)),
            0f,
            (float)Math.Max(0d, contentHeight - ViewportHeight));
        if (Math.Abs(next - scrollPosition) > 0.01f)
        {
            OnScroll(next);
            args.SetHandled();
        }
    }

    private void RegisterFontAwesomeIcons()
    {
        capi.Gui.Icons.CustomIcons[FontAwesomeCheckIcon] = capi.Gui.Icons.SvgIconSource(FontAwesomeCheckAsset);
        capi.Gui.Icons.CustomIcons[FontAwesomeCloseIcon] = capi.Gui.Icons.SvgIconSource(FontAwesomeCloseAsset);
    }

    private static void DrawWindowBackground(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        bounds.CalcWorldBounds();
        GuiElement.RoundRectangle(ctx, bounds.bgDrawX, bounds.bgDrawY, bounds.OuterWidth, bounds.OuterHeight, GuiElement.scaled(4));
        ctx.SetSourceRGBA(0.015, 0.02, 0.028, 0.84);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(0.78, 0.82, 0.9, 0.22);
        ctx.LineWidth = GuiElement.scaled(1);
        ctx.Stroke();
    }

    private static void DrawFlatButtonBackground(Context ctx, ImageSurface surface, ElementBounds bounds, bool active)
    {
        bounds.CalcWorldBounds();
        ctx.Rectangle(bounds.drawX, bounds.drawY, bounds.InnerWidth, bounds.InnerHeight);
        ctx.SetSourceRGBA(0.62, 0.66, 0.72, active ? 0.36 : 0.22);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(0.92, 0.95, 1.0, active ? 0.95 : 0.88);
        ctx.LineWidth = GuiElement.scaled(1);
        ctx.Stroke();
    }

    private static void AddFlatButton(GuiComposer composer, string text, ActionConsumable action, ElementBounds bounds, string key, bool active = false)
    {
        composer.AddStaticCustomDraw(bounds, (ctx, surface, elementBounds) => DrawFlatButtonBackground(ctx, surface, elementBounds, active))
            .AddButton(
                text,
                action,
                bounds,
                CairoFont.WhiteSmallText().WithFontSize(14).WithOrientation(EnumTextOrientation.Center).WithColor(new[] { 0.96, 0.97, 1.0, 1.0 }),
                EnumButtonStyle.None,
                key);
    }

    private static VoiceSettingsCheckBox AddCheckBox(GuiComposer composer, Action<bool> changed, ElementBounds bounds, string key, bool value)
    {
        VoiceSettingsCheckBox checkBox = new(composer.Api, bounds, changed);
        composer.AddInteractiveElement(checkBox, key);
        checkBox.SetValue(value);
        return checkBox;
    }

    private static VoiceSettingsCheckBox GetCheckBox(GuiComposer composer, string key)
    {
        return (VoiceSettingsCheckBox)composer.GetElement(key);
    }

    private double AddAudioPage(GuiComposer composer)
    {
        const double labelX = 18;
        const double controlX = 235;
        const double controlWidth = 250;
        double y = 0;
        CairoFont section = CairoFont.WhiteSmallishText().WithColor(new[] { 0.96, 0.97, 1.0, 1.0 });
        CairoFont label = CairoFont.WhiteSmallText().WithColor(new[] { 0.9, 0.92, 0.96, 1.0 });

        string[] inputValues = controller.GetInputDeviceValues();
        string[] inputNames = ClientVoiceController.GetInputDeviceNames(inputValues);
        string selectedInput = config.InputDeviceName ?? string.Empty;
        string[] outputValues = controller.GetOutputDeviceValues();
        string[] outputNames = ClientVoiceController.GetOutputDeviceNames(outputValues);
        string selectedOutput = config.OutputDeviceName ?? string.Empty;

        composer
            .AddStaticText(SVCLang.Get("label-input-device"), label, ElementBounds.Fixed(labelX, y + 3, 210, 30))
            .AddVoiceDropDown(inputValues, inputNames, Math.Max(0, Array.IndexOf(inputValues, selectedInput)), OnInputDeviceChanged, ElementBounds.Fixed(controlX, y, controlWidth, 32), "inputDevice")
            .AddStaticText(SVCLang.Get("label-output-device"), label, ElementBounds.Fixed(labelX, y += 48, 210, 30))
            .AddVoiceDropDown(outputValues, outputNames, Math.Max(0, Array.IndexOf(outputValues, selectedOutput)), OnOutputDeviceChanged, ElementBounds.Fixed(controlX, y, controlWidth, 32), "outputDevice")
            .AddStaticText(SVCLang.Get("label-output-volume"), label, ElementBounds.Fixed(labelX, y += 48, 210, 30))
            .AddVoiceSlider(value => { controller.SetOutputVolumeFromSettings(value); return true; }, ElementBounds.Fixed(controlX, y, controlWidth, 24), "outputVolume")
            .AddStaticText(SVCLang.Get("label-mic-gain"), label, ElementBounds.Fixed(labelX, y += 48, 210, 30))
            .AddVoiceSlider(value => { controller.SetMicGainFromSettings(value); return true; }, ElementBounds.Fixed(controlX, y, controlWidth, 24), "micGain")
            .AddStaticText(SVCLang.Get("label-noise-gate"), label, ElementBounds.Fixed(labelX, y += 48, 210, 30))
            .AddVoiceSlider(value => { controller.SetNoiseGateFromSettings(value); return true; }, ElementBounds.Fixed(controlX, y, controlWidth, 24), "noiseGate");

        ConfigureSlider(composer, "outputVolume", (int)Math.Round(config.OutputVolume * 100), 0, 200, "%");
        ConfigureSlider(composer, "micGain", (int)Math.Round(config.MicGain * 100), 10, 400, "%");
        ConfigureSlider(composer, "noiseGate", (int)Math.Round(config.NoiseGate * 1000), 0, 200);

        y += 58;
        composer.AddStaticText(SVCLang.Get("ui-section-behavior"), section, ElementBounds.Fixed(labelX, y, 868, 26));
        y += 36;
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

        GetCheckBox(composer, "continuous").Enabled = controller.ContinuousTalkAllowed;
        GetCheckBox(composer, "noiseSuppression").Enabled = VoiceProcessingCapabilities.NoiseSuppressionAvailable;
        GetCheckBox(composer, "echoCancellation").Enabled = VoiceProcessingCapabilities.EchoCancellationAvailable;
        GetCheckBox(composer, "occlusion").Enabled = !controller.OcclusionForced;
        return Math.Max(leftY, rightY) + 24;
    }

    private double AddChannelsPage(GuiComposer composer)
    {
        const double x = 18;
        const double rightX = 466;
        const double fieldWidth = 210;
        const double controlWidth = 190;
        const double channelActionWidth = 240;
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
            .AddStaticText(SVCLang.Get("label-channel-select"), label, ElementBounds.Fixed(x, y + 3, fieldWidth, 30))
            .AddStaticText(SVCLang.Get("label-transmit-target"), label, ElementBounds.Fixed(rightX, y + 3, fieldWidth, 30))
            .AddVoiceDropDown(channelValues, channelNames, Math.Max(0, Array.IndexOf(channelValues, config.SelectedChannelId)), OnChannelChanged, ElementBounds.Fixed(x + 217, y, controlWidth, 32), "channel")
            .AddVoiceDropDown(transmitValues, transmitNames, Math.Max(0, Array.IndexOf(transmitValues, TransmitCode(config.TransmitTarget))), OnTransmitChanged, ElementBounds.Fixed(rightX + 217, y, controlWidth, 32), "transmit")
            .AddStaticText(SVCLang.Get("label-channel-volume"), label, ElementBounds.Fixed(x, y += 48, fieldWidth, 30))
            .AddVoiceSlider(value => { controller.SetChannelVolumeFromSettings(value); return true; }, ElementBounds.Fixed(x + 217, y, controlWidth, 24), "channelVolume")
            .AddStaticText(SVCLang.Get("ui-section-player"), section, ElementBounds.Fixed(x, y += 48, 868, 26))
            .AddStaticText(SVCLang.Get("ui-target-player"), label, ElementBounds.Fixed(x, y += 38, fieldWidth, 30))
            .AddVoiceDropDown(playerValues, playerNames, Math.Max(0, Array.IndexOf(playerValues, selectedPlayerUid)), OnPlayerChanged, ElementBounds.Fixed(x + 217, y, controlWidth, 32), "selectedPlayer")
            .AddStaticText(SVCLang.Get("label-player-volume"), label, ElementBounds.Fixed(x, y += 48, fieldWidth, 30))
            .AddVoiceSlider(OnSelectedPlayerVolumeChanged, ElementBounds.Fixed(x + 217, y, 132, 24), "selectedPlayerVolume")
            ;
        AddCheckBox(composer, OnSelectedPlayerMuteChanged, ElementBounds.Fixed(x + 397, y - 2, 28, 28), "selectedPlayerMute", false);

        ((GuiElementControl)composer.GetElement("channel")).Enabled = true;
        ConfigureSlider(composer, "channelVolume", (int)Math.Round(config.ChannelOutputVolume * 100), 0, 200, "%");
        bool hasPlayer = !string.IsNullOrWhiteSpace(selectedPlayerUid);
        ConfigureSlider(composer, "selectedPlayerVolume", hasPlayer ? controller.GetPlayerVolumePercent(selectedPlayerUid) : 100, 0, 200, "%");
        composer.GetSlider("selectedPlayerVolume")!.Enabled = hasPlayer;
        GetCheckBox(composer, "selectedPlayerMute").SetValue(hasPlayer && controller.IsPlayerMuted(selectedPlayerUid));
        GetCheckBox(composer, "selectedPlayerMute").Enabled = hasPlayer;

        List<string> actions = BuildChannelActions(channels);
        if (!actions.Contains(selectedChannelAction, StringComparer.Ordinal))
        {
            selectedChannelAction = actions[0];
        }
        y += 58;
        composer
            .AddStaticText(SVCLang.Get("label-channel-manage"), section, ElementBounds.Fixed(x, y, 868, 26))
            .AddVoiceDropDown(actions.ToArray(), actions.Select(action => SVCLang.Get("channel-action-" + action)).ToArray(), Math.Max(0, actions.IndexOf(selectedChannelAction)), OnChannelActionChanged, ElementBounds.Fixed(x, y += 38, channelActionWidth, 32), "channelAction");
        AddFlatButton(composer, SVCLang.Get("button-apply"), ExecuteChannelAction, ElementBounds.Fixed(x + channelActionWidth + 12, y, 136, 32), "applyChannelAction");
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
                .AddStaticText(SVCLang.Get("label-channel-name"), label, ElementBounds.Fixed(x, y += 48, fieldWidth, 30))
                .AddTextInput(ElementBounds.Fixed(x + 217, y, 320, 32), OnRenameTextChanged, CairoFont.TextInput(), "channelRename");
            AddFlatButton(composer, SVCLang.Get("button-rename-channel"), RenameSelectedChannel, ElementBounds.Fixed(x + 550, y, 86, 32), "renameChannel");
            composer.GetTextInput("channelRename").SetValue(renameText);
            composer.GetTextInput("channelRename").SetMaxLength(VoiceProtocol.MaxControlStringLength);
        }

        y += 64;
        composer
                .AddStaticText(SVCLang.Get("setting-voice-players"), section, ElementBounds.Fixed(x, y, 868, 26));
        y += 38;

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
            double cardY = y;
            composer.AddStaticCustomDraw(ElementBounds.Fixed(x, cardY, 868, 44), DrawPlayerCardBackground)
                .AddStaticText(Truncate(player.Name, 30), label, ElementBounds.Fixed(x + 12, cardY + 8, 206, 28), "playerName" + index)
                .AddVoiceSlider(value => SetPlayerVolume(player.Id, value), ElementBounds.Fixed(x + 225, cardY + 10, 540, 24), sliderKey);
            VoiceSettingsMuteButton muteButton = new(
                composer.Api,
                ElementBounds.Fixed(x + 820, cardY + 8, 28, 28),
                value => controller.SetPlayerMutedFromSettings(player.Id, value));
            composer.AddInteractiveElement(muteButton, muteKey);
            ConfigureSlider(composer, sliderKey, controller.GetPlayerVolumePercent(player.Id), 0, 200, "%");
            muteButton.SetValue(controller.IsPlayerMuted(player.Id));
            y += 50;
        }
        return y + 18;
    }

    private double AddAdminPage(GuiComposer composer)
    {
        const double leftX = 18;
        const double rightX = 466;
        const double columnWidth = 420;
        const double fieldWidth = 210;
        const double controlWidth = 190;
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
            .AddStaticText(SVCLang.Get("ui-section-admin-target"), section, ElementBounds.Fixed(leftX, leftY, columnWidth, 26))
            .AddStaticText(SVCLang.Get("ui-target-player"), label, ElementBounds.Fixed(leftX, leftY += 38, fieldWidth, 30))
            .AddVoiceDropDown(playerValues, playerNames, Math.Max(0, Array.IndexOf(playerValues, selectedPlayerUid)), OnPlayerChanged, ElementBounds.Fixed(leftX + 217, leftY, controlWidth, 32), "adminPlayer")
            .AddStaticText(SVCLang.Get("ui-section-temporary-actions"), section, ElementBounds.Fixed(leftX, leftY += 48, columnWidth, 26));
        leftY += 38;
        AddFlatButton(composer, SVCLang.Get("channel-action-tempmute"), () => ExecuteModeration("tempmute"), ElementBounds.Fixed(leftX, leftY, 196, 32), "tempmute");
        AddFlatButton(composer, SVCLang.Get("channel-action-deafen"), () => ExecuteModeration("deafen"), ElementBounds.Fixed(leftX + 210, leftY, 196, 32), "deafen");
        leftY += 48;
        composer.AddStaticText(SVCLang.Get("ui-section-persistent-actions"), section, ElementBounds.Fixed(leftX, leftY, columnWidth, 26));
        leftY += 38;
        AddFlatButton(composer, SVCLang.Get("channel-action-adminmute"), () => ExecuteModeration("adminmute"), ElementBounds.Fixed(leftX, leftY, 196, 32), "adminmute");
        AddFlatButton(composer, SVCLang.Get("channel-action-adminunmute"), () => ExecuteModeration("adminunmute"), ElementBounds.Fixed(leftX + 210, leftY, 196, 32), "adminunmute");
        leftY += 44;
        AddFlatButton(composer, SVCLang.Get("channel-action-forceblock"), () => ExecuteModeration("forceblock"), ElementBounds.Fixed(leftX, leftY, 196, 32), "forceblock");
        AddFlatButton(composer, SVCLang.Get("channel-action-unforceblock"), () => ExecuteModeration("unforceblock"), ElementBounds.Fixed(leftX + 210, leftY, 196, 32), "unforceblock");
        leftY += 44;
        composer.AddStaticText(SVCLang.Get("ui-admin-warning"), detail, ElementBounds.Fixed(leftX, leftY, columnWidth, 44));

        bool hasTarget = !string.IsNullOrWhiteSpace(selectedPlayerUid);
        foreach (string key in new[] { "tempmute", "deafen", "adminmute", "adminunmute", "forceblock", "unforceblock" })
        {
            composer.GetButton(key).Enabled = hasTarget;
        }

        composer
            .AddStaticText(SVCLang.Get("label-channel-manage"), section, ElementBounds.Fixed(rightX, rightY, columnWidth, 26))
            .AddStaticText(SVCLang.Get("label-current-channel"), label, ElementBounds.Fixed(rightX, rightY += 38, fieldWidth, 30))
            .AddVoiceDropDown(channelValues, channelNames, Math.Max(0, Array.IndexOf(channelValues, selectedAdminChannelId)), OnAdminChannelChanged, ElementBounds.Fixed(rightX + 217, rightY, controlWidth, 32), "adminChannel")
            .AddStaticText(SVCLang.Get("ui-action-select"), label, ElementBounds.Fixed(rightX, rightY += 48, fieldWidth, 30))
            .AddVoiceDropDown(adminActions, adminActions.Select(action => SVCLang.Get("channel-action-" + action)).ToArray(), Math.Max(0, Array.IndexOf(adminActions, selectedAdminAction)), OnAdminActionChanged, ElementBounds.Fixed(rightX + 217, rightY, 120, 32), "adminAction");
        AddFlatButton(composer, SVCLang.Get("button-apply"), ExecuteAdminChannelAction, ElementBounds.Fixed(rightX + 345, rightY, 76, 32), "adminApply");
        composer
            .AddStaticText(SVCLang.Get("label-channel-name"), section, ElementBounds.Fixed(rightX, rightY += 52, columnWidth, 26))
            .AddTextInput(ElementBounds.Fixed(rightX + 217, rightY += 38, 120, 32), OnRenameTextChanged, input, "adminRenameInput");
        AddFlatButton(composer, SVCLang.Get("button-rename-channel"), RenameAdminChannel, ElementBounds.Fixed(rightX + 345, rightY, 76, 32), "adminRename");
        composer
            .AddStaticText(SVCLang.Get("ui-section-create-channel"), section, ElementBounds.Fixed(rightX, rightY += 48, columnWidth, 26))
            .AddTextInput(ElementBounds.Fixed(rightX + 217, rightY += 38, controlWidth, 32), OnCreateNameChanged, input, "createName");
        rightY += 44;
        AddFlatButton(composer, SVCLang.Get("channel-action-create"), CreateChannel, ElementBounds.Fixed(rightX + 217, rightY, 120, 32), "createChannel");

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
        composer.AddStaticText(text, CairoFont.WhiteSmallText().WithColor(new[] { 0.9, 0.92, 0.96, 1.0 }), ElementBounds.Fixed(x, y + 2, 210, 30));
        AddCheckBox(composer, changed, ElementBounds.Fixed(x + 217, y, 28, 28), key, value);
        y += 40;
    }

    private static void DrawPlayerCardBackground(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        bounds.CalcWorldBounds();
        ctx.Rectangle(bounds.drawX, bounds.drawY, bounds.InnerWidth, bounds.InnerHeight);
        ctx.SetSourceRGBA(0.08, 0.1, 0.13, 0.94);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(0.86, 0.9, 0.96, 0.5);
        ctx.LineWidth = GuiElement.scaled(1);
        ctx.Stroke();
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
        if (slider is VoiceSettingsSlider styledSlider)
        {
            styledSlider.Configure(value, minimum, maximum, 1, suffix);
        }
        else
        {
            slider.SetValues(value, minimum, maximum, 1, suffix);
        }
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

    private void OnOutputDeviceChanged(string value, bool selected)
    {
        if (selected) controller.SetOutputDeviceFromSettings(value);
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
        SingleComposer?.ReCompose();
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
