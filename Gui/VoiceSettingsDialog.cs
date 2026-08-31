using Cairo;
using SimpleVoiceChat.Audio;
using SimpleVoiceChat.Config;
using SimpleVoiceChat.Integration;
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

    public static bool RequiresConfirmation(string action)
    {
        return action is "leave" or "disband" or "delete-owned-channel";
    }
}

internal enum VoiceSettingsPage
{
    Home,
    Audio,
    SpeechRecognition,
    Channels,
    Admin
}

internal enum VoiceSettingsOverlay
{
    None,
    Channel,
    Players,
    Player,
    CreateChannel,
    RecordingMode,
    OwnerLeave,
    JoinChannel,
    ConfirmChannelAction,
    MultiTrackRecording,
    CurrentStatus
}

internal static class VoiceSettingsNavigation
{
    public static VoiceSettingsPage[] BuildPages(bool hasServerControl)
    {
        List<VoiceSettingsPage> pages = new()
        {
            VoiceSettingsPage.Home,
            VoiceSettingsPage.Audio,
            VoiceSettingsPage.SpeechRecognition,
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
    // Recompose creates new control instances, so retain the open dropdown by key.
    private static readonly string[] DropdownElementKeys =
    {
        "inputDevice",
        "outputDevice",
        "opusBitrate",
        "quick-channel",
        "quick-transmit",
        "speech-recognition-provider",
        "owner-leave-target",
        "overlay-channel-target-player",
        "overlay-channel-action",
        "overlay-player-channel",
        "overlay-player-action",
        "overlay-create-visibility",
        "adminPlayer",
        "adminChannel",
        "adminAction"
    };

    private static readonly string[] TextInputElementKeys =
    {
        "speech-recognition-api-key",
        "speech-recognition-model",
        "speech-recognition-endpoint",
        "channel-search",
        "players-search",
        "join-channel-password",
        "overlay-create-name",
        "overlay-create-password",
        "adminRenameInput"
    };

    private readonly record struct DropdownSnapshot(string Key, string? HoveredValue, string SearchText);

    private readonly record struct FocusSnapshot(string Key, int CaretLine, int CaretPosition);

    private readonly record struct OverlaySnapshot(
        VoiceSettingsOverlay Overlay,
        string ChannelId,
        string PlayerUid,
        string TargetPlayerUid,
        string Action);

    private const double WindowWidth = 940;
    // The home page is sized to its quick-control row rather than the wider
    // settings pages: five icon buttons, two selectors, six gaps, and margins.
    private const double HomeWindowWidth = 640;
    private const double WindowHeight = 650;
    private const double ContentLeft = 14;
    private const double ContentTop = 58;
    private const double ContentWidth = 900;
    private const double ViewportHeight = 580;
    private const double HomeBaseWindowHeight = 190;
    private const double HomeMaxWindowHeight = WindowHeight;
    private const double HomeExtensionStartY = 156;
    private const double HomeExtensionDefaultRowHeight = 40;
    private const double HomeExtensionGap = 6;
    private const double HomeExtensionMinControlWidth = 28;
    private const double HomeExtensionMinControlHeight = 28;
    private const double HomeExtensionMaxControlHeight = 96;
    private const string FontAwesomeCheckIcon = "svc-fa-check";
    private const string FontAwesomeCloseIcon = "svc-fa-xmark";
    private const string FontAwesomeGearIcon = "svc-fa-gear";
    private const string FontAwesomeUsersIcon = "svc-fa-users";

    private static readonly AssetLocation FontAwesomeCheckAsset = new("simplevoicechat", "icons/fontawesome/check.svg");
    private static readonly AssetLocation FontAwesomeCloseAsset = new("simplevoicechat", "icons/fontawesome/xmark.svg");
    private static readonly AssetLocation FontAwesomeGearAsset = new("simplevoicechat", "icons/fontawesome/gear.svg");
    private static readonly AssetLocation FontAwesomeUsersAsset = new("simplevoicechat", "icons/fontawesome/users.svg");
    private static readonly AssetLocation MicMutedAsset = new("simplevoicechat", "gui/svc_mic_muted.png");
    private static readonly AssetLocation MicTalkingAsset = new("simplevoicechat", "gui/svc_talking.png");
    private static readonly AssetLocation SpeakerAsset = new("simplevoicechat", "gui/svc_speaker.png");
    private static readonly AssetLocation SpeakerDisabledAsset = new("simplevoicechat", "gui/svc_speaker_disable.png");
    private static readonly AssetLocation EyeAsset = new("simplevoicechat", "gui/svc_eye.png");
    private static readonly AssetLocation NoEyeAsset = new("simplevoicechat", "gui/svc_no_eye.png");
    private static readonly AssetLocation PlayersAsset = new("simplevoicechat", "gui/svc_players.png");
    private static readonly AssetLocation RecordingAsset = new("simplevoicechat", "gui/svc_record_vinyl.png");
    private static readonly AssetLocation RecordingStopAsset = new("simplevoicechat", "gui/svc_record_stop.png");
    private static readonly AssetLocation StatusAsset = new("simplevoicechat", "gui/svc_status.png");

    private readonly ClientVoiceController controller;
    private readonly SimpleVoiceChatClientConfig config;
    private readonly VoiceSettingsExtensionRegistry settingsExtensions;
    private VoiceSettingsExtensionDialog? extensionDialog;

    private VoiceSettingsPage selectedPage = VoiceSettingsPage.Home;
    private ElementBounds? contentBounds;
    private float scrollPosition;
    private double contentHeight = ViewportHeight;
    private double activeViewportHeight = ViewportHeight;
    private double activeContentWidth = ContentWidth;
    private bool composeQueued;
    private bool composePending;
    private bool pointerPressed;
    private ServerVoiceConfigPacket adminConfigDraft = new();
    private bool adminConfigDirty;

    private string selectedPlayerUid = string.Empty;
    private string selectedChannelAction = "invite";
    private string selectedAdminChannelId = string.Empty;
    private string selectedAdminAction = "mute";
    private string renameText = string.Empty;
    private bool hudPositionEditing;
    private string createName = string.Empty;
    private string createPassword = string.Empty;
    private string channelSearch = string.Empty;
    private string channelSearchDraft = string.Empty;
    private string playerSearch = string.Empty;
    private string playerSearchDraft = string.Empty;
    private VoiceChannelVisibility createVisibility = VoiceChannelVisibility.Open;
    private string overlayChannelId = string.Empty;
    private string overlayPlayerUid = string.Empty;
    private string overlayTargetPlayerUid = string.Empty;
    private string overlayAction = "none";
    private VoiceSettingsOverlay overlay;
    private string ownerLeaveChannelId = string.Empty;
    private string confirmChannelId = string.Empty;
    private string confirmChannelAction = string.Empty;
    private string joinChannelId = string.Empty;
    private string joinPassword = string.Empty;
    private int channelListPage;
    private int playerListPage;
    private readonly Stack<OverlaySnapshot> overlayStack = new();

    public VoiceSettingsDialog(
        ICoreClientAPI capi,
        ClientVoiceController controller,
        VoiceSettingsExtensionRegistry? settingsExtensions = null)
        : base(capi)
    {
        this.controller = controller;
        config = controller.SettingsConfig;
        this.settingsExtensions = settingsExtensions ?? new VoiceSettingsExtensionRegistry();
        this.settingsExtensions.Attach(QueueCompose, ShowExtensionWindow);
    }

    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;
    public override bool DisableMouseGrab => true;
    // During HUD positioning, let the transparent position dialog receive
    // clicks on the HUD. The settings window still receives clicks inside its
    // own bounds through the normal dialog dispatch path, including confirm.
    public override bool CaptureAllInputs() => !hudPositionEditing;
    public override bool CaptureRawMouse() => true;
    public override EnumDialogType DialogType => EnumDialogType.Dialog;
    public override double DrawOrder => 0.48;
    public override double InputOrder => 0.3;

    internal bool IsCurrentStatusOpen => IsOpened() && overlay == VoiceSettingsOverlay.CurrentStatus;

    public override void OnMouseDown(MouseEvent args)
    {
        // Several settings controls publish their value from mouse-down. Mark
        // the gesture before dispatching so those callbacks cannot trigger a
        // composer rebuild while the pressed control is still being tracked.
        if (args.Button is EnumMouseButton.Left or EnumMouseButton.Right)
        {
            pointerPressed = true;
        }
        if (!args.Handled)
        {
            CloseDropdownsOutside(args.X, args.Y);
        }

        base.OnMouseDown(args);
    }

    public override void OnMouseUp(MouseEvent args)
    {
        // Native controls invoke their callbacks during mouse-up.  Keep the
        // composer alive for that whole gesture, then apply the latest queued
        // state change once no control still owns the pressed state.
        base.OnMouseUp(args);
        if (args.Button is EnumMouseButton.Left or EnumMouseButton.Right)
        {
            pointerPressed = false;
            FlushQueuedCompose();
        }
    }

    public override bool TryOpen()
    {
        controller.RequestSettingsRefresh();
        selectedPage = VoiceSettingsPage.Home;
        scrollPosition = 0;
        composePending = false;
        pointerPressed = false;
        overlay = VoiceSettingsOverlay.None;
        overlayStack.Clear();
        Compose();
        return base.TryOpen();
    }

    public override void Dispose()
    {
        settingsExtensions.Detach();
        extensionDialog?.TryClose();
        extensionDialog?.Dispose();
        extensionDialog = null;
        base.Dispose();
    }

    public void RefreshData()
    {
        if (selectedPage == VoiceSettingsPage.Admin && !controller.HasServerControl)
        {
            selectedPage = VoiceSettingsPage.Home;
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
        if (selectedPage == VoiceSettingsPage.Admin && !controller.HasServerControl)
        {
            selectedPage = VoiceSettingsPage.Home;
            scrollPosition = 0;
        }

        DropdownSnapshot? expandedDropdown = CaptureExpandedDropdown();
        FocusSnapshot? focusedElement = CaptureFocusedElement();
        SingleComposer?.Dispose();
        bool home = selectedPage == VoiceSettingsPage.Home && overlay == VoiceSettingsOverlay.None;
        double windowWidth = home ? HomeWindowWidth : WindowWidth;
        IReadOnlyList<IVoiceSettingsExtensionControl> homeExtensions = home
            ? GetVisibleExtensionControls()
            : Array.Empty<IVoiceSettingsExtensionControl>();
        List<ExtensionRowLayout> homeExtensionRows = home
            ? BuildExtensionRows(homeExtensions, windowWidth - ContentLeft * 2)
            : new();
        double extensionHeight = homeExtensionRows.Count == 0
            ? 0
            : homeExtensionRows.Sum(row => row.Height) + (homeExtensionRows.Count - 1) * HomeExtensionGap;
        double requestedHomeHeight = home
            ? Math.Max(
                HomeBaseWindowHeight,
                HomeExtensionStartY + extensionHeight + 2)
            : WindowHeight;
        double windowHeight = home
            ? Math.Min(HomeMaxWindowHeight, requestedHomeHeight)
            : WindowHeight;
        activeViewportHeight = home ? windowHeight : ViewportHeight;
        activeContentWidth = home ? windowWidth - ContentLeft * 2 : ContentWidth;
        ElementBounds root = ElementBounds.Fixed(EnumDialogArea.CenterMiddle, 0, 0, windowWidth, windowHeight);
        ElementBounds background = ElementBounds.Fixed(0, 0, windowWidth, windowHeight);
        bool clipContent = !home || requestedHomeHeight > windowHeight;
        double viewportTop = home ? 0 : ContentTop;
        ElementBounds viewport = ElementBounds.Fixed(ContentLeft, viewportTop, activeContentWidth, activeViewportHeight);
        contentBounds = ElementBounds.Fixed(0, 0, activeContentWidth, activeViewportHeight);
        bool overlayActive = overlay != VoiceSettingsOverlay.None;

        GuiComposer composer = capi.Gui.CreateCompo("simplevoicechat-settings", root);
        if (!overlayActive)
        {
            composer.AddStaticCustomDraw(background, DrawWindowBackground);
        }
        composer = composer.BeginChildElements(background);

        if (!overlayActive)
        {
            composer.AddStaticText(
                    SVCLang.Get("settings-brand-title"),
                    CairoFont.WhiteSmallishText().WithFontSize(20).WithOrientation(EnumTextOrientation.Center)
                        .WithColor(new[] { 1.0, 1.0, 1.0, 1.0 }),
                    ElementBounds.Fixed(ContentLeft, 10, activeContentWidth, 30),
                    "brand-title");

            composer.AddInteractiveElement(
                new VoiceSettingsIconButton(
                    capi,
                    ElementBounds.Fixed(windowWidth - 42, 10, 28, 28),
                    FontAwesomeCloseIcon,
                    _ => OnClose()),
                "close");

            if (clipContent)
            {
                composer.BeginClip(viewport);
            }
            composer.BeginChildElements(contentBounds);

            contentHeight = selectedPage switch
            {
                VoiceSettingsPage.Home => AddHomePage(composer, homeExtensionRows),
                VoiceSettingsPage.SpeechRecognition => AddSpeechRecognitionPage(composer),
                VoiceSettingsPage.Channels => AddChannelsPage(composer),
                VoiceSettingsPage.Admin => AddAdminPage(composer),
                _ => AddAudioPage(composer)
            };
            contentHeight = Math.Max(activeViewportHeight, contentHeight);
            scrollPosition = Math.Clamp(
                scrollPosition,
                0f,
                (float)Math.Max(0d, contentHeight - activeViewportHeight));
            contentBounds.fixedY = -scrollPosition;

            composer = composer.EndChildElements();
            if (clipContent)
            {
                composer = composer.EndClip();
            }
            if (contentHeight > activeViewportHeight)
            {
                const string scrollbarKey = "settings-scrollbar";
                VoiceSettingsScrollbar scrollbar = new(
                    composer.Api,
                    ElementBounds.Fixed(windowWidth - 20, viewportTop + 4, 8, activeViewportHeight - 8),
                    OnScroll);
                scrollbar.SetHeights(activeViewportHeight, contentHeight, scrollPosition);
                composer.AddInteractiveElement(scrollbar, scrollbarKey);
            }
        }
        if (overlay != VoiceSettingsOverlay.None)
        {
            AddOverlay(composer);
        }
        composer = composer.EndChildElements();
        RestoreExpandedDropdown(composer, expandedDropdown);
        SingleComposer = composer.Compose(focusFirstElement: false);
        if (!RestoreFocusedElement(SingleComposer, focusedElement))
        {
            SingleComposer.FocusElement(0);
        }
    }

    internal void OnServerConfigRefreshed()
    {
        if (!adminConfigDirty)
        {
            adminConfigDraft = CloneServerConfig(controller.ServerSettings);
        }
    }

    private DropdownSnapshot? CaptureExpandedDropdown()
    {
        if (SingleComposer == null)
        {
            return null;
        }

        foreach (string key in DropdownElementKeys)
        {
            if (SingleComposer.GetElement(key) is VoiceSettingsDropDown dropdown && dropdown.IsExpanded)
            {
                return new DropdownSnapshot(key, dropdown.HoveredValue, dropdown.SearchText);
            }
        }

        return null;
    }

    private static void RestoreExpandedDropdown(GuiComposer composer, DropdownSnapshot? snapshot)
    {
        if (snapshot is not { } state)
        {
            return;
        }

        if (composer.GetElement(state.Key) is VoiceSettingsDropDown dropdown)
        {
            dropdown.RestoreExpanded(state.HoveredValue, state.SearchText);
        }
    }

    private FocusSnapshot? CaptureFocusedElement()
    {
        if (SingleComposer == null)
        {
            return null;
        }

        foreach (string key in DropdownElementKeys.Concat(TextInputElementKeys))
        {
            if (SingleComposer.GetElement(key) is not { Focusable: true, HasFocus: true } element)
            {
                continue;
            }

            if (element is GuiElementTextInput textInput)
            {
                return new FocusSnapshot(key, textInput.CaretPosLine, textInput.CaretPosInLine);
            }

            return new FocusSnapshot(key, 0, 0);
        }

        return null;
    }

    private static bool RestoreFocusedElement(GuiComposer composer, FocusSnapshot? snapshot)
    {
        if (snapshot is not { } state
            || composer.GetElement(state.Key) is not { Focusable: true } element)
        {
            return false;
        }

        if (!composer.FocusElement(element.TabIndex))
        {
            return false;
        }

        if (element is GuiElementTextInput textInput)
        {
            textInput.SetCaretPos(state.CaretPosition, state.CaretLine);
        }

        return true;
    }

    private void CloseDropdownsOutside(int posX, int posY)
    {
        VoiceSettingsDropDown? keepOpen = null;
        if (SingleComposer != null)
        {
            foreach (string key in DropdownElementKeys)
            {
                if (SingleComposer.GetElement(key) is VoiceSettingsDropDown dropdown
                    && dropdown.IsExpanded
                    && dropdown.IsPositionInside(posX, posY))
                {
                    keepOpen = dropdown;
                    break;
                }
            }

            foreach (string key in DropdownElementKeys)
            {
                if (SingleComposer.GetElement(key) is VoiceSettingsDropDown dropdown
                    && dropdown.IsExpanded
                    && !ReferenceEquals(dropdown, keepOpen))
                {
                    dropdown.Close();
                }
            }
        }
    }

    internal void OpenOwnerLeaveOverlay(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId)) return;
        overlayStack.Clear();
        ownerLeaveChannelId = channelId;
        overlay = VoiceSettingsOverlay.OwnerLeave;
        QueueCompose();
    }

    private void OpenJoinChannelOverlay(string channelId)
    {
        VoiceSettingsChannelOption channel = controller.BuildChannelOptions().FirstOrDefault(option => option.Id == channelId);
        if (channel.Visibility == VoiceChannelVisibility.Open)
        {
            controller.JoinChannelFromSettings(channelId, string.Empty);
            return;
        }
        PushOverlayState();
        joinChannelId = channelId;
        joinPassword = string.Empty;
        overlay = VoiceSettingsOverlay.JoinChannel;
        QueueCompose();
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
            (float)Math.Max(0d, contentHeight - activeViewportHeight));
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
        capi.Gui.Icons.CustomIcons[FontAwesomeGearIcon] = capi.Gui.Icons.SvgIconSource(FontAwesomeGearAsset);
        capi.Gui.Icons.CustomIcons[FontAwesomeUsersIcon] = capi.Gui.Icons.SvgIconSource(FontAwesomeUsersAsset);
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

    private static void AddFlatButton(GuiComposer composer, string text, ActionConsumable action, ElementBounds bounds, string key, bool active = false)
    {
        composer.AddInteractiveElement(
            new VoiceSettingsTextButton(
                composer.Api,
                text,
                action,
                bounds,
                CairoFont.WhiteSmallText().WithFontSize(14).WithOrientation(EnumTextOrientation.Center).WithColor(new[] { 0.96, 0.97, 1.0, 1.0 }),
                active),
            key);
    }

    private static void AddPagination(
        GuiComposer composer,
        double x,
        double y,
        double width,
        int pageIndex,
        int pageCount,
        ActionConsumable previous,
        ActionConsumable next,
        string keyPrefix)
    {
        const double buttonWidth = 110;
        const double indicatorWidth = 110;
        const double gap = 10;
        double totalWidth = buttonWidth * 2 + indicatorWidth + gap * 2;
        double startX = x + (width - totalWidth) / 2d;
        string previousKey = keyPrefix + "-previous";
        string nextKey = keyPrefix + "-next";

        AddFlatButton(composer, SVCLang.Get("pagination-previous"), previous,
            ElementBounds.Fixed(startX, y, buttonWidth, 34), previousKey);
        composer.AddStaticText(
            SVCLang.Get("pagination-page", pageIndex + 1, pageCount),
            CairoFont.WhiteSmallText().WithFontSize(14).WithOrientation(EnumTextOrientation.Center)
                .WithColor(new[] { 0.96, 0.97, 1.0, 1.0 }),
            ElementBounds.Fixed(startX + buttonWidth + gap, y + 3, indicatorWidth, 28));
        AddFlatButton(composer, SVCLang.Get("pagination-next"), next,
            ElementBounds.Fixed(startX + buttonWidth + gap + indicatorWidth + gap, y, buttonWidth, 34), nextKey);
        composer.GetButton(previousKey).Enabled = pageIndex > 0;
        composer.GetButton(nextKey).Enabled = pageIndex + 1 < pageCount;
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
        const double controlWidth = 600;
        const double controlHeight = 34;
        double y = 0;
        CairoFont label = CairoFont.WhiteSmallText().WithColor(new[] { 0.9, 0.92, 0.96, 1.0 });

        string[] inputValues = controller.GetInputDeviceValues();
        string[] inputNames = ClientVoiceController.GetInputDeviceNames(inputValues);
        string selectedInput = config.InputDeviceName ?? string.Empty;
        string[] outputValues = controller.GetOutputDeviceValues();
        string[] outputNames = ClientVoiceController.GetOutputDeviceNames(outputValues);
        string selectedOutput = config.OutputDeviceName ?? string.Empty;
        string[] bitrateValues = { "0", "8", "12", "16", "20", "24", "32" };
        string[] bitrateNames = bitrateValues
            .Select(value => value == "0" ? SVCLang.Get("bitrate-auto") : SVCLang.Get("bitrate-kbps", value))
            .ToArray();

        composer
            .AddStaticText(SVCLang.Get("label-input-device"), label, ElementBounds.Fixed(labelX, y + 3, 210, 30))
            .AddVoiceDropDown(inputValues, inputNames, Math.Max(0, Array.IndexOf(inputValues, selectedInput)), OnInputDeviceChanged, ElementBounds.Fixed(controlX, y, controlWidth, controlHeight), "inputDevice")
            .AddStaticText(SVCLang.Get("label-output-device"), label, ElementBounds.Fixed(labelX, y += 46, 210, 30))
            .AddVoiceDropDown(outputValues, outputNames, Math.Max(0, Array.IndexOf(outputValues, selectedOutput)), OnOutputDeviceChanged, ElementBounds.Fixed(controlX, y, controlWidth, controlHeight), "outputDevice")
            .AddStaticText(SVCLang.Get("label-output-volume"), label, ElementBounds.Fixed(labelX, y += 46, 210, 30))
            .AddVoiceSlider(value => { controller.SetOutputVolumeFromSettings(value); return true; }, ElementBounds.Fixed(controlX, y, controlWidth, controlHeight), "outputVolume")
            .AddStaticText(SVCLang.Get("label-mic-gain"), label, ElementBounds.Fixed(labelX, y += 46, 210, 30))
            .AddVoiceSlider(value => { controller.SetMicGainFromSettings(value); return true; }, ElementBounds.Fixed(controlX, y, controlWidth, controlHeight), "micGain")
            .AddStaticText(SVCLang.Get("label-opus-bitrate"), label, ElementBounds.Fixed(labelX, y += 46, 210, 30))
            .AddVoiceDropDown(
                bitrateValues,
                bitrateNames,
                Math.Max(0, Array.IndexOf(bitrateValues, config.PreferredOpusBitrateKbps.ToString())),
                OnPreferredOpusBitrateChanged,
                ElementBounds.Fixed(controlX, y, controlWidth, controlHeight),
                "opusBitrate")
            .AddStaticText(SVCLang.Get("setting-voice-noise-gate"), label, ElementBounds.Fixed(labelX, y += 46, 210, 30))
            .AddVoiceActivationThresholdControl(
                () => controller.MicrophoneRms,
                value => { controller.SetNoiseGateFromSettings(value); return true; },
                value => { controller.SetVoiceActivationThresholdFromSettings(value); return true; },
                ElementBounds.Fixed(controlX, y, controlWidth, 58),
                "activationThresholds");

        ConfigureSlider(composer, "outputVolume", (int)Math.Round(config.OutputVolume * 100), 0, 200, "%");
        ConfigureSlider(composer, "micGain", (int)Math.Round(config.MicGain * 100), 10, 400, "%");
        VoiceActivationThresholdControl thresholdControl =
            (VoiceActivationThresholdControl)composer.GetElement("activationThresholds");
        thresholdControl.Configure(
            (int)Math.Round(config.NoiseGate * 1000),
            (int)Math.Round(config.VoiceActivationThreshold * 1000));

        y += 70;
        double recordingY = y;
        composer.AddStaticText(SVCLang.Get("label-local-recording"), label,
            ElementBounds.Fixed(labelX, recordingY + 3, 210, 30));
        AddFlatButton(composer,
            controller.IsMicrophoneTestRecording
                ? SVCLang.Get("button-recording-stop")
                : SVCLang.Get("button-recording-start"),
            controller.ToggleMicrophoneTestRecording,
            ElementBounds.Fixed(controlX, recordingY, 190, controlHeight),
            "recording-toggle",
            active: controller.IsMicrophoneTestRecording);
        AddFlatButton(composer,
            controller.IsMicrophoneTestPlaybackActive
                ? SVCLang.Get("button-play-recording-stop")
                : SVCLang.Get("button-play-recording-start"),
            controller.ToggleMicrophoneTestPlayback,
            ElementBounds.Fixed(controlX + 202, recordingY, 190, controlHeight),
            "recording-playback-toggle",
            active: controller.IsMicrophoneTestPlaybackActive);
        composer.AddStaticText(
            controller.IsMicrophoneTestRecording
                ? SVCLang.Get("recording-status-recording")
                : controller.IsMicrophoneTestPlaybackActive
                    ? SVCLang.Get("recording-status-playback")
                    : controller.HasMicrophoneTestRecording
                        ? SVCLang.Get("recording-status-ready")
                        : SVCLang.Get("recording-status-none"),
            CairoFont.WhiteDetailText().WithColor(new[] { 0.78, 0.82, 0.88, 1.0 }),
            ElementBounds.Fixed(controlX + 404, recordingY + 3, 190, 30));

        // Keep the first behavior row clear of the recording and playback controls.
        y = recordingY + 54;
        double behaviorY = y;
        AddSwitchRow(composer, labelX, ref behaviorY, SVCLang.Get("label-mic-muted"), "localMute", controller.LocalMuted, controller.SetLocalMutedFromSettings);
        AddSwitchRow(composer, labelX, ref behaviorY, SVCLang.Get("label-deafened"), "globalMute", controller.GlobalMuted, controller.SetGlobalMutedFromSettings);
        AddSwitchRow(composer, labelX, ref behaviorY, SVCLang.Get("label-adaptive-jitter"), "jitter", config.AdaptiveJitterBuffer, controller.SetAdaptiveJitterFromSettings);
        AddSwitchRow(composer, labelX, ref behaviorY, SVCLang.Get("label-occlusion"), "occlusion", config.EnableOcclusionEffects, controller.SetOcclusionFromSettings);
        AddSwitchRow(composer, labelX, ref behaviorY, SVCLang.Get("label-performance-mode"), "performance", config.PerformanceMode, controller.SetPerformanceModeFromSettings);

        double secondaryBehaviorY = y;
        AddSwitchRow(composer, labelX + 430, ref secondaryBehaviorY, SVCLang.Get("label-hide-self"),
            "hide-self", config.HideSelfFromPlayerLists, controller.SetHideSelfFromPlayerListsFromSettings);
        AddSwitchRow(composer, labelX + 430, ref secondaryBehaviorY, SVCLang.Get("label-reject-invites"),
            "reject-invites", config.RejectChannelInvites, controller.SetRejectChannelInvitesFromSettings);
        AddSwitchRow(composer, labelX + 430, ref secondaryBehaviorY, SVCLang.Get("label-hide-chat-messages"),
            "hide-chat-messages", config.HideChatMessages, controller.SetHideChatMessagesFromSettings);
        AddFlatButton(composer, hudPositionEditing
                ? SVCLang.Get("button-confirm-hud-position")
                : SVCLang.Get("button-adjust-hud-position"),
            () => { controller.OpenHudPositionDialogFromSettings(); return true; },
            ElementBounds.Fixed(labelX + 430, secondaryBehaviorY - 4, 220, 32), "adjust-hud-position");
        secondaryBehaviorY += 40;

        GetCheckBox(composer, "occlusion").Enabled = !controller.OcclusionForced;
        // Include the full final control row and bottom padding so the page
        // activates the shared scrollbar instead of clipping the last row.
        return Math.Max(behaviorY, secondaryBehaviorY) + 44;
    }

    private double AddHomePage(
        GuiComposer composer,
        IReadOnlyList<ExtensionRowLayout> extensionRows)
    {
        const double buttonWidth = 138;
        const double navigationY = 54;
        const double quickY = 104;
        const double quickIconSize = 42;
        const double quickGap = 6;
        const int quickIconCount = 5;
        double navigationGap = (activeContentWidth - ContentLeft * 2 - buttonWidth * 4) / 3d;

        // Keep both quick selectors inside the compact home window.  Their
        // popup is drawn independently, so a selector must never rely on the
        // popup width to determine the row layout.
        double quickDropDownWidth = Math.Min(
            230,
            Math.Max(
                160,
                Math.Floor((activeContentWidth
                    - ContentLeft
                    - (ContentLeft + quickIconSize * quickIconCount + quickGap * quickIconCount)
                    - quickGap) / 2d)));

        AddFlatButton(
            composer,
            SVCLang.Get("button-settings"),
            () => SelectPage(VoiceSettingsPage.Audio),
            ElementBounds.Fixed(ContentLeft, navigationY, buttonWidth, 42),
            "open-settings",
            active: true);
        AddFlatButton(
            composer,
            SVCLang.Get("button-speech-recognition"),
            () => SelectPage(VoiceSettingsPage.SpeechRecognition),
            ElementBounds.Fixed(ContentLeft + buttonWidth + navigationGap, navigationY, buttonWidth, 42),
            "open-speech-recognition");
        double channelsX = activeContentWidth - buttonWidth - ContentLeft;
        AddFlatButton(
            composer,
            SVCLang.Get("button-channels"),
            () => SelectPage(VoiceSettingsPage.Channels),
            ElementBounds.Fixed(channelsX, navigationY, buttonWidth, 42),
            "open-channels");
        if (controller.HasServerControl)
        {
            AddFlatButton(
                composer,
                SVCLang.Get("button-admin"),
                () => SelectPage(VoiceSettingsPage.Admin),
                ElementBounds.Fixed(channelsX - buttonWidth - navigationGap, navigationY, buttonWidth, 42),
                "open-admin");
        }

        double x = ContentLeft;
        AddIconToggle(composer, MicMutedAsset, MicTalkingAsset, controller.LocalMuted,
            controller.SetLocalMutedFromSettings, ElementBounds.Fixed(x, quickY, quickIconSize, quickIconSize), "quick-mute");
        x += quickIconSize + quickGap;
        AddIconToggle(composer, SpeakerDisabledAsset, SpeakerAsset, controller.GlobalMuted,
            controller.SetGlobalMutedFromSettings, ElementBounds.Fixed(x, quickY, quickIconSize, quickIconSize), "quick-deafen");
        x += quickIconSize + quickGap;
        AddIconToggle(composer, EyeAsset, NoEyeAsset, config.ShowMicrophoneHud,
            controller.SetHudVisibleFromSettings, ElementBounds.Fixed(x, quickY, quickIconSize, quickIconSize), "quick-hud");
        x += quickIconSize + quickGap;
        AssetLocation recordingIcon = controller.IsRecording ? RecordingStopAsset : RecordingAsset;
        VoiceSettingsImageButton recordingButton = new(
            composer.Api,
            ElementBounds.Fixed(x, quickY, quickIconSize, quickIconSize),
            recordingIcon,
            _ => controller.ToggleRecordingFromSettings());
        composer.AddInteractiveElement(recordingButton, "quick-recording");
        x += quickIconSize + quickGap;

        VoiceSettingsImageButton statusButton = new(
            composer.Api,
            ElementBounds.Fixed(x, quickY, quickIconSize, quickIconSize),
            StatusAsset,
            _ => OpenCurrentStatusOverlay());
        composer.AddInteractiveElement(statusButton, "quick-current-status");
        x += quickIconSize + quickGap;

        VoiceSettingsChannelOption[] channels = controller.BuildJoinedChannelOptions();
        string[] channelValues = new[] { string.Empty }.Concat(channels.Select(channel => channel.Id)).ToArray();
        string[] channelNames = new[] { SVCLang.Get("channel-none") }
            .Concat(channels.Select(channel => channel.Name))
            .ToArray();
        composer.AddVoiceDropDown(
            channelValues,
            channelNames,
            Math.Max(0, Array.IndexOf(channelValues, config.SelectedChannelId)),
            OnQuickChannelChanged,
            ElementBounds.Fixed(x, quickY, quickDropDownWidth, 42),
            "quick-channel");
        x += quickDropDownWidth + quickGap;

        string[] transmitValues = { "proximity", "channel", "both" };
        string[] transmitNames = transmitValues.Select(value => SVCLang.Get("transmit-" + value)).ToArray();
        composer.AddVoiceDropDown(
            transmitValues,
            transmitNames,
            Math.Max(0, Array.IndexOf(transmitValues, TransmitCode(config.TransmitTarget))),
            OnQuickTransmitChanged,
            ElementBounds.Fixed(x, quickY, quickDropDownWidth, 42),
            "quick-transmit");

        double extensionY = HomeExtensionStartY;
        foreach (ExtensionRowLayout row in extensionRows)
        {
            double xPosition = ContentLeft;
            foreach (ExtensionControlLayout control in row.Controls)
            {
                try
                {
                    control.Control.Compose(
                        composer.Api,
                        composer,
                        ElementBounds.Fixed(xPosition, extensionY, control.Width, row.Height));
                }
                catch (Exception ex)
                {
                    capi.Logger.Warning(
                        "SimpleVoiceChat: settings extension control '{0}' failed to compose: {1}",
                        GetExtensionControlId(control.Control),
                        ex.Message);
                }
                xPosition += control.Width + HomeExtensionGap;
            }
            extensionY += row.Height + HomeExtensionGap;
        }

        return Math.Max(quickY + 45, extensionY + (extensionRows.Count > 0 ? 2 : 0));
    }

    private IReadOnlyList<IVoiceSettingsExtensionControl> GetVisibleExtensionControls()
    {
        List<IVoiceSettingsExtensionControl> visible = new();
        foreach (IVoiceSettingsExtensionControl control in settingsExtensions.SnapshotControls())
        {
            try
            {
                if (control.IsVisible)
                {
                    visible.Add(control);
                }
            }
            catch (Exception ex)
            {
                capi.Logger.Warning(
                    "SimpleVoiceChat: settings extension control '{0}' visibility check failed: {1}",
                    GetExtensionControlId(control),
                    ex.Message);
            }
        }
        return visible;
    }

    private List<ExtensionRowLayout> BuildExtensionRows(
        IReadOnlyList<IVoiceSettingsExtensionControl> extensions,
        double availableWidth)
    {
        List<ExtensionRowLayout> rows = new();
        if (extensions.Count == 0)
        {
            return rows;
        }

        availableWidth = Math.Max(1, availableWidth);
        ExtensionRowLayout? current = null;
        double currentWidth = 0;
        foreach (IVoiceSettingsExtensionControl control in extensions)
        {
            try
            {
                double minimumWidth = ClampExtensionDimension(control.MinimumWidth, 96, HomeExtensionMinControlWidth, availableWidth);
                double preferredWidth = control.PreferredWidth;
                if (control is VoiceSettingsExtensionButton button)
                {
                    preferredWidth = Math.Max(preferredWidth, MeasureExtensionButtonWidth(button.Text));
                }
                preferredWidth = ClampExtensionDimension(preferredWidth, 140, minimumWidth, availableWidth);
                double height = ClampExtensionDimension(
                    control.Height,
                    HomeExtensionDefaultRowHeight - 6,
                    HomeExtensionMinControlHeight,
                    HomeExtensionMaxControlHeight);

                if (current == null || current.Controls.Count > 0 && currentWidth + HomeExtensionGap + preferredWidth > availableWidth)
                {
                    current = new ExtensionRowLayout();
                    rows.Add(current);
                    currentWidth = 0;
                }

                current.Controls.Add(new ExtensionControlLayout(control, preferredWidth));
                current.Height = Math.Max(current.Height, height);
                currentWidth += (current.Controls.Count > 1 ? HomeExtensionGap : 0) + preferredWidth;
            }
            catch (Exception ex)
            {
                capi.Logger.Warning(
                    "SimpleVoiceChat: settings extension control '{0}' has invalid layout properties: {1}",
                    GetExtensionControlId(control),
                    ex.Message);
            }
        }
        return rows;
    }

    private static double ClampExtensionDimension(double value, double fallback, double minimum, double maximum)
    {
        if (!double.IsFinite(value))
        {
            value = fallback;
        }
        return Math.Clamp(value, minimum, Math.Max(minimum, maximum));
    }

    private static string GetExtensionControlId(IVoiceSettingsExtensionControl control)
    {
        try
        {
            return string.IsNullOrWhiteSpace(control.Id) ? "<unnamed>" : control.Id;
        }
        catch
        {
            return "<unavailable>";
        }
    }

    private static double MeasureExtensionButtonWidth(string text)
    {
        using ImageSurface surface = new(Format.Argb32, 1, 1);
        using Context context = new(surface);
        CairoFont font = CairoFont.WhiteSmallText().WithFontSize(14);
        font.SetupContext(context);
        return Math.Ceiling(context.TextExtents(text).XAdvance + GuiElement.scaled(24));
    }

    private bool ShowExtensionWindow(string id)
    {
        if (!settingsExtensions.TryGetWindow(id, out VoiceSettingsExtensionWindow window))
        {
            return false;
        }

        extensionDialog?.TryClose();
        extensionDialog?.Dispose();
        extensionDialog = new VoiceSettingsExtensionDialog(capi, window, () =>
        {
            if (extensionDialog != null && !extensionDialog.IsOpened())
            {
                extensionDialog.Dispose();
                extensionDialog = null;
            }
        });
        if (!IsOpened())
        {
            extensionDialog.TryOpen();
            return true;
        }

        extensionDialog.TryOpen();
        return true;
    }

    private sealed class ExtensionRowLayout
    {
        public List<ExtensionControlLayout> Controls { get; } = new();
        public double Height { get; set; }
    }

    private readonly record struct ExtensionControlLayout(
        IVoiceSettingsExtensionControl Control,
        double Width);

    private double AddSpeechRecognitionPage(GuiComposer composer)
    {
        const double labelX = 18;
        const double controlX = 250;
        const double controlWidth = 585;
        const double controlHeight = 34;
        double y = 0;
        CairoFont label = CairoFont.WhiteSmallText().WithColor(new[] { 0.9, 0.92, 0.96, 1.0 });
        CairoFont detail = CairoFont.WhiteDetailText().WithColor(new[] { 0.76, 0.8, 0.87, 1.0 });

        composer.AddStaticText(SVCLang.Get("tab-speech-recognition"),
            CairoFont.WhiteSmallText().WithFontSize(18).WithColor(new[] { 0.96, 0.97, 1.0, 1.0 }),
            ElementBounds.Fixed(labelX, y, 500, 30));
        y += 42;
        composer.AddStaticText(SVCLang.Get("speech-recognition-description"), detail,
            ElementBounds.Fixed(labelX, y, 820, 48));
        y += 58;

        composer.AddStaticText(SVCLang.Get("label-speech-recognition-enabled"), label, ElementBounds.Fixed(labelX, y + 2, 220, 30));
        AddCheckBox(composer, controller.SetSpeechRecognitionEnabledFromSettings,
            ElementBounds.Fixed(controlX, y, 28, 28), "speech-recognition-enabled", config.EnableSpeechRecognition);
        y += 46;

        composer.AddStaticText(SVCLang.Get("label-speech-recognition-provider"), label, ElementBounds.Fixed(labelX, y, 220, 30));
        string[] providerValues =
        {
            SimpleVoiceChatClientConfig.AlibabaSpeechRecognitionProvider,
            SimpleVoiceChatClientConfig.SiliconFlowSpeechRecognitionProvider,
            SimpleVoiceChatClientConfig.DeepgramSpeechRecognitionProvider,
            SimpleVoiceChatClientConfig.WhisperSpeechRecognitionProvider
        };
        string[] providerNames =
        {
            SVCLang.Get("speech-recognition-provider-alibaba"),
            SVCLang.Get("speech-recognition-provider-siliconflow"),
            SVCLang.Get("speech-recognition-provider-deepgram"),
            SVCLang.Get("speech-recognition-provider-whisper")
        };
        composer.AddVoiceDropDown(
            providerValues,
            providerNames,
            Math.Max(0, Array.IndexOf(providerValues, config.SpeechRecognitionProvider)),
            OnSpeechRecognitionProviderChanged,
            ElementBounds.Fixed(controlX, y, controlWidth, controlHeight),
            "speech-recognition-provider");
        y += 46;
        bool localProvider = config.SpeechRecognitionProvider ==
            SimpleVoiceChatClientConfig.WhisperSpeechRecognitionProvider;
        if (!localProvider)
        {
            composer.AddStaticText(SVCLang.Get("label-speech-recognition-api-key"), label, ElementBounds.Fixed(labelX, y, 220, 30))
                .AddTextInput(ElementBounds.Fixed(controlX, y, controlWidth, controlHeight), controller.SetSpeechRecognitionApiKeyFromSettings,
                    CairoFont.TextInput(), "speech-recognition-api-key");
            y += 46;
        }
        composer
            .AddStaticText(SVCLang.Get(localProvider ? "label-speech-recognition-model-path" : "label-speech-recognition-model"), label, ElementBounds.Fixed(labelX, y, 220, 30))
            .AddTextInput(ElementBounds.Fixed(controlX, y, controlWidth, controlHeight), controller.SetSpeechRecognitionModelFromSettings,
                CairoFont.TextInput(), "speech-recognition-model")
            ;
        if (!localProvider)
        {
            composer
                .AddStaticText(SVCLang.Get("label-speech-recognition-endpoint"), label, ElementBounds.Fixed(labelX, y += 46, 220, 30))
                .AddTextInput(ElementBounds.Fixed(controlX, y, controlWidth, controlHeight), controller.SetSpeechRecognitionEndpointFromSettings,
                    CairoFont.TextInput(), "speech-recognition-endpoint");
        }

        if (!localProvider)
        {
            GuiElementTextInput apiKey = composer.GetTextInput("speech-recognition-api-key");
            apiKey.SetValue(config.SpeechRecognitionApiKey);
            apiKey.SetMaxLength(512);
            apiKey.HideCharacters();
        }
        GuiElementTextInput model = composer.GetTextInput("speech-recognition-model");
        model.SetValue(config.SpeechRecognitionModel);
        model.SetMaxLength(localProvider ? 2048 : 128);
        if (!localProvider)
        {
            GuiElementTextInput endpoint = composer.GetTextInput("speech-recognition-endpoint");
            endpoint.SetValue(config.SpeechRecognitionEndpoint);
            endpoint.SetMaxLength(1024);
        }

        y += 52;
        composer.AddStaticText(SVCLang.Get("speech-recognition-privacy"), detail,
            ElementBounds.Fixed(labelX, y, 820, 58));
        if (localProvider)
        {
            y += 66;
            composer.AddStaticText(SVCLang.Get("speech-recognition-whisper-download"), detail,
                ElementBounds.Fixed(labelX, y, 820, 34));
            y += 42;
            AddFlatButton(composer, SVCLang.Get("button-open-browser"),
                OpenWhisperModelDownloadLink,
                ElementBounds.Fixed(labelX, y, 220, 34), "speech-recognition-download");
            return y + 52;
        }
        return y + 76;
    }

    private bool OpenWhisperModelDownloadLink()
    {
        capi.Gui.OpenLink("https://huggingface.co/ggerganov/whisper.cpp/tree/main");
        return true;
    }

    private void OnSpeechRecognitionProviderChanged(string value, bool selected)
    {
        if (selected)
        {
            controller.SetSpeechRecognitionProviderFromSettings(value);
        }
    }

    private static void AddIconToggle(
        GuiComposer composer,
        AssetLocation onIcon,
        AssetLocation offIcon,
        bool value,
        Action<bool> changed,
        ElementBounds bounds,
        string key)
    {
        composer.AddInteractiveElement(
            new VoiceSettingsIconToggleButton(composer.Api, bounds, onIcon, offIcon, value, changed),
            key);
    }

    private double AddChannelsPage(GuiComposer composer)
    {
        const double x = 18;
        const double width = 868;
        const double cardHeight = 58;
        const double cardGap = 8;
        const double listTop = 46;
        const int pageSize = 6;
        CairoFont section = CairoFont.WhiteSmallishText().WithColor(new[] { 0.96, 0.97, 1.0, 1.0 });
        CairoFont label = CairoFont.WhiteSmallText().WithColor(new[] { 0.9, 0.92, 0.96, 1.0 });
        CairoFont detail = CairoFont.WhiteDetailText().WithColor(new[] { 0.78, 0.82, 0.88, 1.0 });

        VoiceSettingsChannelOption[] channels = controller.BuildChannelOptions();
        double y = 0;
        composer.AddStaticText(SVCLang.Get("tab-channels"), section, ElementBounds.Fixed(x, y, width, 28));
        composer.AddTextInput(ElementBounds.Fixed(x + 300, y - 2, 330, 34), value =>
        {
            channelSearchDraft = value;
        }, CairoFont.TextInput(), "channel-search");
        GuiElementTextInput channelSearchInput = composer.GetTextInput("channel-search");
        if (string.Join(string.Empty, channelSearchInput.GetLines()) != channelSearchDraft)
        {
            channelSearchInput.SetValue(channelSearchDraft);
        }
        channelSearchInput.SetPlaceHolderText(SVCLang.Get("channel-search-placeholder"));
        channelSearchInput.SetMaxLength(VoiceProtocol.MaxControlStringLength);
        AddFlatButton(composer, SVCLang.Get("button-search"), ApplyChannelSearch,
            ElementBounds.Fixed(x + 646, y - 2, 100, 34), "channel-search-submit");
        AddFlatButton(composer, SVCLang.Get("button-cancel"), ClearChannelSearch,
            ElementBounds.Fixed(x + 752, y - 2, 100, 34), "channel-search-cancel");
        string normalizedSearch = channelSearch.Trim();
        if (!string.IsNullOrEmpty(normalizedSearch))
        {
            channels = channels.Where(channel => channel.Name.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
                || channel.Id.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        double footerY = activeViewportHeight - 44;
        double listViewportHeight = pageSize * (cardHeight + cardGap);
        int pageCount = Math.Max(1, (channels.Length + pageSize - 1) / pageSize);
        channelListPage = Math.Clamp(channelListPage, 0, pageCount - 1);
        VoiceSettingsChannelOption[] visibleChannels = channels
            .Skip(channelListPage * pageSize)
            .Take(pageSize)
            .ToArray();
        ElementBounds listViewport = ElementBounds.Fixed(x, listTop, width, listViewportHeight);
        ElementBounds listBounds = ElementBounds.Fixed(0, 0, width, listViewportHeight);
        composer.BeginClip(listViewport).BeginChildElements(listBounds);
        if (visibleChannels.Length == 0)
        {
            composer.AddStaticText(SVCLang.Get("channel-none"), detail, ElementBounds.Fixed(0, 0, width, 30));
        }
        else
        {
            for (int index = 0; index < visibleChannels.Length; index++)
            {
                VoiceSettingsChannelOption channel = visibleChannels[index];
                double cardY = index * (cardHeight + cardGap);
                string meta = SVCLang.Get("ui-member-count", channel.MemberCount);
                if (channel.Locked)
                {
                    meta += " | " + SVCLang.Get("channel-locked");
                }
                meta += " | " + SVCLang.Get("channel-visibility-" + channel.Visibility.ToString().ToLowerInvariant());
                composer.AddStaticCustomDraw(ElementBounds.Fixed(0, cardY, width, cardHeight), DrawChannelCardBackground)
                    .AddStaticText(Truncate(channel.Name, 42), label, ElementBounds.Fixed(14, cardY + 7, 470, 24), "channel-name-" + index)
                    .AddStaticText(meta, detail, ElementBounds.Fixed(14, cardY + 32, 470, 18), "channel-meta-" + index);
                string channelActionLabel = channel.LocalRole == VoiceChannelRole.Banned
                    ? (channel.Visibility == VoiceChannelVisibility.Password
                        ? SVCLang.Get("button-join-password")
                        : SVCLang.Get("button-join-channel"))
                    : SVCLang.Get("button-select-channel");
                AddFlatButton(composer, Truncate(channel.Id, 22), () =>
                {
                    CopyChannelId(channel.Id);
                    return true;
                }, ElementBounds.Fixed(500, cardY + 12, 140, 34), "channel-id-" + index);
                VoiceSettingsIconButton settings = new(
                    composer.Api,
                    ElementBounds.Fixed(width - 48, cardY + 11, 36, 36),
                    FontAwesomeGearIcon,
                    _ => OpenChannelOverlay(channel.Id),
                    darkIcon: true);
                composer.AddInteractiveElement(settings, "channel-settings-" + index);
                if (channel.LocalRole == VoiceChannelRole.Banned && !channel.ExternallyManaged)
                {
                    AddFlatButton(composer,
                        channelActionLabel,
                        () =>
                        {
                            OpenJoinChannelOverlay(channel.Id);
                            return true;
                        },
                        ElementBounds.Fixed(width - 218, cardY + 12, 150, 34),
                        "channel-join-" + index);
                }
            }
        }
        composer.EndChildElements().EndClip();

        AddPagination(
            composer,
            x,
            footerY - 42,
            width,
            channelListPage,
            pageCount,
            () => ChangeChannelPage(-1),
            () => ChangeChannelPage(1),
            "channel-pagination");

        VoiceSettingsImageButton playersButton = new(
            composer.Api,
            ElementBounds.Fixed(x, footerY, 42, 36),
            PlayersAsset,
            _ => OpenPlayersOverlay());
        composer.AddInteractiveElement(playersButton, "open-players");
        AddFlatButton(composer, SVCLang.Get("button-create-channel"), OpenCreateChannelOverlay,
            ElementBounds.Fixed(x + 52, footerY, 180, 36), "open-create-channel");
        composer.GetButton("open-create-channel").Enabled = true;
        return activeViewportHeight;
    }

    private void AddOverlay(GuiComposer composer)
    {
        switch (overlay)
        {
            case VoiceSettingsOverlay.Channel:
                AddChannelOverlay(composer);
                break;
            case VoiceSettingsOverlay.Players:
                AddPlayersOverlay(composer);
                break;
            case VoiceSettingsOverlay.Player:
                AddPlayerOverlay(composer);
                break;
            case VoiceSettingsOverlay.CreateChannel:
                AddCreateChannelOverlay(composer);
                break;
            case VoiceSettingsOverlay.RecordingMode:
                AddRecordingModeOverlay(composer);
                break;
            case VoiceSettingsOverlay.MultiTrackRecording:
                AddMultiTrackRecordingOverlay(composer);
                break;
            case VoiceSettingsOverlay.OwnerLeave:
                AddOwnerLeaveOverlay(composer);
                break;
            case VoiceSettingsOverlay.JoinChannel:
                AddJoinChannelOverlay(composer);
                break;
            case VoiceSettingsOverlay.ConfirmChannelAction:
                AddConfirmChannelActionOverlay(composer);
                break;
            case VoiceSettingsOverlay.CurrentStatus:
                AddCurrentStatusOverlay(composer);
                break;
        }
    }

    private void AddCurrentStatusOverlay(GuiComposer composer)
    {
        const double x = 40;
        const double y = 25;
        const double width = 860;
        const double height = 600;
        const double leftX = 70;
        const double rightX = 490;
        const double labelWidth = 150;
        const double valueWidth = 230;
        VoiceCurrentStatusSnapshot status = controller.BuildCurrentStatusSnapshot();
        VoiceDiagnosticsPacket? diagnostics = status.Diagnostics;

        AddOverlayPanel(composer, x, y, width, height, SVCLang.Get("current-status-title"));
        AddOverlayCloseButton(composer, x, y, width, CloseOverlay, "current-status-close");

        AddStatusSection(composer, leftX, 80, SVCLang.Get("current-status-section-connection"));
        AddStatusRow(composer, leftX, 112, labelWidth, valueWidth, "current-status-server",
            State(status.ServerEnabled));
        AddStatusRow(composer, leftX, 140, labelWidth, valueWidth, "current-status-control",
            State(status.ControlConnected));
        AddStatusRow(composer, leftX, 168, labelWidth, valueWidth, "current-status-handshake",
            Ready(status.HandshakeAccepted));
        AddStatusRow(composer, leftX, 196, labelWidth, valueWidth, "current-status-udp",
            Ready(status.UdpResponsive));
        AddStatusRow(composer, leftX, 224, labelWidth, valueWidth, "current-status-protocol-codec",
            $"V{status.ProtocolVersion} / {CodecName(status.Codec)} / {FormatBitrate(status.EncoderBitrate)}");
        AddStatusRow(composer, leftX, 252, labelWidth, valueWidth, "current-status-epoch-streams",
            $"{status.ConnectionEpoch} / {status.MaxStreamsPerListener}");

        AddStatusSection(composer, leftX, 282, SVCLang.Get("current-status-section-network"));
        AddStatusRow(composer, leftX, 314, labelWidth, valueWidth, "current-status-snapshot",
            diagnostics == null ? SVCLang.Get("current-status-waiting") : SVCLang.Get("state-ready"));
        AddStatusRow(composer, leftX, 342, labelWidth, valueWidth, "current-status-rtt-loss",
            $"{FormatRoundTrip(status.RoundTripMilliseconds)} / {status.ProbeLossPercent:0}%");
        AddStatusRow(composer, leftX, 370, labelWidth, valueWidth, "current-status-clients-talkers",
            DiagnosticValue(diagnostics, packet => $"{packet.HandshakenClients} / {packet.ActiveTalkers}"));
        AddStatusRow(composer, leftX, 398, labelWidth, valueWidth, "current-status-channels-invites",
            DiagnosticValue(diagnostics, packet => $"{packet.Channels} / {packet.PendingInvites}"));
        AddStatusRow(composer, leftX, 426, labelWidth, valueWidth, "current-status-listener-streams",
            DiagnosticValue(diagnostics, packet => packet.ActiveListenerStreams.ToString()));
        AddStatusRow(composer, leftX, 454, labelWidth, valueWidth, "current-status-packets",
            DiagnosticValue(diagnostics, packet => $"{packet.RollingReceivedPackets} / {packet.RollingRelayedPackets}"));
        AddStatusRow(composer, leftX, 482, labelWidth, valueWidth, "current-status-bytes",
            DiagnosticValue(diagnostics, packet => $"{FormatBytes(packet.RollingRelayedBytes)} / {FormatBytes(packet.RollingEstimatedRelayedIpv4UdpBytes > 0 ? packet.RollingEstimatedRelayedIpv4UdpBytes : packet.RollingRelayedBytes)}"));
        AddStatusRow(composer, leftX, 510, labelWidth, valueWidth, "current-status-dropped",
            DiagnosticValue(diagnostics, packet => packet.RollingDroppedPackets.ToString()));
        AddStatusRow(composer, leftX, 538, labelWidth, valueWidth, "current-status-p95",
            DiagnosticValue(diagnostics, packet => $"{packet.P95FanOut:0.0} / {packet.P95RouteMilliseconds:0.000} ms"));
        AddStatusRow(composer, leftX, 566, labelWidth, valueWidth, "current-status-relay-alloc",
            DiagnosticValue(diagnostics, packet => $"{packet.RollingRelayPacketAllocations} / {FormatBytes(packet.RollingRelaySerializationAllocatedBytes)}"));

        AddStatusSection(composer, rightX, 80, SVCLang.Get("current-status-section-audio"));
        AddStatusRow(composer, rightX, 112, labelWidth, valueWidth, "current-status-capture",
            Ready(status.CaptureAvailable));
        AddStatusRow(composer, rightX, 140, labelWidth, valueWidth, "current-status-capture-error",
            string.IsNullOrWhiteSpace(status.CaptureFailure) ? SVCLang.Get("current-status-none") : Truncate(status.CaptureFailure, 34));
        AddStatusRow(composer, rightX, 168, labelWidth, valueWidth, "label-input-device",
            Truncate(status.InputDevice, 34), 40);
        AddStatusRow(composer, rightX, 208, labelWidth, valueWidth, "label-output-device",
            Truncate(status.OutputDevice, 34), 40);
        AddStatusRow(composer, rightX, 248, labelWidth, valueWidth, "current-status-processing",
            Truncate(status.ProcessingBackend, 34));
        AddStatusRow(composer, rightX, 276, labelWidth, valueWidth, "current-status-ns-aec",
            $"{State(status.NoiseSuppressionAvailable)} / {State(status.EchoCancellationAvailable)}");
        AddStatusRow(composer, rightX, 304, labelWidth, valueWidth, "current-status-playback",
            $"{status.PlaybackStatus}\n{status.EncodedFrameAllocationCount} / {FormatBytes(status.EncodedFrameAllocatedBytes)}", 54);

        AddStatusSection(composer, rightX, 372, SVCLang.Get("current-status-section-voice"));
        AddStatusRow(composer, rightX, 404, labelWidth, valueWidth, "current-status-mode",
            ModeName(status.Mode));
        AddStatusRow(composer, rightX, 432, labelWidth, valueWidth, "current-status-control-mode",
            SVCLang.Get(status.VoiceActivationEnabled ? "mode-voice-activation" : "mode-push-to-talk"));
        AddStatusRow(composer, rightX, 460, labelWidth, valueWidth, "label-transmit-target",
            SVCLang.Get("transmit-" + TransmitCode(status.TransmitTarget)));
        AddStatusRow(composer, rightX, 488, labelWidth, valueWidth, "current-status-channel",
            Truncate(status.SelectedChannelName, 34));
        AddStatusRow(composer, rightX, 516, labelWidth, valueWidth, "current-status-channel-id",
            string.IsNullOrWhiteSpace(status.SelectedChannelId) ? "--" : Truncate(status.SelectedChannelId, 34));
        AddStatusRow(composer, rightX, 544, labelWidth, valueWidth, "current-status-mute-deafen",
            $"{State(status.LocalMuted)} / {State(status.GlobalMuted)}");
        AddStatusRow(composer, rightX, 572, labelWidth, valueWidth, "current-status-transmit-recording",
            $"{Ready(!status.TransmitBlocked)} / {RecordingName(status)}");
    }

    private static void AddStatusSection(GuiComposer composer, double x, double y, string title)
    {
        composer.AddStaticText(
            title,
            CairoFont.WhiteSmallishText().WithFontSize(15).WithColor(new[] { 1.0, 1.0, 1.0, 1.0 }),
            ElementBounds.Fixed(x, y, 380, 24));
    }

    private static void AddStatusRow(
        GuiComposer composer,
        double x,
        double y,
        double labelWidth,
        double valueWidth,
        string labelKey,
        string value,
        double height = 26)
    {
        composer.AddStaticText(
                SVCLang.Get(labelKey),
                CairoFont.WhiteDetailText().WithFontSize(12).WithColor(new[] { 0.68, 0.72, 0.79, 1.0 }),
                ElementBounds.Fixed(x, y, labelWidth, height))
            .AddStaticText(
                value,
                CairoFont.WhiteSmallText().WithFontSize(13).WithColor(new[] { 0.96, 0.97, 1.0, 1.0 }),
                ElementBounds.Fixed(x + labelWidth, y, valueWidth, height));
    }

    private static string DiagnosticValue(VoiceDiagnosticsPacket? diagnostics, System.Func<VoiceDiagnosticsPacket, string> value)
    {
        return diagnostics == null ? "--" : value(diagnostics);
    }

    private static string State(bool enabled) => SVCLang.Get(enabled ? "state-on" : "state-off");

    private static string Ready(bool ready) => SVCLang.Get(ready ? "state-ready" : "state-unavailable");

    private static string CodecName(int codec) => codec == VoiceProtocol.CodecOpus ? "Opus" : "ADPCM";

    private static string FormatBitrate(int bitrate)
        => bitrate > 0 ? $"{bitrate / 1000d:0.#} kbps" : "--";

    private static string FormatRoundTrip(double milliseconds) => milliseconds < 0 ? "-- ms" : $"{milliseconds:0} ms";

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024d * 1024d * 1024d):0.0} GiB";
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024d * 1024d):0.0} MiB";
        if (bytes >= 1024L) return $"{bytes / 1024d:0.0} KiB";
        return $"{bytes} B";
    }

    private static string ModeName(VoiceMode mode)
    {
        return SVCLang.Get(mode switch
        {
            VoiceMode.Whisper => "mode-whisper",
            VoiceMode.Shout => "mode-shout",
            _ => "mode-talk"
        });
    }

    private static string RecordingName(VoiceCurrentStatusSnapshot status)
    {
        if (!status.IsRecording || status.RecordingMode == null)
        {
            return SVCLang.Get("state-off");
        }
        return SVCLang.Get(status.RecordingMode switch
        {
            VoiceRecordingMode.InputOnly => "recording-mode-input",
            VoiceRecordingMode.InputAndOutput => "recording-mode-input-output",
            _ => "recording-mode-multitrack"
        });
    }

    private void AddRecordingModeOverlay(GuiComposer composer)
    {
        const double width = 720;
        const double height = 220;
        double x = (WindowWidth - width) / 2d;
        double y = (WindowHeight - height) / 2d;
        AddOverlayPanel(composer, x, y, width, height, SVCLang.Get("recording-mode-title"));
        AddOverlayCloseButton(composer, x, y, width, CloseOverlay, "recording-mode-close");
        CairoFont label = CairoFont.WhiteSmallText().WithColor(new[] { 0.9, 0.92, 0.96, 1.0 });
        composer.AddStaticText(SVCLang.Get("recording-mode-description"), label,
            ElementBounds.Fixed(x + 24, y + 60, width - 48, 28));
        double buttonWidth = (width - 72) / 3d;
        AddFlatButton(composer, SVCLang.Get("recording-mode-input"),
            () => StartRecordingMode(VoiceRecordingMode.InputOnly),
            ElementBounds.Fixed(x + 24, y + 104, buttonWidth, 42),
            "recording-mode-input");
        AddFlatButton(composer, SVCLang.Get("recording-mode-input-output"),
            () => StartRecordingMode(VoiceRecordingMode.InputAndOutput),
            ElementBounds.Fixed(x + 36 + buttonWidth, y + 104, buttonWidth, 42),
            "recording-mode-input-output");
        AddFlatButton(composer, SVCLang.Get("recording-mode-multitrack"),
            () => StartRecordingMode(VoiceRecordingMode.MultiTrack),
            ElementBounds.Fixed(x + 48 + buttonWidth * 2d, y + 104, buttonWidth, 42),
            "recording-mode-multitrack");
        composer.GetButton("recording-mode-multitrack").Enabled = controller.HasServerControl;
    }

    private void AddMultiTrackRecordingOverlay(GuiComposer composer)
    {
        const double width = 640;
        const double height = 245;
        double x = (WindowWidth - width) / 2d;
        double y = (WindowHeight - height) / 2d;
        AddOverlayPanel(composer, x, y, width, height, SVCLang.Get("multitrack-settings-title"));
        AddOverlayCloseButton(composer, x, y, width, CloseOverlay, "multitrack-settings-close");
        CairoFont label = CairoFont.WhiteSmallText().WithColor(new[] { 0.9, 0.92, 0.96, 1.0 });
        string status = controller.IsRecording && controller.RecordingMode == VoiceRecordingMode.MultiTrack
            ? SVCLang.Get("multitrack-status-recording")
            : controller.IsMultiTrackStartPending
                ? SVCLang.Get("multitrack-status-syncing", controller.RecorderClockSampleCount)
                : controller.CanStartMultiTrackRecording
                    ? SVCLang.Get("multitrack-status-ready", controller.RecorderClockRoundTripMilliseconds.ToString("0"))
                    : SVCLang.Get("multitrack-status-waiting", controller.RecorderClockSampleCount);
        if (controller.RecorderStatus is { } recorderStatus)
        {
            status += $"\n{SVCLang.Get("multitrack-status-participants", recorderStatus.ReadyParticipants, recorderStatus.TotalParticipants, recorderStatus.TrackCount, recorderStatus.MissingPackets)}";
        }
        composer.AddStaticText(SVCLang.Get("multitrack-settings-description"), label,
            ElementBounds.Fixed(x + 24, y + 58, width - 48, 44));
        composer.AddStaticText(status, label, ElementBounds.Fixed(x + 24, y + 112, width - 48, 48));
        bool active = controller.IsRecording && controller.RecordingMode == VoiceRecordingMode.MultiTrack
            || controller.IsMultiTrackStartPending
            || controller.RecorderStatus?.Active == true;
        AddFlatButton(composer,
            active ? SVCLang.Get("button-recording-stop") : SVCLang.Get("button-recording-start"),
            () => active ? controller.StopRecordingFromSettings() : controller.StartRecordingFromSettings(VoiceRecordingMode.MultiTrack),
            ElementBounds.Fixed(x + 24, y + 174, 220, 40),
            "multitrack-toggle");
        composer.GetButton("multitrack-toggle").Enabled = active || controller.HasServerControl;
    }

    private void AddOwnerLeaveOverlay(GuiComposer composer)
    {
        const double x = 150;
        const double y = 96;
        const double width = 640;
        const double height = 420;
        AddOverlayPanel(composer, x, y, width, height, SVCLang.Get("channel-owner-leave-title"));
        AddOverlayCloseButton(composer, x, y, width, CloseOverlay, "owner-leave-close");
        CairoFont label = CairoFont.WhiteSmallText().WithColor(new[] { 0.9, 0.92, 0.96, 1.0 });
        composer.AddStaticText(SVCLang.Get("channel-owner-leave-description"), label,
            ElementBounds.Fixed(x + 24, y + 54, width - 48, 34));
        VoiceSettingsMemberOption[] members = controller.BuildChannelMembersForSettings(ownerLeaveChannelId);
        string[] values = members.Length == 0 ? new[] { string.Empty } : members.Select(member => member.Id).ToArray();
        string[] names = members.Length == 0 ? new[] { SVCLang.Get("player-none") } : members.Select(member => member.Name).ToArray();
        composer.AddStaticText(SVCLang.Get("channel-owner-transfer-target"), label,
            ElementBounds.Fixed(x + 24, y + 108, 200, 28));
        composer.AddVoiceDropDown(values, names, 0, OnOwnerTransferTargetChanged,
            ElementBounds.Fixed(x + 230, y + 104, 300, 34), "owner-leave-target");
        AddFlatButton(composer, SVCLang.Get("channel-owner-transfer"), () =>
        {
            string targetUid = string.IsNullOrWhiteSpace(ownerLeaveTargetUid) && members.Length > 0
                ? members[0].Id
                : ownerLeaveTargetUid;
            if (!string.IsNullOrWhiteSpace(targetUid))
            {
                controller.TransferChannelOwnerFromSettings(ownerLeaveChannelId, targetUid);
            }
            CloseOverlay();
            return true;
        }, ElementBounds.Fixed(x + 230, y + 158, 160, 36), "owner-leave-transfer");
        AddFlatButton(composer, SVCLang.Get("channel-owner-delete"), () =>
        {
            return OpenChannelActionConfirmation("delete-owned-channel", ownerLeaveChannelId);
        }, ElementBounds.Fixed(x + 400, y + 158, 160, 36), "owner-leave-delete");
        AddFlatButton(composer, SVCLang.Get("button-close"), () =>
        {
            CloseOverlay();
            return true;
        }, ElementBounds.Fixed(x + 230, y + 212, 160, 36), "owner-leave-cancel");
    }

    private string ownerLeaveTargetUid = string.Empty;

    private void OnOwnerTransferTargetChanged(string value, bool selected)
    {
        if (selected) ownerLeaveTargetUid = value;
    }

    private void AddJoinChannelOverlay(GuiComposer composer)
    {
        const double x = 180;
        const double y = 180;
        const double width = 580;
        const double height = 190;
        VoiceSettingsChannelOption channel = controller.BuildChannelOptions().FirstOrDefault(option => option.Id == joinChannelId);
        AddOverlayPanel(composer, x, y, width, height, SVCLang.Get("channel-join-title"));
        AddOverlayCloseButton(composer, x, y, width, CloseOverlay, "join-channel-close");
        CairoFont label = CairoFont.WhiteSmallText().WithColor(new[] { 0.9, 0.92, 0.96, 1.0 });
        composer.AddStaticText(Truncate(channel.Name, 46), label, ElementBounds.Fixed(x + 24, y + 54, 240, 28));
        composer.AddTextInput(ElementBounds.Fixed(x + 270, y + 50, 260, 34), value => { joinPassword = value; }, CairoFont.TextInput(), "join-channel-password");
        composer.GetTextInput("join-channel-password").SetValue(joinPassword);
        composer.GetTextInput("join-channel-password").SetMaxLength(VoiceProtocol.MaxControlStringLength);
        AddFlatButton(composer, SVCLang.Get("button-join-channel"), () =>
        {
            controller.JoinChannelFromSettings(joinChannelId, joinPassword);
            CloseOverlay();
            return true;
        }, ElementBounds.Fixed(x + 270, y + 108, 150, 36), "join-channel-submit");
    }

    private void AddConfirmChannelActionOverlay(GuiComposer composer)
    {
        const double x = 210;
        const double y = 200;
        const double width = 520;
        const double height = 190;
        bool leaving = confirmChannelAction == "leave";
        VoiceSettingsChannelOption channel = controller.BuildChannelOptions()
            .FirstOrDefault(option => option.Id == confirmChannelId);
        string channelName = string.IsNullOrWhiteSpace(channel.Name) ? confirmChannelId : channel.Name;
        string titleKey = leaving ? "channel-confirm-leave-title" : "channel-confirm-disband-title";
        string descriptionKey = leaving ? "channel-confirm-leave-description" : "channel-confirm-disband-description";

        AddOverlayPanel(composer, x, y, width, height, SVCLang.Get(titleKey));
        composer.AddStaticText(
            SVCLang.Get(descriptionKey, Truncate(channelName, 34)),
            CairoFont.WhiteSmallText().WithColor(new[] { 0.9, 0.92, 0.96, 1.0 }),
            ElementBounds.Fixed(x + 24, y + 64, width - 48, 36));
        AddFlatButton(
            composer,
            SVCLang.Get("button-cancel"),
            () =>
            {
                CloseOverlay();
                return true;
            },
            ElementBounds.Fixed(x + 24, y + 126, 150, 38),
            "confirm-channel-action-cancel");
        AddFlatButton(
            composer,
            SVCLang.Get("button-confirm"),
            ConfirmChannelAction,
            ElementBounds.Fixed(x + width - 174, y + 126, 150, 38),
            "confirm-channel-action-submit");
    }

    private bool StartRecordingMode(VoiceRecordingMode mode)
    {
        bool started = controller.StartRecordingFromSettings(mode);
        if (started)
        {
            if (mode == VoiceRecordingMode.MultiTrack)
            {
                // Keep the dedicated panel visible while clock synchronization and recording run.
                overlay = VoiceSettingsOverlay.MultiTrackRecording;
                QueueCompose();
            }
            else
            {
                CloseOverlay();
            }
        }
        return started;
    }

    private void AddChannelOverlay(GuiComposer composer)
    {
        const double x = 140;
        const double y = 100;
        const double width = 660;
        const double height = 350;
        VoiceSettingsChannelOption channel = controller.BuildChannelOptions()
            .FirstOrDefault(option => option.Id == overlayChannelId);
        if (string.IsNullOrWhiteSpace(channel.Id))
        {
            overlay = VoiceSettingsOverlay.None;
            return;
        }

        AddOverlayPanel(composer, x, y, width, height, SVCLang.Get("channel-settings-title"));
        AddOverlayCloseButton(composer, x, y, width, CloseOverlay, "channel-overlay-close");
        CairoFont label = CairoFont.WhiteSmallText().WithColor(new[] { 0.9, 0.92, 0.96, 1.0 });
        composer.AddStaticText(Truncate(channel.Name, 44), label, ElementBounds.Fixed(x + 24, y + 54, 380, 24));
        composer.AddStaticText(SVCLang.Get("label-channel-volume"), label, ElementBounds.Fixed(x + 24, y + 88, 180, 28));
        composer.AddVoiceSlider(value => { controller.SetChannelVolumeFromSettings(value); return true; },
            ElementBounds.Fixed(x + 214, y + 84, 390, 34), "overlay-channel-volume");
        ConfigureSlider(composer, "overlay-channel-volume", (int)Math.Round(config.ChannelOutputVolume * 100), 0, 200, "%");

        VoiceSettingsPlayerOption[] players = controller.BuildOnlinePlayerOptions();
        string[] playerValues = new[] { string.Empty }.Concat(players.Select(player => player.Id)).ToArray();
        string[] playerNames = new[] { SVCLang.Get("player-none") }.Concat(players.Select(player => player.Name)).ToArray();
        if (!playerValues.Contains(overlayTargetPlayerUid, StringComparer.Ordinal))
        {
            overlayTargetPlayerUid = string.Empty;
        }
        composer.AddStaticText(SVCLang.Get("ui-target-player"), label, ElementBounds.Fixed(x + 24, y + 142, 180, 28));
        composer.AddVoiceDropDown(
            playerValues,
            playerNames,
            Math.Max(0, Array.IndexOf(playerValues, overlayTargetPlayerUid)),
            OnOverlayTargetPlayerChanged,
            ElementBounds.Fixed(x + 214, y + 138, 390, 34),
            "overlay-channel-target-player");

        List<string> actions = BuildChannelActions(new[] { channel }, channel.Id, overlayTargetPlayerUid);
        if (!actions.Contains(overlayAction, StringComparer.Ordinal))
        {
            overlayAction = actions[0];
        }
        composer.AddStaticText(SVCLang.Get("ui-action-select"), label, ElementBounds.Fixed(x + 24, y + 196, 180, 28));
        composer.AddVoiceDropDown(
            actions.ToArray(),
            actions.Select(action => SVCLang.Get("channel-action-" + action)).ToArray(),
            Math.Max(0, actions.IndexOf(overlayAction)),
            OnOverlayActionChanged,
            ElementBounds.Fixed(x + 214, y + 192, 260, 34),
            "overlay-channel-action");
        AddFlatButton(composer, SVCLang.Get("button-apply"), ApplyChannelOverlay,
            ElementBounds.Fixed(x + 478, y + 192, 126, 34), "overlay-channel-apply");
        composer.GetButton("overlay-channel-apply").Enabled = overlayAction != "none";
    }

    private void AddPlayersOverlay(GuiComposer composer)
    {
        const double x = 0;
        const double y = 0;
        const double width = WindowWidth;
        const double height = WindowHeight;
        AddOverlayPanel(composer, x, y, width, height, SVCLang.Get("players-title"));
        AddOverlayCloseButton(composer, x, y, width, CloseOverlay, "players-overlay-close");
        VoiceSettingsPlayerOption[] players = controller.BuildPlayerOptions();
        composer.AddTextInput(ElementBounds.Fixed(x + 300, y + 12, 360, 32), value =>
        {
            playerSearchDraft = value;
        }, CairoFont.TextInput(), "players-search");
        GuiElementTextInput playerSearchInput = composer.GetTextInput("players-search");
        if (string.Join(string.Empty, playerSearchInput.GetLines()) != playerSearchDraft)
        {
            playerSearchInput.SetValue(playerSearchDraft);
        }
        playerSearchInput.SetPlaceHolderText(SVCLang.Get("player-search-placeholder"));
        playerSearchInput.SetMaxLength(VoiceProtocol.MaxControlStringLength);
        AddFlatButton(composer, SVCLang.Get("button-search"), ApplyPlayerSearch,
            ElementBounds.Fixed(x + 670, y + 12, 100, 32), "players-search-submit");
        AddFlatButton(composer, SVCLang.Get("button-cancel"), ClearPlayerSearch,
            ElementBounds.Fixed(x + 778, y + 12, 100, 32), "players-search-cancel");
        if (!string.IsNullOrWhiteSpace(playerSearch))
        {
            players = players.Where(player => player.Name.Contains(playerSearch, StringComparison.OrdinalIgnoreCase)
                || player.Id.Contains(playerSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        CairoFont label = CairoFont.WhiteSmallText().WithColor(new[] { 0.9, 0.92, 0.96, 1.0 });
        const int pageSize = 6;
        const double playerRowHeight = 52;
        int pageCount = Math.Max(1, (players.Length + pageSize - 1) / pageSize);
        playerListPage = Math.Clamp(playerListPage, 0, pageCount - 1);
        VoiceSettingsPlayerOption[] visiblePlayers = players
            .Skip(playerListPage * pageSize)
            .Take(pageSize)
            .ToArray();
        ElementBounds viewport = ElementBounds.Fixed(x + 18, y + 54, width - 36, pageSize * playerRowHeight);
        const double playerInfoWidth = 200;
        const double sliderX = 220;
        double settingsButtonX = viewport.fixedWidth - 48;
        double muteButtonX = settingsButtonX - 42;
        double sliderWidth = muteButtonX - sliderX - 12;
        ElementBounds listBounds = ElementBounds.Fixed(0, 0, viewport.fixedWidth, viewport.fixedHeight);
        composer.BeginClip(viewport).BeginChildElements(listBounds);
        if (visiblePlayers.Length == 0)
        {
            composer.AddStaticText(SVCLang.Get("player-none"), label, ElementBounds.Fixed(6, 8, viewport.fixedWidth - 12, 30));
        }
        for (int index = 0; index < visiblePlayers.Length; index++)
        {
            VoiceSettingsPlayerOption player = visiblePlayers[index];
            double cardY = index * playerRowHeight;
            string sliderKey = "overlay-player-volume-" + index;
            string muteKey = "overlay-player-mute-" + index;
            string settingsKey = "overlay-player-settings-" + index;
            composer.AddStaticCustomDraw(ElementBounds.Fixed(0, cardY, viewport.fixedWidth, 44), DrawPlayerCardBackground)
                .AddStaticText(Truncate(player.Name, 28), label, ElementBounds.Fixed(12, cardY + 8, playerInfoWidth, 28), "overlay-player-name-" + index)
                .AddStaticText(Truncate(player.ChannelSummary, 28), CairoFont.WhiteDetailText().WithColor(new[] { 0.72, 0.76, 0.82, 1.0 }), ElementBounds.Fixed(12, cardY + 27, playerInfoWidth, 14), "overlay-player-channels-" + index)
                .AddVoiceSlider(value => SetPlayerVolume(player.Id, value), ElementBounds.Fixed(sliderX, cardY + 5, sliderWidth, 34), sliderKey);
            VoiceSettingsMuteButton muteButton = new(
                composer.Api,
                ElementBounds.Fixed(muteButtonX, cardY + 5, 34, 34),
                value => controller.SetPlayerMutedFromSettings(player.Id, value));
            composer.AddInteractiveElement(muteButton, muteKey);
            muteButton.SetValue(controller.IsPlayerMuted(player.Id));
            VoiceSettingsIconButton settingsButton = new(
                composer.Api,
                ElementBounds.Fixed(settingsButtonX, cardY + 5, 34, 34),
                FontAwesomeGearIcon,
                _ => OpenPlayerOverlay(player.Id),
                darkIcon: true);
            composer.AddInteractiveElement(settingsButton, settingsKey);
            ConfigureSlider(composer, sliderKey, controller.GetPlayerVolumePercent(player.Id), 0, 200, "%");
        }
        composer.EndChildElements().EndClip();
        AddPagination(
            composer,
            x + 18,
            y + 54 + viewport.fixedHeight + 10,
            width - 36,
            playerListPage,
            pageCount,
            () => ChangePlayerPage(-1),
            () => ChangePlayerPage(1),
            "player-pagination");
    }

    private void AddPlayerOverlay(GuiComposer composer)
    {
        const double x = 170;
        const double y = 142;
        const double width = 600;
        const double defaultHeight = 294;
        VoiceSettingsPlayerOption player = controller.BuildPlayerOptions()
            .FirstOrDefault(option => option.Id == overlayPlayerUid);
        if (string.IsNullOrWhiteSpace(player.Id))
        {
            overlay = VoiceSettingsOverlay.None;
            return;
        }
        AddOverlayPanel(composer, x, y, width, defaultHeight, SVCLang.Get("player-settings-title"));
        AddOverlayCloseButton(composer, x, y, width, CloseOverlay, "player-overlay-close");
        CairoFont label = CairoFont.WhiteSmallText().WithColor(new[] { 0.9, 0.92, 0.96, 1.0 });
        composer.AddStaticText(Truncate(player.Name, 36), label, ElementBounds.Fixed(x + 24, y + 54, width - 48, 24));

        VoiceSettingsChannelOption[] channels = controller.BuildChannelOptions();
        string[] channelValues = channels.Length == 0 ? new[] { string.Empty } : channels.Select(channel => channel.Id).ToArray();
        string[] channelNames = channels.Length == 0 ? new[] { SVCLang.Get("channel-none") } : channels.Select(channel => channel.Name).ToArray();
        string selectedChannel = string.IsNullOrWhiteSpace(overlayChannelId) ? config.SelectedChannelId : overlayChannelId;
        if (!channelValues.Contains(selectedChannel, StringComparer.Ordinal)) selectedChannel = channelValues[0];
        composer.AddStaticText(SVCLang.Get("label-channel-select"), label, ElementBounds.Fixed(x + 24, y + 92, 180, 28));
        composer.AddVoiceDropDown(channelValues, channelNames, Math.Max(0, Array.IndexOf(channelValues, selectedChannel)), OnOverlayChannelChanged,
            ElementBounds.Fixed(x + 214, y + 88, 260, 34), "overlay-player-channel");

        string[] actions = controller.BuildPlayerActions(selectedChannel, overlayPlayerUid);
        if (!actions.Contains(overlayAction, StringComparer.Ordinal)) overlayAction = actions[0];
        composer.AddStaticText(SVCLang.Get("ui-action-select"), label, ElementBounds.Fixed(x + 24, y + 144, 180, 28));
        composer.AddVoiceDropDown(actions, actions.Select(action => SVCLang.Get("channel-action-" + action)).ToArray(), Math.Max(0, Array.IndexOf(actions, overlayAction)), OnOverlayActionChanged,
            ElementBounds.Fixed(x + 214, y + 140, 260, 34), "overlay-player-action");
        AddFlatButton(composer, SVCLang.Get("button-apply"), ApplyPlayerOverlay,
            ElementBounds.Fixed(x + 214, y + 202, 126, 34), "overlay-player-apply");
        composer.GetButton("overlay-player-apply").Enabled = overlayAction != "none";
    }

    private void AddCreateChannelOverlay(GuiComposer composer)
    {
        const double x = 170;
        const double y = 142;
        const double width = 600;
        const double height = 350;
        AddOverlayPanel(composer, x, y, width, height, SVCLang.Get("create-channel-title"));
        AddOverlayCloseButton(composer, x, y, width, CloseOverlay, "create-overlay-close");
        CairoFont label = CairoFont.WhiteSmallText().WithColor(new[] { 0.9, 0.92, 0.96, 1.0 });
        CairoFont input = CairoFont.TextInput();
        composer.AddStaticText(SVCLang.Get("label-channel-name"), label, ElementBounds.Fixed(x + 24, y + 68, 180, 28))
            .AddTextInput(ElementBounds.Fixed(x + 214, y + 64, 340, 34), OnCreateNameChanged, input, "overlay-create-name")
            .AddStaticText(SVCLang.Get("label-channel-password"), label, ElementBounds.Fixed(x + 24, y + 124, 180, 28))
            .AddTextInput(ElementBounds.Fixed(x + 214, y + 120, 340, 34), OnCreatePasswordChanged, input, "overlay-create-password");
        string[] visibilityValues = { "open", "password", "hidden" };
        string[] visibilityNames = visibilityValues.Select(value => SVCLang.Get("channel-visibility-" + value)).ToArray();
        composer.AddStaticText(SVCLang.Get("label-channel-visibility"), label, ElementBounds.Fixed(x + 24, y + 180, 180, 28))
            .AddVoiceDropDown(visibilityValues, visibilityNames, Math.Max(0, Array.IndexOf(visibilityValues, createVisibility.ToString().ToLowerInvariant())),
                OnCreateVisibilityChanged, ElementBounds.Fixed(x + 214, y + 176, 340, 34), "overlay-create-visibility");
        composer.GetTextInput("overlay-create-name").SetValue(createName);
        composer.GetTextInput("overlay-create-password").SetValue(createPassword);
        composer.GetTextInput("overlay-create-name").SetMaxLength(controller.MaxChannelNameLength);
        composer.GetTextInput("overlay-create-password").SetMaxLength(VoiceProtocol.MaxControlStringLength);
        AddFlatButton(composer, SVCLang.Get("channel-action-create"), CreateChannelFromOverlay,
            ElementBounds.Fixed(x + 214, y + 244, 126, 34), "overlay-create-submit");
        composer.GetButton("overlay-create-submit").Enabled = !string.IsNullOrWhiteSpace(createName);
    }

    private static void AddOverlayPanel(GuiComposer composer, double x, double y, double width, double height, string title)
    {
        composer.AddStaticCustomDraw(ElementBounds.Fixed(x, y, width, height), DrawOverlayPanel)
            .AddStaticText(title, CairoFont.WhiteSmallishText().WithFontSize(17).WithColor(new[] { 1.0, 1.0, 1.0, 1.0 }),
                ElementBounds.Fixed(x + 24, y + 16, width - 72, 28));
    }

    private void AddOverlayCloseButton(GuiComposer composer, double x, double y, double width, Action close, string key)
    {
        composer.AddInteractiveElement(
            new VoiceSettingsIconButton(capi, ElementBounds.Fixed(x + width - 44, y + 12, 30, 30), FontAwesomeCloseIcon, _ => close()), key);
    }

    private static void DrawOverlayPanel(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        bounds.CalcWorldBounds();
        GuiElement.RoundRectangle(ctx, bounds.drawX, bounds.drawY, bounds.InnerWidth, bounds.InnerHeight, GuiElement.scaled(4));
        ctx.SetSourceRGBA(0.025, 0.03, 0.04, 0.98);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(0.88, 0.92, 0.98, 0.72);
        ctx.LineWidth = GuiElement.scaled(1);
        ctx.Stroke();
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
        string[] adminActions = BuildAdminChannelActions(channels, selectedPlayerUid);

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
        composer.GetTextInput("adminRenameInput").SetValue(renameText);
        composer.GetTextInput("adminRenameInput").SetMaxLength(controller.MaxChannelNameLength);
        composer.GetButton("adminApply").Enabled = CanExecuteAdminAction();
        composer.GetButton("adminRename").Enabled = CanRenameAdminChannel(channels);
        return AddAdminConfigSection(composer, Math.Max(leftY, rightY) + 30);
    }

    private double AddAdminConfigSection(GuiComposer composer, double startY)
    {
        const double leftX = 18;
        const double rightX = 466;
        const double columnWidth = 420;
        CairoFont section = CairoFont.WhiteSmallishText();
        CairoFont label = CairoFont.WhiteSmallText().WithColor(new[] { 0.9, 0.92, 0.96, 1.0 });

        composer.AddStaticText(SVCLang.Get("ui-section-server-config"), section, ElementBounds.Fixed(leftX, startY, columnWidth, 28));
        AddFlatButton(composer, SVCLang.Get("button-apply-config"), ApplyAdminConfig, ElementBounds.Fixed(rightX + 188, startY - 2, 104, 32), "adminConfigApply", adminConfigDirty);
        AddFlatButton(composer, SVCLang.Get("button-reload-config"), ReloadAdminConfig, ElementBounds.Fixed(rightX + 300, startY - 2, 104, 32), "adminConfigReload");
        composer.GetButton("adminConfigApply").Enabled = adminConfigDirty;

        double leftY = startY + 44;
        AddAdminConfigSwitch(composer, leftX, ref leftY, label, "enabled", SVCLang.Get("admin-config-enabled"), value => adminConfigDraft.Enabled = value);
        AddAdminConfigSwitch(composer, leftX, ref leftY, label, "allow-whisper", SVCLang.Get("admin-config-allow-whisper"), value => adminConfigDraft.AllowWhisper = value);
        AddAdminConfigSwitch(composer, leftX, ref leftY, label, "allow-shout", SVCLang.Get("admin-config-allow-shout"), value => adminConfigDraft.AllowShout = value);
        AddAdminConfigSwitch(composer, leftX, ref leftY, label, "force-immersive", SVCLang.Get("admin-config-force-immersive"), value => adminConfigDraft.ForceImmersive = value);
        AddAdminConfigSwitch(composer, leftX, ref leftY, label, "enable-occlusion", SVCLang.Get("admin-config-enable-occlusion"), value => adminConfigDraft.EnableOcclusion = value);
        AddAdminConfigSwitch(composer, leftX, ref leftY, label, "enable-weather", SVCLang.Get("admin-config-enable-weather"), value => adminConfigDraft.EnableWeatherEffects = value);
        AddAdminConfigSwitch(composer, leftX, ref leftY, label, "enable-hud", SVCLang.Get("admin-config-enable-hud"), value => adminConfigDraft.EnableHudIndicators = value);
        AddAdminConfigSwitch(composer, leftX, ref leftY, label, "allow-continuous-talk", SVCLang.Get("admin-config-allow-continuous-talk"), value => adminConfigDraft.AllowContinuousTalk = value);
        AddAdminConfigSwitch(composer, leftX, ref leftY, label, "enable-channels", SVCLang.Get("admin-config-enable-channels"), value => adminConfigDraft.EnableChannels = value);
        AddAdminConfigSwitch(composer, leftX, ref leftY, label, "allow-channel-creation", SVCLang.Get("admin-config-allow-channel-creation"), value => adminConfigDraft.AllowPlayerChannelCreation = value);
        AddAdminConfigSwitch(composer, leftX, ref leftY, label, "adaptive-bitrate", SVCLang.Get("admin-config-adaptive-bitrate"), value => adminConfigDraft.EnableAdaptiveBitrate = value);
        AddAdminConfigSwitch(composer, leftX, ref leftY, label, "adpcm-fallback", SVCLang.Get("admin-config-adpcm-fallback"), value => adminConfigDraft.AllowAdpcmFallback = value);
        AddAdminConfigSwitch(composer, leftX, ref leftY, label, "director-capture", SVCLang.Get("admin-config-director-capture"), value => adminConfigDraft.EnableDirectorProximityCapture = value);
        AddAdminConfigSwitch(composer, leftX, ref leftY, label, "recorder-capture", SVCLang.Get("admin-config-recorder-capture"), value => adminConfigDraft.EnableRecorderCapture = value);
        AddAdminConfigSwitch(composer, leftX, ref leftY, label, "proximity-chat-text", SVCLang.Get("admin-config-proximity-chat-text"), value => adminConfigDraft.EnableProximityChatText = value);

        double rightY = startY + 44;
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "max-range", SVCLang.Get("admin-config-max-range"), adminConfigDraft.MaxRange, 10, 1280, value => adminConfigDraft.MaxRange = value / 10f, true);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "whisper-range", SVCLang.Get("admin-config-whisper-range"), adminConfigDraft.WhisperRange, 10, 1280, value => adminConfigDraft.WhisperRange = value / 10f, true);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "talk-range", SVCLang.Get("admin-config-talk-range"), adminConfigDraft.TalkRange, 10, 1280, value => adminConfigDraft.TalkRange = value / 10f, true);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "shout-range", SVCLang.Get("admin-config-shout-range"), adminConfigDraft.ShoutRange, 10, 1280, value => adminConfigDraft.ShoutRange = value / 10f, true);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "proximity-chat-range", SVCLang.Get("admin-config-proximity-chat-range"), adminConfigDraft.ProximityChatRange, 10, 1280, value => adminConfigDraft.ProximityChatRange = value / 10f, true);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "default-opus", SVCLang.Get("admin-config-default-opus"), adminConfigDraft.DefaultOpusBitrateKbps, 8, 32, value => adminConfigDraft.DefaultOpusBitrateKbps = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "max-opus", SVCLang.Get("admin-config-max-opus"), adminConfigDraft.MaxOpusBitrateKbps, 8, 32, value => adminConfigDraft.MaxOpusBitrateKbps = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "max-streams", SVCLang.Get("admin-config-max-streams"), adminConfigDraft.MaxStreamsPerListener, 1, 32, value => adminConfigDraft.MaxStreamsPerListener = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "max-proximity-streams", SVCLang.Get("admin-config-max-proximity-streams"), adminConfigDraft.MaxProximityStreams, 1, 32, value => adminConfigDraft.MaxProximityStreams = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "channel-talkers", SVCLang.Get("admin-config-channel-talkers"), adminConfigDraft.MaxChannelTalkers, 1, 12, value => adminConfigDraft.MaxChannelTalkers = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "channel-members", SVCLang.Get("admin-config-channel-members"), adminConfigDraft.MaxChannelMembers, 2, 100, value => adminConfigDraft.MaxChannelMembers = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "channels-per-player", SVCLang.Get("admin-config-channels-per-player"), adminConfigDraft.MaxChannelsPerPlayer, 1, 8, value => adminConfigDraft.MaxChannelsPerPlayer = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "max-channels", SVCLang.Get("admin-config-max-channels"), adminConfigDraft.MaxChannels, 16, 512, value => adminConfigDraft.MaxChannels = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "channel-name-length", SVCLang.Get("admin-config-channel-name-length"), adminConfigDraft.MaxChannelNameLength, 1, VoiceProtocol.MaxControlStringLength, value => adminConfigDraft.MaxChannelNameLength = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "voice-packets", SVCLang.Get("admin-config-voice-packets"), adminConfigDraft.MaxVoicePacketsPerSecond, 5, 100, value => adminConfigDraft.MaxVoicePacketsPerSecond = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "voice-bytes", SVCLang.Get("admin-config-voice-bytes"), adminConfigDraft.MaxVoiceBytesPerSecond, 2048, 65536, value => adminConfigDraft.MaxVoiceBytesPerSecond = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "voice-payload", SVCLang.Get("admin-config-voice-payload"), adminConfigDraft.MaxVoicePayloadBytes, 1, VoiceConstants.MaxUdpPacketBytes - 32, value => adminConfigDraft.MaxVoicePayloadBytes = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "channel-page-size", SVCLang.Get("admin-config-channel-page-size"), adminConfigDraft.ChannelMemberPageSize, 8, 50, value => adminConfigDraft.ChannelMemberPageSize = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "server-egress", SVCLang.Get("admin-config-server-egress"), adminConfigDraft.MaxServerEgressKbps, 1000, 100000, value => adminConfigDraft.MaxServerEgressKbps = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "listener-egress", SVCLang.Get("admin-config-listener-egress"), adminConfigDraft.MaxListenerEgressKbps, 64, 2048, value => adminConfigDraft.MaxListenerEgressKbps = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "director-egress", SVCLang.Get("admin-config-director-egress"), adminConfigDraft.MaxDirectorEgressKbps, 512, 8192, value => adminConfigDraft.MaxDirectorEgressKbps = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "spatial-cell", SVCLang.Get("admin-config-spatial-cell"), adminConfigDraft.SpatialCellSize, 4, 64, value => adminConfigDraft.SpatialCellSize = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "audit-retention", SVCLang.Get("admin-config-audit-retention"), adminConfigDraft.AuditRetention, 50, 2000, value => adminConfigDraft.AuditRetention = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "director-listeners", SVCLang.Get("admin-config-director-listeners"), adminConfigDraft.MaxDirectorListeners, 1, 8, value => adminConfigDraft.MaxDirectorListeners = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "director-streams", SVCLang.Get("admin-config-director-streams"), adminConfigDraft.MaxDirectorStreamsPerListener, 1, 64, value => adminConfigDraft.MaxDirectorStreamsPerListener = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "recorder-listeners", SVCLang.Get("admin-config-recorder-listeners"), adminConfigDraft.MaxRecorderListeners, 1, 4, value => adminConfigDraft.MaxRecorderListeners = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "recorder-egress", SVCLang.Get("admin-config-recorder-egress"), adminConfigDraft.MaxRecorderEgressKbps, 512, 8192, value => adminConfigDraft.MaxRecorderEgressKbps = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "recorder-checkpoint", SVCLang.Get("admin-config-recorder-checkpoint"), adminConfigDraft.RecorderCheckpointSeconds, 1, 60, value => adminConfigDraft.RecorderCheckpointSeconds = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "recorder-session", SVCLang.Get("admin-config-recorder-session"), adminConfigDraft.MaxRecorderSessionMinutes, 1, 1440, value => adminConfigDraft.MaxRecorderSessionMinutes = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "recorder-clock-skew", SVCLang.Get("admin-config-recorder-clock-skew"), adminConfigDraft.MaxRecorderClockSkewMilliseconds, 250, 10000, value => adminConfigDraft.MaxRecorderClockSkewMilliseconds = value);
        AddAdminConfigSlider(composer, rightX, ref rightY, label, "recorder-download", SVCLang.Get("admin-config-recorder-download"), adminConfigDraft.MaxRecorderDownloadKbps, 256, 100000, value => adminConfigDraft.MaxRecorderDownloadKbps = value);
        return Math.Max(leftY, rightY) + 24;
    }

    private void AddAdminConfigSwitch(GuiComposer composer, double x, ref double y, CairoFont label, string key, string text, Action<bool> set)
    {
        composer.AddStaticText(text, label, ElementBounds.Fixed(x, y + 2, 210, 30));
        AddCheckBox(composer, value => { set(value); MarkAdminConfigDirty(); }, ElementBounds.Fixed(x + 217, y, 28, 28), "admin-config-" + key, GetAdminConfigBool(key));
        y += 40;
    }

    private void AddAdminConfigSlider(GuiComposer composer, double x, ref double y, CairoFont label, string key, string text, float value, int minimum, int maximum, Action<int> set, bool range = false)
    {
        composer.AddStaticText(text, label, ElementBounds.Fixed(x, y + 2, 210, 30));
        composer.AddVoiceSlider(number => { set(number); MarkAdminConfigDirty(); return true; }, ElementBounds.Fixed(x + 217, y, 190, 32), "admin-config-" + key);
        ConfigureSlider(composer, "admin-config-" + key, (int)Math.Round(value * (range ? 10 : 1)), minimum, maximum);
        y += 40;
    }

    private bool GetAdminConfigBool(string key) => key switch
    {
        "enabled" => adminConfigDraft.Enabled,
        "allow-whisper" => adminConfigDraft.AllowWhisper,
        "allow-shout" => adminConfigDraft.AllowShout,
        "force-immersive" => adminConfigDraft.ForceImmersive,
        "enable-occlusion" => adminConfigDraft.EnableOcclusion,
        "enable-weather" => adminConfigDraft.EnableWeatherEffects,
        "enable-hud" => adminConfigDraft.EnableHudIndicators,
        "allow-continuous-talk" => adminConfigDraft.AllowContinuousTalk,
        "enable-channels" => adminConfigDraft.EnableChannels,
        "allow-channel-creation" => adminConfigDraft.AllowPlayerChannelCreation,
        "adaptive-bitrate" => adminConfigDraft.EnableAdaptiveBitrate,
        "adpcm-fallback" => adminConfigDraft.AllowAdpcmFallback,
        "director-capture" => adminConfigDraft.EnableDirectorProximityCapture,
        "recorder-capture" => adminConfigDraft.EnableRecorderCapture,
        "proximity-chat-text" => adminConfigDraft.EnableProximityChatText,
        _ => false
    };

    private void MarkAdminConfigDirty()
    {
        adminConfigDirty = true;
        SetButtonEnabled("adminConfigApply", true);
    }

    private bool ApplyAdminConfig()
    {
        if (!controller.HasServerControl || !adminConfigDirty) return false;
        controller.ApplyServerConfigFromSettings(adminConfigDraft, reload: false);
        adminConfigDirty = false;
        QueueCompose();
        return true;
    }

    private bool ReloadAdminConfig()
    {
        if (!controller.HasServerControl) return false;
        controller.ApplyServerConfigFromSettings(controller.ServerSettings, reload: true);
        adminConfigDraft = CloneServerConfig(controller.ServerSettings);
        adminConfigDirty = false;
        QueueCompose();
        return true;
    }

    private static ServerVoiceConfigPacket CloneServerConfig(ServerVoiceConfigPacket source)
    {
        return new ServerVoiceConfigPacket
        {
            Enabled = source.Enabled, AllowWhisper = source.AllowWhisper, AllowShout = source.AllowShout,
            ForceImmersive = source.ForceImmersive, MaxRange = source.MaxRange, WhisperRange = source.WhisperRange,
            TalkRange = source.TalkRange, ShoutRange = source.ShoutRange, EnableOcclusion = source.EnableOcclusion,
            EnableProximityChatText = source.EnableProximityChatText, ProximityChatRange = source.ProximityChatRange,
            EnableWeatherEffects = source.EnableWeatherEffects, EnableHudIndicators = source.EnableHudIndicators,
            ProtocolVersion = source.ProtocolVersion, MaxStreamsPerListener = source.MaxStreamsPerListener,
            AllowContinuousTalk = source.AllowContinuousTalk, ServerInstanceId = source.ServerInstanceId,
            EnableDirectorProximityCapture = source.EnableDirectorProximityCapture, EnableRecorderCapture = source.EnableRecorderCapture,
            DefaultOpusBitrateKbps = source.DefaultOpusBitrateKbps, MaxOpusBitrateKbps = source.MaxOpusBitrateKbps,
            EnableAdaptiveBitrate = source.EnableAdaptiveBitrate, AllowAdpcmFallback = source.AllowAdpcmFallback,
            MaxChannelNameLength = source.MaxChannelNameLength, MaxVoicePacketsPerSecond = source.MaxVoicePacketsPerSecond,
            MaxVoiceBytesPerSecond = source.MaxVoiceBytesPerSecond, MaxVoicePayloadBytes = source.MaxVoicePayloadBytes,
            MaxServerEgressKbps = source.MaxServerEgressKbps, MaxListenerEgressKbps = source.MaxListenerEgressKbps,
            MaxDirectorEgressKbps = source.MaxDirectorEgressKbps, SpatialCellSize = source.SpatialCellSize,
            MaxProximityStreams = source.MaxProximityStreams, MaxChannelTalkers = source.MaxChannelTalkers,
            MaxChannelMembers = source.MaxChannelMembers, MaxChannelsPerPlayer = source.MaxChannelsPerPlayer,
            MaxChannels = source.MaxChannels, ChannelMemberPageSize = source.ChannelMemberPageSize,
            AuditRetention = source.AuditRetention, EnableChannels = source.EnableChannels,
            AllowPlayerChannelCreation = source.AllowPlayerChannelCreation, MaxDirectorListeners = source.MaxDirectorListeners,
            MaxDirectorStreamsPerListener = source.MaxDirectorStreamsPerListener, MaxRecorderListeners = source.MaxRecorderListeners,
            MaxRecorderEgressKbps = source.MaxRecorderEgressKbps, RecorderCheckpointSeconds = source.RecorderCheckpointSeconds,
            MaxRecorderSessionMinutes = source.MaxRecorderSessionMinutes, MaxRecorderClockSkewMilliseconds = source.MaxRecorderClockSkewMilliseconds,
            MaxRecorderDownloadKbps = source.MaxRecorderDownloadKbps
        };
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

    private static void DrawChannelCardBackground(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        bounds.CalcWorldBounds();
        ctx.Rectangle(bounds.drawX, bounds.drawY, bounds.InnerWidth, bounds.InnerHeight);
        ctx.SetSourceRGBA(0.10, 0.12, 0.15, 0.96);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(0.86, 0.90, 0.96, 0.58);
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

    private List<string> BuildChannelActions(
        VoiceSettingsChannelOption[] channels,
        string? channelId = null,
        string? targetPlayerUid = null)
    {
        bool hasPlayer = !string.IsNullOrWhiteSpace(targetPlayerUid);
        string selectedId = channelId ?? config.SelectedChannelId;
        VoiceSettingsChannelOption? selected = channels.Cast<VoiceSettingsChannelOption?>().FirstOrDefault(channel => channel?.Id == selectedId);
        bool hasChannel = selected.HasValue;
        VoiceChannelRole role = selected?.LocalRole ?? VoiceChannelRole.Banned;
        bool external = selected?.ExternallyManaged ?? false;
        bool canInvite = !external && (controller.HasServerControl
            || selected is { LocalRole: >= VoiceChannelRole.Moderator });
        bool targetInChannel = hasPlayer && hasChannel && controller.IsPlayerInChannel(selectedId, targetPlayerUid!);
        List<string> actions = new();
        if (canInvite && hasPlayer && !targetInChannel) actions.Add("invite");
        if (hasChannel && !external && role != VoiceChannelRole.Banned) actions.Add("leave");
        if (hasChannel && role >= VoiceChannelRole.Moderator && targetInChannel)
        {
            actions.AddRange(new[] { "mute", "unmute", "ban", "unban" });
            if (!external) actions.Add("remove");
        }
        if (hasChannel && role == VoiceChannelRole.Owner)
        {
            if (targetInChannel && !external) actions.AddRange(new[] { "listenonly", "member", "moderator" });
            actions.AddRange(new[] { "lock", "unlock" });
            if (!external) actions.Add("disband");
        }
        if (actions.Count == 0) actions.Add("none");
        return actions;
    }

    private string[] BuildAdminChannelActions(VoiceSettingsChannelOption[] channels, string? targetPlayerUid)
    {
        VoiceSettingsChannelOption? selected = channels.Cast<VoiceSettingsChannelOption?>().FirstOrDefault(channel => channel?.Id == selectedAdminChannelId);
        bool targetInChannel = selected.HasValue
            && !string.IsNullOrWhiteSpace(targetPlayerUid)
            && controller.IsPlayerInChannel(selectedAdminChannelId, targetPlayerUid!);
        List<string> actions = new() { "lock", "unlock" };
        if (selected is { ExternallyManaged: false })
        {
            if (!string.IsNullOrWhiteSpace(targetPlayerUid) && !targetInChannel) actions.Add("add");
            if (targetInChannel) actions.AddRange(new[] { "remove", "listenonly", "member", "moderator" });
            actions.Add("disband");
        }
        if (targetInChannel)
        {
            actions.InsertRange(0, new[] { "mute", "unmute", "ban", "unban" });
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
        if (page == VoiceSettingsPage.Admin)
        {
            adminConfigDraft = CloneServerConfig(controller.ServerSettings);
            adminConfigDirty = false;
        }
        if (page == VoiceSettingsPage.Channels)
        {
            channelListPage = 0;
        }
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

    private void OnPreferredOpusBitrateChanged(string value, bool selected)
    {
        if (selected)
        {
            controller.SetPreferredOpusBitrateFromSettings(value);
            QueueCompose();
        }
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

    private bool ApplyChannelSearch()
    {
        channelSearch = channelSearchDraft;
        channelListPage = 0;
        QueueCompose();
        return true;
    }

    private bool ApplyPlayerSearch()
    {
        playerSearch = playerSearchDraft;
        playerListPage = 0;
        QueueCompose();
        return true;
    }

    private bool ClearChannelSearch()
    {
        channelSearch = string.Empty;
        channelSearchDraft = string.Empty;
        channelListPage = 0;
        QueueCompose();
        return true;
    }

    private bool ClearPlayerSearch()
    {
        playerSearch = string.Empty;
        playerSearchDraft = string.Empty;
        playerListPage = 0;
        QueueCompose();
        return true;
    }

    private bool ChangeChannelPage(int offset)
    {
        channelListPage = Math.Max(0, channelListPage + offset);
        QueueCompose();
        return true;
    }

    private bool ChangePlayerPage(int offset)
    {
        playerListPage = Math.Max(0, playerListPage + offset);
        QueueCompose();
        return true;
    }

    private void OnQuickChannelChanged(string value, bool selected)
    {
        OnChannelChanged(value, selected);
    }

    private void OpenChannelOverlay(string channelId)
    {
        PushOverlayState();
        overlayChannelId = channelId;
        overlayTargetPlayerUid = string.Empty;
        overlayAction = "none";
        overlay = VoiceSettingsOverlay.Channel;
        QueueCompose();
    }

    private void OpenPlayersOverlay()
    {
        PushOverlayState();
        playerListPage = 0;
        overlay = VoiceSettingsOverlay.Players;
        QueueCompose();
    }

    internal void OpenRecordingModeOverlay()
    {
        if (overlay != VoiceSettingsOverlay.None)
        {
            return;
        }

        PushOverlayState();
        overlay = VoiceSettingsOverlay.RecordingMode;
        QueueCompose();
    }

    internal void OpenMultiTrackRecordingOverlay()
    {
        if (!IsOpened())
        {
            TryOpen();
        }

        if (overlay != VoiceSettingsOverlay.None)
        {
            return;
        }

        PushOverlayState();
        overlay = VoiceSettingsOverlay.MultiTrackRecording;
        QueueCompose();
    }

    private bool OpenCreateChannelOverlay()
    {
        PushOverlayState();
        createName = string.Empty;
        createPassword = string.Empty;
        overlay = VoiceSettingsOverlay.CreateChannel;
        QueueCompose();
        return true;
    }

    private void OpenPlayerOverlay(string playerUid)
    {
        PushOverlayState();
        overlayPlayerUid = playerUid;
        overlayChannelId = config.SelectedChannelId;
        overlayAction = "invite";
        overlay = VoiceSettingsOverlay.Player;
        QueueCompose();
    }

    private void OpenCurrentStatusOverlay()
    {
        overlayStack.Clear();
        overlay = VoiceSettingsOverlay.CurrentStatus;
        controller.RequestDiagnosticsFromSettings();
        QueueCompose();
    }

    private void CloseOverlay()
    {
        bool closingChannelConfirmation = overlay == VoiceSettingsOverlay.ConfirmChannelAction;
        if (overlayStack.Count > 0)
        {
            OverlaySnapshot previous = overlayStack.Pop();
            overlay = previous.Overlay;
            overlayChannelId = previous.ChannelId;
            overlayPlayerUid = previous.PlayerUid;
            overlayTargetPlayerUid = previous.TargetPlayerUid;
            overlayAction = previous.Action;
        }
        else
        {
            overlay = VoiceSettingsOverlay.None;
            ownerLeaveChannelId = string.Empty;
            ownerLeaveTargetUid = string.Empty;
        }
        if (closingChannelConfirmation)
        {
            confirmChannelAction = string.Empty;
            confirmChannelId = string.Empty;
        }
        QueueCompose();
    }

    private void PushOverlayState()
    {
        if (overlay == VoiceSettingsOverlay.None)
        {
            return;
        }
        overlayStack.Push(new OverlaySnapshot(
            overlay,
            overlayChannelId,
            overlayPlayerUid,
            overlayTargetPlayerUid,
            overlayAction));
    }

    private void OnOverlayChannelChanged(string value, bool selected)
    {
        if (selected)
        {
            overlayChannelId = value;
            overlayAction = "none";
            QueueCompose();
        }
    }

    private void OnOverlayTargetPlayerChanged(string value, bool selected)
    {
        if (!selected)
        {
            return;
        }
        overlayTargetPlayerUid = value;
        overlayAction = "none";
        QueueCompose();
    }

    private void OnOverlayActionChanged(string value, bool selected)
    {
        if (!selected)
        {
            return;
        }
        overlayAction = value;
        SetButtonEnabled(
            overlay == VoiceSettingsOverlay.Player ? "overlay-player-apply" : "overlay-channel-apply",
            value != "none");
    }

    private bool ApplyChannelOverlay()
    {
        if (overlayAction != "none" && !string.IsNullOrWhiteSpace(overlayChannelId))
        {
            VoiceChannelRole role = overlayAction switch
            {
                "listenonly" => VoiceChannelRole.ListenOnly,
                "moderator" => VoiceChannelRole.Moderator,
                _ => VoiceChannelRole.Member
            };
            string action = overlayAction is "listenonly" or "member" or "moderator" ? "role" : overlayAction;
            if (VoiceSettingsActionPolicy.RequiresConfirmation(action))
            {
                return OpenChannelActionConfirmation(action, overlayChannelId);
            }
            controller.ManageSelectedChannel(action, overlayChannelId, overlayTargetPlayerUid, string.Empty, role);
        }
        CloseOverlay();
        return true;
    }

    private bool OpenChannelActionConfirmation(string action, string channelId)
    {
        if (!VoiceSettingsActionPolicy.RequiresConfirmation(action) || string.IsNullOrWhiteSpace(channelId))
        {
            return false;
        }

        PushOverlayState();
        confirmChannelAction = action;
        confirmChannelId = channelId;
        overlay = VoiceSettingsOverlay.ConfirmChannelAction;
        QueueCompose();
        return true;
    }

    private bool ConfirmChannelAction()
    {
        string action = confirmChannelAction;
        string channelId = confirmChannelId;
        if (!VoiceSettingsActionPolicy.RequiresConfirmation(action) || string.IsNullOrWhiteSpace(channelId))
        {
            return false;
        }

        overlayStack.Clear();
        overlay = VoiceSettingsOverlay.None;
        confirmChannelAction = string.Empty;
        confirmChannelId = string.Empty;
        ownerLeaveChannelId = string.Empty;
        ownerLeaveTargetUid = string.Empty;
        if (action == "delete-owned-channel")
        {
            controller.DeleteChannelFromSettings(channelId);
        }
        else
        {
            controller.ManageSelectedChannel(action, channelId);
        }
        QueueCompose();
        return true;
    }

    private bool ApplyPlayerOverlay()
    {
        if (!string.IsNullOrWhiteSpace(overlayPlayerUid) && !string.IsNullOrWhiteSpace(overlayChannelId))
        {
            VoiceChannelRole role = overlayAction switch
            {
                "listenonly" => VoiceChannelRole.ListenOnly,
                "moderator" => VoiceChannelRole.Moderator,
                _ => VoiceChannelRole.Member
            };
            string action = overlayAction is "listenonly" or "member" or "moderator" ? "role" : overlayAction;
            controller.ManageSelectedChannel(action, overlayChannelId, overlayPlayerUid, string.Empty, role);
        }
        CloseOverlay();
        return true;
    }

    private void OnCreatePasswordChanged(string value)
    {
        createPassword = value;
    }

    private void CopyChannelId(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId)) return;
        capi.Input.ClipboardText = channelId;
        capi.ShowChatMessage(SVCLang.Get("channel-id-copied", channelId));
    }

    private void OnCreateVisibilityChanged(string value, bool selected)
    {
        if (selected && Enum.TryParse(value, true, out VoiceChannelVisibility visibility))
        {
            createVisibility = visibility;
            QueueCompose();
        }
    }

    private bool CreateChannelFromOverlay()
    {
        if (string.IsNullOrWhiteSpace(createName))
        {
            return false;
        }
        controller.CreateChannelFromSettings(createName, createPassword, createVisibility);
        createName = string.Empty;
        createPassword = string.Empty;
        createVisibility = VoiceChannelVisibility.Open;
        CloseOverlay();
        return true;
    }

    private void OnTransmitChanged(string value, bool selected)
    {
        if (selected) controller.SetTransmitTargetFromSettings(value);
    }

    private void OnQuickTransmitChanged(string value, bool selected)
    {
        OnTransmitChanged(value, selected);
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
        if (VoiceSettingsActionPolicy.RequiresConfirmation(action))
        {
            return OpenChannelActionConfirmation(action, config.SelectedChannelId);
        }
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
        if (VoiceSettingsActionPolicy.RequiresConfirmation(action))
        {
            return OpenChannelActionConfirmation(action, selectedAdminChannelId);
        }
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
        SetButtonEnabled("overlay-create-submit", !string.IsNullOrWhiteSpace(value));
    }

    internal void SetHudPositionEditing(bool editing)
    {
        if (hudPositionEditing == editing)
        {
            return;
        }

        hudPositionEditing = editing;
        if (IsOpened())
        {
            QueueCompose();
        }
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
            (float)Math.Max(0d, contentHeight - activeViewportHeight));
        if (contentBounds == null) return;
        contentBounds.fixedY = -scrollPosition;
        contentBounds.CalcWorldBounds();
        SingleComposer?.ReCompose();
    }

    private void QueueCompose()
    {
        composePending = true;
        if (pointerPressed || composeQueued) return;
        composeQueued = true;
        capi.Event.EnqueueMainThreadTask(() =>
        {
            composeQueued = false;
            FlushQueuedCompose();
        }, "simplevoicechat-settings-recompose");
    }

    private void FlushQueuedCompose()
    {
        if (pointerPressed || !composePending || !IsOpened())
        {
            return;
        }
        composePending = false;
        Compose();
    }

    private void OnClose()
    {
        if (overlay != VoiceSettingsOverlay.None)
        {
            CloseOverlay();
            return;
        }
        if (selectedPage != VoiceSettingsPage.Home)
        {
            selectedPage = VoiceSettingsPage.Home;
            scrollPosition = 0;
            Compose();
            return;
        }
        TryClose();
    }

    private static string GetPageName(VoiceSettingsPage page)
    {
        return page switch
        {
            VoiceSettingsPage.Channels => SVCLang.Get("tab-channels"),
            VoiceSettingsPage.SpeechRecognition => SVCLang.Get("tab-speech-recognition"),
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
