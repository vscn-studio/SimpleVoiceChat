using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SimpleVoiceChat.Gui;

public sealed class VoiceHud : HudElement
{
    private static readonly AssetLocation MicEnabledIcon = new("simplevoicechat", "gui/haojiao.png");
    private static readonly AssetLocation MicDisabledIcon = new("simplevoicechat", "gui/nohaojiao.png");

    private readonly Func<VoiceHudSnapshot> snapshotProvider;
    private readonly Func<bool> shouldShowProvider;
    private VoiceHudSnapshot lastSnapshot;
    private ImageSurface? micEnabledSurface;
    private ImageSurface? micDisabledSurface;
    private long lastUpdateMs;

    public override double DrawOrder => 0.09;

    public VoiceHud(ICoreClientAPI capi, Func<VoiceHudSnapshot> snapshotProvider, Func<bool> shouldShowProvider)
        : base(capi)
    {
        this.snapshotProvider = snapshotProvider;
        this.shouldShowProvider = shouldShowProvider;
        Compose();
    }

    public override void OnOwnPlayerDataReceived()
    {
        Compose();
        if (shouldShowProvider())
        {
            TryOpen();
        }
    }

    public override void OnRenderGUI(float deltaTime)
    {
        if (capi.World.Player?.Entity != null && capi.ElapsedMilliseconds - lastUpdateMs > 80)
        {
            lastUpdateMs = capi.ElapsedMilliseconds;
            Refresh();
        }

        base.OnRenderGUI(deltaTime);
    }

    public void Refresh()
    {
        if (!shouldShowProvider())
        {
            TryClose();
            return;
        }

        if (!IsOpened())
        {
            TryOpen();
        }

        VoiceHudSnapshot next = snapshotProvider();
        if (SnapshotEquals(next, lastSnapshot))
        {
            return;
        }

        lastSnapshot = next;
        SingleComposer?.GetCustomDraw("hud")?.Redraw();
    }

    private void Compose()
    {
        lastSnapshot = snapshotProvider();
        ElementBounds bounds = ElementBounds.Fixed(EnumDialogArea.RightBottom, -18, -128, 230, 94);
        ElementBounds drawBounds = ElementBounds.Fixed(0, 0, 230, 94);
        SingleComposer = capi.Gui.CreateCompo("simplevoicechat-hud", bounds)
            .AddDynamicCustomDraw(drawBounds, DrawHud, "hud")
            .Compose();
    }

    private void DrawHud(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        VoiceHudSnapshot snapshot = lastSnapshot;
        double width = bounds.OuterWidth;
        double height = bounds.OuterHeight;
        double pad = GuiElement.scaled(8);
        double iconSize = GuiElement.scaled(54);

        ctx.SetSourceRGBA(0.02, 0.02, 0.02, 0.34);
        GuiElement.RoundRectangle(ctx, 0, 0, width, height, GuiElement.scaled(8));
        ctx.Fill();

        DrawIcon(ctx, snapshot.MicrophoneEnabled ? MicEnabledIcon : MicDisabledIcon, pad, (height - iconSize) / 2, iconSize);

        double textX = pad + iconSize + GuiElement.scaled(10);
        double statusY = GuiElement.scaled(22);
        DrawText(ctx, snapshot.Status, textX, statusY, 16, snapshot.MicrophoneEnabled ? new[] { 0.72, 1.0, 0.78, 1.0 } : new[] { 1.0, 0.62, 0.58, 1.0 }, bold: true);
        DrawText(ctx, snapshot.Mode, textX, statusY + GuiElement.scaled(18), 13, new[] { 0.98, 0.94, 0.82, 0.96 }, bold: true);
        DrawText(ctx, snapshot.Detail, textX, statusY + GuiElement.scaled(33), 12, new[] { 0.88, 0.91, 0.94, 0.94 }, bold: true);

        double barX = textX + GuiElement.scaled(32);
        double barY = height - GuiElement.scaled(18);
        double barWidth = width - barX - pad;
        DrawText(ctx, "音量", textX, barY + GuiElement.scaled(8), 11, new[] { 0.80, 0.84, 0.88, 0.92 }, bold: true);
        DrawLevelBlocks(ctx, barX, barY, barWidth, GuiElement.scaled(9), snapshot.VoiceLevel);
    }

