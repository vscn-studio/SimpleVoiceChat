using Cairo;
using OpenTK.Audio.OpenAL;
using SimpleVoiceChat.Config;
using SimpleVoiceChat.Networking;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using EnumMouseButton = Vintagestory.API.Common.EnumMouseButton;

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
            or "lock" or "unlock" or "leave" or "disband" or "rename";
    }
}

public sealed class VoiceSettingsDialog : GuiDialog
{
    private const string DefaultInputDeviceValue = "__default__";
    private const double SidebarWidth = 184;
    private const double HeaderHeight = 48;
    private const double FooterHeight = 42;
    private const int SelectorRows = 8;

    private static readonly UiColor WindowColor = new(0.065, 0.069, 0.073, 0.99);
    private static readonly UiColor SidebarColor = new(0.082, 0.086, 0.090, 1);
    private static readonly UiColor SurfaceColor = new(0.105, 0.109, 0.113, 1);
    private static readonly UiColor SelectedSurfaceColor = new(0.135, 0.140, 0.145, 1);
    private static readonly UiColor BorderColor = new(0.235, 0.245, 0.250, 0.88);
    private static readonly UiColor TextColor = new(0.90, 0.91, 0.92, 1);
    private static readonly UiColor MutedTextColor = new(0.59, 0.61, 0.63, 1);
    private static readonly UiColor AccentColor = new(0.42, 0.50, 0.53, 1);
    private static readonly UiColor AccentDarkColor = new(0.22, 0.27, 0.29, 1);
    private static readonly UiColor DangerColor = new(0.66, 0.27, 0.28, 1);

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
    private readonly Action<string, string, string, string, VoiceChannelRole> manageChannel;
    private readonly Action<bool> setAdaptiveJitter;
    private readonly Func<bool> localMutedProvider;
    private readonly Action<bool> setLocalMuted;
    private readonly Func<bool> globalMutedProvider;
    private readonly Action<bool> setGlobalMuted;
    private readonly Func<bool> continuousTalkEnabledProvider;
    private readonly Func<bool> continuousTalkAllowedProvider;
    private readonly Action<bool> setContinuousTalk;
    private readonly Func<bool> hasServerControlProvider;
    private readonly LucideIconRenderer lucideIcons;

