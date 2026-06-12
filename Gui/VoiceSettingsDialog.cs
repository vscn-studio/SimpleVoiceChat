using OpenTK.Audio.OpenAL;
using SimpleVoiceChat.Config;
using Vintagestory.API.Client;

namespace SimpleVoiceChat.Gui;

public sealed class VoiceSettingsDialog : GuiDialog
{
    private const string DefaultInputDeviceValue = "__default__";
    private const string InputDeviceKey = "inputDevice";
    private const string OutputVolumeKey = "outputVolume";
    private const string MicGainKey = "micGain";
    private const string NoiseGateKey = "noiseGate";
    private const string ShowMicrophoneHudKey = "showMicrophoneHud";
    private const string OcclusionKey = "occlusion";
    private const string PerformanceModeKey = "performanceMode";

    private readonly SimpleVoiceChatClientConfig config;
    private readonly Func<string> summaryProvider;
    private readonly Action saveConfig;
    private readonly Action refreshHud;
    private readonly Action reinitializeCapture;

    public VoiceSettingsDialog(
        ICoreClientAPI capi,
        SimpleVoiceChatClientConfig config,
        Func<string> summaryProvider,
        Action saveConfig,
        Action refreshHud,
        Action reinitializeCapture)
        : base(capi)
    {
        this.config = config;
        this.summaryProvider = summaryProvider;
        this.saveConfig = saveConfig;
        this.refreshHud = refreshHud;
        this.reinitializeCapture = reinitializeCapture;
        Compose();
    }

    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;

    public override bool TryOpen()
    {
        Compose();
        return base.TryOpen();
    }

