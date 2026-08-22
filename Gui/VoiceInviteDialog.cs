using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using SimpleVoiceChat.Networking;

namespace SimpleVoiceChat.Gui;

internal static class VoiceInvitePolicy
{
    public const long ResponseTimeoutMilliseconds = VoiceConstants.ChannelInviteTimeoutMilliseconds;

    public static bool HasExpired(long nowMilliseconds, long deadlineMilliseconds)
    {
        return deadlineMilliseconds > 0 && nowMilliseconds >= deadlineMilliseconds;
    }
}

public sealed class VoiceInviteDialog : GuiDialog
{
    private const double Width = 360;
    private const double Height = 148;
    private const double ButtonY = 110;
    private const double ButtonHeight = 30;
    private const double DeclineX = 16;
    private const double AcceptX = 190;
    private const double ButtonWidth = 154;
    private const double RootX = -18;
    private const double RootY = 18;
    private const long AnimationMilliseconds = 280;
    private const long ButtonScrollPauseMilliseconds = 650;
    private const long ButtonScrollDurationMilliseconds = 1800;
    private const long ButtonScrollCycleMilliseconds = 3100;
    private const double ButtonScrollGap = 28;

    private readonly Func<long> nowProvider;
    private readonly Func<bool> accept;
    private readonly Func<bool> decline;
    private readonly Func<double> bottomReservedHeightProvider;
    private readonly Func<int> offsetYProvider;
    private readonly Func<string> acceptShortcutProvider;
    private readonly Func<string> declineShortcutProvider;

    private string inviterName = string.Empty;
    private string channelId = string.Empty;
    private string channelName = string.Empty;
    private int channelMemberCount;
    private int channelMaxMembers;
    private VoiceChannelVisibility channelVisibility;
    private bool channelLocked;
    private long deadlineMilliseconds;
    private long animationStartMilliseconds;
    private long lastCountdownSecond = -1;
    private string hoveredButton = string.Empty;
    private string lastShortcutText = string.Empty;
    private bool positionPreview;
    private long lastScrollRedrawMilliseconds;

    public VoiceInviteDialog(
        ICoreClientAPI capi,
        Func<long> nowProvider,
        Func<bool> accept,
        Func<bool> decline,
        Func<double> bottomReservedHeightProvider,
        Func<int>? offsetYProvider = null,
        Func<string>? acceptShortcutProvider = null,
        Func<string>? declineShortcutProvider = null)
        : base(capi)
    {
        this.nowProvider = nowProvider;
        this.accept = accept;
        this.decline = decline;
        this.bottomReservedHeightProvider = bottomReservedHeightProvider;
        this.offsetYProvider = offsetYProvider ?? (() => 85);
        this.acceptShortcutProvider = acceptShortcutProvider ?? (() => "Ctrl+F8");
        this.declineShortcutProvider = declineShortcutProvider ?? (() => "F7");
    }

    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => false;
    public override bool DisableMouseGrab => false;
    public override EnumDialogType DialogType => EnumDialogType.HUD;
    public override double DrawOrder => 0.96;
    public override double InputOrder => 1.2;
    public override bool CaptureAllInputs() => false;
    public override bool CaptureRawMouse() => false;

    public void ShowInvite(
        string inviter,
        string channelId,
        string channelName,
        int channelMemberCount,
        int channelMaxMembers,
        VoiceChannelVisibility channelVisibility,
        bool channelLocked,
        long deadline)
    {
        inviterName = inviter ?? string.Empty;
        this.channelId = channelId ?? string.Empty;
        this.channelName = channelName ?? string.Empty;
        this.channelMemberCount = Math.Max(0, channelMemberCount);
        this.channelMaxMembers = Math.Max(0, channelMaxMembers);
        this.channelVisibility = channelVisibility;
        this.channelLocked = channelLocked;
        deadlineMilliseconds = deadline;
        animationStartMilliseconds = nowProvider();
        lastCountdownSecond = -1;
        hoveredButton = string.Empty;
        Compose();
        if (!IsOpened())
        {
            TryOpen();
        }
    }

