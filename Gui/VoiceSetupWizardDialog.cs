using Cairo;
using SimpleVoiceChat.Config;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SimpleVoiceChat.Gui;

internal enum VoiceSetupStep
{
    Welcome,
    Input,
    Output,
    Activation,
    Levels
}

/// <summary>
/// First-run audio setup. It is intentionally separate from the settings
/// workspace so a new player only has to deal with one decision at a time.
/// </summary>
public sealed class VoiceSetupWizardDialog : GuiDialog
{
    private const double PanelWidth = 560;
    private const string ComposerKey = "simplevoicechat-setup-wizard";

    private readonly ClientVoiceController controller;
    private readonly SimpleVoiceChatClientConfig config;
    private VoiceSetupStep step;
    private int frameWidth;
    private int frameHeight;

    public VoiceSetupWizardDialog(ICoreClientAPI capi, ClientVoiceController controller)
        : base(capi)
    {
        this.controller = controller;
        config = controller.SettingsConfig;
    }

    public override string? ToggleKeyCombinationCode => null;
    public override EnumDialogType DialogType => EnumDialogType.Dialog;
    public override double DrawOrder => 0.72;
    public override double InputOrder => 0.72;
    public override bool PrefersUngrabbedMouse => true;
    public override bool DisableMouseGrab => true;
    public override bool CaptureAllInputs() => true;

    public override bool TryOpen()
    {
        step = VoiceSetupStep.Welcome;
        Compose();
        return base.TryOpen();
    }

    public override void OnRenderGUI(float deltaTime)
    {
        if (frameWidth != capi.Render.FrameWidth || frameHeight != capi.Render.FrameHeight)
        {
            Compose();
        }
        base.OnRenderGUI(deltaTime);
    }

    public override bool OnEscapePressed()
    {
        if (step == VoiceSetupStep.Welcome)
        {
            return base.OnEscapePressed();
        }

        MoveBack();
        return true;
    }

    private void Compose()
    {
        frameWidth = capi.Render.FrameWidth;
        frameHeight = capi.Render.FrameHeight;
        double guiScale = Math.Max(0.001, GuiElement.scaled(1));
        double screenWidth = frameWidth / guiScale;
        double screenHeight = frameHeight / guiScale;
        double panelHeight = step == VoiceSetupStep.Levels ? 430 : 350;
        double panelX = Math.Max(8, (screenWidth - PanelWidth) / 2d);
        double panelY = Math.Max(8, (screenHeight - panelHeight) / 2d);
        ElementBounds root = ElementBounds.Fixed(EnumDialogArea.FixedTop, 0, 0, screenWidth, screenHeight);
        ElementBounds overlayBounds = ElementBounds.Fixed(0, 0, screenWidth, screenHeight);
        ElementBounds panelBounds = ElementBounds.Fixed(panelX, panelY, PanelWidth, panelHeight);

        SingleComposer?.Dispose();
        GuiElementDialogBackground backdrop = new(capi, overlayBounds, withTitlebar: false, strokeWidth: 0, alpha: 0.74f)
        {
            FullBlur = true
        };
        GuiComposer composer = capi.Gui.CreateCompo(ComposerKey, root)
            .AddStaticElement(backdrop)
            .AddStaticCustomDraw(panelBounds, DrawPanelBackground);

        double x = panelX + 36;
        double width = PanelWidth - 72;
        CairoFont titleFont = CairoFont.WhiteSmallishText()
            .WithFontSize(21)
            .WithColor(new[] { 0.98, 0.99, 1.0, 1.0 });
        CairoFont bodyFont = CairoFont.WhiteSmallText()
            .WithFontSize(14)
            .WithColor(new[] { 0.87, 0.91, 0.96, 1.0 });
        CairoFont labelFont = CairoFont.WhiteSmallishText()
            .WithFontSize(15)
            .WithColor(new[] { 0.96, 0.97, 1.0, 1.0 });

        composer.AddStaticText(SVCLang.Get("setup-title"), titleFont, ElementBounds.Fixed(x, panelY + 28, width, 30));
        AddStepIndicator(composer, panelX, panelY, panelHeight);

        switch (step)
        {
            case VoiceSetupStep.Input:
                ComposeInputStep(composer, x, panelY, width, panelHeight, bodyFont, labelFont);
                break;
            case VoiceSetupStep.Output:
                ComposeOutputStep(composer, x, panelY, width, panelHeight, bodyFont, labelFont);
                break;
            case VoiceSetupStep.Activation:
                ComposeActivationStep(composer, x, panelY, width, panelHeight, bodyFont, labelFont);
                break;
            case VoiceSetupStep.Levels:
                ComposeLevelsStep(composer, x, panelY, width, panelHeight, bodyFont, labelFont);
                break;
            default:
                ComposeWelcomeStep(composer, x, panelY, width, panelHeight, bodyFont, labelFont);
                break;
        }

        SingleComposer = composer.Compose();
    }

