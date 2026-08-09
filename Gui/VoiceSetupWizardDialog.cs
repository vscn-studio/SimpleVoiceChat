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
    private const double PanelHeight = 430;
    private const string ComposerKey = "simplevoicechat-setup-wizard";
    private const string FontAwesomeCloseIcon = "svc-fa-xmark";
    private static readonly AssetLocation FontAwesomeCloseAsset = new("simplevoicechat", "icons/fontawesome/xmark.svg");

    private readonly ClientVoiceController controller;
    private readonly SimpleVoiceChatClientConfig config;
    private VoiceSetupStep step;
    private int frameWidth;
    private int frameHeight;
    private bool monitoringMicrophone;

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
        monitoringMicrophone = false;
        Compose();
        return base.TryOpen();
    }

    public override void OnRenderGUI(float deltaTime)
    {
        if (frameWidth != capi.Render.FrameWidth || frameHeight != capi.Render.FrameHeight)
        {
            Compose();
        }
        if (step == VoiceSetupStep.Activation && SingleComposer != null)
        {
            SingleComposer.GetDynamicText("mic-level")?.SetNewText(
                SVCLang.Get("setup-mic-level", Math.Round(controller.MicrophoneRms * 100f)));
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

    public override void OnGuiClosed()
    {
        controller.SetSetupMicrophoneMonitoring(false);
        monitoringMicrophone = false;
        base.OnGuiClosed();
    }

    private void Compose()
    {
        frameWidth = capi.Render.FrameWidth;
        frameHeight = capi.Render.FrameHeight;
        double panelHeight = step == VoiceSetupStep.Activation ? 460 : step == VoiceSetupStep.Levels ? PanelHeight : 350;
        ElementBounds root = ElementBounds.Fixed(EnumDialogArea.CenterMiddle, 0, 0, PanelWidth, panelHeight);
        ElementBounds panelBounds = ElementBounds.Fixed(0, 0, PanelWidth, panelHeight);

        SingleComposer?.Dispose();
        capi.Gui.Icons.CustomIcons[FontAwesomeCloseIcon] = capi.Gui.Icons.SvgIconSource(FontAwesomeCloseAsset);
        GuiComposer composer = capi.Gui.CreateCompo(ComposerKey, root)
            .AddStaticCustomDraw(panelBounds, DrawPanelBackground)
            .AddInteractiveElement(new VoiceSettingsIconButton(
                capi,
                ElementBounds.Fixed(PanelWidth - 42, 10, 28, 28),
                FontAwesomeCloseIcon,
                _ => Cancel()), "close");

        double x = 36;
        double width = PanelWidth - 72;
        CairoFont titleFont = CairoFont.WhiteSmallishText()
            .WithFontSize(20)
            .WithColor(new[] { 0.98, 0.99, 1.0, 1.0 })
            .WithOrientation(EnumTextOrientation.Center);
        CairoFont bodyFont = CairoFont.WhiteSmallText()
            .WithFontSize(14)
            .WithColor(new[] { 0.87, 0.91, 0.96, 1.0 });
        CairoFont labelFont = CairoFont.WhiteSmallishText()
            .WithFontSize(15)
            .WithColor(new[] { 0.96, 0.97, 1.0, 1.0 });

        composer.AddStaticText(SVCLang.Get("setup-title"), titleFont, ElementBounds.Fixed(x, 10, width, 30));
        bool shouldMonitor = step == VoiceSetupStep.Activation;
        if (monitoringMicrophone != shouldMonitor)
        {
            monitoringMicrophone = shouldMonitor;
            controller.SetSetupMicrophoneMonitoring(shouldMonitor);
        }
        AddStepIndicator(composer, 0, 0, panelHeight);

        switch (step)
        {
            case VoiceSetupStep.Input:
                ComposeInputStep(composer, x, 0, width, panelHeight, bodyFont, labelFont);
                break;
            case VoiceSetupStep.Output:
                ComposeOutputStep(composer, x, 0, width, panelHeight, bodyFont, labelFont);
                break;
            case VoiceSetupStep.Activation:
                ComposeActivationStep(composer, x, 0, width, panelHeight, bodyFont, labelFont);
                break;
            case VoiceSetupStep.Levels:
                ComposeLevelsStep(composer, x, 0, width, panelHeight, bodyFont, labelFont);
                break;
            default:
                ComposeWelcomeStep(composer, x, 0, width, panelHeight, bodyFont, labelFont);
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
        bool voiceActivation = config.PreferVoiceActivation;

        composer
            .AddStaticText(SVCLang.Get("setup-activation-description"), bodyFont, ElementBounds.Fixed(x, panelY + 78, width, 58))
            .AddStaticText(SVCLang.Get("setup-activation-mode"), labelFont, ElementBounds.Fixed(x, panelY + 145, width, 24));
        AddWizardButton(composer, SVCLang.Get("setup-push-to-talk"), () => SetActivationMode(false),
            ElementBounds.Fixed(x, panelY + 177, (width - 10) / 2, 38), "push", primary: !voiceActivation);
        AddWizardButton(composer, SVCLang.Get("setup-voice-activation-mode"), () => SetActivationMode(true),
            ElementBounds.Fixed(x + (width + 10) / 2, panelY + 177, (width - 10) / 2, 38), "voice", primary: voiceActivation);
        composer
            .AddStaticText(
                $"{SVCLang.Get("setup-push-to-talk-key")}: {config.PushToTalkKey}",
                labelFont,
                ElementBounds.Fixed(x, panelY + 238, width, 24));
        composer.AddDynamicText(SVCLang.Get("setup-mic-level", Math.Round(controller.MicrophoneRms * 100f)), labelFont,
            ElementBounds.Fixed(x, panelY + 286, width, 24), "mic-level")
            .AddVoiceActivationThresholdControl(
                () => controller.MicrophoneRms,
                value => { controller.SetNoiseGateFromSettings(value); return true; },
                value => { controller.SetVoiceActivationThresholdFromSetup(value); return true; },
                ElementBounds.Fixed(x, panelY + 320, width, 58),
                "activation-levels");
        VoiceActivationThresholdControl thresholdControl = (VoiceActivationThresholdControl)composer.GetElement("activation-levels");
        thresholdControl.Configure(
            (int)Math.Round(config.NoiseGate * 1000),
            (int)Math.Round(config.VoiceActivationThreshold * 1000));
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
            .AddStaticText(SVCLang.Get("setup-mic-gain-description"), bodyFont, ElementBounds.Fixed(x, panelY + 239, width, 26));

        ConfigureSlider(composer, "output-volume", (int)Math.Round(config.OutputVolume * 100), 0, 200, "%");
        ConfigureSlider(composer, "mic-gain", (int)Math.Round(config.MicGain * 100), 10, 400, "%");
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
        composer.AddStaticText(text, detail, ElementBounds.Fixed(panelX + PanelWidth - 150, panelY + 48, 114, 22));
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

    private static void DrawPanelBackground(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        bounds.CalcWorldBounds();
        GuiElement.RoundRectangle(ctx, bounds.bgDrawX, bounds.bgDrawY, bounds.OuterWidth, bounds.OuterHeight, GuiElement.scaled(4));
        ctx.SetSourceRGBA(0.015, 0.02, 0.028, 0.84);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(0.78, 0.82, 0.9, 0.22);
        ctx.LineWidth = GuiElement.scaled(1);
        ctx.Stroke();
    }

    private static void DrawButtonBackground(Context ctx, ElementBounds bounds, bool primary)
    {
        bounds.CalcWorldBounds();
        ctx.Rectangle(bounds.drawX, bounds.drawY, bounds.InnerWidth, bounds.InnerHeight);
        ctx.SetSourceRGBA(0.62, 0.66, 0.72, primary ? 0.36 : 0.22);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(0.92, 0.95, 1.0, primary ? 0.95 : 0.88);
        ctx.LineWidth = GuiElement.scaled(1);
        ctx.Stroke();
    }
}