    public void Dismiss()
    {
        deadlineMilliseconds = 0;
        hoveredButton = string.Empty;
        TryClose();
    }

    public void RefreshInvite()
    {
        if (IsOpened())
        {
            Redraw();
        }
    }

    public void RefreshPosition()
    {
        if (!IsOpened()) return;
        Compose();
        TryOpen();
    }

    public void BeginPositionEditing()
    {
        positionPreview = !IsOpened();
        if (positionPreview)
        {
            inviterName = "Player";
            channelId = "preview";
            channelName = "Channel";
            channelMemberCount = 3;
            channelMaxMembers = 16;
            channelVisibility = VoiceChannelVisibility.Open;
            channelLocked = false;
            deadlineMilliseconds = nowProvider() + 30_000;
            animationStartMilliseconds = nowProvider() - AnimationMilliseconds;
        }
        Compose();
        TryOpen();
    }

    public void EndPositionEditing()
    {
        if (positionPreview)
        {
            Dismiss();
        }
        positionPreview = false;
    }

    public bool TryGetInteractionBounds(out double x, out double y, out double width, out double height)
    {
        if (!IsOpened() || SingleComposer == null)
        {
            x = y = width = height = 0;
            return false;
        }

        ElementBounds bounds = SingleComposer.Bounds;
        x = bounds.renderX;
        y = bounds.renderY;
        width = bounds.OuterWidth;
        height = bounds.OuterHeight;
        return width > 0 && height > 0;
    }

    public override void OnRenderGUI(float deltaTime)
    {
        _ = deltaTime;
        long remainingSeconds = Math.Max(0, (deadlineMilliseconds - nowProvider() + 999) / 1000);
        long now = nowProvider();
        bool animating = now - animationStartMilliseconds < AnimationMilliseconds;
        string shortcutText = acceptShortcutProvider() + "|" + declineShortcutProvider();
        if (remainingSeconds != lastCountdownSecond || shortcutText != lastShortcutText || animating)
        {
            lastCountdownSecond = remainingSeconds;
            lastShortcutText = shortcutText;
            Redraw();
        }
        else if (now - lastScrollRedrawMilliseconds >= 33)
        {
            lastScrollRedrawMilliseconds = now;
            Redraw();
        }

        base.OnRenderGUI(deltaTime);
    }

    public override bool OnEscapePressed()
    {
        return false;
    }

    public override void OnMouseMove(MouseEvent args)
    {
        string previous = hoveredButton;
        hoveredButton = GetButtonAt(args) ?? string.Empty;
        if (previous != hoveredButton)
        {
            Redraw();
        }
    }

    public override void OnMouseDown(MouseEvent args)
    {
        if (args.Button == EnumMouseButton.Left)
        {
            string? button = GetButtonAt(args);
            if (button == "accept")
            {
                accept();
                args.Handled = true;
                return;
            }
            if (button == "decline")
            {
                decline();
                args.Handled = true;
                return;
            }
        }
    }

    public override void OnMouseUp(MouseEvent args)
    {
        // This notification is non-modal. Let the game receive release events
        // outside the two explicit action buttons.
        if (args.Button == EnumMouseButton.Left && GetButtonAt(args) != null)
        {
            args.Handled = true;
        }
    }

    private void Compose()
    {
        _ = bottomReservedHeightProvider;
        // Invite notifications are anchored to the upper-left corner. The
        // configurable Y offset keeps the prompt clear of the game HUD.
        ElementBounds root = ElementBounds.Fixed(EnumDialogArea.LeftTop, -RootX, RootY + offsetYProvider(), Width, Height);
        ElementBounds drawBounds = ElementBounds.Fixed(0, 0, Width, Height);
        SingleComposer = capi.Gui.CreateCompo("simplevoicechat-invite", root)
            .AddDynamicCustomDraw(drawBounds, DrawInvite, "invite")
            .Compose();
    }