    private void ComposeWelcomeStep(GuiComposer composer, double x, double panelY, double width, double panelHeight, CairoFont bodyFont, CairoFont labelFont)
    {
        composer
            .AddStaticText(SVCLang.Get("setup-welcome-description"), bodyFont, ElementBounds.Fixed(x, panelY + 78, width, 44))
            .AddStaticText(SVCLang.Get("setup-skip-description"), labelFont, ElementBounds.Fixed(x, panelY + 140, width, 24));
        AddWizardButton(composer, SVCLang.Get("setup-skip-to-settings"), SkipToSettings,
            ElementBounds.Fixed(x, panelY + 172, width, 40), "skip", primary: false);
        AddWizardButton(composer, SVCLang.Get("button-cancel"), Cancel,
            ElementBounds.Fixed(x, panelY + panelHeight - 58, (width - 10) / 2, 38), "cancel", primary: false);
        AddWizardButton(composer, SVCLang.Get("button-confirm"), MoveNext,
            ElementBounds.Fixed(x + (width + 10) / 2, panelY + panelHeight - 58, (width - 10) / 2, 38), "confirm", primary: true);
    }

    private void ComposeInputStep(GuiComposer composer, double x, double panelY, double width, double panelHeight, CairoFont bodyFont, CairoFont labelFont)
    {
        string[] values = controller.GetInputDeviceValues();
        string[] names = ClientVoiceController.GetInputDeviceNames(values);
        int selected = Math.Max(0, Array.IndexOf(values, config.InputDeviceName));
        composer
            .AddStaticText(SVCLang.Get("setup-input-description"), bodyFont, ElementBounds.Fixed(x, panelY + 78, width, 40))
            .AddStaticText(SVCLang.Get("label-input-device"), labelFont, ElementBounds.Fixed(x, panelY + 137, width, 24))
            .AddVoiceDropDown(values, names, selected, OnInputSelected, ElementBounds.Fixed(x, panelY + 168, width, 34), "input-device");
        AddNavigation(composer, x, panelY, width, panelHeight);
    }

    private void ComposeOutputStep(GuiComposer composer, double x, double panelY, double width, double panelHeight, CairoFont bodyFont, CairoFont labelFont)
    {
        string[] values = controller.GetOutputDeviceValues();
        string[] names = ClientVoiceController.GetOutputDeviceNames(values);
        int selected = Math.Max(0, Array.IndexOf(values, config.OutputDeviceName));
        composer
            .AddStaticText(SVCLang.Get("setup-output-description"), bodyFont, ElementBounds.Fixed(x, panelY + 78, width, 40))
            .AddStaticText(SVCLang.Get("label-output-device"), labelFont, ElementBounds.Fixed(x, panelY + 137, width, 24))
            .AddVoiceDropDown(values, names, selected, OnOutputSelected, ElementBounds.Fixed(x, panelY + 168, width, 34), "output-device");
        AddNavigation(composer, x, panelY, width, panelHeight);
    }

