using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace SimpleVoiceChat.Gui;

internal enum DirectorIcon
{
    Lock,
    Unlock,
    Collapse,
    Expand,
    Eye,
    EyeOff,
    Navigate,
    Edit,
    Add,
    Delete,
    Duplicate,
    Export,
    Import,
    Paste,
    Record,
    ZoomIn,
    ZoomOut,
    Fit,
    Select,
    Save,
    Play,
    Pause,
    Stop,
    Reverse,
    Previous,
    Next,
    Capture,
    Scissors,
    Marker,
    Settings,
    Refresh,
    Speed,
    Search,
    Undo,
    Redo,
    Close,
    Help,
    Book
}

/// <summary>
/// Flat workspace button using the vendored Lucide SVG set for command icons.
/// Navigation and category buttons continue to use text labels.
/// </summary>
internal sealed class GuiElementDirectorButton : GuiElementControl
{
    private string text;
    private DirectorIcon? icon;
    private IAsset? iconAsset;
    private readonly ActionConsumable onClick;
    private readonly EnumButtonStyle intent;
    private readonly bool danger;
    private LoadedTexture normalTexture;
    private LoadedTexture hoverTexture;
    private LoadedTexture pressedTexture;
    private LoadedTexture disabledTexture;
    private bool isOver;
    private bool pressed;

    internal bool Visible { get; set; } = true;

    public GuiElementDirectorButton(
        ICoreClientAPI capi,
        string text,
        ActionConsumable onClick,
        ElementBounds bounds,
        EnumButtonStyle intent)
        : this(capi, text, onClick, bounds, intent, danger: false)
    {
    }

    internal GuiElementDirectorButton(
        ICoreClientAPI capi,
        string text,
        ActionConsumable onClick,
        ElementBounds bounds,
        EnumButtonStyle intent,
        bool danger)
        : base(capi, bounds)
    {
        this.text = text ?? string.Empty;
        this.onClick = onClick ?? throw new ArgumentNullException(nameof(onClick));
        this.intent = intent;
        this.danger = danger;
        normalTexture = new LoadedTexture(capi);
        hoverTexture = new LoadedTexture(capi);
        pressedTexture = new LoadedTexture(capi);
        disabledTexture = new LoadedTexture(capi);
    }

    public GuiElementDirectorButton(
        ICoreClientAPI capi,
        DirectorIcon icon,
        ActionConsumable onClick,
        ElementBounds bounds,
        EnumButtonStyle intent)
        : this(capi, string.Empty, onClick, bounds, intent)
    {
        this.icon = icon;
        iconAsset = capi.Assets.TryGet(IconLocation(icon));
    }

    public override bool Focusable => Enabled;

    internal string Text
    {
        get => text;
        set => text = value ?? string.Empty;
    }

    public void SetIcon(DirectorIcon nextIcon)
    {
        if (icon == nextIcon)
        {
            return;
        }

        icon = nextIcon;
        iconAsset = api.Assets.TryGet(IconLocation(nextIcon));
        ComposeTexture(ref normalTexture, 0, 0.92d, 1d);
        ComposeTexture(ref hoverTexture, 1, 0.98d, 1d);
        ComposeTexture(ref pressedTexture, 2, 0.98d, 0.94d);
        ComposeTexture(ref disabledTexture, 3, 0.42d, 0.42d);
    }

    public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
    {
        Bounds.CalcWorldBounds();
        ComposeTexture(ref normalTexture, 0, 0.92d, 1d);
        ComposeTexture(ref hoverTexture, 1, 0.98d, 1d);
        ComposeTexture(ref pressedTexture, 2, 0.98d, 0.94d);
        ComposeTexture(ref disabledTexture, 3, 0.42d, 0.42d);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        _ = deltaTime;
        if (!Visible)
        {
            return;
        }

        LoadedTexture texture = !Enabled
            ? disabledTexture
            : pressed
                ? pressedTexture
                : isOver ? hoverTexture : normalTexture;
        api.Render.Render2DTexturePremultipliedAlpha(texture.TextureId, Bounds);
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        isOver = Visible && Enabled && Bounds.PointInside(args.X, args.Y);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (!Visible || !Enabled || args.Button != EnumMouseButton.Left)
        {
            return;
        }

        base.OnMouseDownOnElement(api, args);
        pressed = true;
        isOver = true;
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        if (Visible && args.Button == EnumMouseButton.Left && pressed)
        {
            bool activate = Enabled && Bounds.PointInside(args.X, args.Y);
            pressed = false;
            if (activate)
            {
                args.Handled = onClick();
            }
        }
    }

    public override void Dispose()
    {
        normalTexture.Dispose();
        hoverTexture.Dispose();
        pressedTexture.Dispose();
        disabledTexture.Dispose();
        base.Dispose();
    }