    private void DrawIcon(Context ctx, AssetLocation icon, double x, double y, double size)
    {
        ImageSurface iconSurface = GetIconSurface(icon);
        ctx.Save();
        ctx.Translate(x, y);
        ctx.Scale(size / iconSurface.Width, size / iconSurface.Height);
        ctx.SetSourceSurface(iconSurface, 0, 0);
        ctx.Rectangle(0, 0, iconSurface.Width, iconSurface.Height);
        ctx.Fill();
        ctx.Restore();
    }

    private ImageSurface GetIconSurface(AssetLocation icon)
    {
        if (icon == MicEnabledIcon)
        {
            return micEnabledSurface ??= GuiElement.getImageSurfaceFromAsset(capi, MicEnabledIcon);
        }

        return micDisabledSurface ??= GuiElement.getImageSurfaceFromAsset(capi, MicDisabledIcon);
    }

    private static void DrawText(Context ctx, string text, double x, double y, double fontSize, double[] color, bool bold)
    {
        ctx.Save();
        ctx.SelectFontFace(GuiStyle.StandardFontName, FontSlant.Normal, bold ? FontWeight.Bold : FontWeight.Bold);
        ctx.SetFontSize(GuiElement.scaled(fontSize));
        ctx.SetSourceRGBA(0, 0, 0, 0.72);
        ctx.MoveTo(x + GuiElement.scaled(1), y + GuiElement.scaled(1));
        ctx.ShowText(text);
        ctx.SetSourceRGBA(color[0], color[1], color[2], color[3]);
        ctx.MoveTo(x, y);
        ctx.ShowText(text);
        ctx.Restore();
    }

    private static void DrawLevelBlocks(Context ctx, double x, double y, double width, double height, float level)
    {
        const int blockCount = 12;
        double gap = Math.Round(GuiElement.scaled(2));
        double blockWidth = Math.Floor(Math.Min(GuiElement.scaled(8), (width - gap * (blockCount - 1)) / blockCount));
        double blockHeight = Math.Round(height);
        double blockY = Math.Round(y);
        double clamped = Math.Clamp(level, 0f, 1f);
        int activeBlocks = (int)Math.Round(clamped * blockCount, MidpointRounding.AwayFromZero);

        for (int i = 0; i < blockCount; i++)
        {
            double px = Math.Round(x + i * (blockWidth + gap));
            bool active = i < activeBlocks;
            double ratio = (i + 1) / (double)blockCount;

            if (!active)
            {
                ctx.SetSourceRGBA(0.48, 0.50, 0.52, 0.46);
            }
            else if (ratio >= 0.75)
            {
                ctx.SetSourceRGBA(1.0, 0.24, 0.20, 0.92);
            }
            else
            {
                ctx.SetSourceRGBA(0.30, 0.95, 0.44, 0.92);
            }

            ctx.Rectangle(px, blockY, blockWidth, blockHeight);
            ctx.Fill();
        }
    }

    private static bool SnapshotEquals(VoiceHudSnapshot left, VoiceHudSnapshot right)
    {
        return left.MicrophoneEnabled == right.MicrophoneEnabled
            && left.Speaking == right.Speaking
            && Math.Abs(left.VoiceLevel - right.VoiceLevel) < 0.01f
            && left.Status == right.Status
            && left.Mode == right.Mode
            && left.Detail == right.Detail;
    }

    public override void Dispose()
    {
        micEnabledSurface?.Dispose();
        micDisabledSurface?.Dispose();
        micEnabledSurface = null;
        micDisabledSurface = null;
        base.Dispose();
    }
}
