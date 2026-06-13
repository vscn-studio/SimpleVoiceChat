using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SimpleVoiceChat.Gui;

public sealed class VoiceHud : HudElement
{
    private const int VolumeFrameCount = 40;
    private const double VolumeImageWidth = 242;
    private const double VolumeImageHeight = 28;

    private static readonly AssetLocation MicEnabledIcon = new("simplevoicechat", "gui/haojiao.png");
    private static readonly AssetLocation MicDisabledIcon = new("simplevoicechat", "gui/nohaojiao.png");
    private static readonly AssetLocation SquadSpeakingIcon = new("simplevoicechat", "gui/phone-volume-solid.png");

    private readonly Func<VoiceHudSnapshot> snapshotProvider;
    private readonly Func<bool> shouldShowProvider;
    private VoiceHudSnapshot lastSnapshot;
    private ImageSurface? micEnabledSurface;
    private ImageSurface? micDisabledSurface;
    private ImageSurface? squadSpeakingSurface;
    private readonly ImageSurface?[] volumeSurfaces = new ImageSurface?[VolumeFrameCount + 1];
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

        bool relayout = GetSquadLineCount(next) != GetSquadLineCount(lastSnapshot);
        lastSnapshot = next;
        if (relayout)
        {
            Compose();
            TryOpen();
            return;
        }