    private void DrawInvite(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        _ = surface;
        long now = nowProvider();
        double progress = Math.Clamp((now - animationStartMilliseconds) / (double)AnimationMilliseconds, 0d, 1d);
        progress = progress * progress * (3d - 2d * progress);
        double offsetX = (progress - 1d) * (Width + 24d);
        double alpha = progress * 0.34d;
        double scale = Math.Max(0.5, GuiElement.scaled(1));
        ctx.Save();
        ctx.Scale(scale, scale);
        ctx.Translate(offsetX, 0);

        ctx.SetSourceRGBA(0.02, 0.02, 0.02, alpha);
        ctx.Rectangle(0, 0, Width, Height);
        ctx.Fill();

        DrawText(ctx, SVCLang.Get("invite-title"), 16, 22, 14, 0.92, 0.95, 0.97, progress, bold: true);
        DrawText(ctx, FitText(ctx, SVCLang.Get("invite-message", inviterName), Width - 32, 12), 16, 44, 12, 0.92, 0.94, 0.97, progress, bold: true);
        string shownChannelName = string.IsNullOrWhiteSpace(channelName) ? SVCLang.Get("channel-none") : channelName;
        DrawText(ctx, FitText(ctx, SVCLang.Get("invite-channel", shownChannelName), Width - 32, 11), 16, 66, 11, 0.86, 0.9, 0.94, progress, bold: true);
        string memberText = channelMaxMembers > 0
            ? SVCLang.Get("invite-members", channelMemberCount, channelMaxMembers)
            : SVCLang.Get("invite-members-count", channelMemberCount);
        string visibilityText = SVCLang.Get(channelVisibility switch
        {
            VoiceChannelVisibility.Password => "channel-visibility-password",
            VoiceChannelVisibility.Hidden => "channel-visibility-hidden",
            _ => "channel-visibility-open"
        });
        if (channelLocked)
        {
            visibilityText += " / " + SVCLang.Get("channel-locked");
        }
        DrawText(ctx, FitText(ctx, memberText + "  " + visibilityText, Width - 32, 11), 16, 86, 11, 0.82, 0.86, 0.9, progress, bold: true);

        long remainingSeconds = Math.Max(0, (deadlineMilliseconds - nowProvider() + 999) / 1000);
        string declineText = SVCLang.Get("button-decline-invite-shortcut", declineShortcutProvider(), remainingSeconds);
        string acceptText = SVCLang.Get("button-accept-invite-shortcut", acceptShortcutProvider());
        DrawButton(ctx, "decline", DeclineX, ButtonY, declineText, hoveredButton == "decline", progress, now);
        DrawButton(ctx, "accept", AcceptX, ButtonY, acceptText, hoveredButton == "accept", progress, now);
        ctx.Restore();
    }

