using Cairo;
using SimpleVoiceChat.Networking;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SimpleVoiceChat.Gui;

internal static class VoiceSettingsVisibility
{
    internal static bool IsVisibleInClip(GuiElement element)
    {
        ElementBounds? clip = element.InsideClipBounds;
        if (clip == null)
        {
            return true;
        }

        element.Bounds.CalcWorldBounds();
        clip.CalcWorldBounds();
        return element.Bounds.renderX + element.Bounds.OuterWidth > clip.renderX
            && element.Bounds.renderX < clip.renderX + clip.OuterWidth
            && element.Bounds.renderY + element.Bounds.OuterHeight > clip.renderY
            && element.Bounds.renderY < clip.renderY + clip.OuterHeight;
    }

    internal static VoiceSettingsClipScope EnterClip(ICoreClientAPI api, GuiElement element)
    {
        ElementBounds? clip = element.InsideClipBounds;
        if (clip == null)
        {
            return default;
        }

        clip.CalcWorldBounds();
        api.Render.PushScissor(clip);
        return new VoiceSettingsClipScope(api);
    }
}

internal readonly struct VoiceSettingsClipScope : IDisposable
{
    private readonly ICoreClientAPI? api;

    internal VoiceSettingsClipScope(ICoreClientAPI api)
    {
        this.api = api;
    }

    public void Dispose()
    {
        api?.Render.PopScissor();
    }
}

internal sealed class VoiceSettingsSlider : GuiElementSlider
{
    private readonly LoadedTexture fillTexture;
    private readonly LoadedTexture handleTexture;
    private readonly LoadedTexture valueTexture;
    private int minimum;
    private int maximum = 100;
    private string suffix = string.Empty;
    private int valueTextureValue = int.MinValue;

    public VoiceSettingsSlider(ICoreClientAPI capi, ActionConsumable<int> changed, ElementBounds bounds)
        : base(capi, changed, bounds)
    {
        fillTexture = new LoadedTexture(capi);
        handleTexture = new LoadedTexture(capi);
        valueTexture = new LoadedTexture(capi);
    }

    public override bool Enabled
    {
        get => base.Enabled;
        set
        {
            base.Enabled = value;
            valueTextureValue = int.MinValue;
            if (Bounds.OuterWidthInt > 0 && Bounds.OuterHeightInt > 0) ComposeTextures();
        }
    }

    public void Configure(int value, int min, int max, int step, string suffix)
    {
        minimum = min;
        maximum = Math.Max(min + 1, max);
        this.suffix = suffix ?? string.Empty;
        valueTextureValue = int.MinValue;
        SetValues(value, min, max, step, suffix);
    }

    public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
    {
        Bounds.CalcWorldBounds();
        ctxStatic.Rectangle(Bounds.drawX, Bounds.drawY, Bounds.InnerWidth, Bounds.InnerHeight);
        ctxStatic.SetSourceRGBA(0.15, 0.18, 0.22, Enabled ? 0.98 : 0.45);
        ctxStatic.FillPreserve();
        ctxStatic.SetSourceRGBA(0.84, 0.89, 0.96, Enabled ? 0.72 : 0.35);
        ctxStatic.LineWidth = GuiElement.scaled(1);
        ctxStatic.Stroke();
        ComposeTextures();
    }

    private void ComposeTextures()
    {
        using (ImageSurface fillSurface = new(Format.Argb32, 2, 2))
        using (Context fillContext = new(fillSurface))
        {
            fillContext.SetSourceRGBA(0.93, 0.95, 0.98, Enabled ? 0.96 : 0.45);
            fillContext.Paint();
            GuiElement.GenerateTexture(api, fillSurface, ref fillTexture.TextureId);
        }

        int handleWidth = Math.Max(8, (int)GuiElement.scaled(10));
        int handleHeight = Math.Max(8, Bounds.OuterHeightInt);
        using (ImageSurface handleSurface = new(Format.Argb32, handleWidth, handleHeight))
        using (Context handleContext = new(handleSurface))
        {
            handleContext.Rectangle(0, 0, handleWidth, handleHeight);
            handleContext.SetSourceRGBA(0.96, 0.97, 0.99, Enabled ? 0.98 : 0.45);
            handleContext.FillPreserve();
            handleContext.SetSourceRGBA(0.48, 0.53, 0.60, Enabled ? 1 : 0.45);
            handleContext.LineWidth = GuiElement.scaled(1);
            handleContext.Stroke();
            GuiElement.GenerateTexture(api, handleSurface, ref handleTexture.TextureId);
        }
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!VoiceSettingsVisibility.IsVisibleInClip(this)) return;
        using VoiceSettingsClipScope clip = VoiceSettingsVisibility.EnterClip(api, this);
        Bounds.CalcWorldBounds();
        double fraction = Math.Clamp((GetValue() - minimum) / (double)(maximum - minimum), 0, 1);
        double fillWidth = Bounds.InnerWidth * fraction;
        if (fillWidth > 0.5)
        {
            api.Render.Render2DTexturePremultipliedAlpha(
                fillTexture.TextureId, Bounds.renderX, Bounds.renderY, fillWidth, Bounds.InnerHeight);
        }

        double handleWidth = Math.Max(GuiElement.scaled(8), Math.Min(GuiElement.scaled(11), Bounds.InnerWidth * 0.04));
        double handleX = Math.Clamp(
            Bounds.renderX + Bounds.InnerWidth * fraction - handleWidth / 2d,
            Bounds.renderX,
            Bounds.renderX + Bounds.InnerWidth - handleWidth);
        api.Render.Render2DTexturePremultipliedAlpha(
            handleTexture.TextureId, handleX, Bounds.renderY, handleWidth, Bounds.InnerHeight);

        int value = GetValue();
        if (value != valueTextureValue || valueTexture.TextureId == 0)
        {
            ComposeValueTexture(value);
        }

        api.Render.Render2DTexturePremultipliedAlpha(
            valueTexture.TextureId, Bounds.renderX, Bounds.renderY, Bounds.InnerWidth, Bounds.InnerHeight);
    }

    private void ComposeValueTexture(int value)
    {
        string text = value + suffix;
        CairoFont font = CairoFont.WhiteSmallText().WithFontSize(13).WithColor(new[] { 0.96, 0.97, 1.0, 1.0 });
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        font.SetupContext(context);
        TextExtents extents = context.TextExtents(text);
        double x = (width - extents.XAdvance) / 2d - extents.XBearing;
        double y = (height - context.FontExtents.Height) / 2d + context.FontExtents.Ascent;
        // A dark outline keeps the centered value readable over both the dark
        // track and the light filled portion.
        context.SetSourceRGBA(0.02, 0.025, 0.032, Enabled ? 0.92 : 0.65);
        context.LineWidth = GuiElement.scaled(3);
        context.MoveTo(x, y);
        context.TextPath(text);
        context.Stroke();
        context.SetSourceRGBA(0.98, 0.99, 1.0, Enabled ? 1.0 : 0.45);
        context.MoveTo(x, y);
        context.ShowText(text);
        GuiElement.GenerateTexture(api, surface, ref valueTexture.TextureId);
        valueTextureValue = value;
    }

    public override void Dispose()
    {
        base.Dispose();
        fillTexture.Dispose();
        handleTexture.Dispose();
        valueTexture.Dispose();
    }
}

internal static class VoiceSettingsComposerExtensions
{
    public static GuiComposer AddVoiceDropDown(
        this GuiComposer composer,
        string[] values,
        string[] names,
        int selectedIndex,
        SelectionChangedDelegate changed,
        ElementBounds bounds,
        string key)
    {
        composer.AddInteractiveElement(new VoiceSettingsDropDown(composer.Api, values, names, selectedIndex, changed, bounds,
            CairoFont.WhiteSmallText().WithFontSize(13).WithColor(new[] { 0.96, 0.97, 1.0, 1.0 })), key);
        return composer;
    }

    public static GuiComposer AddVoiceSlider(this GuiComposer composer, ActionConsumable<int> changed, ElementBounds bounds, string key)
    {
        composer.AddInteractiveElement(new VoiceSettingsSlider(composer.Api, changed, bounds), key);
        return composer;
    }

    public static GuiComposer AddVoiceTextInput(
        this GuiComposer composer,
        ElementBounds bounds,
        Action<string> changed,
        CairoFont font,
        string key)
    {
        composer.AddInteractiveElement(new VoiceSettingsTextInput(composer.Api, bounds, changed, font), key);
        return composer;
    }

    public static GuiComposer AddVoiceKeyBinding(this GuiComposer composer, Action<string> changed, string value, ElementBounds bounds, string key)
    {
        composer.AddInteractiveElement(new VoiceSettingsKeyBinding(composer.Api, bounds, value, changed), key);
        return composer;
    }

    public static GuiComposer AddVoiceLevelMeter(this GuiComposer composer, Func<float> level, ElementBounds bounds, string key)
    {
        composer.AddInteractiveElement(new VoiceSettingsLevelMeter(composer.Api, bounds, level), key);
        return composer;
    }

    public static GuiComposer AddVoiceActivationThresholdControl(
        this GuiComposer composer,
        Func<float> microphoneLevel,
        ActionConsumable<int> noiseGateChanged,
        ActionConsumable<int> triggerChanged,
        ElementBounds bounds,
        string key)
    {
        composer.AddInteractiveElement(new VoiceActivationThresholdControl(
            composer.Api, bounds, microphoneLevel, noiseGateChanged, triggerChanged), key);
        return composer;
    }
}