    public void Compose()
    {
        ElementBounds dialogBounds = ElementBounds.Fixed(EnumDialogArea.CenterMiddle, -330, -300, 660, 600);
        ElementBounds bgBounds = ElementBounds.Fixed(0, 0, 660, 600);
        ElementBounds statusBounds = ElementBounds.Fixed(28, 48, 604, 118);
        ElementBounds closeBounds = ElementBounds.Fixed(275, 550, 110, 32);
        string[] inputDeviceValues = GetInputDeviceValues();
        string[] inputDeviceNames = GetInputDeviceNames(inputDeviceValues);
        int selectedInputDeviceIndex = GetSelectedInputDeviceIndex(inputDeviceValues);

        double labelX = 28;
        double controlX = 260;
        double labelWidth = 210;
        double controlWidth = 350;
        double y = 194;
        double row = 46;

        SingleComposer = capi.Gui.CreateCompo("simplevoicechat-settings", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar("简单语音对话", () => TryClose())
            .BeginChildElements(bgBounds)
            .AddStaticText(summaryProvider(), CairoFont.WhiteSmallText(), statusBounds)
            .AddStaticText("输入设备", CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y + 4, labelWidth, 24))
            .AddDropDown(inputDeviceValues, inputDeviceNames, selectedInputDeviceIndex, OnInputDeviceChanged, ElementBounds.Fixed(controlX, y, controlWidth, 32), InputDeviceKey)
            .AddStaticText("播放音量", CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSlider(OnOutputVolumeChanged, ElementBounds.Fixed(controlX, y, controlWidth, 24), OutputVolumeKey)
            .AddStaticText("麦克风增益", CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSlider(OnMicGainChanged, ElementBounds.Fixed(controlX, y, controlWidth, 24), MicGainKey)
            .AddStaticText("噪声门", CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSlider(OnNoiseGateChanged, ElementBounds.Fixed(controlX, y, controlWidth, 24), NoiseGateKey)
            .AddStaticText("右下角麦克风显示", CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row + 8, labelWidth, 24))
            .AddSwitch(OnShowMicrophoneHudChanged, ElementBounds.Fixed(controlX, y - 6, 36, 32), ShowMicrophoneHudKey, 26, 3)
            .AddStaticText("遮挡/环境音效", CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSwitch(OnOcclusionChanged, ElementBounds.Fixed(controlX, y - 6, 36, 32), OcclusionKey, 26, 3)
            .AddStaticText("性能模式", CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSwitch(OnPerformanceModeChanged, ElementBounds.Fixed(controlX, y - 6, 36, 32), PerformanceModeKey, 26, 3)
            .AddSmallButton("关闭", () => TryClose(), closeBounds)
            .EndChildElements()
            .Compose();

        SingleComposer.GetSlider(OutputVolumeKey).SetValues((int)Math.Round(config.OutputVolume * 100f), 0, 200, 5, "%");
        SingleComposer.GetSlider(MicGainKey).SetValues((int)Math.Round(config.MicGain * 100f), 10, 400, 5, "%");
        SingleComposer.GetSlider(NoiseGateKey).SetValues((int)Math.Round(config.NoiseGate * 1000f), 0, 200, 1, " /1000");
        SingleComposer.GetSwitch(ShowMicrophoneHudKey).SetValue(config.ShowMicrophoneHud);
        SingleComposer.GetSwitch(OcclusionKey).SetValue(config.EnableOcclusionEffects);
        SingleComposer.GetSwitch(PerformanceModeKey).SetValue(config.PerformanceMode);
    }

    private void OnInputDeviceChanged(string value, bool selected)
    {
        if (!selected)
        {
            return;
        }

        string nextDevice = value == DefaultInputDeviceValue ? string.Empty : value;
        if (config.InputDeviceName == nextDevice)
        {
            return;
        }

        config.InputDeviceName = nextDevice;
        ApplyConfig();
        reinitializeCapture();
    }

    private bool OnOutputVolumeChanged(int value)
    {
        config.OutputVolume = value / 100f;
        ApplyConfig();
        return true;
    }

    private bool OnMicGainChanged(int value)
    {
        config.MicGain = value / 100f;
        ApplyConfig();
        return true;
    }

    private bool OnNoiseGateChanged(int value)
    {
        config.NoiseGate = value / 1000f;
        ApplyConfig();
        return true;
    }

    private void OnShowMicrophoneHudChanged(bool enabled)
    {
        config.ShowMicrophoneHud = enabled;
        ApplyConfig();
    }

    private void OnOcclusionChanged(bool enabled)
    {
        config.EnableOcclusionEffects = enabled;
        ApplyConfig();
    }

    private void OnPerformanceModeChanged(bool enabled)
    {
        config.PerformanceMode = enabled;
        ApplyConfig();
    }

    private void ApplyConfig()
    {
        saveConfig();
        refreshHud();
    }

    private string[] GetInputDeviceValues()
    {
        List<string> values = new() { DefaultInputDeviceValue };
        try
        {
            foreach (string device in ALC.GetString(AlcGetStringList.CaptureDeviceSpecifier))
            {
                if (!string.IsNullOrWhiteSpace(device) && !values.Contains(device))
                {
                    values.Add(device);
                }
            }
        }
        catch (Exception ex)
        {
            capi.Logger.Warning("SimpleVoiceChat: failed enumerating capture devices: {0}", ex.Message);
        }

        if (!string.IsNullOrWhiteSpace(config.InputDeviceName) && !values.Contains(config.InputDeviceName))
        {
            values.Add(config.InputDeviceName);
        }

        return values.ToArray();
    }

    private static string[] GetInputDeviceNames(string[] values)
    {
        string[] names = new string[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            names[i] = values[i] == DefaultInputDeviceValue ? "默认麦克风" : values[i];
        }

        return names;
    }

    private int GetSelectedInputDeviceIndex(string[] values)
    {
        string current = string.IsNullOrWhiteSpace(config.InputDeviceName) ? DefaultInputDeviceValue : config.InputDeviceName;
        int index = Array.IndexOf(values, current);
        return index >= 0 ? index : 0;
    }
}