    private void DrawButton(Context ctx, string id, double x, double y, string text, bool hovered, double alpha, long now)
    {
        (double r, double g, double b) = id == "accept"
            ? (0.24, 0.38, 0.29)
            : (0.27, 0.29, 0.32);
        double fillAlpha = (hovered ? 0.36 : 0.22) * alpha / 0.34;
        FillRound(ctx, x, y, ButtonWidth, ButtonHeight, 0, r, g, b, fillAlpha);
        ctx.SetSourceRGBA(0.92, 0.95, 1.0, (hovered ? 0.95 : 0.88) * alpha / 0.34);
        ctx.LineWidth = GuiElement.scaled(1);
        ctx.Rectangle(x + 0.5, y + 0.5, ButtonWidth - 1, ButtonHeight - 1);
        ctx.Stroke();

        CairoFont font = CairoFont.WhiteSmallText().WithFontSize(11f);
        font.WithColor(new[] { 0.94, 0.95, 0.96, alpha / 0.34 }).SetupContext(ctx);
        FontExtents fontExtents = font.GetFontExtents();
        TextExtents extents = ctx.TextExtents(text);
        double availableWidth = ButtonWidth - 16;
        double baseline = y + (ButtonHeight - fontExtents.Height) / 2 + fontExtents.Ascent;
        double startX = x + (ButtonWidth - extents.Width) / 2 - extents.XBearing;
        ctx.Save();
        ctx.Rectangle(x + 8, y, availableWidth, ButtonHeight);
        ctx.Clip();
        if (extents.Width <= availableWidth)
        {
            ctx.MoveTo(startX, baseline);
            ctx.ShowText(text);
        }
        else
        {
            long elapsed = Math.Max(0, now - animationStartMilliseconds) % ButtonScrollCycleMilliseconds;
            double offset = elapsed <= ButtonScrollPauseMilliseconds
                ? 0
                : elapsed >= ButtonScrollPauseMilliseconds + ButtonScrollDurationMilliseconds
                    ? 0
                    : (elapsed - ButtonScrollPauseMilliseconds) / (double)ButtonScrollDurationMilliseconds
                        * (extents.Width + ButtonScrollGap);
            double textX = x + 8 - offset - extents.XBearing;
            ctx.MoveTo(textX, baseline);
            ctx.ShowText(text);
            double secondX = textX + extents.Width + ButtonScrollGap;
            if (secondX < x + ButtonWidth)
            {
                ctx.MoveTo(secondX, baseline);
                ctx.ShowText(text);
            }
        }
        ctx.Restore();
    }

    private string? GetButtonAt(MouseEvent args)
    {
        if (SingleComposer == null || !SingleComposer.Bounds.PointInside(args.X, args.Y))
        {
            return null;
        }
        double scale = Math.Max(0.5, GuiElement.scaled(1));
        double progress = Math.Clamp((nowProvider() - animationStartMilliseconds) / (double)AnimationMilliseconds, 0d, 1d);
        progress = progress * progress * (3d - 2d * progress);
        double x = (args.X - SingleComposer.Bounds.renderX - (progress - 1d) * (Width + 24d)) / scale;
        double y = (args.Y - SingleComposer.Bounds.renderY) / scale;
        if (Contains(x, y, DeclineX, ButtonY, ButtonWidth, ButtonHeight)) return "decline";
        if (Contains(x, y, AcceptX, ButtonY, ButtonWidth, ButtonHeight)) return "accept";
        return null;
    }

    private void Redraw()
    {
        SingleComposer?.GetCustomDraw("invite")?.Redraw();
    }

    private static bool Contains(double x, double y, double left, double top, double width, double height)
    {
        return x >= left && x <= left + width && y >= top && y <= top + height;
    }

    private static void FillRound(Context ctx, double x, double y, double width, double height, double radius, double r, double g, double b, double a)
    {
        ctx.SetSourceRGBA(r, g, b, a);
        if (radius <= 0)
        {
            ctx.Rectangle(x, y, width, height);
        }
        else
        {
            GuiElement.RoundRectangle(ctx, x, y, width, height, radius);
        }
        ctx.Fill();
    }

    private static void DrawText(Context ctx, string text, double x, double y, double size, double r, double g, double b, double a, bool bold)
    {
        CairoFont font = (bold ? CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold) : CairoFont.WhiteSmallText()).WithFontSize((float)size);
        font.WithColor(new[] { r, g, b, a }).SetupContext(ctx);
        ctx.MoveTo(x, y);
        ctx.ShowText(text);
    }

    private static string FitText(Context ctx, string text, double maxWidth, double fontSize)
    {
        CairoFont font = CairoFont.WhiteSmallText().WithFontSize((float)fontSize);
        font.SetupContext(ctx);
        if (ctx.TextExtents(text).Width <= maxWidth) return text;
        const string ellipsis = "...";
        int length = text.Length;
        while (length > 0 && ctx.TextExtents(text[..length] + ellipsis).Width > maxWidth)
        {
            length--;
        }
        return text[..length] + ellipsis;
    }
}