internal sealed class VoiceSettingsTextInput : GuiElementTextInput
{
    public VoiceSettingsTextInput(
        ICoreClientAPI capi,
        ElementBounds bounds,
        Action<string> changed,
        CairoFont font)
        : base(capi, bounds, changed, font)
    {
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!VoiceSettingsVisibility.IsVisibleInClip(this)) return;
        using VoiceSettingsClipScope clip = VoiceSettingsVisibility.EnterClip(api, this);
        base.RenderInteractiveElements(deltaTime);
    }
}

internal sealed class VoiceSettingsTextButton : GuiElementTextButton
{
    private readonly LoadedTexture normalBackground;
    private readonly LoadedTexture pressedBackground;
    private readonly LoadedTexture disabledBackground;
    private readonly bool highlighted;
    private bool pressed;

    public VoiceSettingsTextButton(
        ICoreClientAPI capi,
        string text,
        ActionConsumable clicked,
        ElementBounds bounds,
        CairoFont font,
        bool highlighted = false)
        : base(
            capi,
            text,
            font,
            font.Clone().WithColor(new[] { 0.02, 0.025, 0.032, 1.0 }),
            clicked,
            bounds,
            EnumButtonStyle.None)
    {
        this.highlighted = highlighted;
        normalBackground = new LoadedTexture(capi);
        pressedBackground = new LoadedTexture(capi);
        disabledBackground = new LoadedTexture(capi);
        SetOrientation(font.Orientation);
    }

    public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
    {
        Bounds.CalcWorldBounds();
        ComposeBackground(normalBackground, pressedState: false, disabledState: false);
        ComposeBackground(pressedBackground, pressedState: true, disabledState: false);
        ComposeBackground(disabledBackground, pressedState: false, disabledState: true);
        base.ComposeElements(ctxStatic, surfaceStatic);
    }

    private void ComposeBackground(LoadedTexture texture, bool pressedState, bool disabledState)
    {
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        context.Rectangle(0, 0, width, height);
        if (pressedState)
        {
            context.SetSourceRGBA(1.0, 1.0, 1.0, 0.98);
        }
        else
        {
            context.SetSourceRGBA(0.62, 0.66, 0.72,
                disabledState ? 0.14 : highlighted ? 0.36 : 0.22);
        }
        context.FillPreserve();
        if (pressedState)
        {
            context.SetSourceRGBA(0.02, 0.025, 0.032, 0.96);
        }
        else
        {
            context.SetSourceRGBA(0.92, 0.95, 1.0,
                disabledState ? 0.42 : highlighted ? 0.95 : 0.88);
        }
        context.LineWidth = GuiElement.scaled(1);
        context.Stroke();
        GuiElement.GenerateTexture(api, surface, ref texture.TextureId);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!VoiceSettingsVisibility.IsVisibleInClip(this)) return;
        using VoiceSettingsClipScope clip = VoiceSettingsVisibility.EnterClip(api, this);
        LoadedTexture background = !Enabled
            ? disabledBackground
            : pressed ? pressedBackground : normalBackground;
        api.Render.Render2DTexturePremultipliedAlpha(background.TextureId, Bounds);
        base.RenderInteractiveElements(deltaTime);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseDownOnElement(api, args);
        if (Enabled)
        {
            pressed = true;
        }
    }

    public override void OnMouseUpOnElement(ICoreClientAPI api, MouseEvent args)
    {
        pressed = false;
        base.OnMouseUpOnElement(api, args);
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseUp(api, args);
        pressed = false;
    }

    public override void OnFocusLost()
    {
        pressed = false;
        base.OnFocusLost();
    }

    public override void Dispose()
    {
        normalBackground.Dispose();
        pressedBackground.Dispose();
        disabledBackground.Dispose();
        base.Dispose();
    }
}

internal sealed class VoiceSettingsExtensionButtonElement : GuiElementControl
{
    private readonly string text;
    private readonly ActionConsumable clicked;
    private int textureId;
    private bool pressed;

    public override bool Focusable => Enabled;

    public VoiceSettingsExtensionButtonElement(
        ICoreClientAPI capi,
        string text,
        ActionConsumable clicked,
        ElementBounds bounds)
        : base(capi, bounds)
    {
        this.text = text;
        this.clicked = clicked;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        Redraw();
    }

    private void Redraw()
    {
        if (Bounds.OuterWidthInt <= 0 || Bounds.OuterHeightInt <= 0)
        {
            return;
        }

        using ImageSurface surface = new(Format.Argb32, Bounds.OuterWidthInt, Bounds.OuterHeightInt);
        using Context context = new(surface);
        double width = Bounds.OuterWidth;
        double height = Bounds.OuterHeight;
        bool pressedState = pressed && Enabled;
        double lineWidth = GuiElement.scaled(1);
        double inset = lineWidth / 2d;
        context.Rectangle(inset, inset, Math.Max(0, width - lineWidth), Math.Max(0, height - lineWidth));
        context.SetSourceRGBA(
            pressedState ? 1.0 : 0.62,
            pressedState ? 1.0 : 0.66,
            pressedState ? 1.0 : 0.72,
            Enabled ? pressedState ? 0.98 : 0.22 : 0.14);
        context.FillPreserve();
        context.SetSourceRGBA(
            pressedState ? 0.02 : 0.92,
            pressedState ? 0.025 : 0.95,
            pressedState ? 0.032 : 1.0,
            Enabled ? pressedState ? 0.96 : 0.88 : 0.42);
        context.LineWidth = lineWidth;
        context.Stroke();

        CairoFont font = CairoFont.WhiteSmallText()
            .WithFontSize(14)
            .WithOrientation(EnumTextOrientation.Center)
            .WithColor(new[]
            {
                pressedState ? 0.02 : 0.96,
                pressedState ? 0.025 : 0.97,
                pressedState ? 0.032 : 1.0,
                1.0
            });
        font.SetupContext(context);
        string fitted = FitText(context, text, width - GuiElement.scaled(16));
        FontExtents extents = context.FontExtents;
        TextExtents textExtents = context.TextExtents(fitted);
        double x = (width - textExtents.XAdvance) / 2d - textExtents.XBearing;
        double y = (height - extents.Height) / 2d + extents.Ascent;
        context.SetSourceRGBA(
            pressedState ? 0.02 : 0.96,
            pressedState ? 0.025 : 0.97,
            pressedState ? 0.032 : 1.0,
            Enabled ? 1.0 : 0.45);
        context.MoveTo(x, y);
        context.ShowText(fitted);
        GuiElement.GenerateTexture(api, surface, ref textureId);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!VoiceSettingsVisibility.IsVisibleInClip(this)) return;
        using VoiceSettingsClipScope clip = VoiceSettingsVisibility.EnterClip(api, this);
        api.Render.Render2DTexturePremultipliedAlpha(textureId, Bounds);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseDownOnElement(api, args);
        if (Enabled)
        {
            pressed = true;
            Redraw();
        }
    }

    public override void OnMouseUpOnElement(ICoreClientAPI api, MouseEvent args)
    {
        bool wasPressed = pressed;
        pressed = false;
        base.OnMouseUpOnElement(api, args);
        Redraw();
        if (wasPressed && Enabled)
        {
            api.Gui.PlaySound("menubutton");
            clicked();
            args.Handled = true;
        }
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseUp(api, args);
        if (pressed)
        {
            pressed = false;
            Redraw();
        }
    }

    public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
    {
        if (HasFocus && Enabled && args.KeyCode is 49 or 51)
        {
            args.Handled = true;
            clicked();
            api.Gui.PlaySound("menubutton");
        }
    }

    public override void Dispose()
    {
        if (textureId > 0)
        {
            api.Render.GLDeleteTexture(textureId);
            textureId = 0;
        }
        base.Dispose();
    }

    private static string FitText(Context context, string value, double maxWidth)
    {
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
        return length == 0 ? string.Empty : value[..length] + ellipsis;
    }
}

internal sealed class VoiceSettingsExtensionImageButtonElement : GuiElementControl
{
    private readonly ImageSurface imageSurface;
    private readonly Action clicked;
    private int textureId;
    private bool pressed;

    public override bool Focusable => Enabled;

    public VoiceSettingsExtensionImageButtonElement(
        ICoreClientAPI capi,
        AssetLocation image,
        Action clicked,
        ElementBounds bounds)
        : base(capi, bounds)
    {
        imageSurface = GuiElement.getImageSurfaceFromAsset(capi, image);
        this.clicked = clicked;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        Redraw();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!VoiceSettingsVisibility.IsVisibleInClip(this)) return;
        using VoiceSettingsClipScope clip = VoiceSettingsVisibility.EnterClip(api, this);
        api.Render.Render2DTexturePremultipliedAlpha(textureId, Bounds);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseDownOnElement(api, args);
        if (!Enabled)
        {
            return;
        }