    private readonly List<UiHit> hits = new();
    private string selectedPlayerUid = string.Empty;
    private string selectedChannelAction = "invite";
    private string channelNameDraft = string.Empty;
    private string channelNameDraftChannelId = string.Empty;
    private string createChannelName = string.Empty;
    private string activeTextInputId = string.Empty;
    private int textCaretIndex;
    private bool textSelectAll;
    private string hoveredId = string.Empty;
    private string displayedHoverId = string.Empty;
    private string activeSliderId = string.Empty;
    private int memberPage;
    private int selectedPage;
    private int mouseX = -1;
    private int mouseY = -1;
    private long lastDynamicRefresh;
    private long hoverStartedMilliseconds;
    private double dialogWidth;
    private double dialogHeight;
    private double canvasLayoutScale = 1;
    private double windowOffsetX;
    private double windowOffsetY;
    private bool draggingWindow;
    private int dragStartX;
    private int dragStartY;
    private double dragOffsetX;
    private double dragOffsetY;
    private ImageSurface? currentSurface;
    private double currentIconScale = 1;
    private SelectorPopup? selectorPopup;
    private ConfirmationPopup? confirmationPopup;

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
        Action<string, string, string, string, VoiceChannelRole> manageChannel,
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
        lucideIcons = new LucideIconRenderer(capi);
        Compose();
    }

    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;
    public override bool DisableMouseGrab => true;
    public override EnumDialogType DialogType => EnumDialogType.Dialog;
    public override double DrawOrder => 0.98;
    public override double InputOrder => 0.98;

    public override bool TryOpen()
    {
        requestSquadStatus();
        selectorPopup = null;
        confirmationPopup = null;
        windowOffsetX = 0;
        windowOffsetY = 0;
        draggingWindow = false;
        Compose();
        return base.TryOpen();
    }

    public override void OnRenderGUI(float deltaTime)
    {
        if (!string.IsNullOrEmpty(hoveredId)
            && displayedHoverId != hoveredId
            && capi.ElapsedMilliseconds - hoverStartedMilliseconds >= 300)
        {
            displayedHoverId = hoveredId;
            RedrawHint();
        }
        if (selectedPage == 3 && capi.ElapsedMilliseconds - lastDynamicRefresh > 1000)
        {
            lastDynamicRefresh = capi.ElapsedMilliseconds;
            Redraw();
        }
        base.OnRenderGUI(deltaTime);
    }

    public override void OnMouseMove(MouseEvent args)
    {
        if (draggingWindow && SingleComposer != null)
        {
            double scale = Math.Max(0.001, GuiElement.scaled(1));
            windowOffsetX = dragOffsetX + (args.X - dragStartX) / scale;
            windowOffsetY = dragOffsetY + (args.Y - dragStartY) / scale;
            SingleComposer.Bounds.WithFixedAlignmentOffset(windowOffsetX, windowOffsetY);
            SingleComposer.Bounds.MarkDirtyRecursive();
            SingleComposer.Bounds.CalcWorldBounds();
            args.Handled = true;
            return;
        }
        UpdateLocalMouse(args);
        string previous = hoveredId;
        hoveredId = FindHit(mouseX, mouseY)?.Id ?? string.Empty;
        if (previous != hoveredId)
        {
            hoverStartedMilliseconds = capi.ElapsedMilliseconds;
            bool clearVisibleHint = !string.IsNullOrEmpty(displayedHoverId);
            displayedHoverId = string.Empty;
            if (clearVisibleHint)
            {
                RedrawHint();
            }
        }
        if (!string.IsNullOrEmpty(activeSliderId))
        {
            UiHit? active = hits.LastOrDefault(hit => hit.Id == activeSliderId);
            active?.Invoke(mouseX);
            args.Handled = true;
        }
        if (!string.IsNullOrEmpty(activeSliderId))
        {
            Redraw();
        }
    }

    public override void OnMouseDown(MouseEvent args)
    {
        UpdateLocalMouse(args);
        if (args.Button == EnumMouseButton.Left
            && SingleComposer != null
            && SingleComposer.Bounds.PointInside(args.X, args.Y)
            && args.Y <= SingleComposer.Bounds.absY + GuiElement.scaled(HeaderHeight * canvasLayoutScale)
            && args.X < SingleComposer.Bounds.absX + SingleComposer.Bounds.OuterWidth - GuiElement.scaled(54 * canvasLayoutScale))
        {
            draggingWindow = true;
            dragStartX = args.X;
            dragStartY = args.Y;
            dragOffsetX = windowOffsetX;
            dragOffsetY = windowOffsetY;
            args.Handled = true;
            return;
        }
        UiHit? hit = FindHit(mouseX, mouseY);
        if (hit != null)
        {
            if (hit.Enabled)
            {
                if (hit.Id is not "channel-name-input" and not "create-channel-name-input")
                {
                    activeTextInputId = string.Empty;
                    textSelectAll = false;
                }
                if (hit.IsSlider)
                {
                    activeSliderId = hit.Id;
                }
                hit.Invoke(mouseX);
            }
            args.Handled = true;
            hoveredId = string.Empty;
            displayedHoverId = string.Empty;
            Redraw();
            RedrawHint();
            return;
        }

        if ((selectorPopup != null || confirmationPopup != null) && new UiRect(0, 0, dialogWidth, dialogHeight).Contains(mouseX, mouseY))
        {
            selectorPopup = null;
            confirmationPopup = null;
            args.Handled = true;
            Redraw();
            return;
        }
        base.OnMouseDown(args);
    }

    public override void OnMouseUp(MouseEvent args)
    {
        if (draggingWindow)
        {
            draggingWindow = false;
            args.Handled = true;
            return;
        }
        if (!string.IsNullOrEmpty(activeSliderId))
        {
            activeSliderId = string.Empty;
            args.Handled = true;
            Redraw();
            return;
        }
        base.OnMouseUp(args);
    }

    public override void OnKeyDown(KeyEvent args)
    {
        if (string.IsNullOrEmpty(activeTextInputId))
        {
            base.OnKeyDown(args);
            return;
        }

        string text = GetActiveText();
        if (args.CtrlPressed || args.CommandPressed)
        {
            switch ((GlKeys)args.KeyCode)
            {
                case GlKeys.A:
                    textSelectAll = text.Length > 0;
                    textCaretIndex = text.Length;
                    Redraw();
                    args.Handled = true;
                    return;
                case GlKeys.C:
                    if (textSelectAll && text.Length > 0)
                    {
                        capi.Forms.SetClipboardText(text);
                    }
                    args.Handled = true;
                    return;
                case GlKeys.X:
                    if (textSelectAll && text.Length > 0)
                    {
                        capi.Forms.SetClipboardText(text);
                        SetActiveText(string.Empty);
                    }
                    args.Handled = true;
                    return;
                case GlKeys.V:
                    InsertActiveText((capi.Forms.GetClipboardText() ?? string.Empty)
                        .Replace("\r", string.Empty)
                        .Replace("\n", " "));
                    args.Handled = true;
                    return;
            }
        }

        switch ((GlKeys)args.KeyCode)
        {
            case GlKeys.Escape:
            case GlKeys.Tab:
                activeTextInputId = string.Empty;
                textSelectAll = false;
                Redraw();
                args.Handled = true;
                return;
            case GlKeys.Enter:
            case GlKeys.KeypadEnter:
                if (activeTextInputId == "channel-name-input")
                {
                    OnRenameChannelClicked();
                }
                activeTextInputId = string.Empty;
                textSelectAll = false;
                Redraw();
                args.Handled = true;
                return;
            case GlKeys.Back:
                if (textSelectAll)
                {
                    SetActiveText(string.Empty);
                }
                else if (textCaretIndex > 0)
                {
                    SetActiveText(text.Remove(textCaretIndex - 1, 1), textCaretIndex - 1);
                }
                args.Handled = true;
                return;
            case GlKeys.Delete:
                if (textSelectAll)
                {
                    SetActiveText(string.Empty);
                }
                else if (textCaretIndex < text.Length)
                {
                    SetActiveText(text.Remove(textCaretIndex, 1), textCaretIndex);
                }
                args.Handled = true;
                return;
            case GlKeys.Left:
                textSelectAll = false;
                textCaretIndex = Math.Max(0, textCaretIndex - 1);
                Redraw();
                args.Handled = true;
                return;
            case GlKeys.Right:
                textSelectAll = false;
                textCaretIndex = Math.Min(text.Length, textCaretIndex + 1);
                Redraw();
                args.Handled = true;
                return;
            case GlKeys.Home:
                textSelectAll = false;
                textCaretIndex = 0;
                Redraw();
                args.Handled = true;
                return;
            case GlKeys.End:
                textSelectAll = false;
                textCaretIndex = text.Length;
                Redraw();
                args.Handled = true;
                return;
        }

        args.Handled = true;
    }

    public override void OnKeyPress(KeyEvent args)
    {
        if (string.IsNullOrEmpty(activeTextInputId))
        {
            base.OnKeyPress(args);
            return;
        }
        if (!args.CtrlPressed
            && !args.CommandPressed
            && !args.AltPressed
            && args.KeyChar != '\0'
            && !char.IsControl(args.KeyChar))
        {
            InsertActiveText(args.KeyChar.ToString());
        }
        args.Handled = true;
    }

    public void Compose()
    {
        double scale = Math.Max(0.5, GuiElement.scaled(1));
        double availableWidth = capi.Render.FrameWidth / scale - 32;
        double availableHeight = capi.Render.FrameHeight / scale - 32;
        dialogWidth = Math.Min(960, Math.Max(520, availableWidth));
        dialogHeight = Math.Min(730, Math.Max(440, availableHeight));
        SingleComposer?.Dispose();
        ElementBounds dialogBounds = ElementBounds.Fixed(
                EnumDialogArea.CenterMiddle,
                0,
                0,
                dialogWidth,
                dialogHeight)
            .WithFixedAlignmentOffset(windowOffsetX, windowOffsetY);
        ElementBounds canvasBounds = ElementBounds.Fixed(0, 0, dialogWidth, dialogHeight);
        canvasLayoutScale = Math.Min(1, Math.Min(dialogWidth / 760, dialogHeight / 620));
        double layoutWidth = dialogWidth / canvasLayoutScale;
        double layoutHeight = dialogHeight / canvasLayoutScale;
        ElementBounds hintBounds = ElementBounds.Fixed(
            24 * canvasLayoutScale,
            (layoutHeight - FooterHeight - 8) * canvasLayoutScale,
            Math.Max(120, (layoutWidth - 204) * canvasLayoutScale),
            Math.Max(24, (FooterHeight + 2) * canvasLayoutScale));
        SingleComposer = capi.Gui.CreateCompo("simplevoicechat-settings-custom", dialogBounds)
            .AddDynamicCustomDraw(canvasBounds, DrawCanvas, "canvas")
            .AddDynamicCustomDraw(hintBounds, DrawHintCanvas, "hint")
            .Compose();
    }

    private void DrawCanvas(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        hits.Clear();
        double scale = Math.Max(0.5, GuiElement.scaled(1));
        double actualWidth = bounds.OuterWidth / scale;
        double actualHeight = bounds.OuterHeight / scale;
        canvasLayoutScale = Math.Min(1, Math.Min(actualWidth / 760, actualHeight / 620));
        ctx.Save();
        ctx.Scale(scale * canvasLayoutScale, scale * canvasLayoutScale);
        double width = actualWidth / canvasLayoutScale;
        double height = actualHeight / canvasLayoutScale;
        currentSurface = surface;
        currentIconScale = scale * canvasLayoutScale;
        dialogWidth = width;
        dialogHeight = height;

        FillRound(ctx, new UiRect(10, 8, width - 20, height - 16), 8, WindowColor);
        StrokeRound(ctx, new UiRect(10, 8, width - 20, height - 16), 8, BorderColor, 1);
        FillRound(ctx, new UiRect(10, 8, width - 20, HeaderHeight + 8), 8, SidebarColor);
        FillRect(ctx, new UiRect(10, HeaderHeight + 8, SidebarWidth, height - HeaderHeight - FooterHeight - 16), SidebarColor);
        DrawLine(ctx, 10, HeaderHeight + 8, width - 10, HeaderHeight + 8, BorderColor);
        DrawLine(ctx, SidebarWidth + 10, HeaderHeight + 8, SidebarWidth + 10, height - FooterHeight - 8, BorderColor);

        DrawText(ctx, SVCLang.Get("title"), 28, 39, 21, TextColor, true);
        AddHit("window-drag", new UiRect(10, 8, width - 68, HeaderHeight), SVCLang.Get("tooltip-drag-window"), () => { });
        DrawIconButton(ctx, "close", new UiRect(width - 48, 17, 26, 26), LucideIcon.Close, SVCLang.Get("tooltip-close"), ToAction(TryClose));

        DrawNavigation(ctx);
        DrawPage(ctx);

        DrawLine(ctx, 10, height - FooterHeight - 8, width - 10, height - FooterHeight - 8, BorderColor);
        DrawTextRight(ctx, hasServerControlProvider() ? SVCLang.Get("ui-role-admin") : SVCLang.Get("ui-role-player"), width - 28, height - 20, 11, MutedTextColor);

        if (confirmationPopup != null)
        {
            DrawConfirmationPopup(ctx, width, height);
        }
        else if (selectorPopup != null)
        {
            DrawSelectorPopup(ctx, width, height);
        }
        ctx.Restore();
        currentSurface = null;
    }

    private void DrawHintCanvas(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        _ = surface;
        double scale = Math.Max(0.5, GuiElement.scaled(1));
        double drawScale = scale * Math.Max(0.01, canvasLayoutScale);
        double width = bounds.OuterWidth / drawScale;
        string hint = SVCLang.Get("ui-footer-help");
        if (!string.IsNullOrEmpty(displayedHoverId))
        {
            UiHit? hit = hits.LastOrDefault(candidate => candidate.Id == displayedHoverId);
            if (hit != null && !string.IsNullOrWhiteSpace(hit.Tooltip))
            {
                hint = hit.Tooltip;
            }
        }

        ctx.Save();
        ctx.Scale(drawScale, drawScale);
        DrawClippedText(ctx, hint, 0, FooterHeight - 12, width, 11, MutedTextColor);
        ctx.Restore();
    }

    private void DrawNavigation(Context ctx)
    {
        double x = 24;
        double y = HeaderHeight + 28;
        double width = SidebarWidth - 28;
        DrawText(ctx, SVCLang.Get("ui-navigation"), x, y, 11, MutedTextColor, true);
        y += 18;
        DrawNavItem(ctx, 0, new UiRect(x, y, width, 43), SVCLang.Get("tab-audio"), SVCLang.Get("ui-nav-audio-hint"));
        y += 49;
        DrawNavItem(ctx, 1, new UiRect(x, y, width, 43), SVCLang.Get("tab-channels"), SVCLang.Get("ui-nav-channels-hint"));
        y += 49;
        if (hasServerControlProvider())
        {
            DrawNavItem(ctx, 2, new UiRect(x, y, width, 43), SVCLang.Get("tab-admin"), SVCLang.Get("ui-nav-admin-hint"));
            y += 49;
        }
        DrawNavItem(ctx, 3, new UiRect(x, y, width, 43), SVCLang.Get("tab-status"), SVCLang.Get("ui-nav-status-hint"));

        double quickY = dialogHeight - FooterHeight - 112;
        DrawText(ctx, SVCLang.Get("ui-quick-state"), x, quickY, 11, MutedTextColor, true);
        quickY += 14;
        DrawStatusPill(ctx, new UiRect(x, quickY, width, 29), localMutedProvider() ? SVCLang.Get("state-muted") : SVCLang.Get("state-ready"), localMutedProvider() ? DangerColor : AccentColor);
        quickY += 34;
        DrawStatusPill(ctx, new UiRect(x, quickY, width, 29), globalMutedProvider() ? SVCLang.Get("label-deafened") : SVCLang.Get("ui-listening"), globalMutedProvider() ? DangerColor : AccentColor);
    }

    private void DrawNavItem(Context ctx, int page, UiRect rect, string label, string tooltip)
    {
        bool selected = selectedPage == page;
        if (selected)
        {
            FillRound(ctx, new UiRect(rect.X, rect.Y + 8, 2, rect.Height - 16), 1, AccentColor);
        }
        DrawTextVerticallyCentered(ctx, label, new UiRect(rect.X + 14, rect.Y, rect.Width - 20, rect.Height), 16, selected ? TextColor : MutedTextColor, selected);
        AddHit($"nav-{page}", rect, tooltip, () =>
        {
            selectedPage = page;
            selectorPopup = null;
            Redraw();
        });
    }

    private void DrawPage(Context ctx)
    {
        if (selectedPage == 2 && !hasServerControlProvider())
        {
            selectedPage = 0;
        }
        switch (selectedPage)
        {
            case 1:
                DrawChannelsPage(ctx);
                break;
            case 2:
                DrawAdminPage(ctx);
                break;
            case 3:
                DrawStatusPage(ctx);
                break;
            default:
                DrawAudioPage(ctx);
                break;
        }
    }

    private void DrawAudioPage(Context ctx)
    {
        ContentFrame(out double x, out double y, out double width, out double bottom);
        DrawPageHeading(ctx, ref y, SVCLang.Get("ui-audio-title"), SVCLang.Get("ui-audio-subtitle"));
        double columnGap = 18;
        double columnWidth = (width - columnGap) / 2;
        double leftY = y;
        double rightX = x + columnWidth + columnGap;
        double rightY = y;

        DrawSectionHeading(ctx, ref leftY, SVCLang.Get("ui-section-device"), x, columnWidth);
        string[] devices = GetInputDeviceValues();
        string[] deviceNames = GetInputDeviceNames(devices);
        int deviceIndex = GetSelectedInputDeviceIndex(devices);
        DrawSelector(ctx, "input-device", new UiRect(x, leftY, columnWidth, 38), SVCLang.Get("label-input-device"), devices, deviceNames, deviceIndex, SVCLang.Get("tooltip-input-device"), value => OnInputDeviceChanged(value, true));
        leftY += 50;

        DrawSectionHeading(ctx, ref leftY, SVCLang.Get("ui-section-levels"), x, columnWidth);
        DrawSliderRow(ctx, "output-volume", x, ref leftY, columnWidth, SVCLang.Get("label-output-volume"), (int)Math.Round(config.OutputVolume * 100), 0, 200, 5, "%", SVCLang.Get("tooltip-output-volume"), OnOutputVolumeChanged);
        DrawSliderRow(ctx, "mic-gain", x, ref leftY, columnWidth, SVCLang.Get("label-mic-gain"), (int)Math.Round(config.MicGain * 100), 10, 400, 5, "%", SVCLang.Get("tooltip-mic-gain"), OnMicGainChanged);
        DrawSliderRow(ctx, "noise-gate", x, ref leftY, columnWidth, SVCLang.Get("label-noise-gate"), (int)Math.Round(config.NoiseGate * 1000), 0, 200, 1, string.Empty, SVCLang.Get("tooltip-noise-gate"), OnNoiseGateChanged);

        DrawSectionHeading(ctx, ref leftY, SVCLang.Get("label-debug-recording"), x, columnWidth);
        double recordWidth = (columnWidth - 10) / 2;
        if (leftY + 36 <= bottom)
        {
            DrawButton(ctx, "record", new UiRect(x, leftY, recordWidth, 34), SVCLang.Get("button-record-3s"), SVCLang.Get("tooltip-record"), ToAction(OnDebugRecordClicked), ButtonTone.Secondary, icon: LucideIcon.Record);
            DrawButton(ctx, "play-recording", new UiRect(x + recordWidth + 10, leftY, recordWidth, 34), SVCLang.Get("button-play-recording"), SVCLang.Get("tooltip-play-recording"), ToAction(OnDebugPlayClicked), ButtonTone.Secondary, icon: LucideIcon.Play);
            DrawClippedText(ctx, Audio.VoiceProcessingCapabilities.BackendName, x, leftY + 53, columnWidth, 11, MutedTextColor);
        }

        DrawSectionHeading(ctx, ref rightY, SVCLang.Get("ui-section-processing"), rightX, columnWidth);
        DrawToggleCard(ctx, "noise-suppression", new UiRect(rightX, rightY, columnWidth, 43), "NS", Audio.VoiceProcessingCapabilities.NoiseSuppressionAvailable, config.EnableNoiseSuppression, SVCLang.Get("tooltip-noise-suppression"), OnNoiseSuppressionChanged);
        rightY += 49;
        DrawToggleCard(ctx, "echo-cancellation", new UiRect(rightX, rightY, columnWidth, 43), "AEC", Audio.VoiceProcessingCapabilities.EchoCancellationAvailable, config.EnableEchoCancellation, SVCLang.Get("tooltip-echo-cancellation"), OnEchoCancellationChanged);
        rightY += 49;
        DrawToggleCard(ctx, "adaptive-jitter", new UiRect(rightX, rightY, columnWidth, 43), SVCLang.Get("label-adaptive-jitter"), true, config.AdaptiveJitterBuffer, SVCLang.Get("tooltip-adaptive-jitter"), OnAdaptiveJitterChanged);
        rightY += 55;

        DrawSectionHeading(ctx, ref rightY, SVCLang.Get("ui-section-behavior"), rightX, columnWidth);
        double toggleGap = 8;
        double half = (columnWidth - toggleGap) / 2;
        DrawToggleRow(ctx, "local-mute", new UiRect(rightX, rightY, half, 38), SVCLang.Get("label-mic-muted"), localMutedProvider(), true, SVCLang.Get("tooltip-local-mute"), OnLocalMuteChanged);
        DrawToggleRow(ctx, "global-mute", new UiRect(rightX + half + toggleGap, rightY, half, 38), SVCLang.Get("label-deafened"), globalMutedProvider(), true, SVCLang.Get("tooltip-global-mute"), OnGlobalMuteChanged);
        rightY += 45;
        DrawToggleRow(ctx, "continuous", new UiRect(rightX, rightY, half, 38), SVCLang.Get("label-continuous-talk"), continuousTalkEnabledProvider(), continuousTalkAllowedProvider(), SVCLang.Get("tooltip-continuous-talk"), OnContinuousTalkChanged);
        DrawToggleRow(ctx, "mic-hud", new UiRect(rightX + half + toggleGap, rightY, half, 38), SVCLang.Get("label-show-mic-hud"), config.ShowMicrophoneHud, true, SVCLang.Get("tooltip-mic-hud"), OnShowMicrophoneHudChanged);
        rightY += 45;
        DrawToggleRow(ctx, "occlusion", new UiRect(rightX, rightY, half, 38), SVCLang.Get("label-occlusion"), config.EnableOcclusionEffects, !forceImmersiveProvider(), forceImmersiveProvider() ? SVCLang.Get("tooltip-occlusion-forced") : SVCLang.Get("tooltip-occlusion"), OnOcclusionChanged);
        DrawToggleRow(ctx, "performance", new UiRect(rightX + half + toggleGap, rightY, half, 38), SVCLang.Get("label-performance-mode"), config.PerformanceMode, true, SVCLang.Get("tooltip-performance"), OnPerformanceModeChanged);
    }

    private void DrawChannelsPage(Context ctx)
    {
        ContentFrame(out double x, out double y, out double width, out double bottom);
        DrawPageHeading(ctx, ref y, SVCLang.Get("ui-channels-title"), SVCLang.Get("ui-channels-subtitle"));
        VoiceSettingsChannelOption[] channels = channelOptionsProvider();
        string[] channelValues = channels.Length == 0 ? new[] { string.Empty } : channels.Select(option => option.Id).ToArray();
        string[] channelNames = channels.Length == 0
            ? new[] { SVCLang.Get("channel-none") }
            : channels.Select(option => $"{FormatChannelKind(option.Kind)}: {option.Name}").ToArray();
        int channelIndex = Math.Max(0, Array.IndexOf(channelValues, config.SelectedChannelId));
        bool hasSelectedChannel = channels.Length > 0 && channelIndex < channels.Length;
        VoiceSettingsChannelOption selectedChannel = hasSelectedChannel ? channels[channelIndex] : default;
        EnsureChannelNameDraft(hasSelectedChannel ? selectedChannel : null);
        string[] transmitValues = { "proximity", "channel", "both" };
        string[] transmitNames = { SVCLang.Get("transmit-proximity"), SVCLang.Get("transmit-channel"), SVCLang.Get("transmit-both") };
        int transmitIndex = config.TransmitTarget switch
        {
            VoiceTransmitTarget.SelectedChannel => 1,
            VoiceTransmitTarget.ProximityAndChannel => 2,
            _ => 0
        };
        double gap = 12;
        double half = (width - gap) / 2;
        DrawSelector(ctx, "channel", new UiRect(x, y, half, 38), SVCLang.Get("label-channel-select"), channelValues, channelNames, channelIndex, SVCLang.Get("tooltip-channel-select"), value => OnChannelChanged(value, true));
        DrawSelector(ctx, "transmit", new UiRect(x + half + gap, y, half, 38), SVCLang.Get("label-transmit-target"), transmitValues, transmitNames, transmitIndex, SVCLang.Get("tooltip-transmit-target"), value => OnTransmitTargetChanged(value, true));
        y += 52;
        bool canRenameChannel = hasSelectedChannel
            && !selectedChannel.ExternallyManaged
            && (selectedChannel.LocalRole == VoiceChannelRole.Owner || hasServerControlProvider());
        double renameButtonWidth = Math.Min(138, width * 0.24);
        DrawTextInput(
            ctx,
            "channel-name-input",
            new UiRect(x, y, width - renameButtonWidth - gap, 42),
            SVCLang.Get("label-channel-name"),
            channelNameDraft,
            SVCLang.Get("placeholder-channel-name"),
            SVCLang.Get("tooltip-channel-name"),
            value => channelNameDraft = value,
            canRenameChannel);
        DrawButton(
            ctx,
            "channel-rename",
            new UiRect(x + width - renameButtonWidth, y, renameButtonWidth, 42),
            SVCLang.Get("button-rename-channel"),
            SVCLang.Get("tooltip-rename-channel"),
            ToAction(OnRenameChannelClicked),
            ButtonTone.Primary,
            canRenameChannel && !string.IsNullOrWhiteSpace(channelNameDraft),
            LucideIcon.Check);
        y += 54;
        DrawSliderRow(ctx, "channel-volume", x, ref y, width, SVCLang.Get("label-channel-volume"), (int)Math.Round(config.ChannelOutputVolume * 100), 0, 200, 5, "%", SVCLang.Get("tooltip-channel-volume"), OnChannelVolumeChanged);

        DrawSectionHeading(ctx, ref y, SVCLang.Get("ui-section-invite"));
        FillRound(ctx, new UiRect(x, y, width, 58), 6, SurfaceColor);
        DrawWrappedText(ctx, squadStatusProvider(), x + 12, y + 21, width - 320, 30, 12, TextColor);
        DrawButton(ctx, "invite-accept", new UiRect(x + width - 294, y + 12, 90, 34), SVCLang.Get("button-accept-invite"), SVCLang.Get("tooltip-invite-accept"), ToAction(OnAcceptInviteClicked), ButtonTone.Positive, icon: LucideIcon.Check);
        DrawButton(ctx, "invite-decline", new UiRect(x + width - 196, y + 12, 86, 34), SVCLang.Get("button-decline-invite"), SVCLang.Get("tooltip-invite-decline"), ToAction(OnDeclineInviteClicked), ButtonTone.Secondary, icon: LucideIcon.Close);
        DrawIconButton(ctx, "invite-refresh", new UiRect(x + width - 98, y + 15, 28, 28), LucideIcon.Refresh, SVCLang.Get("tooltip-refresh"), ToAction(OnRefreshSquadClicked));
        DrawButton(ctx, "leave-squad", new UiRect(x + width - 62, y + 12, 50, 34), SVCLang.Get("ui-leave-short"), SVCLang.Get("tooltip-leave-squad"), ToAction(OnLeaveSquadClicked), ButtonTone.Danger);
        y += 70;

        DrawSectionHeading(ctx, ref y, SVCLang.Get("ui-section-player"));
        VoiceSettingsPlayerOption[] players = GetPlayers();
        string[] playerValues = players.Length == 0 ? new[] { string.Empty } : players.Select(player => player.Id).ToArray();
        string[] playerNames = players.Length == 0 ? new[] { SVCLang.Get("player-none") } : players.Select(player => player.Name).ToArray();
        int playerIndex = Math.Max(0, Array.IndexOf(playerValues, selectedPlayerUid));
        DrawSelector(ctx, "player", new UiRect(x, y, width * 0.38, 38), SVCLang.Get("ui-target-player"), playerValues, playerNames, playerIndex, SVCLang.Get("tooltip-player-select"), value => OnPlayerChanged(value, true));
        double sliderX = x + width * 0.40;
        DrawInlineSlider(ctx, "player-volume", new UiRect(sliderX, y, width * 0.43, 38), playerVolumeProvider(selectedPlayerUid), 0, 200, 5, "%", SVCLang.Get("tooltip-player-volume"), OnPlayerVolumeChanged);
        DrawCompactToggle(ctx, "player-mute", new UiRect(x + width - 68, y, 68, 38), config.MutedPlayerUids.Contains(selectedPlayerUid), !string.IsNullOrWhiteSpace(selectedPlayerUid), SVCLang.Get("tooltip-player-mute"), OnPlayerMuteChanged);
        y += 52;

        double panelHeight = Math.Max(120, bottom - y);
        double leftWidth = width * 0.45;
        DrawMembersPanel(ctx, new UiRect(x, y, leftWidth, panelHeight));
        DrawChannelActionsPanel(ctx, new UiRect(x + leftWidth + gap, y, width - leftWidth - gap, panelHeight), channels);
    }

    private void DrawAdminPage(Context ctx)
    {
        ContentFrame(out double x, out double y, out double width, out double bottom);
        DrawPageHeading(ctx, ref y, SVCLang.Get("ui-admin-title"), SVCLang.Get("ui-admin-subtitle"));
        VoiceSettingsPlayerOption[] players = GetPlayers();
        string[] values = players.Length == 0 ? new[] { string.Empty } : players.Select(player => player.Id).ToArray();
        string[] names = players.Length == 0 ? new[] { SVCLang.Get("player-none") } : players.Select(player => player.Name).ToArray();
        int playerIndex = Math.Max(0, Array.IndexOf(values, selectedPlayerUid));

        DrawSectionHeading(ctx, ref y, SVCLang.Get("ui-section-admin-target"));
        DrawSelector(ctx, "admin-player", new UiRect(x, y, width, 38), SVCLang.Get("ui-target-player"), values, names, playerIndex, SVCLang.Get("tooltip-admin-target"), value => OnPlayerChanged(value, true));
        y += 48;
        bool hasTarget = !string.IsNullOrWhiteSpace(selectedPlayerUid);

        DrawSectionHeading(ctx, ref y, SVCLang.Get("ui-section-temporary-actions"));
        double gap = 10;
        double half = (width - gap) / 2;
        DrawAdminAction(ctx, "admin-tempmute", new UiRect(x, y, half, 44), "tempmute", ButtonTone.Warning, hasTarget);
        DrawAdminAction(ctx, "admin-deafen", new UiRect(x + half + gap, y, half, 44), "deafen", ButtonTone.Warning, hasTarget);
        y += 50;

        DrawSectionHeading(ctx, ref y, SVCLang.Get("ui-section-persistent-actions"));
        DrawAdminAction(ctx, "admin-mute", new UiRect(x, y, half, 44), "adminmute", ButtonTone.Danger, hasTarget);
        DrawAdminAction(ctx, "admin-unmute", new UiRect(x + half + gap, y, half, 44), "adminunmute", ButtonTone.Secondary, hasTarget);
        y += 48;
        DrawAdminAction(ctx, "admin-block", new UiRect(x, y, half, 44), "forceblock", ButtonTone.Danger, hasTarget);
        DrawAdminAction(ctx, "admin-unblock", new UiRect(x + half + gap, y, half, 44), "unforceblock", ButtonTone.Secondary, hasTarget);
        y += 54;

        DrawSectionHeading(ctx, ref y, SVCLang.Get("ui-section-create-channel"));
        DrawTextInput(
            ctx,
            "create-channel-name-input",
            new UiRect(x, y, width, 42),
            SVCLang.Get("label-channel-name"),
            createChannelName,
            SVCLang.Get("placeholder-channel-name"),
            SVCLang.Get("tooltip-create-channel-name"),
            value => createChannelName = value,
            true);
        y += 48;
        string[] createActions = { "create-civilization", "create-command", "create-diplomacy", "create-staff", "create-broadcast", "create-radio" };
        double third = (width - gap * 2) / 3;
        bool canCreateChannel = !string.IsNullOrWhiteSpace(createChannelName);
        for (int i = 0; i < createActions.Length; i++)
        {
            int row = i / 3;
            int column = i % 3;
            string action = createActions[i];
            DrawButton(ctx, "admin-" + action, new UiRect(x + column * (third + gap), y + row * 45, third, 36), SVCLang.Get("channel-action-" + action), SVCLang.Get("tooltip-" + action), () => ExecuteAction(action), ButtonTone.Secondary, canCreateChannel, LucideIcon.Add);
        }
        y += 90;

        if (y + 48 <= bottom)
        {
            FillRound(ctx, new UiRect(x, y, width, 46), 6, SurfaceColor);
            DrawText(ctx, "!", x + 14, y + 28, 18, DangerColor, true);
            DrawWrappedText(ctx, SVCLang.Get("ui-admin-warning"), x + 40, y + 18, width - 52, 28, 12, TextColor);
        }
    }

    private void DrawStatusPage(Context ctx)
    {
        ContentFrame(out double x, out double y, out double width, out double bottom);
        DrawPageHeading(ctx, ref y, SVCLang.Get("ui-status-title"), SVCLang.Get("ui-status-subtitle"));
        DrawSectionHeading(ctx, ref y, SVCLang.Get("label-current-status"));
        double summaryHeight = Math.Min(190, (bottom - y) * 0.43);
        FillRound(ctx, new UiRect(x, y, width, summaryHeight), 6, SurfaceColor);
        DrawWrappedText(ctx, summaryProvider(), x + 14, y + 24, width - 28, summaryHeight - 24, 13, TextColor);
        y += summaryHeight + 14;
        DrawSectionHeading(ctx, ref y, SVCLang.Get("label-diagnostics"));
        double diagnosticHeight = Math.Max(120, bottom - y - 46);
        FillRound(ctx, new UiRect(x, y, width, diagnosticHeight), 6, new UiColor(0.045, 0.050, 0.055, 1));
        StrokeRound(ctx, new UiRect(x, y, width, diagnosticHeight), 6, BorderColor, 1);
        DrawWrappedText(ctx, diagnosticsProvider(), x + 14, y + 23, width - 28, diagnosticHeight - 24, 12, TextColor);
        y += diagnosticHeight + 10;
        DrawButton(ctx, "status-refresh", new UiRect(x, Math.Min(y, bottom - 34), 150, 34), SVCLang.Get("button-refresh-status"), SVCLang.Get("tooltip-refresh"), ToAction(OnRefreshSquadClicked), ButtonTone.Secondary, icon: LucideIcon.Refresh);
    }

    private void DrawMembersPanel(Context ctx, UiRect rect)
    {
        FillRound(ctx, rect, 6, SurfaceColor);
        DrawText(ctx, SVCLang.Get("label-channel-members"), rect.X + 12, rect.Y + 24, 14, TextColor, true);
        VoiceSettingsMemberPage page = memberPageProvider(config.SelectedChannelId, memberPage);
        int pageSize = Math.Max(1, page.PageSize);
        int pageCount = Math.Max(1, (page.TotalMembers + pageSize - 1) / pageSize);
        DrawTextRight(ctx, $"{page.Page + 1}/{pageCount}", rect.Right - 12, rect.Y + 23, 11, MutedTextColor);
        double itemY = rect.Y + 38;
        if (page.Members.Length == 0)
        {
            DrawText(ctx, SVCLang.Get("channel-members-loading"), rect.X + 12, itemY + 19, 12, MutedTextColor);
        }
        else
        {
            foreach (VoiceSettingsMemberOption member in page.Members.Take(8))
            {
                DrawText(ctx, Truncate(member.Name, 22), rect.X + 12, itemY + 18, 12, TextColor);
                DrawTextRight(ctx, FormatRole(member.Role), rect.Right - 12, itemY + 18, 11, RoleColor(member.Role));
                itemY += 23;
                if (itemY > rect.Bottom - 38)
                {
                    break;
                }
            }
        }
        double pagerY = rect.Bottom - 31;
        DrawIconButton(ctx, "members-prev", new UiRect(rect.X + 12, pagerY, 27, 23), LucideIcon.Previous, SVCLang.Get("tooltip-previous-page"), ToAction(OnPreviousMemberPage), memberPage > 0);
        DrawText(ctx, SVCLang.Get("ui-member-count", page.TotalMembers), rect.X + 48, pagerY + 17, 11, MutedTextColor);
        DrawIconButton(ctx, "members-next", new UiRect(rect.Right - 39, pagerY, 27, 23), LucideIcon.Next, SVCLang.Get("tooltip-next-page"), ToAction(OnNextMemberPage), page.Page + 1 < pageCount);
    }

    private void DrawChannelActionsPanel(Context ctx, UiRect rect, VoiceSettingsChannelOption[] channels)
    {
        FillRound(ctx, rect, 6, SurfaceColor);
        DrawText(ctx, SVCLang.Get("label-channel-manage"), rect.X + 12, rect.Y + 24, 14, TextColor, true);
        List<string> actions = BuildChannelActions(channels);
        if (!actions.Contains(selectedChannelAction, StringComparer.Ordinal))
        {
            selectedChannelAction = actions[0];
        }
        string[] names = actions.Select(action => SVCLang.Get("channel-action-" + action)).ToArray();
        int index = Math.Max(0, actions.IndexOf(selectedChannelAction));
        DrawSelector(ctx, "channel-action", new UiRect(rect.X + 12, rect.Y + 35, rect.Width - 24, 38), SVCLang.Get("ui-action-select"), actions.ToArray(), names, index, SVCLang.Get("tooltip-channel-action"), value => OnChannelActionChanged(value, true));
        DrawWrappedText(ctx, SVCLang.Get("tooltip-action-" + selectedChannelAction), rect.X + 12, rect.Y + 92, rect.Width - 24, Math.Max(34, rect.Height - 145), 12, MutedTextColor);
        ButtonTone tone = IsDangerousAction(selectedChannelAction) ? ButtonTone.Danger : ButtonTone.Primary;
        DrawButton(ctx, "channel-apply", new UiRect(rect.X + 12, rect.Bottom - 44, rect.Width - 24, 32), SVCLang.Get("button-apply"), SVCLang.Get("tooltip-apply-action"), ToAction(RequestSelectedChannelAction), tone, selectedChannelAction != "none", LucideIcon.Check);
    }

    private List<string> BuildChannelActions(VoiceSettingsChannelOption[] channelOptions)
    {
        bool hasPlayer = !string.IsNullOrWhiteSpace(selectedPlayerUid);
        int selectedIndex = Array.FindIndex(channelOptions, option => option.Id == config.SelectedChannelId);
        bool hasChannel = selectedIndex >= 0;
        VoiceChannelRole role = hasChannel ? channelOptions[selectedIndex].LocalRole : VoiceChannelRole.Banned;
        bool external = hasChannel && channelOptions[selectedIndex].ExternallyManaged;
        VoiceSettingsChannelOption[] squads = channelOptions.Where(option => option.Kind == VoiceChannelKind.Squad).ToArray();
        bool canInvite = squads.Length == 0 || squads.Any(option => option.LocalRole >= VoiceChannelRole.Officer);
        List<string> actions = new();
        if (canInvite && hasPlayer) actions.Add("invite");
        if (hasChannel && !external) actions.Add("leave");
        if (hasChannel && role >= VoiceChannelRole.Officer && hasPlayer)
        {
            actions.AddRange(new[] { "mute", "unmute", "ban", "unban" });
            if (!external) actions.Add("remove");
        }
        if (hasChannel && role == VoiceChannelRole.Owner)
        {
            if (hasPlayer && !external) actions.AddRange(new[] { "listenonly", "member", "officer" });
            actions.AddRange(new[] { "lock", "unlock" });
            if (!external) actions.Add("disband");
        }
        if (hasServerControlProvider() && hasChannel)
        {
            if (hasPlayer)
            {
                actions.AddRange(new[] { "mute", "unmute", "ban", "unban" });
                if (!external) actions.AddRange(new[] { "add", "remove", "listenonly", "member", "officer" });
            }
            actions.AddRange(new[] { "lock", "unlock" });
            if (!external) actions.Add("disband");
        }
        actions = actions.Distinct(StringComparer.Ordinal).ToList();
        if (actions.Count == 0) actions.Add("none");
        return actions;
    }

    private VoiceSettingsPlayerOption[] GetPlayers()
    {
        VoiceSettingsPlayerOption[] players = playerOptionsProvider();
        if (!players.Any(player => player.Id == selectedPlayerUid))
        {
            selectedPlayerUid = players.FirstOrDefault().Id ?? string.Empty;
        }
        return players;
    }

    private void DrawSelector(Context ctx, string id, UiRect rect, string label, string[] values, string[] names, int selectedIndex, string tooltip, Action<string> onSelected)
    {
        selectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, names.Length - 1));
        FillRound(ctx, rect, 5, SurfaceColor);
        StrokeRound(ctx, rect, 5, BorderColor, 1);
        DrawText(ctx, label.ToUpperInvariant(), rect.X + 10, rect.Y + 14, 10, MutedTextColor, true);
        DrawClippedText(ctx, names.ElementAtOrDefault(selectedIndex) ?? string.Empty, rect.X + 10, rect.Y + 32, rect.Width - 38, 13, TextColor);
        DrawLucideIcon(ctx, LucideIcon.Expand, rect.Right - 25, rect.Y + 12, 13, 0.72);
        AddHit(id, rect, tooltip, () =>
        {
            confirmationPopup = null;
            selectorPopup = new SelectorPopup(label, values, names, selectedIndex, onSelected);
            Redraw();
        });
    }

    private void DrawSelectorPopup(Context ctx, double width, double height)
    {
        SelectorPopup popup = selectorPopup!;
        hits.Clear();
        FillRect(ctx, new UiRect(10, 8, width - 20, height - 16), new UiColor(0, 0, 0, 0.62));
        double popupWidth = Math.Min(520, width - 80);
        double popupHeight = Math.Min(438, height - 110);
        UiRect panel = new((width - popupWidth) / 2, (height - popupHeight) / 2, popupWidth, popupHeight);
        FillRound(ctx, panel, 8, WindowColor);
        StrokeRound(ctx, panel, 8, BorderColor, 1.2);
        DrawText(ctx, popup.Title, panel.X + 20, panel.Y + 32, 18, TextColor, true);
        DrawIconButton(ctx, "selector-close", new UiRect(panel.Right - 42, panel.Y + 14, 26, 26), LucideIcon.Close, SVCLang.Get("tooltip-close"), () => selectorPopup = null);

        int pageCount = Math.Max(1, (popup.Values.Length + SelectorRows - 1) / SelectorRows);
        popup.Page = Math.Clamp(popup.Page, 0, pageCount - 1);
        int start = popup.Page * SelectorRows;
        double itemY = panel.Y + 54;
        for (int row = 0; row < SelectorRows; row++)
        {
            int index = start + row;
            if (index >= popup.Values.Length) break;
            UiRect item = new(panel.X + 16, itemY, panel.Width - 32, 36);
            bool selected = index == popup.SelectedIndex;
            if (selected)
            {
                FillRound(ctx, item, 5, SelectedSurfaceColor);
                FillRound(ctx, new UiRect(item.X, item.Y + 7, 2, item.Height - 14), 1, AccentColor);
            }
            DrawClippedText(ctx, popup.Names[index], item.X + 12, item.Y + 24, item.Width - 24, 13, selected ? TextColor : MutedTextColor, selected);
            int captured = index;
            AddHit($"selector-item-{index}", item, popup.Names[index], () =>
            {
                popup.OnSelected(popup.Values[captured]);
                selectorPopup = null;
                Redraw();
            });
            itemY += 39;
        }
        double pagerY = panel.Bottom - 42;
        DrawIconButton(ctx, "selector-prev", new UiRect(panel.X + 18, pagerY, 30, 26), LucideIcon.Previous, SVCLang.Get("tooltip-previous-page"), () => { popup.Page--; Redraw(); }, popup.Page > 0);
        DrawTextCentered(ctx, $"{popup.Page + 1} / {pageCount}", panel.X + panel.Width / 2, pagerY + 19, 12, MutedTextColor);
        DrawIconButton(ctx, "selector-next", new UiRect(panel.Right - 48, pagerY, 30, 26), LucideIcon.Next, SVCLang.Get("tooltip-next-page"), () => { popup.Page++; Redraw(); }, popup.Page + 1 < pageCount);
    }

    private void DrawConfirmationPopup(Context ctx, double width, double height)
    {
        ConfirmationPopup popup = confirmationPopup!;
        hits.Clear();
        FillRect(ctx, new UiRect(10, 8, width - 20, height - 16), new UiColor(0, 0, 0, 0.68));
        double panelWidth = Math.Min(500, width - 80);
        UiRect panel = new((width - panelWidth) / 2, (height - 260) / 2, panelWidth, 260);
        FillRound(ctx, panel, 8, WindowColor);
        StrokeRound(ctx, panel, 8, DangerColor, 1.2);
        DrawText(ctx, SVCLang.Get("ui-confirm-title"), panel.X + 22, panel.Y + 36, 19, DangerColor, true);
        DrawIconButton(ctx, "confirm-close", new UiRect(panel.Right - 44, panel.Y + 16, 26, 26), LucideIcon.Close, SVCLang.Get("tooltip-close"), () => confirmationPopup = null);
        DrawWrappedText(ctx, popup.Description, panel.X + 22, panel.Y + 72, panel.Width - 44, 70, 13, TextColor);
        FillRound(ctx, new UiRect(panel.X + 22, panel.Y + 144, panel.Width - 44, 42), 5, SurfaceColor);
        DrawClippedText(ctx, popup.Context, panel.X + 34, panel.Y + 171, panel.Width - 68, 13, TextColor, true);
        double buttonWidth = (panel.Width - 54) / 2;
        DrawButton(ctx, "confirm-cancel", new UiRect(panel.X + 22, panel.Bottom - 54, buttonWidth, 34), SVCLang.Get("ui-confirm-cancel"), SVCLang.Get("tooltip-confirm-cancel"), () => confirmationPopup = null, ButtonTone.Secondary, icon: LucideIcon.Close);
        DrawButton(ctx, "confirm-action", new UiRect(panel.X + 32 + buttonWidth, panel.Bottom - 54, buttonWidth, 34), SVCLang.Get("ui-confirm-action"), popup.Description, () =>
        {
            confirmationPopup = null;
            popup.Confirm();
        }, ButtonTone.Danger, icon: LucideIcon.Check);
    }

    private void DrawSliderRow(Context ctx, string id, double x, ref double y, double width, string label, int value, int min, int max, int step, string suffix, string tooltip, Func<int, bool> changed)
    {
        DrawText(ctx, label, x, y + 22, 13, TextColor);
        DrawInlineSlider(ctx, id, new UiRect(x + width * 0.34, y, width * 0.66, 36), value, min, max, step, suffix, tooltip, changed);
        y += 42;
    }

    private void DrawInlineSlider(Context ctx, string id, UiRect rect, int value, int min, int max, int step, string suffix, string tooltip, Func<int, bool> changed)
    {
        value = Math.Clamp(value, min, max);
        double trackLeft = rect.X + 8;
        double valueWidth = 58;
        double trackRight = rect.Right - valueWidth - 10;
        double trackY = rect.Y + rect.Height / 2;
        double ratio = max == min ? 0 : (value - min) / (double)(max - min);
        FillRound(ctx, new UiRect(trackLeft, trackY - 3, trackRight - trackLeft, 6), 3, BorderColor);
        FillRound(ctx, new UiRect(trackLeft, trackY - 3, Math.Max(4, (trackRight - trackLeft) * ratio), 6), 3, AccentColor);
        FillRound(ctx, new UiRect(trackLeft + (trackRight - trackLeft) * ratio - 7, trackY - 7, 14, 14), 7, TextColor);
        DrawTextRight(ctx, value + suffix, rect.Right - 2, trackY + 6, 12, TextColor, true);
        RegisterSliderHit(id, rect, tooltip, mouse =>
        {
            double normalized = Math.Clamp((mouse - trackLeft) / Math.Max(1, trackRight - trackLeft), 0, 1);
            int next = min + (int)Math.Round((max - min) * normalized / step) * step;
            changed(Math.Clamp(next, min, max));
        });
    }

    private void DrawToggleCard(Context ctx, string id, UiRect rect, string label, bool enabled, bool value, string tooltip, Action<bool> changed)
    {
        FillRound(ctx, rect, 6, enabled ? SurfaceColor : new UiColor(0.08, 0.085, 0.09, 1));
        StrokeRound(ctx, rect, 6, value && enabled ? AccentColor : BorderColor, 1);
        DrawText(ctx, label, rect.X + 12, rect.Y + 29, 13, enabled ? TextColor : MutedTextColor, true);
        DrawSwitch(ctx, new UiRect(rect.Right - 45, rect.Y + 14, 32, 19), value, enabled);
        AddHit(id, rect, tooltip, () => changed(!value), enabled);
    }

    private void DrawToggleRow(Context ctx, string id, UiRect rect, string label, bool value, bool enabled, string tooltip, Action<bool> changed)
    {
        FillRound(ctx, rect, 5, SurfaceColor);
        DrawClippedText(ctx, label, rect.X + 10, rect.Y + 25, rect.Width - 58, 13, enabled ? TextColor : MutedTextColor);
        DrawSwitch(ctx, new UiRect(rect.Right - 43, rect.Y + 10, 32, 19), value, enabled);
        AddHit(id, rect, tooltip, () => changed(!value), enabled);
    }

    private void DrawCompactToggle(Context ctx, string id, UiRect rect, bool value, bool enabled, string tooltip, Action<bool> changed)
    {
        FillRound(ctx, rect, 5, SurfaceColor);
        DrawSwitch(ctx, new UiRect(rect.X + (rect.Width - 32) / 2, rect.Y + 10, 32, 19), value, enabled);
        AddHit(id, rect, tooltip, () => changed(!value), enabled);
    }

    private void DrawTextInput(
        Context ctx,
        string id,
        UiRect rect,
        string label,
        string value,
        string placeholder,
        string tooltip,
        Action<string> changed,
        bool enabled)
    {
        bool active = activeTextInputId == id && enabled;
        FillRound(ctx, rect, 5, SurfaceColor);
        StrokeRound(ctx, rect, 5, active ? AccentColor : BorderColor, active ? 1.4 : 1);
        DrawText(ctx, label.ToUpperInvariant(), rect.X + 11, rect.Y + 14, 10, enabled ? MutedTextColor : new UiColor(0.42, 0.43, 0.44, 1), true);
        string display = string.IsNullOrEmpty(value) ? placeholder : value;
        UiColor valueColor = enabled && !string.IsNullOrEmpty(value) ? TextColor : MutedTextColor;
        DrawClippedText(ctx, display, rect.X + 11, rect.Y + 33, rect.Width - 22, 13, valueColor);
        if (active)
        {
            CairoFont font = SetupFont(ctx, 13, TextColor, false);
            string beforeCaret = value[..Math.Clamp(textCaretIndex, 0, value.Length)];
            double caretX = rect.X + 11 + Math.Min(rect.Width - 24, font.GetTextExtents(beforeCaret).Width);
            DrawLine(ctx, caretX, rect.Y + 19, caretX, rect.Bottom - 7, TextColor);
        }
        AddHit(id, rect, tooltip, () =>
        {
            activeTextInputId = id;
            textCaretIndex = value.Length;
            textSelectAll = false;
            changed(value);
        }, enabled);
    }

    private static void DrawSwitch(Context ctx, UiRect rect, bool value, bool enabled)
    {
        UiColor track = !enabled ? new UiColor(0.20, 0.21, 0.22, 1) : value ? AccentColor : BorderColor;
        FillRound(ctx, rect, rect.Height / 2, track);
        double knobX = value ? rect.Right - rect.Height + 2 : rect.X + 2;
        FillRound(ctx, new UiRect(knobX, rect.Y + 2, rect.Height - 4, rect.Height - 4), (rect.Height - 4) / 2, enabled ? TextColor : MutedTextColor);
    }

    private void DrawButton(Context ctx, string id, UiRect rect, string label, string tooltip, Action action, ButtonTone tone, bool enabled = true, LucideIcon? icon = null)
    {
        UiColor color = tone switch
        {
            ButtonTone.Primary => AccentDarkColor,
            ButtonTone.Positive => AccentDarkColor,
            ButtonTone.Warning => SurfaceColor,
            ButtonTone.Danger => new UiColor(0.43, 0.15, 0.16, 1),
            _ => SurfaceColor
        };
        if (!enabled) color = new UiColor(0.10, 0.105, 0.11, 1);
        FillRound(ctx, rect, 5, color);
        StrokeRound(ctx, rect, 5, enabled ? BorderColor : new UiColor(0.18, 0.18, 0.18, 1), 1);
        DrawButtonContent(ctx, rect, label, 12, enabled ? TextColor : MutedTextColor, icon, enabled ? 0.92 : 0.42);
        AddHit(id, rect, tooltip, action, enabled);
    }

    private void DrawIconButton(Context ctx, string id, UiRect rect, LucideIcon icon, string tooltip, Action action, bool enabled = true)
    {
        FillRound(ctx, rect, 5, SurfaceColor);
        StrokeRound(ctx, rect, 5, enabled ? BorderColor : new UiColor(0.18, 0.18, 0.18, 1), 1);
        double size = Math.Max(10, Math.Min(rect.Width, rect.Height) - 10);
        DrawLucideIcon(ctx, icon, rect.X + (rect.Width - size) / 2, rect.Y + (rect.Height - size) / 2, size, enabled ? 0.92 : 0.42);
        AddHit(id, rect, tooltip, action, enabled);
    }

    private void DrawAdminAction(Context ctx, string id, UiRect rect, string action, ButtonTone tone, bool enabled)
    {
        DrawButton(ctx, id, rect, SVCLang.Get("channel-action-" + action), SVCLang.Get("tooltip-action-" + action), () => ExecuteAction(action), tone, enabled);
    }

    private static Action ToAction(Func<bool> action)
    {
        return UiActionAdapter.FromBoolean(action);
    }

    private void ExecuteAction(string action)
    {
        if (IsDangerousAction(action))
        {
            RequestConfirmation(action, () => ExecuteActionNow(action));
            return;
        }
        ExecuteActionNow(action);
    }

    private void ExecuteActionNow(string action)
    {
        string previous = selectedChannelAction;
        selectedChannelAction = action;
        OnApplyChannelAction();
        selectedChannelAction = previous;
    }

    private bool RequestSelectedChannelAction()
    {
        if (IsDangerousAction(selectedChannelAction))
        {
            string action = selectedChannelAction;
            RequestConfirmation(action, () =>
            {
                string previous = selectedChannelAction;
                selectedChannelAction = action;
                OnApplyChannelAction();
                selectedChannelAction = previous;
            });
            return true;
        }
        return OnApplyChannelAction();
    }

    private void RequestConfirmation(string action, Action confirm)
    {
        selectorPopup = null;
        string target = string.IsNullOrWhiteSpace(selectedPlayerUid) ? SVCLang.Get("channel-none") : GetPlayers().FirstOrDefault(player => player.Id == selectedPlayerUid).Name ?? selectedPlayerUid;
        string context = VoiceSettingsActionPolicy.RequiresTarget(action)
            ? SVCLang.Get("ui-confirm-target", target)
            : SVCLang.Get("ui-confirm-channel", GetSelectedChannelName());
        confirmationPopup = new ConfirmationPopup(SVCLang.Get("tooltip-action-" + action), context, confirm);
        Redraw();
    }

    private string GetSelectedChannelName()
    {
        return channelOptionsProvider().FirstOrDefault(channel => channel.Id == config.SelectedChannelId).Name
            ?? SVCLang.Get("channel-none");
    }

    private void DrawPageHeading(Context ctx, ref double y, string title, string subtitle, UiColor? titleColor = null)
    {
        _ = subtitle;
        DrawText(ctx, title, SidebarWidth + 34, y + 23, 22, titleColor ?? TextColor, true);
        y += 42;
    }

    private void ContentFrame(out double x, out double y, out double width, out double bottom)
    {
        x = SidebarWidth + 34;
        y = HeaderHeight + 19;
        width = dialogWidth - x - 28;
        bottom = dialogHeight - FooterHeight - 19;
    }

    private void DrawSectionHeading(Context ctx, ref double y, string title)
    {
        ContentFrame(out double x, out _, out double width, out _);
        DrawSectionHeading(ctx, ref y, title, x, width);
    }

    private static void DrawSectionHeading(Context ctx, ref double y, string title, double x, double width)
    {
        DrawText(ctx, title.ToUpperInvariant(), x, y + 13, 11, MutedTextColor, true);
        DrawLine(ctx, x, y + 21, x + width, y + 21, new UiColor(0.22, 0.24, 0.25, 0.75));
        y += 26;
    }

    private void DrawStatusPill(Context ctx, UiRect rect, string label, UiColor color)
    {
        FillRound(ctx, rect, 5, SurfaceColor);
        FillRound(ctx, new UiRect(rect.X + 9, rect.Y + 10, 8, 8), 4, color);
        DrawClippedText(ctx, label, rect.X + 25, rect.Y + 20, rect.Width - 32, 12, TextColor, true);
    }

    private void AddHit(string id, UiRect rect, string tooltip, Action action, bool enabled = true)
    {
        hits.Add(new UiHit(id, rect, tooltip, action, null, enabled));
    }

    private void RegisterSliderHit(string id, UiRect rect, string tooltip, Action<double> action)
    {
        hits.Add(new UiHit(id, rect, tooltip, null, action, true));
    }

    private UiHit? FindHit(double x, double y)
    {
        for (int i = hits.Count - 1; i >= 0; i--)
        {
            if (hits[i].Rect.Contains(x, y)) return hits[i];
        }
        return null;
    }

    private void UpdateLocalMouse(MouseEvent args)
    {
        if (SingleComposer == null) return;
        double scale = Math.Max(0.5, GuiElement.scaled(1));
        mouseX = (int)Math.Round((args.X - SingleComposer.Bounds.renderX) / scale / canvasLayoutScale);
        mouseY = (int)Math.Round((args.Y - SingleComposer.Bounds.renderY) / scale / canvasLayoutScale);
    }

    private void Redraw()
    {
        SingleComposer?.GetCustomDraw("canvas")?.Redraw();
    }

    private void RedrawHint()
    {
        SingleComposer?.GetCustomDraw("hint")?.Redraw();
    }

    private static bool IsDangerousAction(string action)
    {
        return action is "mute" or "ban" or "remove" or "disband" or "tempmute" or "deafen" or "adminmute" or "forceblock";
    }

    private void OnInputDeviceChanged(string value, bool selected)
    {
        if (!selected) return;
        string nextDevice = value == DefaultInputDeviceValue ? string.Empty : value;
        if (config.InputDeviceName == nextDevice) return;
        config.InputDeviceName = nextDevice;
        ApplyConfig();
        reinitializeCapture();
    }

    private bool OnOutputVolumeChanged(int value) { config.OutputVolume = value / 100f; ApplyConfig(); return true; }
    private bool OnMicGainChanged(int value) { config.MicGain = value / 100f; ApplyConfig(); return true; }
    private bool OnNoiseGateChanged(int value) { config.NoiseGate = value / 1000f; ApplyConfig(); return true; }
    private void OnNoiseSuppressionChanged(bool enabled) { config.EnableNoiseSuppression = enabled && Audio.VoiceProcessingCapabilities.NoiseSuppressionAvailable; ApplyConfig(); }
    private void OnEchoCancellationChanged(bool enabled) { config.EnableEchoCancellation = enabled && Audio.VoiceProcessingCapabilities.EchoCancellationAvailable; ApplyConfig(); }
    private void OnAdaptiveJitterChanged(bool enabled) { config.AdaptiveJitterBuffer = enabled; ApplyConfig(); setAdaptiveJitter(enabled); }
    private void OnLocalMuteChanged(bool muted) { setLocalMuted(muted); RefreshStatusTexts(); }
    private void OnGlobalMuteChanged(bool muted) { setGlobalMuted(muted); RefreshStatusTexts(); }
    private void OnContinuousTalkChanged(bool enabled) { setContinuousTalk(enabled); RefreshStatusTexts(); }
    private void OnShowMicrophoneHudChanged(bool enabled) { config.ShowMicrophoneHud = enabled; config.ShowHudIndicator = enabled; ApplyConfig(); }
    private void OnOcclusionChanged(bool enabled) { if (!enabled && forceImmersiveProvider()) return; config.EnableOcclusionEffects = enabled; ApplyConfig(); }
    private void OnPerformanceModeChanged(bool enabled) { config.PerformanceMode = enabled; ApplyConfig(); }

    private void OnChannelChanged(string value, bool selected)
    {
        if (!selected || config.SelectedChannelId == value) return;
        config.SelectedChannelId = value;
        memberPage = 0;
        selectChannel(value);
        Redraw();
    }

    private void OnTransmitTargetChanged(string value, bool selected)
    {
        if (!selected) return;
        config.TransmitTarget = value switch
        {
            "channel" => VoiceTransmitTarget.SelectedChannel,
            "both" => VoiceTransmitTarget.ProximityAndChannel,
            _ => VoiceTransmitTarget.Proximity
        };
        ApplyConfig();
    }

    private bool OnChannelVolumeChanged(int value) { config.ChannelOutputVolume = value / 100f; ApplyConfig(); return true; }
    private void OnPlayerChanged(string value, bool selected) { if (!selected) return; selectedPlayerUid = value; Redraw(); }
    private bool OnPlayerVolumeChanged(int value) { setPlayerVolume(selectedPlayerUid, value); return true; }
    private void OnPlayerMuteChanged(bool muted) { setPlayerMuted(selectedPlayerUid, muted); Redraw(); }
    private void OnChannelActionChanged(string value, bool selected) { if (selected) { selectedChannelAction = value; Redraw(); } }
    private bool OnPreviousMemberPage() { memberPage = Math.Max(0, memberPage - 1); Redraw(); return true; }

    private bool OnNextMemberPage()
    {
        VoiceSettingsMemberPage current = memberPageProvider(config.SelectedChannelId, memberPage);
        int pageSize = Math.Max(1, current.PageSize);
        int maxPage = Math.Max(0, (current.TotalMembers + pageSize - 1) / pageSize - 1);
        memberPage = Math.Min(maxPage, memberPage + 1);
        Redraw();
        return true;
    }

    private bool OnApplyChannelAction()
    {
        if (selectedChannelAction == "none") return true;
        if (VoiceSettingsActionPolicy.RequiresTarget(selectedChannelAction) && string.IsNullOrWhiteSpace(selectedPlayerUid))
        {
            capi.ShowChatMessage(SVCLang.Get("chat-channel-action-requires-player"));
            return true;
        }
        if (VoiceSettingsActionPolicy.RequiresChannel(selectedChannelAction) && string.IsNullOrWhiteSpace(config.SelectedChannelId))
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
        string commandName = selectedChannelAction.StartsWith("create-", StringComparison.Ordinal)
            ? createChannelName.Trim()
            : string.Empty;
        manageChannel(action, config.SelectedChannelId, selectedPlayerUid, commandName, role);
        if (selectedChannelAction.StartsWith("create-", StringComparison.Ordinal))
        {
            createChannelName = string.Empty;
            activeTextInputId = string.Empty;
        }
        return true;
    }

    private bool OnRenameChannelClicked()
    {
        string name = channelNameDraft.Trim();
        if (string.IsNullOrEmpty(config.SelectedChannelId) || string.IsNullOrWhiteSpace(name))
        {
            return true;
        }
        manageChannel("rename", config.SelectedChannelId, string.Empty, name, VoiceChannelRole.Member);
        activeTextInputId = string.Empty;
        textSelectAll = false;
        return true;
    }

    private bool OnDebugRecordClicked() => startDebugRecording();
    private bool OnDebugPlayClicked() => playDebugRecording();
    private bool OnLeaveSquadClicked() => leaveSquad();
    private bool OnDisbandSquadClicked() => disbandSquad();
    private bool OnRefreshSquadClicked() { requestSquadStatus(); Redraw(); return true; }
    private bool OnAcceptInviteClicked() => acceptInvite();
    private bool OnDeclineInviteClicked() => declineInvite();

    private void ApplyConfig()
    {
        saveConfig();
        refreshHud();
        Redraw();
    }

    public void RefreshStatusTexts() => Redraw();
    public void RefreshChannelData()
    {
        selectorPopup = null;
        confirmationPopup = null;
        if (activeTextInputId != "channel-name-input")
        {
            channelNameDraftChannelId = string.Empty;
        }
        Redraw();
    }
    public void RefreshConfiguration() { selectorPopup = null; confirmationPopup = null; if (IsOpened()) Compose(); }

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

    private void EnsureChannelNameDraft(VoiceSettingsChannelOption? channel)
    {
        string channelId = channel?.Id ?? string.Empty;
        if (channelNameDraftChannelId == channelId)
        {
            return;
        }
        channelNameDraftChannelId = channelId;
        channelNameDraft = channel?.Name ?? string.Empty;
        if (activeTextInputId == "channel-name-input")
        {
            activeTextInputId = string.Empty;
        }
        textCaretIndex = channelNameDraft.Length;
        textSelectAll = false;
    }

    private string GetActiveText()
    {
        return activeTextInputId switch
        {
            "channel-name-input" => channelNameDraft,
            "create-channel-name-input" => createChannelName,
            _ => string.Empty
        };
    }

    private void SetActiveText(string value, int? caretIndex = null)
    {
        string sanitized = new(value.Where(character => !char.IsControl(character)).ToArray());
        if (sanitized.Length > VoiceProtocol.MaxControlStringLength)
        {
            sanitized = sanitized[..VoiceProtocol.MaxControlStringLength];
        }
        switch (activeTextInputId)
        {
            case "channel-name-input":
                channelNameDraft = sanitized;
                break;
            case "create-channel-name-input":
                createChannelName = sanitized;
                break;
            default:
                return;
        }
        textCaretIndex = Math.Clamp(caretIndex ?? sanitized.Length, 0, sanitized.Length);
        textSelectAll = false;
        Redraw();
    }

    private void InsertActiveText(string inserted)
    {
        string current = GetActiveText();
        if (textSelectAll)
        {
            current = string.Empty;
            textCaretIndex = 0;
        }
        string sanitized = new(inserted.Where(character => !char.IsControl(character)).ToArray());
        int room = Math.Max(0, VoiceProtocol.MaxControlStringLength - current.Length);
        if (sanitized.Length > room)
        {
            sanitized = sanitized[..room];
        }
        int caret = Math.Clamp(textCaretIndex, 0, current.Length);
        SetActiveText(current.Insert(caret, sanitized), caret + sanitized.Length);
    }

    private static UiColor RoleColor(VoiceChannelRole role)
    {
        return role switch
        {
            VoiceChannelRole.Owner => TextColor,
            VoiceChannelRole.Officer => AccentColor,
            VoiceChannelRole.Member => TextColor,
            VoiceChannelRole.ListenOnly => MutedTextColor,
            _ => DangerColor
        };
    }

    private static string Truncate(string value, int maximumLength) => value.Length <= maximumLength ? value : value[..Math.Max(1, maximumLength - 3)] + "...";

    private string[] GetInputDeviceValues()
    {
        List<string> values = new() { DefaultInputDeviceValue };
        try
        {
            foreach (string device in ALC.GetString(AlcGetStringList.CaptureDeviceSpecifier))
            {
                if (!string.IsNullOrWhiteSpace(device) && !values.Contains(device)) values.Add(device);
            }
        }
        catch (Exception ex)
        {
            capi.Logger.Warning("SimpleVoiceChat: failed enumerating capture devices: {0}", ex.Message);
        }
        if (!string.IsNullOrWhiteSpace(config.InputDeviceName) && !values.Contains(config.InputDeviceName)) values.Add(config.InputDeviceName);
        return values.ToArray();
    }

    private static string[] GetInputDeviceNames(string[] values) => values.Select(value => value == DefaultInputDeviceValue ? SVCLang.Get("default-microphone") : value).ToArray();

    private int GetSelectedInputDeviceIndex(string[] values)
    {
        string current = string.IsNullOrWhiteSpace(config.InputDeviceName) ? DefaultInputDeviceValue : config.InputDeviceName;
        int index = Array.IndexOf(values, current);
        return index >= 0 ? index : 0;
    }

    private static void FillRect(Context ctx, UiRect rect, UiColor color)
    {
        ctx.SetSourceRGBA(color.R, color.G, color.B, color.A);
        ctx.Rectangle(rect.X, rect.Y, rect.Width, rect.Height);
        ctx.Fill();
    }

    private static void FillRound(Context ctx, UiRect rect, double radius, UiColor color)
    {
        ctx.SetSourceRGBA(color.R, color.G, color.B, color.A);
        GuiElement.RoundRectangle(ctx, rect.X, rect.Y, rect.Width, rect.Height, radius);
        ctx.Fill();
    }

    private static void StrokeRound(Context ctx, UiRect rect, double radius, UiColor color, double lineWidth)
    {
        ctx.SetSourceRGBA(color.R, color.G, color.B, color.A);
        ctx.LineWidth = lineWidth;
        GuiElement.RoundRectangle(ctx, rect.X + 0.5, rect.Y + 0.5, rect.Width - 1, rect.Height - 1, radius);
        ctx.Stroke();
    }

    private static void DrawLine(Context ctx, double x1, double y1, double x2, double y2, UiColor color)
    {
        ctx.SetSourceRGBA(color.R, color.G, color.B, color.A);
        ctx.LineWidth = 1;
        ctx.MoveTo(x1, y1 + 0.5);
        ctx.LineTo(x2, y2 + 0.5);
        ctx.Stroke();
    }

    private static CairoFont SetupFont(Context ctx, double size, UiColor color, bool bold)
    {
        CairoFont font = (bold ? CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold) : CairoFont.WhiteSmallText()).WithFontSize((float)size);
        font.WithColor(new[] { color.R, color.G, color.B, color.A }).SetupContext(ctx);
        ctx.SetSourceRGBA(color.R, color.G, color.B, color.A);
        return font;
    }

    private static void DrawText(Context ctx, string text, double x, double y, double size, UiColor color, bool bold = false)
    {
        SetupFont(ctx, size, color, bold);
        ctx.MoveTo(x, y);
        ctx.ShowText(text ?? string.Empty);
    }

    private static void DrawTextVerticallyCentered(Context ctx, string text, UiRect rect, double size, UiColor color, bool bold = false)
    {
        CairoFont font = SetupFont(ctx, size, color, bold);
        string fitted = FitText(ctx, text ?? string.Empty, rect.Width);
        TextExtents extents = font.GetTextExtents(fitted);
        FontExtents fontExtents = font.GetFontExtents();
        double x = rect.X - extents.XBearing;
        double y = rect.Y + (rect.Height - fontExtents.Height) / 2 + fontExtents.Ascent;
        ctx.MoveTo(x, y);
        ctx.ShowText(fitted);
    }

    private static void DrawTextRight(Context ctx, string text, double right, double y, double size, UiColor color, bool bold = false)
    {
        SetupFont(ctx, size, color, bold);
        TextExtents extents = ctx.TextExtents(text ?? string.Empty);
        ctx.MoveTo(right - extents.Width, y);
        ctx.ShowText(text ?? string.Empty);
    }

    private static void DrawTextCentered(Context ctx, string text, double center, double y, double size, UiColor color, bool bold = false)
    {
        SetupFont(ctx, size, color, bold);
        TextExtents extents = ctx.TextExtents(text ?? string.Empty);
        ctx.MoveTo(center - extents.Width / 2, y);
        ctx.ShowText(text ?? string.Empty);
    }

    private static void DrawClippedText(Context ctx, string text, double x, double y, double maxWidth, double size, UiColor color, bool bold = false)
    {
        SetupFont(ctx, size, color, bold);
        string fitted = FitText(ctx, text ?? string.Empty, maxWidth);
        ctx.MoveTo(x, y);
        ctx.ShowText(fitted);
    }

    private static void DrawClippedTextCentered(Context ctx, string text, UiRect rect, double size, UiColor color, bool bold = false)
    {
        CairoFont font = SetupFont(ctx, size, color, bold);
        string fitted = FitText(ctx, text ?? string.Empty, rect.Width - 14);
        TextExtents extents = ctx.TextExtents(fitted);
        FontExtents fontExtents = font.GetFontExtents();
        double x = rect.X + (rect.Width - extents.Width) / 2 - extents.XBearing;
        double y = rect.Y + (rect.Height - fontExtents.Height) / 2 + fontExtents.Ascent;
        ctx.MoveTo(x, y);
        ctx.ShowText(fitted);
    }

    private void DrawButtonContent(Context ctx, UiRect rect, string text, double size, UiColor color, LucideIcon? icon, double iconAlpha)
    {
        CairoFont font = SetupFont(ctx, size, color, true);
        double iconSize = icon.HasValue ? 15 : 0;
        double gap = icon.HasValue ? 6 : 0;
        string fitted = FitText(ctx, text ?? string.Empty, Math.Max(1, rect.Width - 14 - iconSize - gap));
        TextExtents extents = ctx.TextExtents(fitted);
        FontExtents fontExtents = font.GetFontExtents();
        double groupWidth = iconSize + gap + extents.Width;
        double groupX = rect.X + (rect.Width - groupWidth) / 2;
        if (icon is LucideIcon iconValue)
        {
            DrawLucideIcon(ctx, iconValue, groupX, rect.Y + (rect.Height - iconSize) / 2, iconSize, iconAlpha);
        }
        double textX = groupX + iconSize + gap - extents.XBearing;
        double textY = rect.Y + (rect.Height - fontExtents.Height) / 2 + fontExtents.Ascent;
        ctx.MoveTo(textX, textY);
        ctx.ShowText(fitted);
    }

    private void DrawLucideIcon(Context ctx, LucideIcon icon, double x, double y, double size, double alpha)
    {
        if (currentSurface != null && lucideIcons.Draw(currentSurface, icon, x, y, size, currentIconScale, alpha))
        {
            return;
        }
        DrawLucideFallback(ctx, icon, x, y, size, alpha);
    }

    private static void DrawLucideFallback(Context ctx, LucideIcon icon, double x, double y, double size, double alpha)
    {
        double left = x + size * 0.2;
        double right = x + size * 0.8;
        double top = y + size * 0.2;
        double bottom = y + size * 0.8;
        double centerX = x + size / 2;
        double centerY = y + size / 2;
        ctx.Save();
        ctx.NewPath();
        ctx.SetSourceRGBA(TextColor.R, TextColor.G, TextColor.B, alpha);
        ctx.LineWidth = Math.Max(1.5, size / 12);
        switch (icon)
        {
            case LucideIcon.Close:
                ctx.MoveTo(left, top);
                ctx.LineTo(right, bottom);
                ctx.MoveTo(right, top);
                ctx.LineTo(left, bottom);
                ctx.Stroke();
                break;
            case LucideIcon.Previous:
                ctx.MoveTo(x + size * 0.64, top);
                ctx.LineTo(x + size * 0.36, centerY);
                ctx.LineTo(x + size * 0.64, bottom);
                ctx.Stroke();
                break;
            case LucideIcon.Next:
                ctx.MoveTo(x + size * 0.36, top);
                ctx.LineTo(x + size * 0.64, centerY);
                ctx.LineTo(x + size * 0.36, bottom);
                ctx.Stroke();
                break;
            case LucideIcon.Expand:
                ctx.MoveTo(left, y + size * 0.38);
                ctx.LineTo(centerX, y + size * 0.64);
                ctx.LineTo(right, y + size * 0.38);
                ctx.Stroke();
                break;
            case LucideIcon.Check:
                ctx.MoveTo(left, centerY);
                ctx.LineTo(x + size * 0.43, bottom);
                ctx.LineTo(right, top);
                ctx.Stroke();
                break;
            case LucideIcon.Record:
                ctx.Arc(centerX, centerY, size * 0.3, 0, Math.PI * 2);
                ctx.Stroke();
                ctx.Arc(centerX, centerY, size * 0.08, 0, Math.PI * 2);
                ctx.Fill();
                break;
            case LucideIcon.Play:
                ctx.MoveTo(x + size * 0.34, top);
                ctx.LineTo(right, centerY);
                ctx.LineTo(x + size * 0.34, bottom);
                ctx.ClosePath();
                ctx.Stroke();
                break;
            case LucideIcon.Add:
                ctx.MoveTo(centerX, top);
                ctx.LineTo(centerX, bottom);
                ctx.MoveTo(left, centerY);
                ctx.LineTo(right, centerY);
                ctx.Stroke();
                break;
            case LucideIcon.Refresh:
                ctx.Arc(centerX, centerY, size * 0.3, -Math.PI * 0.85, Math.PI * 0.62);
                ctx.Stroke();
                ctx.MoveTo(x + size * 0.78, y + size * 0.28);
                ctx.LineTo(x + size * 0.79, y + size * 0.53);
                ctx.LineTo(x + size * 0.57, y + size * 0.43);
                ctx.Stroke();
                break;
        }
        ctx.Restore();
    }

    private static string FitText(Context ctx, string text, double maxWidth)
    {
        if (ctx.TextExtents(text).Width <= maxWidth) return text;
        const string ellipsis = "...";
        int low = 0;
        int high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (ctx.TextExtents(text[..middle] + ellipsis).Width <= maxWidth) low = middle;
            else high = middle - 1;
        }
        return text[..low] + ellipsis;
    }

    private static void DrawWrappedText(Context ctx, string text, double x, double y, double width, double height, double size, UiColor color)
    {
        SetupFont(ctx, size, color, false);
        double lineHeight = size * 1.45;
        double cursorY = y;
        foreach (string paragraph in (text ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
        {
            string remaining = paragraph;
            if (remaining.Length == 0)
            {
                cursorY += lineHeight;
                continue;
            }
            while (remaining.Length > 0 && cursorY <= y + height)
            {
                int take = FindWrapLength(ctx, remaining, width);
                string line = remaining[..take].TrimEnd();
                ctx.MoveTo(x, cursorY);
                ctx.ShowText(line);
                remaining = remaining[take..].TrimStart();
                cursorY += lineHeight;
            }
            if (cursorY > y + height) break;
        }
    }

    private static int FindWrapLength(Context ctx, string text, double width)
    {
        if (ctx.TextExtents(text).Width <= width) return text.Length;
        int low = 1;
        int high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (ctx.TextExtents(text[..middle]).Width <= width) low = middle;
            else high = middle - 1;
        }
        int whitespace = text.LastIndexOf(' ', Math.Max(0, low - 1), low);
        return whitespace > 0 ? whitespace + 1 : Math.Max(1, low);
    }

    private sealed class UiHit
    {
        private readonly Action? action;
        private readonly Action<double>? sliderAction;

        public UiHit(string id, UiRect rect, string tooltip, Action? action, Action<double>? sliderAction, bool enabled)
        {
            Id = id;
            Rect = rect;
            Tooltip = tooltip;
            this.action = action;
            this.sliderAction = sliderAction;
            Enabled = enabled;
        }

        public string Id { get; }
        public UiRect Rect { get; }
        public string Tooltip { get; }
        public bool Enabled { get; }
        public bool IsSlider => sliderAction != null;
        public void Invoke(double mouseX) { if (sliderAction != null) sliderAction(mouseX); else action?.Invoke(); }
    }

    private sealed class SelectorPopup
    {
        public SelectorPopup(string title, string[] values, string[] names, int selectedIndex, Action<string> onSelected)
        {
            Title = title;
            Values = values;
            Names = names;
            SelectedIndex = selectedIndex;
            OnSelected = onSelected;
            Page = selectedIndex / SelectorRows;
        }

        public string Title { get; }
        public string[] Values { get; }
        public string[] Names { get; }
        public int SelectedIndex { get; }
        public Action<string> OnSelected { get; }
        public int Page { get; set; }
    }

    private sealed record ConfirmationPopup(string Description, string Context, Action Confirm);

    private readonly record struct UiRect(double X, double Y, double Width, double Height)
    {
        public double Right => X + Width;
        public double Bottom => Y + Height;
        public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
    }

    private readonly record struct UiColor(double R, double G, double B, double A);
    private enum ButtonTone { Primary, Secondary, Positive, Warning, Danger }
}
