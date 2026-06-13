using OpenTK.Audio.OpenAL;
using SimpleVoiceChat.Config;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

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
    private const string SquadStatusKey = "squadStatus";

    private readonly SimpleVoiceChatClientConfig config;
    private readonly Func<string> summaryProvider;
    private readonly Func<string> squadStatusProvider;
    private readonly Action saveConfig;
    private readonly Action refreshHud;
    private readonly Action reinitializeCapture;
    private readonly Func<bool> startDebugRecording;
    private readonly Func<bool> playDebugRecording;
    private readonly Func<bool> leaveSquad;
    private readonly Func<bool> disbandSquad;
    private readonly Action requestSquadStatus;

    public VoiceSettingsDialog(
        ICoreClientAPI capi,
        SimpleVoiceChatClientConfig config,
        Func<string> summaryProvider,
        Func<string> squadStatusProvider,
        Action saveConfig,
        Action refreshHud,
        Action reinitializeCapture,
        Func<bool> startDebugRecording,
        Func<bool> playDebugRecording,
        Func<bool> leaveSquad,
        Func<bool> disbandSquad,
        Action requestSquadStatus)
        : base(capi)
    {
        this.config = config;
        this.summaryProvider = summaryProvider;
        this.squadStatusProvider = squadStatusProvider;
        this.saveConfig = saveConfig;
        this.refreshHud = refreshHud;
        this.reinitializeCapture = reinitializeCapture;
        this.startDebugRecording = startDebugRecording;
        this.playDebugRecording = playDebugRecording;
        this.leaveSquad = leaveSquad;
        this.disbandSquad = disbandSquad;
        this.requestSquadStatus = requestSquadStatus;
        Compose();
    }

    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;

    public override bool TryOpen()
    {
        requestSquadStatus();
        Compose();
        return base.TryOpen();
    }

    public void Compose()
    {
        ElementBounds dialogBounds = ElementBounds.Fixed(EnumDialogArea.CenterMiddle, -330, -285, 660, 570);
        ElementBounds bgBounds = ElementBounds.Fixed(0, 0, 660, 570);
        ElementBounds closeBounds = ElementBounds.Fixed(275, 520, 110, 32);
        string[] inputDeviceValues = GetInputDeviceValues();
        string[] inputDeviceNames = GetInputDeviceNames(inputDeviceValues);
        int selectedInputDeviceIndex = GetSelectedInputDeviceIndex(inputDeviceValues);

        double labelX = 28;
        double controlX = 260;
        double labelWidth = 210;
        double controlWidth = 350;
        double y = 64;
        double row = 46;

        SingleComposer = capi.Gui.CreateCompo("simplevoicechat-settings", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(SVCLang.Get("title"), () => TryClose())
            .BeginChildElements(bgBounds)
            .AddStaticText(SVCLang.Get("label-input-device"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y + 4, labelWidth, 24))
            .AddDropDown(inputDeviceValues, inputDeviceNames, selectedInputDeviceIndex, OnInputDeviceChanged, ElementBounds.Fixed(controlX, y, controlWidth, 32), InputDeviceKey)
            .AddStaticText(SVCLang.Get("label-output-volume"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSlider(OnOutputVolumeChanged, ElementBounds.Fixed(controlX, y, controlWidth, 24), OutputVolumeKey)
            .AddStaticText(SVCLang.Get("label-mic-gain"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSlider(OnMicGainChanged, ElementBounds.Fixed(controlX, y, controlWidth, 24), MicGainKey)
            .AddStaticText(SVCLang.Get("label-noise-gate"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSlider(OnNoiseGateChanged, ElementBounds.Fixed(controlX, y, controlWidth, 24), NoiseGateKey)
            .AddStaticText(SVCLang.Get("label-show-mic-hud"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row + 8, labelWidth, 24))
            .AddSwitch(OnShowMicrophoneHudChanged, ElementBounds.Fixed(controlX, y - 6, 36, 32), ShowMicrophoneHudKey, 26, 3)
            .AddStaticText(SVCLang.Get("label-occlusion"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSwitch(OnOcclusionChanged, ElementBounds.Fixed(controlX, y - 6, 36, 32), OcclusionKey, 26, 3)
            .AddStaticText(SVCLang.Get("label-performance-mode"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSwitch(OnPerformanceModeChanged, ElementBounds.Fixed(controlX, y - 6, 36, 32), PerformanceModeKey, 26, 3)
            .AddStaticText(SVCLang.Get("label-debug-recording"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row, labelWidth, 24))
            .AddSmallButton(SVCLang.Get("button-record-3s"), OnDebugRecordClicked, ElementBounds.Fixed(controlX, y - 6, 104, 32))
            .AddSmallButton(SVCLang.Get("button-play-recording"), OnDebugPlayClicked, ElementBounds.Fixed(controlX + 124, y - 6, 104, 32))
            .AddStaticText(SVCLang.Get("label-squad-channel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, y += row + 6, labelWidth, 24))
            .AddDynamicText("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(controlX, y - 2, controlWidth, 46), SquadStatusKey)
            .AddSmallButton(SVCLang.Get("button-leave-squad"), OnLeaveSquadClicked, ElementBounds.Fixed(controlX, y + 34, 104, 32))
            .AddSmallButton(SVCLang.Get("button-disband-squad"), OnDisbandSquadClicked, ElementBounds.Fixed(controlX + 124, y + 34, 104, 32))
            .AddSmallButton(SVCLang.Get("button-refresh-status"), OnRefreshSquadClicked, ElementBounds.Fixed(controlX + 248, y + 34, 104, 32))
            .AddSmallButton(SVCLang.Get("button-close"), () => TryClose(), closeBounds)
            .EndChildElements()
            .Compose();

        SingleComposer.GetSlider(OutputVolumeKey).SetValues((int)Math.Round(config.OutputVolume * 100f), 0, 200, 5, "%");
        SingleComposer.GetSlider(MicGainKey).SetValues((int)Math.Round(config.MicGain * 100f), 10, 400, 5, "%");
        SingleComposer.GetSlider(NoiseGateKey).SetValues((int)Math.Round(config.NoiseGate * 1000f), 0, 200, 1, " /1000");
        SingleComposer.GetSwitch(ShowMicrophoneHudKey).SetValue(config.ShowMicrophoneHud);
        SingleComposer.GetSwitch(OcclusionKey).SetValue(config.EnableOcclusionEffects);
        SingleComposer.GetSwitch(PerformanceModeKey).SetValue(config.PerformanceMode);
        SingleComposer.GetDynamicText(SquadStatusKey).SetNewText(squadStatusProvider(), true, true, true);
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

    private bool OnDebugRecordClicked()
    {
        return startDebugRecording();
    }

    private bool OnDebugPlayClicked()
    {
        return playDebugRecording();
    }

    private bool OnLeaveSquadClicked()
    {
        return leaveSquad();
    }

    private bool OnDisbandSquadClicked()
    {
        return disbandSquad();
    }

    private bool OnRefreshSquadClicked()
    {
        requestSquadStatus();
        RefreshStatusTexts();
        return true;
    }

    private void ApplyConfig()
    {
        saveConfig();
        refreshHud();
        RefreshStatusTexts();
    }

    public void RefreshStatusTexts()
    {
        if (SingleComposer == null)
        {
            return;
        }

        SingleComposer.GetDynamicText(SquadStatusKey)?.SetNewText(squadStatusProvider(), true, true, true);
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
            names[i] = values[i] == DefaultInputDeviceValue ? SVCLang.Get("default-microphone") : values[i];
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
