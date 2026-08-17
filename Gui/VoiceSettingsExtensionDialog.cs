using Cairo;
using SimpleVoiceChat.Integration;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SimpleVoiceChat.Gui;

internal sealed class VoiceSettingsExtensionDialog : GuiDialog
{
    private const string ComposerKey = "simplevoicechat-extension-window";
    private const string CloseIcon = "svc-fa-xmark";
    private const double MinimumWidth = 360;
    private const double MaximumWidth = 940;
    private const double MinimumHeight = 220;
    private const double MaximumHeight = 650;
    private static readonly AssetLocation CloseAsset = new("simplevoicechat", "icons/fontawesome/xmark.svg");

    private readonly VoiceSettingsExtensionWindow definition;
    private readonly Action closed;

    public VoiceSettingsExtensionDialog(
        ICoreClientAPI capi,
        VoiceSettingsExtensionWindow definition,
        Action closed)
        : base(capi)
    {
        this.definition = definition;
        this.closed = closed;
    }

    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;
    public override bool DisableMouseGrab => true;
    public override bool CaptureAllInputs() => true;
    public override bool CaptureRawMouse() => true;
    public override EnumDialogType DialogType => EnumDialogType.Dialog;
    public override double DrawOrder => 0.49;
    public override double InputOrder => 0.29;

    public override bool TryOpen()
    {
        Compose();
        return base.TryOpen();
    }

    public override void OnGuiClosed()
    {
        closed();
        base.OnGuiClosed();
    }

    private void Compose()
    {
        capi.Gui.Icons.CustomIcons[CloseIcon] = capi.Gui.Icons.SvgIconSource(CloseAsset);
        SingleComposer?.Dispose();

        double width = Math.Clamp(definition.Width, MinimumWidth, MaximumWidth);
        double height = Math.Clamp(definition.Height, MinimumHeight, MaximumHeight);
        ElementBounds root = ElementBounds.Fixed(EnumDialogArea.CenterMiddle, 0, 0, width, height);
        ElementBounds panel = ElementBounds.Fixed(0, 0, width, height);
        ElementBounds content = ElementBounds.Fixed(24, 54, width - 48, height - 74);
        GuiComposer composer = capi.Gui.CreateCompo(ComposerKey, root)
            .AddStaticCustomDraw(panel, DrawPanel)
            .AddStaticText(
                FitText(definition.Title, width - 92),
                CairoFont.WhiteSmallishText().WithFontSize(18).WithColor(new[] { 1.0, 1.0, 1.0, 1.0 }),
                ElementBounds.Fixed(24, 14, width - 72, 28))
            .AddInteractiveElement(
                new VoiceSettingsIconButton(
                    capi,
                    ElementBounds.Fixed(width - 44, 12, 30, 30),
                    CloseIcon,
                    _ => TryClose()),
                "close");

        composer.BeginClip(content).BeginChildElements(ElementBounds.Fixed(0, 0, content.fixedWidth, content.fixedHeight));
        try
        {
            VoiceSettingsExtensionWindowContext context = new(
                capi,
                composer,
                ElementBounds.Fixed(0, 0, content.fixedWidth, content.fixedHeight),
                () => TryClose());
            definition.Compose(context);
        }
        catch (Exception ex)
        {
            capi.Logger.Warning("SimpleVoiceChat: settings extension window '{0}' failed to compose: {1}", definition.Id, ex.Message);
        }
        composer.EndChildElements().EndClip();
        SingleComposer = composer.Compose(focusFirstElement: false);
    }

    private static void DrawPanel(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        bounds.CalcWorldBounds();
        GuiElement.RoundRectangle(ctx, bounds.bgDrawX, bounds.bgDrawY, bounds.OuterWidth, bounds.OuterHeight, GuiElement.scaled(4));
        ctx.SetSourceRGBA(0.015, 0.02, 0.028, 0.98);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(0.78, 0.82, 0.9, 0.22);
        ctx.LineWidth = GuiElement.scaled(1);
        ctx.Stroke();
    }

    private static string FitText(string value, double maxWidth)
    {
        using ImageSurface surface = new(Format.Argb32, 1, 1);
        using Context context = new(surface);
        CairoFont font = CairoFont.WhiteSmallishText().WithFontSize(18);
        font.SetupContext(context);
        if (context.TextExtents(value).XAdvance <= maxWidth)
        {
            return value;
        }

        const string ellipsis = "...";
        int length = value.Length;
        while (length > 0 && context.TextExtents(value[..length] + ellipsis).XAdvance > maxWidth)
        {
            length--;
        }
        return length == 0 ? ellipsis : value[..length] + ellipsis;
    }
}