    private void ComposeTexture(ref LoadedTexture target, int state, double alpha, double contentAlpha)
    {
        using ImageSurface surface = new(Format.Argb32, Bounds.OuterWidthInt, Bounds.OuterHeightInt);
        using Context context = new(surface);
        double radius = DirectorGuiTheme.ScaledCornerRadius;
        double width = Bounds.OuterWidth;
        double height = Bounds.OuterHeight;
        bool primary = intent == EnumButtonStyle.Normal;
        (double red, double green, double blue) = danger
            ? (0.46d, 0.105d, 0.12d)
            : primary
                ? (0.105d, 0.355d, 0.39d)
                : (0.105d, 0.13d, 0.145d);
        if (state == 1)
        {
            red += 0.035d;
            green += danger ? 0.035d : primary ? 0.075d : 0.045d;
            blue += danger ? 0.035d : primary ? 0.075d : 0.05d;
        }
        else if (state == 2)
        {
            red *= 0.76d;
            green *= 0.76d;
            blue *= 0.76d;
        }

        DrawRoundedRect(context, 0.5d, 0.5d, width - 1d, height - 1d, radius);
        context.SetSourceRGBA(red, green, blue, alpha);
        context.FillPreserve();
        context.SetSourceRGBA(
            danger ? 0.78d : primary ? 0.3d : DirectorGuiTheme.BorderR,
            danger ? 0.29d : primary ? 0.62d : DirectorGuiTheme.BorderG,
            danger ? 0.31d : primary ? 0.65d : DirectorGuiTheme.BorderB,
            state == 3 ? 0.16d : danger ? 0.62d : primary ? 0.48d : 0.42d);
        context.LineWidth = 1d;
        context.Stroke();

        if (icon is DirectorIcon iconValue)
        {
            DrawLucideIcon(surface, iconValue, width, height, contentAlpha);
            generateTexture(surface, ref target, linearMag: true);
            return;
        }

        CairoFont font = CairoFont.WhiteSmallText();
        font.SetupContext(context);
        double horizontalPadding = GuiElement.scaled(8d);
        double availableWidth = Math.Max(1d, width - horizontalPadding * 2d);
        TextExtents fullExtents = font.GetTextExtents(text);
        if (fullExtents.Width > availableWidth)
        {
            double fit = availableWidth / Math.Max(1d, fullExtents.Width);
            font.UnscaledFontsize = Math.Max(9d, font.UnscaledFontsize * fit);
            font.SetupContext(context);
        }
        string displayText = DirectorGuiTheme.Ellipsize(context, font, text, availableWidth);
        TextExtents extents = font.GetTextExtents(displayText);
        FontExtents fontExtents = font.GetFontExtents();
        double x = Math.Max(horizontalPadding, (width - extents.Width) / 2d - extents.XBearing);
        double y = Math.Max(fontExtents.Ascent, (height - fontExtents.Height) / 2d + fontExtents.Ascent);
        context.SetSourceRGBA(0.92d, 0.97d, 0.98d, contentAlpha);
        context.MoveTo(x, y);
        context.ShowText(displayText);
        generateTexture(surface, ref target, linearMag: true);
    }

    private void DrawLucideIcon(
        ImageSurface surface,
        DirectorIcon iconValue,
        double width,
        double height,
        double alpha)
    {
        if (iconAsset is null || icon != iconValue)
        {
            return;
        }

        double padding = GuiElement.scaled(4d);
        int size = Math.Max(1, (int)Math.Floor(Math.Min(width, height) - padding * 2d));
        int x = Math.Max(0, (int)Math.Round((width - size) / 2d));
        int y = Math.Max(0, (int)Math.Round((height - size) / 2d));
        int color = ColorUtil.ToRgba(
            Math.Clamp((int)Math.Round(alpha * 255d), 0, 255),
            235,
            247,
            250);
        surface.Flush();
        api.Gui.DrawSvg(iconAsset, surface, x, y, size, size, color);
    }

    private static AssetLocation IconLocation(DirectorIcon icon)
        => new("simplevoicechat:textures/icons/lucide/" + IconFileName(icon) + ".svg");