        pressed = true;
        Redraw();
        args.Handled = true;
    }

    public override void OnMouseUpOnElement(ICoreClientAPI api, MouseEvent args)
    {
        bool wasPressed = pressed;
        pressed = false;
        base.OnMouseUpOnElement(api, args);
        Redraw();
        if (wasPressed && Enabled)
        {
            api.Gui.PlaySound("menubutton");
            clicked();
            args.Handled = true;
        }
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseUp(api, args);
        if (pressed)
        {
            pressed = false;
            Redraw();
        }
    }

    public override void Dispose()
    {
        if (textureId > 0)
        {
            api.Render.GLDeleteTexture(textureId);
            textureId = 0;
        }
        imageSurface.Dispose();
        base.Dispose();
    }

    private void Redraw()
    {
        if (Bounds.OuterWidthInt <= 0 || Bounds.OuterHeightInt <= 0)
        {
            return;
        }

        using ImageSurface surface = new(Format.Argb32, Bounds.OuterWidthInt, Bounds.OuterHeightInt);
        using Context context = new(surface);
        double width = Bounds.OuterWidth;
        double height = Bounds.OuterHeight;
        bool pressedState = pressed && Enabled;
        double lineWidth = GuiElement.scaled(1);
        double inset = lineWidth / 2d;
        context.Rectangle(inset, inset, Math.Max(0, width - lineWidth), Math.Max(0, height - lineWidth));
        context.SetSourceRGBA(
            pressedState ? 1.0 : 0.62,
            pressedState ? 1.0 : 0.66,
            pressedState ? 1.0 : 0.72,
            Enabled ? pressedState ? 0.98 : 0.30 : 0.14);
        context.FillPreserve();
        context.SetSourceRGBA(
            pressedState ? 0.02 : 0.92,
            pressedState ? 0.025 : 0.95,
            pressedState ? 0.032 : 1.0,
            Enabled ? pressedState ? 0.96 : 0.88 : 0.42);
        context.LineWidth = lineWidth;
        context.Stroke();

        double padding = GuiElement.scaled(5);
        double imageHeight = Math.Max(1, height - padding * 2);
        double imageWidth = imageSurface.Width * imageHeight / Math.Max(1, imageSurface.Height);
        if (imageWidth > width - padding * 2)
        {
            imageWidth = width - padding * 2;
            imageHeight = imageSurface.Height * imageWidth / Math.Max(1, imageSurface.Width);
        }
        context.Save();
        context.Translate((width - imageWidth) / 2d, (height - imageHeight) / 2d);
        context.Scale(imageWidth / imageSurface.Width, imageHeight / imageSurface.Height);
        context.SetSourceSurface(imageSurface, 0, 0);
        context.Rectangle(0, 0, imageSurface.Width, imageSurface.Height);
        context.Fill();
        context.Restore();
        GuiElement.GenerateTexture(api, surface, ref textureId);
    }
}

internal sealed class VoiceSettingsLevelMeter : GuiElementControl
{
    private readonly Func<float> level;
    private readonly LoadedTexture fillTexture;

    public override bool Focusable => false;

    public VoiceSettingsLevelMeter(ICoreClientAPI capi, ElementBounds bounds, Func<float> level)
        : base(capi, bounds)
    {
        this.level = level;
        fillTexture = new LoadedTexture(capi);
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        ctx.Rectangle(Bounds.drawX, Bounds.drawY, Bounds.InnerWidth, Bounds.InnerHeight);
        ctx.SetSourceRGBA(0.15, 0.18, 0.22, 0.98);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(0.84, 0.89, 0.96, 0.72);
        ctx.LineWidth = GuiElement.scaled(1);
        ctx.Stroke();
        using ImageSurface fillSurface = new(Format.Argb32, 2, 2);
        using Context fillContext = new(fillSurface);
        fillContext.SetSourceRGBA(0.93, 0.95, 0.98, 0.96);
        fillContext.Paint();
        GuiElement.GenerateTexture(api, fillSurface, ref fillTexture.TextureId);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!VoiceSettingsVisibility.IsVisibleInClip(this)) return;
        using VoiceSettingsClipScope clip = VoiceSettingsVisibility.EnterClip(api, this);
        double fraction = Math.Clamp(level() / 0.2f, 0f, 1f);
        if (fraction > 0.001)
        {
            api.Render.Render2DTexturePremultipliedAlpha(
                fillTexture.TextureId, Bounds.renderX, Bounds.renderY, Bounds.InnerWidth * fraction, Bounds.InnerHeight);
        }
    }

    public override void Dispose()
    {
        fillTexture.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Single compact control for the voice-activation test. One live microphone
/// meter contains two draggable handles for the noise gate and trigger level.
/// </summary>
internal sealed class VoiceActivationThresholdControl : GuiElementControl
{
    private readonly Func<float> microphoneLevel;
    private readonly ActionConsumable<int> noiseGateChanged;
    private readonly ActionConsumable<int> triggerChanged;
    private readonly LoadedTexture texture;
    private int noiseGate;
    private int triggerThreshold;
    private int activeThumb = -1;
    private float lastMicrophoneLevel = -1f;
    private int lastNoiseGate = -1;
    private int lastTriggerThreshold = -1;

    public override bool Focusable => Enabled;

    public VoiceActivationThresholdControl(
        ICoreClientAPI capi,
        ElementBounds bounds,
        Func<float> microphoneLevel,
        ActionConsumable<int> noiseGateChanged,
        ActionConsumable<int> triggerChanged)
        : base(capi, bounds)
    {
        this.microphoneLevel = microphoneLevel;
        this.noiseGateChanged = noiseGateChanged;
        this.triggerChanged = triggerChanged;
        texture = new LoadedTexture(capi);
    }

    public void Configure(int noiseGate, int triggerThreshold)
    {
        this.noiseGate = Math.Clamp(noiseGate, 0, 200);
        this.triggerThreshold = Math.Clamp(triggerThreshold, this.noiseGate, 200);
        lastMicrophoneLevel = -1f;
        Redraw();
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        Redraw();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!VoiceSettingsVisibility.IsVisibleInClip(this)) return;
        using VoiceSettingsClipScope clip = VoiceSettingsVisibility.EnterClip(api, this);
        float level = Math.Clamp(microphoneLevel(), 0f, 0.2f);
        if (texture.TextureId == 0
            || Math.Abs(level - lastMicrophoneLevel) > 0.001f
            || noiseGate != lastNoiseGate
            || triggerThreshold != lastTriggerThreshold)
        {
            Redraw();
        }

        api.Render.Render2DTexturePremultipliedAlpha(texture.TextureId, Bounds);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseDownOnElement(api, args);
        if (!Enabled)
        {
            return;
        }

        activeThumb = TrackIndex(args.X);
        if (activeThumb >= 0)
        {
            UpdateValue(args.X, activeThumb);
            args.Handled = true;
        }
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (activeThumb < 0 || !Enabled)
        {
            return;
        }

        UpdateValue(args.X, activeThumb);
        args.Handled = true;
    }

    public override void OnMouseUpOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (activeThumb >= 0)
        {
            activeThumb = -1;
            args.Handled = true;
        }
        base.OnMouseUpOnElement(api, args);
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseUp(api, args);
        activeThumb = -1;
    }

    public override void OnFocusLost()
    {
        activeThumb = -1;
        base.OnFocusLost();
    }

    private int TrackIndex(int screenX)
    {
        Bounds.CalcWorldBounds();
        double localX = screenX - Bounds.renderX;
        double left = GuiElement.scaled(12);
        double width = Bounds.OuterWidth - GuiElement.scaled(24);
        double fraction = Math.Clamp((localX - left) / width, 0d, 1d);
        double noisePosition = noiseGate / 200d;
        double triggerPosition = triggerThreshold / 200d;
        if (Math.Abs(noisePosition - triggerPosition) < 0.0001d)
        {
            return fraction < noisePosition ? 0 : 1;
        }
        return Math.Abs(fraction - noisePosition) <= Math.Abs(fraction - triggerPosition) ? 0 : 1;
    }

    private void UpdateValue(int screenX, int thumb)
    {
        Bounds.CalcWorldBounds();
        double left = GuiElement.scaled(12);
        double trackWidth = Bounds.OuterWidth - GuiElement.scaled(24);
        double fraction = Math.Clamp((screenX - Bounds.renderX - left) / trackWidth, 0d, 1d);
        int value = (int)Math.Round(fraction * 200d);
        if (thumb == 0)
        {
            noiseGate = Math.Min(value, triggerThreshold);
            noiseGateChanged(noiseGate);
        }
        else
        {
            triggerThreshold = Math.Max(value, Math.Max(noiseGate, 5));
            triggerChanged(triggerThreshold);
        }
        Redraw();
    }

    private void Redraw()
    {
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        float level = Math.Clamp(microphoneLevel(), 0f, 0.2f);
        double levelFraction = level / 0.2d;
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        double trackWidth = width - GuiElement.scaled(24);
        double trackLeft = GuiElement.scaled(12);
        double trackTop = GuiElement.scaled(27);
        double trackHeight = Math.Max(GuiElement.scaled(18), height - trackTop - GuiElement.scaled(7));
        CairoFont label = CairoFont.WhiteSmallText().WithFontSize(12).WithColor(new[] { 0.9, 0.92, 0.96, 1.0 });
        label.SetupContext(context);
        DrawTrack(context, label, trackLeft, trackWidth, trackTop, trackHeight, levelFraction,
            noiseGate, triggerThreshold);
        GuiElement.GenerateTexture(api, surface, ref texture.TextureId);
        lastMicrophoneLevel = level;
        lastNoiseGate = noiseGate;
        lastTriggerThreshold = triggerThreshold;
    }

    private static void DrawTrack(
        Context context,
        CairoFont label,
        double left,
        double width,
        double top,
        double height,
        double levelFraction,
        int noiseGate,
        int triggerThreshold)
    {
        context.SetSourceRGBA(0.15, 0.18, 0.22, 0.98);
        context.Rectangle(left, top, width, height);
        context.FillPreserve();
        context.SetSourceRGBA(0.84, 0.89, 0.96, 0.72);
        context.LineWidth = GuiElement.scaled(1);
        context.Stroke();

        if (levelFraction > 0.001)
        {
            context.SetSourceRGBA(0.88, 0.91, 0.95, 0.72);
            context.Rectangle(left + 1, top + 1, Math.Max(0, (width - 2) * levelFraction), height - 2);
            context.Fill();
        }

        double handleWidth = GuiElement.scaled(8);
        double noiseHandleX = left + (width - handleWidth) * noiseGate / 200d;
        double triggerHandleX = left + (width - handleWidth) * triggerThreshold / 200d;
        context.SetSourceRGBA(0.62, 0.66, 0.72, 1.0);
        context.Rectangle(noiseHandleX, top - GuiElement.scaled(3), handleWidth, height + GuiElement.scaled(6));
        context.Fill();
        context.SetSourceRGBA(0.98, 0.99, 1.0, 1.0);
        context.Rectangle(triggerHandleX, top - GuiElement.scaled(3), handleWidth, height + GuiElement.scaled(6));
        context.Fill();

        label.SetupContext(context);
        context.SetSourceRGBA(0.9, 0.92, 0.96, 1.0);
        string noiseLabel = SVCLang.Get("setup-noise-gate");
        string triggerLabel = SVCLang.Get("setup-voice-trigger-threshold");
        context.MoveTo(left, GuiElement.scaled(16));
        context.ShowText(noiseLabel);
        TextExtents triggerExtents = context.TextExtents(triggerLabel);
        context.MoveTo(left + width - triggerExtents.XAdvance, GuiElement.scaled(16));
        context.ShowText(triggerLabel);
    }

    public override void Dispose()
    {
        texture.Dispose();
        base.Dispose();
    }
}