    private void ComposeActivationStep(GuiComposer composer, double x, double panelY, double width, double panelHeight, CairoFont bodyFont, CairoFont labelFont)
    {
        bool continuous = config.PreferContinuousTalk;
        string[] keyValues = { "N", "V", "B", "CapsLock" };
        string[] keyNames = { "N", "V", "B", SVCLang.Get("setup-key-caps-lock") };
        int selected = Math.Max(0, Array.IndexOf(keyValues, config.PushToTalkKey));

        composer
            .AddStaticText(SVCLang.Get("setup-activation-description"), bodyFont, ElementBounds.Fixed(x, panelY + 78, width, 40))
            .AddStaticText(SVCLang.Get("setup-activation-mode"), labelFont, ElementBounds.Fixed(x, panelY + 126, width, 24));
        AddWizardButton(composer, SVCLang.Get("setup-push-to-talk"), () => SetActivationMode(false),
            ElementBounds.Fixed(x, panelY + 156, (width - 10) / 2, 38), "push", primary: !continuous);
        AddWizardButton(composer, SVCLang.Get("setup-continuous-talk"), () => SetActivationMode(true),
            ElementBounds.Fixed(x + (width + 10) / 2, panelY + 156, (width - 10) / 2, 38), "continuous", primary: continuous);
        composer
            .AddStaticText(SVCLang.Get("setup-push-to-talk-key"), labelFont, ElementBounds.Fixed(x, panelY + 213, width, 24))
            .AddVoiceDropDown(keyValues, keyNames, selected, OnPushToTalkKeySelected, ElementBounds.Fixed(x, panelY + 244, width, 34), "push-to-talk-key");
        AddNavigation(composer, x, panelY, width, panelHeight);
    }

    private void ComposeLevelsStep(GuiComposer composer, double x, double panelY, double width, double panelHeight, CairoFont bodyFont, CairoFont labelFont)
    {
        const double sliderX = 170;
        const double sliderWidth = 250;
        composer
            .AddStaticText(SVCLang.Get("setup-levels-description"), bodyFont, ElementBounds.Fixed(x, panelY + 78, width, 40))
            .AddStaticText(SVCLang.Get("label-output-volume"), labelFont, ElementBounds.Fixed(x, panelY + 137, sliderX - 20, 24))
            .AddVoiceSlider(value => { controller.SetOutputVolumeFromSettings(value); return true; }, ElementBounds.Fixed(x + sliderX, panelY + 137, sliderWidth, 24), "output-volume")
            .AddStaticText(SVCLang.Get("label-mic-gain"), labelFont, ElementBounds.Fixed(x, panelY + 188, sliderX - 20, 24))
            .AddVoiceSlider(value => { controller.SetMicGainFromSettings(value); return true; }, ElementBounds.Fixed(x + sliderX, panelY + 188, sliderWidth, 24), "mic-gain")
            .AddStaticText(SVCLang.Get("label-noise-gate"), labelFont, ElementBounds.Fixed(x, panelY + 239, sliderX - 20, 24))
            .AddVoiceSlider(value => { controller.SetNoiseGateFromSettings(value); return true; }, ElementBounds.Fixed(x + sliderX, panelY + 239, sliderWidth, 24), "noise-gate");

        ConfigureSlider(composer, "output-volume", (int)Math.Round(config.OutputVolume * 100), 0, 200, "%");
        ConfigureSlider(composer, "mic-gain", (int)Math.Round(config.MicGain * 100), 10, 400, "%");
        ConfigureSlider(composer, "noise-gate", (int)Math.Round(config.NoiseGate * 1000), 0, 200);
        AddNavigation(composer, x, panelY, width, panelHeight, SVCLang.Get("button-finish"));
    }

    private void AddNavigation(GuiComposer composer, double x, double panelY, double width, double panelHeight, string? nextText = null)
    {
        AddWizardButton(composer, SVCLang.Get("button-back"), MoveBack,
            ElementBounds.Fixed(x, panelY + panelHeight - 58, (width - 10) / 2, 38), "back", primary: false);
        AddWizardButton(composer, nextText ?? SVCLang.Get("button-next"), MoveNext,
            ElementBounds.Fixed(x + (width + 10) / 2, panelY + panelHeight - 58, (width - 10) / 2, 38), "next", primary: true);
    }

    private void AddStepIndicator(GuiComposer composer, double panelX, double panelY, double panelHeight)
    {
        if (step == VoiceSetupStep.Welcome)
        {
            return;
        }

        int number = (int)step;
        string text = SVCLang.Get("setup-step", number, 4);
        CairoFont detail = CairoFont.WhiteSmallText()
            .WithFontSize(13)
            .WithColor(new[] { 0.72, 0.78, 0.86, 1.0 })
            .WithOrientation(EnumTextOrientation.Right);
        composer.AddStaticText(text, detail, ElementBounds.Fixed(panelX + PanelWidth - 150, panelY + 33, 114, 22));
    }

