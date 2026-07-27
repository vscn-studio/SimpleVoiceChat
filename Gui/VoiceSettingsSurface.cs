using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SimpleVoiceChat.Gui;

internal enum DirectorSurfaceKind
{
    Window,
    Card,
    OpaqueWindow,
    DockedWindow
}

internal sealed class GuiElementDirectorSurface : GuiElement
{
    private readonly DirectorSurfaceKind kind;

    public GuiElementDirectorSurface(
        ICoreClientAPI capi,
        ElementBounds bounds,
        DirectorSurfaceKind kind)
        : base(capi, bounds)
    {
        this.kind = kind;
    }

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        _ = surface;
        Bounds.CalcWorldBounds();
        bool insetPercentualBounds = Bounds.horizontalSizing is
            ElementSizing.Percentual or ElementSizing.PercentualSubstractFixed
            || Bounds.verticalSizing is
                ElementSizing.Percentual or ElementSizing.PercentualSubstractFixed;
        double x = insetPercentualBounds ? Bounds.drawX : Bounds.bgDrawX;
        double y = insetPercentualBounds ? Bounds.drawY : Bounds.bgDrawY;
        double width = insetPercentualBounds
            ? Bounds.InnerWidth - Bounds.absPaddingX * 2d
            : Bounds.OuterWidth;
        double height = insetPercentualBounds
            ? Bounds.InnerHeight - Bounds.absPaddingY * 2d
            : Bounds.OuterHeight;
        if (width <= 1d || height <= 1d)
        {
            return;
        }

        context.Save();
        bool docked = kind == DirectorSurfaceKind.DockedWindow;
        if (docked)
        {
            context.Rectangle(x + 0.5d, y + 0.5d, width - 1d, height - 1d);
        }
        else
        {
            DirectorGuiTheme.RoundedRectangle(
                context,
                x + 0.5d,
                y + 0.5d,
                width - 1d,
                height - 1d,
                DirectorGuiTheme.ScaledCornerRadius);
        }
        bool window = kind is DirectorSurfaceKind.Window
            or DirectorSurfaceKind.OpaqueWindow
            or DirectorSurfaceKind.DockedWindow;
        context.SetSourceRGBA(
            window ? DirectorGuiTheme.SurfaceR : DirectorGuiTheme.RaisedR,
            window ? DirectorGuiTheme.SurfaceG : DirectorGuiTheme.RaisedG,
            window ? DirectorGuiTheme.SurfaceB : DirectorGuiTheme.RaisedB,
            kind == DirectorSurfaceKind.OpaqueWindow ? 0.985d
                : docked ? 0.68d
                : kind == DirectorSurfaceKind.Window ? 0.52d : 0.46d);
        context.FillPreserve();
        context.SetSourceRGBA(
            DirectorGuiTheme.BorderR,
            DirectorGuiTheme.BorderG,
            DirectorGuiTheme.BorderB,
            window ? 0.72d : 0.38d);
        context.LineWidth = 1d;
        context.Stroke();
        context.Restore();
    }
}

internal sealed class GuiElementDirectorStaticClip : GuiElement
{
    private readonly bool begin;

    public GuiElementDirectorStaticClip(
        ICoreClientAPI capi,
        ElementBounds bounds,
        bool begin)
        : base(capi, bounds)
    {
        this.begin = begin;
    }

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        _ = surface;
        if (!begin)
        {
            context.Restore();
            return;
        }

        Bounds.CalcWorldBounds();
        context.Save();
        context.Rectangle(Bounds.drawX, Bounds.drawY, Bounds.OuterWidth, Bounds.OuterHeight);
        context.Clip();
    }
}

internal static class DirectorGuiSurfaceExtensions
{
    public static GuiComposer AddDirectorWindowSurface(
        this GuiComposer composer,
        ElementBounds bounds,
        string? key = null)
        => AddDirectorSurface(composer, bounds, DirectorSurfaceKind.Window, key);