internal sealed class VoiceSettingsKeyBinding : GuiElementControl
{
    private readonly Action<string> changed;
    private readonly CairoFont font;
    private LoadedTexture texture;
    private string value;
    private bool capturing;

    public override bool Focusable => Enabled;

    public VoiceSettingsKeyBinding(ICoreClientAPI capi, ElementBounds bounds, string value, Action<string> changed)
        : base(capi, bounds)
    {
        this.value = string.IsNullOrWhiteSpace(value) ? "N" : value;
        this.changed = changed;
        font = CairoFont.WhiteSmallText().WithFontSize(14).WithColor(new[] { 0.96, 0.97, 1.0, 1.0 }).WithOrientation(EnumTextOrientation.Center);
        texture = new LoadedTexture(capi);
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        Redraw();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!VoiceSettingsVisibility.IsVisibleInClip(this)) return;
        using VoiceSettingsClipScope clip = VoiceSettingsVisibility.EnterClip(api, this);
        api.Render.Render2DTexturePremultipliedAlpha(texture.TextureId, Bounds);
    }

    private void Redraw()
    {
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        bool pressedState = capturing && Enabled;
        context.Rectangle(0, 0, width, height);
        context.SetSourceRGBA(
            pressedState ? 1.0 : 0.15,
            pressedState ? 1.0 : 0.18,
            pressedState ? 1.0 : 0.22,
            Enabled ? 0.98 : 0.45);
        context.FillPreserve();
        context.SetSourceRGBA(
            pressedState ? 0.02 : 0.84,
            pressedState ? 0.025 : 0.89,
            pressedState ? 0.032 : 0.96,
            Enabled ? pressedState ? 0.96 : 0.72 : 0.35);
        context.LineWidth = GuiElement.scaled(1);
        context.Stroke();
        font.SetupContext(context);
        string text = capturing ? SVCLang.Get("setup-key-binding-waiting") : value;
        TextExtents extents = context.TextExtents(text);
        context.SetSourceRGBA(
            pressedState ? 0.02 : 0.96,
            pressedState ? 0.025 : 0.97,
            pressedState ? 0.032 : 1.0,
            1.0);
        context.MoveTo((width - extents.XAdvance) / 2d - extents.XBearing, (height - context.FontExtents.Height) / 2d + context.FontExtents.Ascent);
        context.ShowText(text);
        GuiElement.GenerateTexture(api, surface, ref texture.TextureId);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseDownOnElement(api, args);
        if (!Enabled) return;
        capturing = true;
        Redraw();
        args.Handled = true;
    }

    public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
    {
        if (!HasFocus || !Enabled || !capturing)
        {
            return;
        }

        GlKeys key = Enum.IsDefined(typeof(GlKeys), args.KeyCode) ? (GlKeys)args.KeyCode : GlKeys.Unknown;
        if (key is GlKeys.Unknown or GlKeys.LControl or GlKeys.RControl or GlKeys.AltLeft or GlKeys.AltRight or GlKeys.LShift or GlKeys.RShift)
        {
            args.Handled = true;
            return;
        }

        value = key.ToString();
        capturing = false;
        changed(value);
        Redraw();
        args.Handled = true;
        api.Gui.PlaySound("menubutton");
    }

    public override void Dispose()
    {
        texture.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Drop-down with solid, compact surfaces while retaining the native
/// list-menu hit testing, keyboard navigation, and selection callbacks.
/// </summary>
internal sealed class VoiceSettingsDropDown : GuiElementControl
{
    private readonly string[] values;
    private readonly string[] names;
    private readonly SelectionChangedDelegate changed;
    private readonly CairoFont font;
    private readonly LoadedTexture valueTexture;
    private readonly LoadedTexture popupTexture;
    private int selectedIndex;
    private int hoveredIndex;
    private int popupWidth;
    private int popupHeight;
    private int rowHeight;
    private bool expanded;
    private bool searchFocused;
    private string searchText = string.Empty;
    private int[] matchingIndices = Array.Empty<int>();
    private long popupScrollStartMilliseconds;
    private long lastPopupScrollRedrawMilliseconds;

    private const long PopupScrollPauseMilliseconds = 700;
    private const long PopupScrollDurationMilliseconds = 2200;
    private const long PopupScrollCycleMilliseconds = 3600;
    private const double PopupScrollGap = 24;

    public override bool Focusable => Enabled;

    public bool IsExpanded => expanded;

    public string? HoveredValue => hoveredIndex >= 0 && hoveredIndex < values.Length
        ? values[hoveredIndex]
        : null;

    public string SearchText => searchText;

    public VoiceSettingsDropDown(
        ICoreClientAPI capi,
        string[] values,
        string[] names,
        int selectedIndex,
        SelectionChangedDelegate changed,
        ElementBounds bounds,
        CairoFont font)
        : base(capi, bounds)
    {
        if (values.Length != names.Length)
        {
            throw new ArgumentException("Dropdown values and names must have the same length.");
        }

        this.values = values;
        this.names = names;
        this.changed = changed;
        this.font = font;
        this.selectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, values.Length - 1));
        hoveredIndex = this.selectedIndex;
        valueTexture = new LoadedTexture(capi);
        popupTexture = new LoadedTexture(capi);
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        RebuildTextures();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!VoiceSettingsVisibility.IsVisibleInClip(this)) return;
        using VoiceSettingsClipScope clip = VoiceSettingsVisibility.EnterClip(api, this);
        Bounds.CalcWorldBounds();
        api.Render.Render2DTexturePremultipliedAlpha(valueTexture.TextureId,
            Bounds.renderX, Bounds.renderY, Bounds.OuterWidth, Bounds.OuterHeight);
        if (expanded)
        {
            if (HasOverflowingPopupText()
                && api.ElapsedMilliseconds - lastPopupScrollRedrawMilliseconds >= 50)
            {
                lastPopupScrollRedrawMilliseconds = api.ElapsedMilliseconds;
                RebuildPopupTexture();
            }
            api.Render.Render2DTexturePremultipliedAlpha(popupTexture.TextureId,
                Bounds.renderX, GetPopupTop(), popupWidth, popupHeight, 320f);
        }
    }

    public override bool IsPositionInside(int posX, int posY)
    {
        Bounds.CalcWorldBounds();
        bool insideControl = posX >= Bounds.renderX
            && posX <= Bounds.renderX + Bounds.OuterWidth
            && posY >= Bounds.renderY
            && posY <= Bounds.renderY + Bounds.OuterHeight;
        if (insideControl)
        {
            return true;
        }

        double popupTop = GetPopupTop();
        return expanded
            && posX >= Bounds.renderX
            && posX <= Bounds.renderX + popupWidth
            && posY >= popupTop
            && posY <= popupTop + popupHeight;
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (!Enabled)
        {
            return;
        }

        // Explicitly claim focus before opening the popup. Without this the
        // dialog can immediately report focus loss and close the new popup.
        base.OnMouseDownOnElement(api, args);
        Bounds.CalcWorldBounds();
        if (!expanded)
        {
            expanded = true;
            hoveredIndex = selectedIndex;
            searchFocused = true;
            popupScrollStartMilliseconds = api.ElapsedMilliseconds;
            RebuildTextures();
            api.Gui.PlaySound("menubutton");
            args.Handled = true;
            return;
        }

        double popupTop = GetPopupTop();
        if (args.Y >= popupTop && args.Y <= popupTop + popupHeight)
        {
            int popupRow = (int)((args.Y - popupTop) / rowHeight);
            if (popupRow == 0)
            {
                searchFocused = true;
                RebuildPopupTexture();
                args.Handled = true;
                return;
            }

            int matchIndex = popupRow - 1;
            if (matchIndex >= 0 && matchIndex < matchingIndices.Length)
            {
                int nextIndex = matchingIndices[matchIndex];
                selectedIndex = nextIndex;
                hoveredIndex = nextIndex;
                changed?.Invoke(values[selectedIndex], true);
                api.Gui.PlaySound("toggleswitch");
            }
        }

        Close();
        args.Handled = true;
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (!expanded)
        {
            return;
        }

        double popupTop = GetPopupTop();
        if (args.X < Bounds.renderX || args.X > Bounds.renderX + popupWidth
            || args.Y < popupTop || args.Y > popupTop + popupHeight)
        {
            return;
        }

        int popupRow = (int)((args.Y - popupTop) / rowHeight);
        if (popupRow == 0)
        {
            if (!searchFocused)
            {
                searchFocused = true;
                RebuildPopupTexture();
            }
            args.Handled = true;
            return;
        }

        int matchIndex = popupRow - 1;
        if (matchIndex >= 0 && matchIndex < matchingIndices.Length)
        {
            int nextIndex = matchingIndices[matchIndex];
            if (nextIndex != hoveredIndex || searchFocused)
            {
                hoveredIndex = nextIndex;
                searchFocused = false;
                popupScrollStartMilliseconds = api.ElapsedMilliseconds;
                RebuildPopupTexture();
            }
        }
        args.Handled = true;
    }

    public override void OnFocusLost()
    {
        // The popup is rendered as part of the control, but the GUI focus
        // manager may briefly move focus while dispatching the opening click.
        // Do not treat that transient focus change as an outside click.
        base.OnFocusLost();
    }

    public void Close()
    {
        if (!expanded)
        {
            return;
        }

        expanded = false;
        searchFocused = false;
        searchText = string.Empty;
        RebuildTextures();
    }

    public void RestoreExpanded(string? hoveredValue, string? restoredSearchText)
    {
        if (!Enabled || values.Length == 0)
        {
            return;
        }

        int restoredIndex = string.IsNullOrEmpty(hoveredValue)
            ? selectedIndex
            : Array.IndexOf(values, hoveredValue);
        expanded = true;
        searchFocused = true;
        searchText = LimitSearchText(restoredSearchText);
        hoveredIndex = restoredIndex >= 0
            ? restoredIndex
            : selectedIndex;
        popupScrollStartMilliseconds = api.ElapsedMilliseconds;
        RebuildTextures();
    }

    public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
    {
        if (!HasFocus || values.Length == 0)
        {
            return;
        }

        GlKeys key = Enum.IsDefined(typeof(GlKeys), args.KeyCode) ? (GlKeys)args.KeyCode : GlKeys.Unknown;
        if (expanded && key == GlKeys.Escape)
        {
            Close();
        }
        else if (expanded && (args.CtrlPressed || args.CommandPressed) && key == GlKeys.A)
        {
            SetSearchText(string.Empty);
        }
        else if (expanded && (args.CtrlPressed || args.CommandPressed) && key == GlKeys.V)
        {
            AppendSearchText(api.Input.ClipboardText);
        }
        else if (expanded && key is GlKeys.Back or GlKeys.Delete)
        {
            if (searchText.Length > 0)
            {
                SetSearchText(searchText[..^1]);
            }
        }
        else if (key == GlKeys.Enter && expanded)
        {
            if (hoveredIndex >= 0 && hoveredIndex < values.Length)
            {
                selectedIndex = hoveredIndex;
                changed?.Invoke(values[selectedIndex], true);
                api.Gui.PlaySound("toggleswitch");
            }
            Close();
        }
        else if (key is GlKeys.Enter or GlKeys.Space)
        {
            expanded = !expanded;
            hoveredIndex = selectedIndex;
            searchFocused = expanded;
            if (expanded)
            {
                popupScrollStartMilliseconds = api.ElapsedMilliseconds;
            }
        }
        else if (key is GlKeys.Up or GlKeys.Down)
        {
            int delta = key == GlKeys.Up ? -1 : 1;
            if (expanded)
            {
                if (matchingIndices.Length > 0)
                {
                    int matchIndex = Array.IndexOf(matchingIndices, hoveredIndex);
                    matchIndex = matchIndex < 0
                        ? 0
                        : Math.Clamp(matchIndex + delta, 0, matchingIndices.Length - 1);
                    hoveredIndex = matchingIndices[matchIndex];
                    searchFocused = false;
                    popupScrollStartMilliseconds = api.ElapsedMilliseconds;
                }
            }
            else
            {
                selectedIndex = Math.Clamp(selectedIndex + delta, 0, values.Length - 1);
                changed?.Invoke(values[selectedIndex], true);
            }
        }
        else
        {
            return;
        }

        RebuildTextures();
        args.Handled = true;
    }

    public override void OnKeyPress(ICoreClientAPI api, KeyEvent args)
    {
        if (!HasFocus || !Enabled || !expanded
            || args.CtrlPressed || args.CommandPressed || args.AltPressed
            || args.KeyChar == '\0' || char.IsControl(args.KeyChar))
        {
            return;
        }

        AppendSearchText(args.KeyChar.ToString());
        args.Handled = true;
    }

    private void AppendSearchText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        string normalized = value.Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (normalized.Length == 0)
        {
            return;
        }

        SetSearchText(searchText + normalized);
    }

    private void SetSearchText(string? value)
    {
        string next = LimitSearchText(value);
        if (searchText == next)
        {
            return;
        }

        searchText = next;
        searchFocused = true;
        RebuildPopupTexture();
    }

    private static string LimitSearchText(string? value)
    {
        string normalized = (value ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
        return normalized.Length <= VoiceProtocol.MaxControlStringLength
            ? normalized
            : normalized[..VoiceProtocol.MaxControlStringLength];
    }

    private void RebuildTextures()
    {
        RebuildValueTexture();
        RebuildPopupTexture();
    }

    private void RebuildValueTexture()
    {
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        int arrowWidth = Math.Max((int)GuiElement.scaled(24), width / 7);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        bool pressedState = expanded && Enabled;
        context.SetSourceRGBA(
            pressedState ? 1.0 : 0.18,
            pressedState ? 1.0 : 0.21,
            pressedState ? 1.0 : 0.25,
            Enabled ? pressedState ? 0.98 : 0.96 : 0.45);
        context.Rectangle(0, 0, width, height);
        context.FillPreserve();
        context.SetSourceRGBA(
            pressedState ? 0.02 : 0.88,
            pressedState ? 0.025 : 0.92,
            pressedState ? 0.032 : 0.98,
            Enabled ? pressedState ? 0.96 : 0.72 : 0.35);
        context.LineWidth = GuiElement.scaled(1);
        context.Stroke();
        font.SetupContext(context);
        string text = names.Length == 0 ? string.Empty : FitText(context, names[selectedIndex], width - arrowWidth - GuiElement.scaled(16));
        FontExtents extents = context.FontExtents;
        context.SetSourceRGBA(
            pressedState ? 0.02 : 0.96,
            pressedState ? 0.025 : 0.97,
            pressedState ? 0.032 : 1.0,
            1.0);
        DrawTextLineAt(context, text, GuiElement.scaled(9), (height - extents.Height) / 2d);
        context.SetSourceRGBA(
            pressedState ? 0.92 : 0.26,
            pressedState ? 0.93 : 0.30,
            pressedState ? 0.95 : 0.35,
            0.98);
        context.Save();
        context.Rectangle(0, 0, width, height);
        context.Clip();
        context.Rectangle(width - arrowWidth, 0, arrowWidth, height);
        context.Fill();
        context.Restore();
        context.NewPath();
        context.MoveTo(width - arrowWidth / 2d - GuiElement.scaled(5), height / 2d - GuiElement.scaled(2));
        context.LineTo(width - arrowWidth / 2d + GuiElement.scaled(5), height / 2d - GuiElement.scaled(2));
        context.LineTo(width - arrowWidth / 2d, height / 2d + GuiElement.scaled(4));
        context.ClosePath();
        context.SetSourceRGBA(
            pressedState ? 0.02 : 1.0,
            pressedState ? 0.025 : 1.0,
            pressedState ? 0.032 : 1.0,
            1.0);
        context.Fill();
        GuiElement.GenerateTexture(api, surface, ref valueTexture.TextureId);
    }

    private void RebuildPopupTexture()
    {
        rowHeight = Math.Max(1, (int)GuiElement.scaled(30));
        popupWidth = Math.Max(1, Bounds.OuterWidthInt);
        matchingIndices = Enumerable.Range(0, names.Length)
            .Where(index => string.IsNullOrWhiteSpace(searchText)
                || names[index].Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || values[index].Contains(searchText, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (!matchingIndices.Contains(hoveredIndex))
        {
            hoveredIndex = matchingIndices.Length > 0 ? matchingIndices[0] : -1;
        }

        int popupRows = Math.Max(2, matchingIndices.Length + 1);
        popupHeight = rowHeight * popupRows;
        using ImageSurface surface = new(Format.Argb32, popupWidth, popupHeight);
        using Context context = new(surface);
        context.SetSourceRGBA(0.02, 0.025, 0.032, 0.98);
        context.Rectangle(0, 0, popupWidth, popupHeight);
        context.FillPreserve();
        context.SetSourceRGBA(0.9, 0.94, 1.0, 0.82);
        context.LineWidth = GuiElement.scaled(1);
        context.Stroke();
        font.SetupContext(context);
        FontExtents extents = context.FontExtents;
        context.SetSourceRGBA(searchFocused ? 0.16 : 0.10, searchFocused ? 0.20 : 0.13, searchFocused ? 0.26 : 0.17, 0.98);
        context.Rectangle(1, 1, popupWidth - 2, rowHeight - 2);
        context.FillPreserve();
        context.SetSourceRGBA(searchFocused ? 0.86 : 0.58, searchFocused ? 0.91 : 0.64, searchFocused ? 0.98 : 0.72, 0.86);
        context.LineWidth = GuiElement.scaled(1);
        context.Stroke();
        string searchDisplay = string.IsNullOrEmpty(searchText) ? SVCLang.Get("dropdown-search-placeholder") : searchText;
        string visibleSearchText = FitText(context, searchDisplay, popupWidth - GuiElement.scaled(26));
        context.SetSourceRGBA(
            string.IsNullOrEmpty(searchText) ? 0.58 : 0.98,
            string.IsNullOrEmpty(searchText) ? 0.63 : 0.99,
            string.IsNullOrEmpty(searchText) ? 0.70 : 1.0,
            1.0);
        DrawTextLineAt(context, visibleSearchText, GuiElement.scaled(10), (rowHeight - extents.Height) / 2d);
        if (searchFocused)
        {
            double caretX = GuiElement.scaled(10)
                + (string.IsNullOrEmpty(searchText) ? 0d : context.TextExtents(visibleSearchText).XAdvance)
                + GuiElement.scaled(2);
            context.SetSourceRGBA(0.96, 0.97, 1.0, 0.96);
            context.LineWidth = GuiElement.scaled(1);
            context.MoveTo(caretX, GuiElement.scaled(7));
            context.LineTo(caretX, rowHeight - GuiElement.scaled(7));
            context.Stroke();
        }

        for (int matchIndex = 0; matchIndex < matchingIndices.Length; matchIndex++)
        {
            int i = matchingIndices[matchIndex];
            int row = matchIndex + 1;
            if (i == selectedIndex)
            {
                context.SetSourceRGBA(0.18, 0.24, 0.31, 0.96);
                context.Rectangle(1, row * rowHeight + 1, popupWidth - 2, rowHeight - 2);
                context.Fill();
            }
            if (i == hoveredIndex)
            {
                context.SetSourceRGBA(0.34, 0.39, 0.46, 0.94);
                context.Rectangle(1, row * rowHeight + 1, popupWidth - 2, rowHeight - 2);
                context.Fill();
            }

            context.SetSourceRGBA(1, 1, 1, 1);
            double textLeft = GuiElement.scaled(10);
            double textWidth = popupWidth - GuiElement.scaled(20);
            double textOffset = GetHorizontalScrollOffset(context, names[i], textWidth);
            context.Save();
            context.Rectangle(GuiElement.scaled(6), row * rowHeight, popupWidth - GuiElement.scaled(12), rowHeight);
            context.Clip();
            DrawTextLineAt(context, names[i], textLeft - textOffset,
                row * rowHeight + (rowHeight - extents.Height) / 2d);
            context.Restore();
            if (matchIndex < matchingIndices.Length - 1)
            {
                context.SetSourceRGBA(0.78, 0.83, 0.9, 0.16);
                context.LineWidth = GuiElement.scaled(1);
                context.MoveTo(GuiElement.scaled(8), (row + 1) * rowHeight - GuiElement.scaled(0.5));
                context.LineTo(popupWidth - GuiElement.scaled(8), (row + 1) * rowHeight - GuiElement.scaled(0.5));
                context.Stroke();
            }
        }
        if (matchingIndices.Length == 0)
        {
            context.SetSourceRGBA(0.72, 0.76, 0.82, 1.0);
            DrawTextLineAt(context, SVCLang.Get("dropdown-no-matches"), GuiElement.scaled(10),
                rowHeight + (rowHeight - extents.Height) / 2d);
        }
        GuiElement.GenerateTexture(api, surface, ref popupTexture.TextureId);
    }

    private double GetPopupTop()
    {
        double below = Bounds.renderY + Bounds.InnerHeight;
        double frameHeight = api.Render.FrameHeight;
        return below + popupHeight <= frameHeight - GuiElement.scaled(6)
            ? below
            : Math.Max(GuiElement.scaled(6), Bounds.renderY - popupHeight);
    }

    private static string FitText(Context context, string text, double maxWidth)
    {
        if (context.TextExtents(text).XAdvance <= maxWidth)
        {
            return text;
        }

        const string ellipsis = "...";
        int length = text.Length;
        while (length > 0 && context.TextExtents(text[..length] + ellipsis).XAdvance > maxWidth)
        {
            length--;
        }
        return text[..length] + ellipsis;
    }

    private bool HasOverflowingPopupText()
    {
        if (!expanded || matchingIndices.Length == 0)
        {
            return false;
        }

        using ImageSurface surface = new(Format.Argb32, 1, 1);
        using Context context = new(surface);
        font.SetupContext(context);
        double maxWidth = popupWidth - GuiElement.scaled(20);
        return matchingIndices.Any(index => context.TextExtents(names[index]).XAdvance > maxWidth);
    }

    private double GetHorizontalScrollOffset(Context context, string text, double maxWidth)
    {
        double textWidth = context.TextExtents(text).XAdvance;
        if (textWidth <= maxWidth)
        {
            return 0;
        }

        long elapsed = Math.Max(0, api.ElapsedMilliseconds - popupScrollStartMilliseconds)
            % PopupScrollCycleMilliseconds;
        if (elapsed <= PopupScrollPauseMilliseconds)
        {
            return 0;
        }

        double distance = textWidth - maxWidth + GuiElement.scaled(PopupScrollGap);
        double progress = Math.Clamp(
            (elapsed - PopupScrollPauseMilliseconds) / (double)PopupScrollDurationMilliseconds,
            0d,
            1d);
        return distance * progress;
    }

    private static void DrawTextLineAt(Context context, string text, double x, double top)
    {
        FontExtents extents = context.FontExtents;
        context.MoveTo(x, top + extents.Ascent);
        context.ShowText(text);
    }

    public override void Dispose()
    {
        base.Dispose();
        valueTexture.Dispose();
        popupTexture.Dispose();
    }
}

internal sealed class VoiceSettingsIconButton : GuiElementControl
{
    private readonly string iconName;
    private readonly Action<bool>? clicked;
    private readonly bool darkIcon;
    private int textureId;
    private bool pressed;

    public override bool Focusable => Enabled;

    public VoiceSettingsIconButton(
        ICoreClientAPI capi,
        ElementBounds bounds,
        string iconName,
        Action<bool>? clicked,
        bool darkIcon = false)
        : base(capi, bounds)
    {
        this.iconName = iconName;
        this.clicked = clicked;
        this.darkIcon = darkIcon;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        Redraw();
    }

    private void Redraw()
    {
        using ImageSurface surface = new(Format.Argb32, Bounds.OuterWidthInt, Bounds.OuterHeightInt);
        using Context ctx = new(surface);
        ctx.Rectangle(0, 0, Bounds.OuterWidth, Bounds.OuterHeight);
        bool pressedState = pressed && Enabled;
        ctx.SetSourceRGBA(
            pressedState ? 1.0 : 0.62,
            pressedState ? 1.0 : 0.66,
            pressedState ? 1.0 : 0.72,
            Enabled ? pressedState ? 0.98 : 0.30 : 0.14);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(
            pressedState ? 0.02 : 0.92,
            pressedState ? 0.025 : 0.95,
            pressedState ? 0.032 : 1.0,
            Enabled ? pressedState ? 0.96 : 0.88 : 0.42);
        ctx.LineWidth = GuiElement.scaled(1);
        ctx.Stroke();
        double[] iconColor = pressedState || darkIcon
            ? new[] { 0.02, 0.025, 0.032, 1.0 }
            : new[] { 1.0, 1.0, 1.0, 1.0 };
        double iconAreaWidth = Bounds.OuterWidth - GuiElement.scaled(10);
        double iconAreaHeight = Bounds.OuterHeight - GuiElement.scaled(10);
        double aspect = iconName == "svc-fa-users" ? 640d / 512d : iconName == "svc-fa-xmark" ? 384d / 512d : 1d;
        double iconWidth = iconAreaWidth;
        double iconHeight = iconWidth / aspect;
        if (iconHeight > iconAreaHeight)
        {
            iconHeight = iconAreaHeight;
            iconWidth = iconHeight * aspect;
        }
        api.Gui.Icons.DrawIcon(
            ctx,
            iconName,
            (Bounds.OuterWidth - iconWidth) / 2d,
            (Bounds.OuterHeight - iconHeight) / 2d,
            iconWidth,
            iconHeight,
            iconColor);
        if (iconName == "svc-fa-xmark")
        {
            // Fallback for SVG loaders that do not resolve the custom asset.
            double inset = GuiElement.scaled(7);
            ctx.SetSourceRGBA(iconColor[0], iconColor[1], iconColor[2], iconColor[3]);
            ctx.LineWidth = Math.Max(GuiElement.scaled(2), Bounds.OuterWidth * 0.09);
            ctx.LineCap = LineCap.Butt;
            ctx.MoveTo(inset, inset);
            ctx.LineTo(Bounds.OuterWidth - inset, Bounds.OuterHeight - inset);
            ctx.MoveTo(Bounds.OuterWidth - inset, inset);
            ctx.LineTo(inset, Bounds.OuterHeight - inset);
            ctx.Stroke();
        }
        else if (darkIcon && iconName == "svc-fa-gear")
        {
            DrawGearFallback(ctx, Bounds.OuterWidth, Bounds.OuterHeight, pressedState);
        }
        else if (darkIcon && iconName == "svc-fa-users")
        {
            DrawUsersFallback(ctx, Bounds.OuterWidth, Bounds.OuterHeight);
        }
        GuiElement.GenerateTexture(api, surface, ref textureId);
    }

    private static void DrawGearFallback(Context ctx, double width, double height, bool pressedState)
    {
        double cx = width / 2d;
        double cy = height / 2d;
        ctx.Save();
        ctx.SetSourceRGBA(0.02, 0.025, 0.032, 1.0);
        ctx.LineWidth = GuiElement.scaled(4);
        ctx.LineCap = LineCap.Butt;
        for (int index = 0; index < 8; index++)
        {
            double angle = index * Math.PI / 4d;
            ctx.MoveTo(cx + Math.Cos(angle) * GuiElement.scaled(6), cy + Math.Sin(angle) * GuiElement.scaled(6));
            ctx.LineTo(cx + Math.Cos(angle) * GuiElement.scaled(11), cy + Math.Sin(angle) * GuiElement.scaled(11));
        }
        ctx.Stroke();
        ctx.Arc(cx, cy, GuiElement.scaled(8), 0, Math.PI * 2);
        ctx.Fill();
        ctx.SetSourceRGBA(
            pressedState ? 1.0 : 0.62,
            pressedState ? 1.0 : 0.66,
            pressedState ? 1.0 : 0.72,
            1.0);
        ctx.Arc(cx, cy, GuiElement.scaled(3), 0, Math.PI * 2);
        ctx.Fill();
        ctx.Restore();
    }

    private static void DrawUsersFallback(Context ctx, double width, double height)
    {
        double cx = width / 2d;
        double top = GuiElement.scaled(7);
        ctx.Save();
        ctx.SetSourceRGBA(0.02, 0.025, 0.032, 1.0);
        ctx.Arc(cx - GuiElement.scaled(5), top + GuiElement.scaled(4), GuiElement.scaled(4), 0, Math.PI * 2);
        ctx.Arc(cx + GuiElement.scaled(5), top + GuiElement.scaled(4), GuiElement.scaled(4), 0, Math.PI * 2);
        ctx.Fill();
        GuiElement.RoundRectangle(ctx, cx - GuiElement.scaled(11), top + GuiElement.scaled(10), GuiElement.scaled(12), GuiElement.scaled(10), GuiElement.scaled(2));
        GuiElement.RoundRectangle(ctx, cx - GuiElement.scaled(1), top + GuiElement.scaled(10), GuiElement.scaled(12), GuiElement.scaled(10), GuiElement.scaled(2));
        ctx.Fill();
        ctx.Restore();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!VoiceSettingsVisibility.IsVisibleInClip(this)) return;
        using VoiceSettingsClipScope clip = VoiceSettingsVisibility.EnterClip(api, this);
        api.Render.Render2DTexturePremultipliedAlpha(textureId, Bounds);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseDownOnElement(api, args);
        if (!Enabled) return;
        pressed = true;
        Redraw();
    }

    public override void OnMouseUpOnElement(ICoreClientAPI api, MouseEvent args)
    {
        bool wasPressed = pressed;
        pressed = false;
        base.OnMouseUpOnElement(api, args);
        Redraw();
        if (wasPressed && Enabled)
        {
            api.Gui.PlaySound("menubutton");
            clicked?.Invoke(true);
        }
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseUp(api, args);
        if (pressed)
        {
            pressed = false;
            Redraw();
        }
    }

    public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
    {
        if (HasFocus && Enabled && (args.KeyCode == 49 || args.KeyCode == 51))
        {
            args.Handled = true;
            clicked?.Invoke(true);
            api.Gui.PlaySound("menubutton");
        }
    }

    public override void Dispose()
    {
        if (textureId > 0)
        {
            api.Render.GLDeleteTexture(textureId);
            textureId = 0;
        }
        base.Dispose();
    }
}

internal sealed class VoiceSettingsClickArea : GuiElementControl
{
    private readonly Action<bool>? clicked;
    private bool pressed;

    public override bool Focusable => Enabled;

    public VoiceSettingsClickArea(ICoreClientAPI capi, ElementBounds bounds, Action<bool>? clicked)
        : base(capi, bounds)
    {
        this.clicked = clicked;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseDownOnElement(api, args);
        if (!Enabled) return;
        pressed = true;
        args.Handled = true;
    }

    public override void OnMouseUpOnElement(ICoreClientAPI api, MouseEvent args)
    {
        bool wasPressed = pressed;
        pressed = false;
        base.OnMouseUpOnElement(api, args);
        if (wasPressed && Enabled)
        {
            api.Gui.PlaySound("menubutton");
            clicked?.Invoke(true);
            args.Handled = true;
        }
    }
}

internal sealed class VoiceSettingsImageButton : GuiElementControl
{
    private readonly ImageSurface imageSurface;
    private readonly Action<bool>? clicked;
    private int textureId;
    private bool pressed;

    public override bool Focusable => Enabled;

    public VoiceSettingsImageButton(
        ICoreClientAPI capi,
        ElementBounds bounds,
        AssetLocation image,
        Action<bool>? clicked)
        : base(capi, bounds)
    {
        this.clicked = clicked;
        imageSurface = GuiElement.getImageSurfaceFromAsset(capi, image);
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        Redraw();
    }

    private void Redraw()
    {
        using ImageSurface surface = new(Format.Argb32, Bounds.OuterWidthInt, Bounds.OuterHeightInt);
        using Context ctx = new(surface);
        double width = Bounds.OuterWidth;
        double height = Bounds.OuterHeight;
        ctx.Rectangle(0, 0, width, height);
        bool pressedState = pressed && Enabled;
        ctx.SetSourceRGBA(
            pressedState ? 1.0 : 0.62,
            pressedState ? 1.0 : 0.66,
            pressedState ? 1.0 : 0.72,
            Enabled ? pressedState ? 0.98 : 0.30 : 0.14);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(
            pressedState ? 0.02 : 0.92,
            pressedState ? 0.025 : 0.95,
            pressedState ? 0.032 : 1.0,
            Enabled ? pressedState ? 0.96 : 0.88 : 0.42);
        ctx.LineWidth = GuiElement.scaled(1);
        ctx.Stroke();

        double padding = GuiElement.scaled(5);
        double imageHeight = Math.Max(1, height - padding * 2);
        double imageWidth = imageSurface.Width * imageHeight / Math.Max(1, imageSurface.Height);
        if (imageWidth > width - padding * 2)
        {
            imageWidth = width - padding * 2;
            imageHeight = imageSurface.Height * imageWidth / Math.Max(1, imageSurface.Width);
        }
        DrawImage(ctx, imageSurface, (width - imageWidth) / 2d, (height - imageHeight) / 2d,
            imageWidth, imageHeight);
        GuiElement.GenerateTexture(api, surface, ref textureId);
    }

    private static void DrawImage(
        Context ctx,
        ImageSurface image,
        double x,
        double y,
        double width,
        double height)
    {
        ctx.Save();
        ctx.Translate(x, y);
        ctx.Scale(width / image.Width, height / image.Height);
        ctx.SetSourceSurface(image, 0, 0);
        ctx.Rectangle(0, 0, image.Width, image.Height);
        ctx.Fill();
        ctx.Restore();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!VoiceSettingsVisibility.IsVisibleInClip(this)) return;
        using VoiceSettingsClipScope clip = VoiceSettingsVisibility.EnterClip(api, this);
        api.Render.Render2DTexturePremultipliedAlpha(textureId, Bounds);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseDownOnElement(api, args);
        if (!Enabled) return;
        pressed = true;
        Redraw();
    }

    public override void OnMouseUpOnElement(ICoreClientAPI api, MouseEvent args)
    {
        bool wasPressed = pressed;
        pressed = false;
        base.OnMouseUpOnElement(api, args);
        Redraw();
        if (wasPressed && Enabled)
        {
            api.Gui.PlaySound("menubutton");
            clicked?.Invoke(true);
        }
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseUp(api, args);
        if (pressed)
        {
            pressed = false;
            Redraw();
        }
    }

    public override void Dispose()
    {
        if (textureId > 0)
        {
            api.Render.GLDeleteTexture(textureId);
            textureId = 0;
        }
        imageSurface.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Compact square toggle used by the settings dialog. The checked mark is
/// rendered through the Font Awesome SVG registered by the dialog.
/// </summary>
internal sealed class VoiceSettingsCheckBox : GuiElementControl
{
    private const string CheckedIcon = "svc-fa-check";
    private readonly Action<bool>? changed;
    private int textureId;
    private bool pressed;

    public bool On { get; private set; }
    public override bool Focusable => Enabled;

    public VoiceSettingsCheckBox(ICoreClientAPI capi, ElementBounds bounds, Action<bool>? changed)
        : base(capi, bounds)
    {
        this.changed = changed;
        Bounds.fixedWidth = 28;
        Bounds.fixedHeight = 28;
    }

    public void SetValue(bool value)
    {
        On = value;
        if (Bounds.OuterWidthInt > 0 && Bounds.OuterHeightInt > 0) Redraw();
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        Redraw();
    }

    public void Redraw()
    {
        if (Bounds.OuterWidthInt <= 0 || Bounds.OuterHeightInt <= 0) return;
        using ImageSurface surface = new(Format.Argb32, Bounds.OuterWidthInt, Bounds.OuterHeightInt);
        using Context ctx = new(surface);
        double width = Bounds.OuterWidth;
        double height = Bounds.OuterHeight;
        ctx.Rectangle(0, 0, width, height);
        bool pressedState = pressed && Enabled;
        ctx.SetSourceRGBA(
            pressedState ? 1.0 : 0.22,
            pressedState ? 1.0 : 0.25,
            pressedState ? 1.0 : 0.30,
            Enabled ? pressedState ? 0.98 : 0.96 : 0.45);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(
            pressedState ? 0.02 : 0.86,
            pressedState ? 0.025 : 0.90,
            pressedState ? 0.032 : 0.96,
            Enabled ? pressedState ? 0.96 : 0.78 : 0.35);
        ctx.LineWidth = GuiElement.scaled(1);
        ctx.Stroke();
        if (On)
        {
            double[] iconColor = pressedState
                ? new[] { 0.02, 0.025, 0.032, 1.0 }
                : new[] { 0.96, 0.97, 1.0, 1.0 };
            api.Gui.Icons.DrawIcon(ctx, CheckedIcon, GuiElement.scaled(5), GuiElement.scaled(5), width - GuiElement.scaled(10), height - GuiElement.scaled(10), iconColor);
            // Keep the FA icon as the primary renderer and provide a Cairo fallback
            // for clients whose SVG rasterizer cannot resolve custom mod assets.
            ctx.NewPath();
            ctx.MoveTo(width * 0.22, height * 0.52);
            ctx.LineTo(width * 0.43, height * 0.73);
            ctx.LineTo(width * 0.80, height * 0.27);
            ctx.SetSourceRGBA(iconColor[0], iconColor[1], iconColor[2], iconColor[3]);
            ctx.LineWidth = Math.Max(GuiElement.scaled(2), width * 0.11);
            ctx.LineCap = LineCap.Butt;
            ctx.LineJoin = LineJoin.Miter;
            ctx.Stroke();
        }
        GuiElement.GenerateTexture(api, surface, ref textureId);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!VoiceSettingsVisibility.IsVisibleInClip(this)) return;
        using VoiceSettingsClipScope clip = VoiceSettingsVisibility.EnterClip(api, this);
        api.Render.Render2DTexturePremultipliedAlpha(textureId, Bounds);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseDownOnElement(api, args);
        if (!Enabled) return;
        pressed = true;
        On = !On;
        Redraw();
        api.Gui.PlaySound("toggleswitch");
        changed?.Invoke(On);
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseUp(api, args);
        if (pressed)
        {
            pressed = false;
            Redraw();
        }
    }

    public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
    {
        if (HasFocus && Enabled && (args.KeyCode == 49 || args.KeyCode == 51))
        {
            args.Handled = true;
            On = !On;
            changed?.Invoke(On);
            Redraw();
            api.Gui.PlaySound("toggleswitch");
        }
    }

    public override void Dispose()
    {
        if (textureId > 0)
        {
            api.Render.GLDeleteTexture(textureId);
            textureId = 0;
        }
        base.Dispose();
    }
}

/// <summary>
/// Player mute control that uses the same HUD artwork as the voice HUD.
/// </summary>
internal sealed class VoiceSettingsMuteButton : GuiElementControl
{
    private static readonly AssetLocation MutedIcon = new("simplevoicechat", "gui/svc_mic_muted.png");
    private static readonly AssetLocation UnmutedIcon = new("simplevoicechat", "gui/phone-volume-solid.png");
    private readonly Action<bool>? changed;
    private readonly ImageSurface mutedSurface;
    private readonly ImageSurface unmutedSurface;
    private int textureId;
    private bool pressed;

    public bool On { get; private set; }
    public override bool Focusable => Enabled;

    public VoiceSettingsMuteButton(ICoreClientAPI capi, ElementBounds bounds, Action<bool>? changed)
        : base(capi, bounds)
    {
        this.changed = changed;
        mutedSurface = GuiElement.getImageSurfaceFromAsset(capi, MutedIcon);
        unmutedSurface = GuiElement.getImageSurfaceFromAsset(capi, UnmutedIcon);
    }

    public void SetValue(bool value)
    {
        On = value;
        if (Bounds.OuterWidthInt > 0 && Bounds.OuterHeightInt > 0) Redraw();
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        Redraw();
    }

    private void DrawImage(Context ctx, ImageSurface image, double x, double y, double width, double height)
    {
        ctx.Save();
        ctx.Translate(x, y);
        ctx.Scale(width / image.Width, height / image.Height);
        ctx.SetSourceSurface(image, 0, 0);
        ctx.Rectangle(0, 0, image.Width, image.Height);
        ctx.Fill();
        ctx.Restore();
    }

    public void Redraw()
    {
        if (Bounds.OuterWidthInt <= 0 || Bounds.OuterHeightInt <= 0) return;
        using ImageSurface surface = new(Format.Argb32, Bounds.OuterWidthInt, Bounds.OuterHeightInt);
        using Context ctx = new(surface);
        double width = Bounds.OuterWidth;
        double height = Bounds.OuterHeight;
        ctx.Rectangle(0, 0, width, height);
        bool pressedState = pressed && Enabled;
        ctx.SetSourceRGBA(
            pressedState ? 1.0 : 0.62,
            pressedState ? 1.0 : 0.66,
            pressedState ? 1.0 : 0.72,
            Enabled ? pressedState ? 0.98 : 0.30 : 0.14);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(
            pressedState ? 0.02 : 0.92,
            pressedState ? 0.025 : 0.95,
            pressedState ? 0.032 : 1.0,
            Enabled ? pressedState ? 0.96 : 0.88 : 0.42);
        ctx.LineWidth = GuiElement.scaled(1);
        ctx.Stroke();
        ImageSurface icon = On ? mutedSurface : unmutedSurface;
        double iconHeight = Math.Min(GuiElement.scaled(20), height - GuiElement.scaled(8));
        double iconWidth = icon.Width * iconHeight / icon.Height;
        DrawImage(ctx, icon, (width - iconWidth) / 2, (height - iconHeight) / 2, iconWidth, iconHeight);
        GuiElement.GenerateTexture(api, surface, ref textureId);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!VoiceSettingsVisibility.IsVisibleInClip(this)) return;
        using VoiceSettingsClipScope clip = VoiceSettingsVisibility.EnterClip(api, this);
        api.Render.Render2DTexturePremultipliedAlpha(textureId, Bounds);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseDownOnElement(api, args);
        if (!Enabled) return;
        pressed = true;
        On = !On;
        Redraw();
        api.Gui.PlaySound("toggleswitch");
        changed?.Invoke(On);
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseUp(api, args);
        if (pressed)
        {
            pressed = false;
            Redraw();
        }
    }

    public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
    {
        if (HasFocus && Enabled && (args.KeyCode == 49 || args.KeyCode == 51))
        {
            args.Handled = true;
            On = !On;
            changed?.Invoke(On);
            Redraw();
            api.Gui.PlaySound("toggleswitch");
        }
    }

    public override void Dispose()
    {
        if (textureId > 0)
        {
            api.Render.GLDeleteTexture(textureId);
            textureId = 0;
        }
        mutedSurface.Dispose();
        unmutedSurface.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Square first-page toggle that renders the supplied pixel-art asset for each
/// state. The image is fitted by height so narrow artwork remains centered.
/// </summary>
internal sealed class VoiceSettingsIconToggleButton : GuiElementControl
{
    private readonly Action<bool>? changed;
    private readonly ImageSurface onSurface;
    private readonly ImageSurface offSurface;
    private int textureId;
    private bool pressed;

    public bool On { get; private set; }
    public override bool Focusable => Enabled;

    public VoiceSettingsIconToggleButton(
        ICoreClientAPI capi,
        ElementBounds bounds,
        AssetLocation onIcon,
        AssetLocation offIcon,
        bool value,
        Action<bool>? changed)
        : base(capi, bounds)
    {
        this.changed = changed;
        double size = Math.Min(bounds.fixedWidth, bounds.fixedHeight);
        Bounds.fixedWidth = size;
        Bounds.fixedHeight = size;
        On = value;
        onSurface = GuiElement.getImageSurfaceFromAsset(capi, onIcon);
        offSurface = GuiElement.getImageSurfaceFromAsset(capi, offIcon);
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        Redraw();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!VoiceSettingsVisibility.IsVisibleInClip(this)) return;
        using VoiceSettingsClipScope clip = VoiceSettingsVisibility.EnterClip(api, this);
        api.Render.Render2DTexturePremultipliedAlpha(textureId, Bounds);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseDownOnElement(api, args);
        if (!Enabled)
        {
            return;
        }

        pressed = true;
        On = !On;
        Redraw();
        api.Gui.PlaySound("toggleswitch");
        changed?.Invoke(On);
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseUp(api, args);
        if (pressed)
        {
            pressed = false;
            Redraw();
        }
    }

    public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
    {
        if (!HasFocus || !Enabled || args.KeyCode is not (49 or 51))
        {
            return;
        }

        args.Handled = true;
        On = !On;
        changed?.Invoke(On);
        Redraw();
        api.Gui.PlaySound("toggleswitch");
    }

    private void Redraw()
    {
        if (Bounds.OuterWidthInt <= 0 || Bounds.OuterHeightInt <= 0)
        {
            return;
        }

        using ImageSurface surface = new(Format.Argb32, Bounds.OuterWidthInt, Bounds.OuterHeightInt);
        using Context ctx = new(surface);
        double width = Bounds.OuterWidth;
        double height = Bounds.OuterHeight;
        ctx.Rectangle(0, 0, width, height);
        bool pressedState = pressed && Enabled;
        ctx.SetSourceRGBA(
            pressedState ? 1.0 : 0.62,
            pressedState ? 1.0 : 0.66,
            pressedState ? 1.0 : 0.72,
            pressedState ? 0.98 : On ? 0.36 : 0.22);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(
            pressedState ? 0.02 : 0.92,
            pressedState ? 0.025 : 0.95,
            pressedState ? 0.032 : 1.0,
            Enabled ? pressedState ? 0.96 : 0.95 : 0.45);
        ctx.LineWidth = GuiElement.scaled(1);
        ctx.Stroke();

        ImageSurface icon = On ? onSurface : offSurface;
        double padding = GuiElement.scaled(7);
        double iconHeight = Math.Max(1, height - padding * 2);
        double iconWidth = icon.Width * iconHeight / Math.Max(1, icon.Height);
        if (iconWidth > width - padding * 2)
        {
            iconWidth = width - padding * 2;
            iconHeight = icon.Height * iconWidth / Math.Max(1, icon.Width);
        }

        ctx.Save();
        ctx.Translate((width - iconWidth) / 2d, (height - iconHeight) / 2d);
        ctx.Scale(iconWidth / icon.Width, iconHeight / icon.Height);
        ctx.SetSourceSurface(icon, 0, 0);
        ctx.Rectangle(0, 0, icon.Width, icon.Height);
        ctx.Fill();
        ctx.Restore();
        GuiElement.GenerateTexture(api, surface, ref textureId);
    }

    public override void Dispose()
    {
        if (textureId > 0)
        {
            api.Render.GLDeleteTexture(textureId);
            textureId = 0;
        }
        onSurface.Dispose();
        offSurface.Dispose();
        base.Dispose();
    }
}
