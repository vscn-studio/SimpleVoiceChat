using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

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
    private const double Width = 300;
    private const double Height = 112;
    private const double ButtonY = 72;
    private const double ButtonHeight = 28;
    private const double DeclineX = 16;
    private const double AcceptX = 158;
    private const double ButtonWidth = 126;

    private readonly Func<long> nowProvider;
    private readonly Func<bool> accept;
    private readonly Func<bool> decline;
    private readonly Func<double> bottomReservedHeightProvider;

    private string inviterName = string.Empty;
    private long deadlineMilliseconds;
    private long lastCountdownSecond = -1;
    private string hoveredButton = string.Empty;

    public VoiceInviteDialog(
        ICoreClientAPI capi,
        Func<long> nowProvider,
        Func<bool> accept,
        Func<bool> decline,
        Func<double> bottomReservedHeightProvider)
        : base(capi)
    {
        this.nowProvider = nowProvider;
        this.accept = accept;
        this.decline = decline;
        this.bottomReservedHeightProvider = bottomReservedHeightProvider;
    }

    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;
    public override bool DisableMouseGrab => true;
    public override EnumDialogType DialogType => EnumDialogType.Dialog;
    public override double DrawOrder => 0.96;
    public override double InputOrder => 0.2;

    public void ShowInvite(string inviter, long deadline)
    {
        inviterName = inviter ?? string.Empty;
        deadlineMilliseconds = deadline;
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

    public override void OnRenderGUI(float deltaTime)
    {
        long remainingSeconds = Math.Max(0, (deadlineMilliseconds - nowProvider() + 999) / 1000);
        if (remainingSeconds != lastCountdownSecond)
        {
            lastCountdownSecond = remainingSeconds;
            Redraw();
        }

        base.OnRenderGUI(deltaTime);
    }

    public override bool OnEscapePressed()
    {
        decline();
        return true;
    }

    public override void OnMouseMove(MouseEvent args)
    {
        string previous = hoveredButton;
        hoveredButton = GetButtonAt(args) ?? string.Empty;
        if (previous != hoveredButton)
        {
            Redraw();
        }
        base.OnMouseMove(args);
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
        base.OnMouseDown(args);
    }

    private void Compose()
    {
        double bottomOffset = 30 + Math.Max(0, bottomReservedHeightProvider());
        ElementBounds root = ElementBounds.Fixed(EnumDialogArea.RightBottom, -18, -bottomOffset, Width, Height);
        ElementBounds drawBounds = ElementBounds.Fixed(0, 0, Width, Height);
        SingleComposer = capi.Gui.CreateCompo("simplevoicechat-invite", root)
            .AddDynamicCustomDraw(drawBounds, DrawInvite, "invite")
            .Compose();
    }

    private void DrawInvite(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        _ = surface;
        double scale = Math.Max(0.5, GuiElement.scaled(1));
        ctx.Save();
        ctx.Scale(scale, scale);

        FillRound(ctx, 0, 0, Width, Height, 4, 0.02, 0.02, 0.02, 0.78);
        StrokeRound(ctx, 0.5, 0.5, Width - 1, Height - 1, 4, 0.33, 0.38, 0.42, 0.75);

        DrawText(ctx, SVCLang.Get("invite-title"), 16, 21, 14, 0.92, 0.95, 0.97, 1, bold: true);
        DrawText(ctx, FitText(ctx, SVCLang.Get("invite-message", inviterName), Width - 32, 13), 16, 42, 13, 0.84, 0.87, 0.9, 1, bold: false);

        long remainingSeconds = Math.Max(0, (deadlineMilliseconds - nowProvider() + 999) / 1000);
        DrawText(ctx, SVCLang.Get("invite-auto-decline", remainingSeconds), 16, 62, 11, 0.68, 0.72, 0.76, 1, bold: false);
        DrawButton(ctx, "decline", DeclineX, ButtonY, SVCLang.Get("button-decline-invite"), hoveredButton == "decline");
        DrawButton(ctx, "accept", AcceptX, ButtonY, SVCLang.Get("button-accept-invite"), hoveredButton == "accept");
        ctx.Restore();
    }

    private void DrawButton(Context ctx, string id, double x, double y, string text, bool hovered)
    {
        (double r, double g, double b) = id == "accept"
            ? (0.24, 0.38, 0.29)
            : (0.27, 0.29, 0.32);
        if (hovered)
        {
            r += 0.08;
            g += 0.08;
            b += 0.08;
        }
        FillRound(ctx, x, y, ButtonWidth, ButtonHeight, 0, r, g, b, 0.98);
        StrokeRound(ctx, x + 0.5, y + 0.5, ButtonWidth - 1, ButtonHeight - 1, 0, 0.52, 0.57, 0.6, hovered ? 0.95 : 0.62);

        CairoFont font = CairoFont.WhiteSmallText().WithFontSize(12f);
        font.WithColor(new[] { 0.94, 0.95, 0.96, 1.0 }).SetupContext(ctx);
        string fitted = FitText(ctx, text, ButtonWidth - 16, 12);
        TextExtents extents = ctx.TextExtents(fitted);
        FontExtents fontExtents = font.GetFontExtents();
        ctx.MoveTo(x + (ButtonWidth - extents.Width) / 2 - extents.XBearing, y + (ButtonHeight - fontExtents.Height) / 2 + fontExtents.Ascent);
        ctx.ShowText(fitted);
    }

    private string? GetButtonAt(MouseEvent args)
    {
        if (SingleComposer == null || !SingleComposer.Bounds.PointInside(args.X, args.Y))
        {
            return null;
        }
        double scale = Math.Max(0.5, GuiElement.scaled(1));
        double x = (args.X - SingleComposer.Bounds.renderX) / scale;
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

    private static void StrokeRound(Context ctx, double x, double y, double width, double height, double radius, double r, double g, double b, double a)
    {
        ctx.SetSourceRGBA(r, g, b, a);
        ctx.LineWidth = 1;
        if (radius <= 0)
        {
            ctx.Rectangle(x, y, width, height);
        }
        else
        {
            GuiElement.RoundRectangle(ctx, x, y, width, height, radius);
        }
        ctx.Stroke();
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