    public static GuiComposer AddDirectorCard(
        this GuiComposer composer,
        ElementBounds bounds,
        string? key = null)
        => AddDirectorSurface(composer, bounds, DirectorSurfaceKind.Card, key);

    public static GuiComposer AddDirectorOpaqueWindowSurface(
        this GuiComposer composer,
        ElementBounds bounds,
        string? key = null)
        => AddDirectorSurface(composer, bounds, DirectorSurfaceKind.OpaqueWindow, key);

    public static GuiComposer AddDirectorDockedSurface(
        this GuiComposer composer,
        ElementBounds bounds,
        string? key = null)
        => AddDirectorSurface(composer, bounds, DirectorSurfaceKind.DockedWindow, key);

    public static GuiComposer BeginDirectorStaticClip(
        this GuiComposer composer,
        ElementBounds bounds,
        string? key = null)
    {
        if (!composer.Composed)
        {
            composer.AddStaticElement(
                new GuiElementDirectorStaticClip(composer.Api, bounds, begin: true),
                key);
        }
        return composer;
    }

    public static GuiComposer EndDirectorStaticClip(
        this GuiComposer composer,
        string? key = null)
    {
        if (!composer.Composed)
        {
            composer.AddStaticElement(
                new GuiElementDirectorStaticClip(
                    composer.Api,
                    ElementBounds.Fixed(0, 0, 1, 1),
                    begin: false),
                key);
        }
        return composer;
    }

    public static GuiComposer AddDirectorDialogHeader(
        this GuiComposer composer,
        string title,
        Action close,
        double width,
        string key = "directorHeader")
    {
        if (composer.Composed)
        {
            return composer;
        }

        composer.AddInteractiveElement(
            new GuiElementDirectorDragHandle(
                composer.Api,
                composer,
                ElementBounds.Fixed(8, 4, Math.Max(40d, width - 54d), 32)),
            key + "-drag");
        composer
            .AddStaticText(title, CairoFont.WhiteSmallishText(),
                ElementBounds.Fixed(14, 8, Math.Max(40d, width - 70d), 28), key + "-title")
            .AddDirectorIconButton(
                DirectorIcon.Close,
                SVCLang.Get("button-close"),
                () => { close(); return true; },
                ElementBounds.Fixed(width - 38d, 6, 30, 28),
                EnumButtonStyle.Small,
                key + "-close");
        return composer;
    }

    private static GuiComposer AddDirectorSurface(
        GuiComposer composer,
        ElementBounds bounds,
        DirectorSurfaceKind kind,
        string? key)
    {
        if (!composer.Composed)
        {
            composer.AddStaticElement(
                new GuiElementDirectorSurface(composer.Api, bounds, kind),
                key);
        }
        return composer;
    }
}

internal sealed class GuiElementDirectorDragHandle : GuiElementControl
{
    private readonly GuiComposer composer;
    private bool dragging;
    private int dragStartX;
    private int dragStartY;
    private double startOffsetX;
    private double startOffsetY;

    public GuiElementDirectorDragHandle(
        ICoreClientAPI capi,
        GuiComposer composer,
        ElementBounds bounds)
        : base(capi, bounds)
    {
        this.composer = composer;
        MouseOverCursor = "move";
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (!Enabled || args.Button != EnumMouseButton.Left)
        {
            return;
        }

        dragging = true;
        dragStartX = args.X;
        dragStartY = args.Y;
        startOffsetX = composer.Bounds.fixedOffsetX;
        startOffsetY = composer.Bounds.fixedOffsetY;
        args.Handled = true;
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (!dragging)
        {
            return;
        }

        double scale = Math.Max(0.001d, GuiElement.scaled(1d));
        composer.Bounds.WithFixedAlignmentOffset(
            startOffsetX + (args.X - dragStartX) / scale,
            startOffsetY + (args.Y - dragStartY) / scale);
        composer.Bounds.MarkDirtyRecursive();
        composer.Bounds.CalcWorldBounds();
        args.Handled = true;
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        if (args.Button == EnumMouseButton.Left && dragging)
        {
            dragging = false;
            args.Handled = true;
        }
    }
}