    private static string IconFileName(DirectorIcon icon)
        => icon switch
        {
            DirectorIcon.Lock => "lock",
            DirectorIcon.Unlock => "lock-open",
            DirectorIcon.Collapse => "chevron-up",
            DirectorIcon.Expand => "chevron-down",
            DirectorIcon.Eye => "eye",
            DirectorIcon.EyeOff => "eye-off",
            DirectorIcon.Navigate => "mouse-pointer-2",
            DirectorIcon.Edit => "pencil",
            DirectorIcon.Add => "plus",
            DirectorIcon.Delete => "trash-2",
            DirectorIcon.Duplicate => "copy",
            DirectorIcon.Export => "upload",
            DirectorIcon.Import => "download",
            DirectorIcon.Paste => "clipboard-paste",
            DirectorIcon.Record => "circle-dot",
            DirectorIcon.ZoomIn => "zoom-in",
            DirectorIcon.ZoomOut => "zoom-out",
            DirectorIcon.Fit => "maximize",
            DirectorIcon.Select => "check",
            DirectorIcon.Save => "save",
            DirectorIcon.Play => "play",
            DirectorIcon.Pause => "pause",
            DirectorIcon.Stop => "square",
            DirectorIcon.Reverse => "rewind",
            DirectorIcon.Previous => "chevron-left",
            DirectorIcon.Next => "chevron-right",
            DirectorIcon.Capture => "camera",
            DirectorIcon.Scissors => "scissors",
            DirectorIcon.Marker => "bookmark",
            DirectorIcon.Settings => "settings",
            DirectorIcon.Refresh => "refresh-cw",
            DirectorIcon.Speed => "gauge",
            DirectorIcon.Search => "search",
            DirectorIcon.Undo => "undo-2",
            DirectorIcon.Redo => "redo-2",
            DirectorIcon.Close => "x",
            DirectorIcon.Help => "circle-help",
            DirectorIcon.Book => "book-open",
            _ => throw new ArgumentOutOfRangeException(nameof(icon), icon, null)
        };

    private static void DrawRoundedRect(Context context, double x, double y, double width, double height, double radius)
    {
        radius = Math.Min(radius, Math.Min(width, height) / 2d);
        context.NewPath();
        context.Arc(x + width - radius, y + radius, radius, -Math.PI / 2d, 0d);
        context.Arc(x + width - radius, y + height - radius, radius, 0d, Math.PI / 2d);
        context.Arc(x + radius, y + height - radius, radius, Math.PI / 2d, Math.PI);
        context.Arc(x + radius, y + radius, radius, Math.PI, Math.PI * 1.5d);
        context.ClosePath();
    }
}

internal static class DirectorGuiButtonExtensions
{
    public static GuiComposer AddDirectorButton(
        this GuiComposer composer,
        string text,
        ActionConsumable onClick,
        ElementBounds bounds,
        EnumButtonStyle style = EnumButtonStyle.Normal,
        string? key = null,
        string? tooltip = null)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorButton(composer.Api, text, onClick, bounds, style),
                key);
            AddButtonTooltip(composer, tooltip ?? text, bounds, key);
        }
        return composer;
    }

    public static GuiComposer AddDirectorIconButton(
        this GuiComposer composer,
        DirectorIcon icon,
        string tooltip,
        ActionConsumable onClick,
        ElementBounds bounds,
        EnumButtonStyle style = EnumButtonStyle.Small,
        string? key = null)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorButton(composer.Api, icon, onClick, bounds, style),
                key);
            AddButtonTooltip(composer, tooltip, bounds, key);
        }
        return composer;
    }

    public static GuiComposer AddDirectorDangerButton(
        this GuiComposer composer,
        string text,
        ActionConsumable onClick,
        ElementBounds bounds,
        string? key = null,
        string? tooltip = null)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorButton(
                    composer.Api,
                    text,
                    onClick,
                    bounds,
                    EnumButtonStyle.Normal,
                    danger: true),
                key);
            AddButtonTooltip(composer, tooltip ?? text, bounds, key);
        }
        return composer;
    }

    // Kept under the existing helper name for the workspace source until all
    // panels migrate. Calls in this namespace resolve this overload first.
    public static GuiComposer AddSmallButton(
        this GuiComposer composer,
        string text,
        ActionConsumable onClick,
        ElementBounds bounds,
        EnumButtonStyle style = EnumButtonStyle.Normal,
        string? key = null,
        string? tooltip = null)
        => AddDirectorButton(composer, text, onClick, bounds, style, key, tooltip);

    private static void AddButtonTooltip(
        GuiComposer composer,
        string tooltip,
        ElementBounds bounds,
        string? key)
    {
        if (string.IsNullOrWhiteSpace(tooltip))
        {
            return;
        }

        string tooltipKey = string.IsNullOrWhiteSpace(key) ? null! : key + "-tooltip";
        composer.AddHoverText(tooltip, CairoFont.WhiteSmallText(), 320, bounds.FlatCopy(), tooltipKey);
        if (!string.IsNullOrWhiteSpace(tooltipKey))
        {
            composer.GetHoverText(tooltipKey).SetAutoWidth(true);
        }
    }

    public static GuiElementDirectorButton? TryGetButton(this GuiComposer composer, string key)
        => composer.GetElement(key) as GuiElementDirectorButton;

    public static GuiElementDirectorButton GetButton(this GuiComposer composer, string key)
    {
        object? element = composer.GetElement(key);
        if (element is GuiElementDirectorButton button)
        {
            return button;
        }

        throw element is null
            ? new KeyNotFoundException($"GUI button '{key}' was not found.")
            : new InvalidOperationException(
                $"GUI element '{key}' is {element.GetType().Name}, not a director button.");
    }
}