        SingleComposer?.GetCustomDraw("hud")?.Redraw();
    }

    private void Compose()
    {
        lastSnapshot = snapshotProvider();
        double width = 386;
        double height = CalculateHudHeight(lastSnapshot);
        ElementBounds bounds = ElementBounds.Fixed(EnumDialogArea.RightBottom, -18, -34 - height, width, height);
        ElementBounds drawBounds = ElementBounds.Fixed(0, 0, width, height);
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

        DrawIcon(ctx, snapshot.MicrophoneEnabled ? MicEnabledIcon : MicDisabledIcon, pad, GuiElement.scaled(20), iconSize);

        double textX = pad + iconSize + GuiElement.scaled(10);
        double statusY = GuiElement.scaled(22);
        DrawText(ctx, snapshot.Status, textX, statusY, 16, snapshot.MicrophoneEnabled ? new[] { 0.72, 1.0, 0.78, 1.0 } : new[] { 1.0, 0.62, 0.58, 1.0 }, bold: true);
        DrawText(ctx, snapshot.Mode, textX, statusY + GuiElement.scaled(18), 13, new[] { 0.98, 0.94, 0.82, 0.96 }, bold: true);
        DrawText(ctx, snapshot.Detail, textX, statusY + GuiElement.scaled(33), 12, new[] { 0.88, 0.91, 0.94, 0.94 }, bold: true);

        double barX = textX + GuiElement.scaled(32);
        double barY = GuiElement.scaled(72);
        DrawText(ctx, SVCLang.Get("hud-volume"), textX, barY + GuiElement.scaled(16), 11, new[] { 0.80, 0.84, 0.88, 0.92 }, bold: true);
        DrawVolumeImage(ctx, barX, barY - GuiElement.scaled(2), snapshot.VoiceLevel);

        DrawSquadMembers(ctx, snapshot, textX, GuiElement.scaled(112), width - textX - pad);
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

    private ImageSurface GetSquadSpeakingSurface()
    {
        return squadSpeakingSurface ??= GuiElement.getImageSurfaceFromAsset(capi, SquadSpeakingIcon);
    }

    private ImageSurface GetVolumeSurface(int frame)
    {
        frame = Math.Clamp(frame, 0, VolumeFrameCount);
        return volumeSurfaces[frame] ??= GuiElement.getImageSurfaceFromAsset(capi, new AssetLocation("simplevoicechat", $"gui/volume/volume-{frame:00}.png"));
    }

    private static void DrawText(Context ctx, string text, double x, double y, double fontSize, double[] color, bool bold)
    {
        CairoFont font = (bold ? CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold) : CairoFont.WhiteSmallText())
            .WithFontSize((float)fontSize);
        ctx.Save();
        font.WithColor(new[] { 0.0, 0.0, 0.0, 0.72 }).SetupContext(ctx);
        ctx.SetSourceRGBA(0, 0, 0, 0.72);
        ctx.MoveTo(x + GuiElement.scaled(1), y + GuiElement.scaled(1));
        ctx.ShowText(text);
        font.WithColor(color).SetupContext(ctx);
        ctx.SetSourceRGBA(color[0], color[1], color[2], color[3]);
        ctx.MoveTo(x, y);
        ctx.ShowText(text);
        ctx.Restore();
    }

    private void DrawVolumeImage(Context ctx, double x, double y, float level)
    {
        int frame = Math.Clamp((int)Math.Ceiling(Math.Clamp(level, 0f, 1f) * VolumeFrameCount), 0, VolumeFrameCount);
        DrawImage(ctx, GetVolumeSurface(frame), Math.Round(x), Math.Round(y), GuiElement.scaled(VolumeImageWidth), GuiElement.scaled(VolumeImageHeight));
    }

    private void DrawSquadMembers(Context ctx, VoiceHudSnapshot snapshot, double x, double y, double maxWidth)
    {
        if (snapshot.SquadMembers.Length == 0)
        {
            return;
        }

        double cursorX = x;
        double cursorY = y;
        double rowHeight = GuiElement.scaled(17);
        double gap = GuiElement.scaled(10);
        double iconSize = GuiElement.scaled(11);
        ImageSurface icon = GetSquadSpeakingSurface();

        foreach (VoiceHudSquadMember member in snapshot.SquadMembers)
        {
            string name = member.Name;
            double textWidth = MeasureText(ctx, name, 11, bold: true);
            double itemWidth = iconSize + GuiElement.scaled(4) + textWidth + gap;
            if (cursorX > x && cursorX + itemWidth > x + maxWidth)
            {
                cursorX = x;
                cursorY += rowHeight;
            }

            if (member.Speaking)
            {
                DrawImage(ctx, icon, cursorX, cursorY - GuiElement.scaled(10), iconSize, iconSize);
            }
            else
            {
                DrawSmallStatusDot(ctx, cursorX + iconSize * 0.5, cursorY - GuiElement.scaled(5), GuiElement.scaled(3), 0.38, 0.42, 0.45, 0.72);
            }

            DrawText(ctx, name, cursorX + iconSize + GuiElement.scaled(4), cursorY, 11, member.Speaking ? new[] { 0.62, 1.0, 0.68, 0.96 } : new[] { 0.68, 0.72, 0.76, 0.78 }, bold: true);
            cursorX += itemWidth;
        }
    }

    private static void DrawImage(Context ctx, ImageSurface icon, double x, double y, double width, double height)
    {
        ctx.Save();
        ctx.Translate(x, y);
        ctx.Scale(width / icon.Width, height / icon.Height);
        ctx.SetSourceSurface(icon, 0, 0);
        ctx.Rectangle(0, 0, icon.Width, icon.Height);
        ctx.Fill();
        ctx.Restore();
    }

    private static void DrawSmallStatusDot(Context ctx, double x, double y, double radius, double r, double g, double b, double a)
    {
        ctx.Save();
        ctx.SetSourceRGBA(r, g, b, a);
        ctx.Arc(x, y, radius, 0, Math.PI * 2);
        ctx.Fill();
        ctx.Restore();
    }

    private static double MeasureText(Context ctx, string text, double fontSize, bool bold)
    {
        CairoFont font = (bold ? CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold) : CairoFont.WhiteSmallText())
            .WithFontSize((float)fontSize);
        ctx.Save();
        font.SetupContext(ctx);
        TextExtents extents = font.GetTextExtents(text);
        ctx.Restore();
        return extents.Width;
    }

    private static double CalculateHudHeight(VoiceHudSnapshot snapshot)
    {
        return 110 + GetSquadLineCount(snapshot) * 17;
    }

    private static int GetSquadLineCount(VoiceHudSnapshot snapshot)
    {
        if (snapshot.SquadMembers.Length == 0)
        {
            return 0;
        }

        int lines = 1;
        int cursor = 0;
        foreach (VoiceHudSquadMember member in snapshot.SquadMembers)
        {
            int width = Math.Min(14, member.Name.Length) + 3;
            if (cursor > 0 && cursor + width > 24)
            {
                lines++;
                cursor = 0;
            }

            cursor += width;
        }

        return Math.Clamp(lines, 1, 4);
    }

    private static bool SnapshotEquals(VoiceHudSnapshot left, VoiceHudSnapshot right)
    {
        return left.MicrophoneEnabled == right.MicrophoneEnabled
            && left.Speaking == right.Speaking
            && Math.Abs(left.VoiceLevel - right.VoiceLevel) < 0.01f
            && left.Status == right.Status
            && left.Mode == right.Mode
            && left.Detail == right.Detail
            && SquadMembersEqual(left.SquadMembers, right.SquadMembers);
    }

    private static bool SquadMembersEqual(VoiceHudSquadMember[] left, VoiceHudSquadMember[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i].Name != right[i].Name || left[i].Speaking != right[i].Speaking)
            {
                return false;
            }
        }

        return true;
    }

    public override void Dispose()
    {
        micEnabledSurface?.Dispose();
        micDisabledSurface?.Dispose();
        squadSpeakingSurface?.Dispose();
        foreach (ImageSurface? surface in volumeSurfaces)
        {
            surface?.Dispose();
        }

        micEnabledSurface = null;
        micDisabledSurface = null;
        squadSpeakingSurface = null;
        base.Dispose();
    }
}