    private static void AddWizardButton(GuiComposer composer, string text, ActionConsumable action, ElementBounds bounds, string key, bool primary)
    {
        composer
            .AddStaticCustomDraw(bounds, (ctx, surface, elementBounds) => DrawButtonBackground(ctx, elementBounds, primary))
            .AddButton(text, action, bounds,
                CairoFont.WhiteSmallText().WithFontSize(15).WithColor(new[] { 1.0, 1.0, 1.0, 1.0 }).WithOrientation(EnumTextOrientation.Center),
                EnumButtonStyle.None, key);
    }

    private static void ConfigureSlider(GuiComposer composer, string key, int value, int minimum, int maximum, string suffix = "")
    {
        VoiceSettingsSlider slider = (VoiceSettingsSlider)composer.GetElement(key);
        slider.Configure(value, minimum, maximum, 1, suffix);
    }

    private bool SkipToSettings()
    {
        controller.SkipInitialSetupToSettings();
        return true;
    }

    private bool Cancel()
    {
        TryClose();
        return true;
    }

    private bool MoveBack()
    {
        step = step switch
        {
            VoiceSetupStep.Output => VoiceSetupStep.Input,
            VoiceSetupStep.Activation => VoiceSetupStep.Output,
            VoiceSetupStep.Levels => VoiceSetupStep.Activation,
            _ => VoiceSetupStep.Welcome
        };
        Compose();
        return true;
    }

    private bool MoveNext()
    {
        if (step == VoiceSetupStep.Levels)
        {
            controller.CompleteInitialSetup();
            TryClose();
            return true;
        }

        step = step switch
        {
            VoiceSetupStep.Welcome => VoiceSetupStep.Input,
            VoiceSetupStep.Input => VoiceSetupStep.Output,
            VoiceSetupStep.Output => VoiceSetupStep.Activation,
            _ => VoiceSetupStep.Levels
        };
        Compose();
        return true;
    }

    private bool SetActivationMode(bool continuous)
    {
        controller.SetVoiceActivationFromSetup(continuous);
        Compose();
        return true;
    }

    private void OnInputSelected(string value, bool selected)
    {
        if (selected)
        {
            controller.SetInputDeviceFromSettings(value);
        }
    }

    private void OnOutputSelected(string value, bool selected)
    {
        if (selected)
        {
            controller.SetOutputDeviceFromSettings(value);
        }
    }

    private void OnPushToTalkKeySelected(string value, bool selected)
    {
        if (selected)
        {
            controller.SetPushToTalkKeyFromSetup(value);
        }
    }

    private static void DrawPanelBackground(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        bounds.CalcWorldBounds();
        GuiElement.RoundRectangle(ctx, bounds.bgDrawX, bounds.bgDrawY, bounds.OuterWidth, bounds.OuterHeight, GuiElement.scaled(4));
        ctx.SetSourceRGBA(0.015, 0.02, 0.028, 0.94);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(0.78, 0.82, 0.9, 0.30);
        ctx.LineWidth = GuiElement.scaled(1);
        ctx.Stroke();
    }

    private static void DrawButtonBackground(Context ctx, ElementBounds bounds, bool primary)
    {
        bounds.CalcWorldBounds();
        GuiElement.RoundRectangle(ctx, bounds.drawX, bounds.drawY, bounds.InnerWidth, bounds.InnerHeight, GuiElement.scaled(4));
        if (primary)
        {
            ctx.SetSourceRGBA(0.24, 0.55, 0.69, 1.0);
            ctx.FillPreserve();
            ctx.SetSourceRGBA(0.62, 0.84, 0.96, 0.95);
        }
        else
        {
            ctx.SetSourceRGBA(0.20, 0.23, 0.28, 0.96);
            ctx.FillPreserve();
            ctx.SetSourceRGBA(0.78, 0.83, 0.90, 0.72);
        }
        ctx.LineWidth = GuiElement.scaled(1);
        ctx.Stroke();
    }
}
