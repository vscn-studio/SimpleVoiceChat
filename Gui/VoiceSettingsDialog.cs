using SimpleVoiceChat.Audio;
using SimpleVoiceChat.Config;
using SimpleVoiceChat.Networking;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SimpleVoiceChat.Gui;

internal static class VoiceSettingsActionPolicy
{
    public static bool RequiresTarget(string action)
    {
        return action is "invite" or "add" or "mute" or "unmute" or "remove" or "ban" or "unban"
            or "listenonly" or "member" or "officer" or "role"
            or "tempmute" or "deafen" or "adminmute" or "adminunmute" or "forceblock" or "unforceblock";
    }

    public static bool RequiresChannel(string action)
    {
        return action is "add" or "mute" or "unmute" or "remove" or "ban" or "unban"
            or "listenonly" or "member" or "officer" or "role"
            or "lock" or "unlock" or "leave" or "disband" or "rename";
    }
}

internal enum VoiceSettingsPage
{
    Audio,
    Channels,
    Status,
    Admin
}

internal static class VoiceSettingsNavigation
{
    public static VoiceSettingsPage[] BuildPages(bool hasServerControl)
    {
        List<VoiceSettingsPage> pages = new()
        {
            VoiceSettingsPage.Audio,
            VoiceSettingsPage.Channels,
            VoiceSettingsPage.Status
        };
        if (hasServerControl)
        {
            pages.Add(VoiceSettingsPage.Admin);
        }
        return pages.ToArray();
    }
}

public sealed class VoiceSettingsDialog : GuiDialog
{
    private const double NavigationWidth = 184;
    private const double ContentLeft = 198;
    private const double ContentTop = 78;
    private const double ContentWidth = 730;
    private const double ViewportHeight = 534;
    private const double FooterY = 646;

    private readonly ClientVoiceController controller;
    private readonly SimpleVoiceChatClientConfig config;

    private VoiceSettingsPage selectedPage = VoiceSettingsPage.Audio;
    private ElementBounds? contentBounds;
    private float scrollPosition;
    private double contentHeight = ViewportHeight;
    private bool suppressScrollCallback;
    private bool composeQueued;

    private string selectedPlayerUid = string.Empty;
    private string selectedChannelAction = "invite";
    private string selectedAdminChannelId = string.Empty;
    private string selectedAdminAction = "mute";
    private string selectedCreateKind = "civilization";
    private string renameText = string.Empty;
    private string createName = string.Empty;

    public VoiceSettingsDialog(ICoreClientAPI capi, ClientVoiceController controller)
        : base(capi)
    {
        this.controller = controller;
        config = controller.SettingsConfig;
    }

    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;
    public override bool DisableMouseGrab => true;
    public override EnumDialogType DialogType => EnumDialogType.Dialog;
    public override double DrawOrder => 0.48;
    public override double InputOrder => 0.3;

    public override bool TryOpen()
    {
        controller.RequestSettingsRefresh();
        Compose();
        return base.TryOpen();
    }

    public void RefreshData()
    {
        if (selectedPage == VoiceSettingsPage.Admin && !controller.HasServerControl)
        {
            selectedPage = VoiceSettingsPage.Audio;
            scrollPosition = 0;
        }
        if (IsOpened())
        {
            QueueCompose();
        }
    }

    public void RefreshConfiguration()
    {
        if (IsOpened())
        {
            QueueCompose();
        }
    }

    private void Compose()
    {
        VoiceSettingsPage[] pages = VoiceSettingsNavigation.BuildPages(controller.HasServerControl);
        if (!pages.Contains(selectedPage))
        {
            selectedPage = VoiceSettingsPage.Audio;
            scrollPosition = 0;
        }

        SingleComposer?.Dispose();
        ElementBounds root = ElementStdBounds.AutosizedMainDialog;
        ElementBounds background = ElementStdBounds.DialogBackground();
        ElementBounds viewport = ElementBounds.Fixed(ContentLeft, ContentTop, ContentWidth, ViewportHeight);
        contentBounds = ElementBounds.Fixed(0, 0, ContentWidth, ViewportHeight);

        CairoFont titleFont = CairoFont.WhiteSmallText().WithFontSize(15f);

        GuiComposer composer = capi.Gui.CreateCompo("simplevoicechat-settings", root)
            .AddShadedDialogBG(background)
            .AddDialogTitleBar(SVCLang.Get("title"), OnClose, titleFont)
            .BeginChildElements(background)
            .AddStaticText(
                SVCLang.Get("ui-navigation"),
                CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(14, 48, NavigationWidth - 14, 24));

        double navY = 76;
        foreach (VoiceSettingsPage page in pages)
        {
            VoiceSettingsPage captured = page;
            string key = "nav-" + page.ToString().ToLowerInvariant();
            composer.AddDirectorButton(
                GetPageName(page),
                () => SelectPage(captured),
                ElementBounds.Fixed(14, navY, 170, 32),
                page == selectedPage ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                key);
            navY += 38;
        }

        composer
            .AddStaticText(
                GetPageTitle(selectedPage),
                titleFont,
                ElementBounds.Fixed(ContentLeft + 14, 46, ContentWidth - 28, 26))
            .BeginDirectorStaticClip(viewport.FlatCopy(), "pageStaticClip")
            .BeginClip(viewport)
            .BeginChildElements(contentBounds);

        contentHeight = selectedPage switch
        {
            VoiceSettingsPage.Channels => AddChannelsPage(composer),
            VoiceSettingsPage.Status => AddStatusPage(composer),
            VoiceSettingsPage.Admin => AddAdminPage(composer),
            _ => AddAudioPage(composer)
        };
        contentHeight = Math.Max(ViewportHeight, contentHeight);
        scrollPosition = Math.Clamp(
            scrollPosition,
            0f,
            (float)Math.Max(0d, contentHeight - ViewportHeight));
        contentBounds.fixedY = -scrollPosition;

        composer
            .EndChildElements()
            .EndClip()
            .EndDirectorStaticClip("pageStaticClipEnd")
            .AddVerticalScrollbar(
                OnScroll,
                ElementBounds.Fixed(ContentLeft + ContentWidth + 6, ContentTop, 16, ViewportHeight),
                "pageScrollbar")
            .AddDynamicText(
                controller.HasServerControl ? SVCLang.Get("ui-role-admin") : SVCLang.Get("ui-role-player"),
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(ContentLeft, FooterY, 560, 32),
                "footerStatus")
            .AddDirectorIconButton(
                DirectorIcon.Refresh,
                SVCLang.Get("button-refresh-status"),
                RefreshStatus,
                ElementBounds.Fixed(ContentLeft + ContentWidth - 34, FooterY - 2, 34, 30),
                EnumButtonStyle.Small,
                "footerRefresh");

        SingleComposer = composer.EndChildElements().Compose();
        GuiElementScrollbar? scrollbar = SingleComposer.GetScrollbar("pageScrollbar");
        if (scrollbar != null)
        {
            float totalHeight = (float)contentHeight;
            suppressScrollCallback = true;
            try
            {
                scrollbar.SetHeights((float)ViewportHeight, totalHeight);
                scrollbar.CurrentYPosition = scrollPosition;
                scrollbar.TriggerChanged();
            }
            finally
            {
                suppressScrollCallback = false;
            }
        }
    }

    private double AddAudioPage(GuiComposer composer)
    {
        const double labelX = 18;
        const double controlX = 350;
        const double controlWidth = 360;
        double y = 16;
        CairoFont section = CairoFont.WhiteSmallText().WithFontSize(15f);
        CairoFont label = CairoFont.WhiteSmallText().WithFontSize(14f);

        string[] inputValues = controller.GetInputDeviceValues();
        string[] inputNames = ClientVoiceController.GetInputDeviceNames(inputValues);
        string selectedInput = config.InputDeviceName ?? string.Empty;

        composer
            .AddStaticText(SVCLang.Get("label-input-device"), label, ElementBounds.Fixed(labelX, y, 310, 30))
            .AddDropDown(inputValues, inputNames, Math.Max(0, Array.IndexOf(inputValues, selectedInput)), OnInputDeviceChanged, ElementBounds.Fixed(controlX, y, controlWidth, 30), "inputDevice")
            .AddStaticText(SVCLang.Get("label-output-volume"), label, ElementBounds.Fixed(labelX, y += 48, 310, 30))
            .AddDirectorSlider(value => { controller.SetOutputVolumeFromSettings(value); return true; }, ElementBounds.Fixed(controlX, y, controlWidth, 28), "outputVolume")
            .AddStaticText(SVCLang.Get("label-mic-gain"), label, ElementBounds.Fixed(labelX, y += 48, 310, 30))
            .AddDirectorSlider(value => { controller.SetMicGainFromSettings(value); return true; }, ElementBounds.Fixed(controlX, y, controlWidth, 28), "micGain")
            .AddStaticText(SVCLang.Get("label-noise-gate"), label, ElementBounds.Fixed(labelX, y += 48, 310, 30))
            .AddDirectorSlider(value => { controller.SetNoiseGateFromSettings(value); return true; }, ElementBounds.Fixed(controlX, y, controlWidth, 28), "noiseGate");

        ConfigureSlider(composer, "outputVolume", (int)Math.Round(config.OutputVolume * 100), 0, 200, "%");
        ConfigureSlider(composer, "micGain", (int)Math.Round(config.MicGain * 100), 10, 400, "%");
        ConfigureSlider(composer, "noiseGate", (int)Math.Round(config.NoiseGate * 1000), 0, 200);

        y += 54;
        composer.AddStaticText(SVCLang.Get("ui-section-behavior"), section, ElementBounds.Fixed(labelX, y, 700, 28));
        y += 38;
        double leftY = y;
        double rightY = y;
        AddSwitchRow(composer, labelX, ref leftY, SVCLang.Get("label-mic-muted"), "localMute", controller.LocalMuted, controller.SetLocalMutedFromSettings);
        AddSwitchRow(composer, labelX, ref leftY, SVCLang.Get("label-deafened"), "globalMute", controller.GlobalMuted, controller.SetGlobalMutedFromSettings);
        AddSwitchRow(composer, labelX, ref leftY, SVCLang.Get("label-continuous-talk"), "continuous", controller.ContinuousTalkEnabled, controller.SetContinuousTalkFromSettings);
        AddSwitchRow(composer, labelX, ref leftY, SVCLang.Get("label-adaptive-jitter"), "jitter", config.AdaptiveJitterBuffer, controller.SetAdaptiveJitterFromSettings);
        AddSwitchRow(composer, labelX, ref leftY, SVCLang.Get("label-show-mic-hud"), "showHud", config.ShowMicrophoneHud, controller.SetHudVisibleFromSettings);

        const double rightX = 380;
        AddSwitchRow(composer, rightX, ref rightY, SVCLang.Get("label-noise-suppression"), "noiseSuppression", config.EnableNoiseSuppression, controller.SetNoiseSuppressionFromSettings);
        AddSwitchRow(composer, rightX, ref rightY, SVCLang.Get("label-echo-cancellation"), "echoCancellation", config.EnableEchoCancellation, controller.SetEchoCancellationFromSettings);
        AddSwitchRow(composer, rightX, ref rightY, SVCLang.Get("label-occlusion"), "occlusion", config.EnableOcclusionEffects, controller.SetOcclusionFromSettings);
        AddSwitchRow(composer, rightX, ref rightY, SVCLang.Get("label-performance-mode"), "performance", config.PerformanceMode, controller.SetPerformanceModeFromSettings);

        composer.GetSwitch("continuous").Enabled = controller.ContinuousTalkAllowed;
        composer.GetSwitch("noiseSuppression").Enabled = VoiceProcessingCapabilities.NoiseSuppressionAvailable;
        composer.GetSwitch("echoCancellation").Enabled = VoiceProcessingCapabilities.EchoCancellationAvailable;
        composer.GetSwitch("occlusion").Enabled = !controller.OcclusionForced;
        return Math.Max(leftY, rightY) + 18;
    }

    private double AddChannelsPage(GuiComposer composer)
    {
        const double x = 18;
        const double rightX = 380;
        const double columnWidth = 336;
        double y = 16;
        CairoFont section = CairoFont.WhiteSmallText().WithFontSize(15f);
        CairoFont label = CairoFont.WhiteSmallText().WithFontSize(14f);
        CairoFont detail = CairoFont.WhiteDetailText().WithFontSize(13f);

        VoiceSettingsChannelOption[] channels = controller.BuildChannelOptions();
        VoiceSettingsPlayerOption[] players = controller.BuildPlayerOptions();
        NormalizePlayer(players);
        string[] channelValues = new[] { string.Empty }.Concat(channels.Select(channel => channel.Id)).ToArray();
        string[] channelNames = new[] { SVCLang.Get("channel-none") }
            .Concat(channels.Select(channel => $"{FormatChannelKind(channel.Kind)}: {channel.Name}"))
            .ToArray();
        string[] playerValues = players.Length == 0 ? new[] { string.Empty } : players.Select(player => player.Id).ToArray();
        string[] playerNames = players.Length == 0 ? new[] { SVCLang.Get("player-none") } : players.Select(player => player.Name).ToArray();
        string[] transmitValues = { "proximity", "channel", "both" };
        string[] transmitNames = transmitValues.Select(value => SVCLang.Get("transmit-" + value)).ToArray();

        composer
            .AddStaticText(SVCLang.Get("label-channel-select"), label, ElementBounds.Fixed(x, y, columnWidth, 26))
            .AddStaticText(SVCLang.Get("label-transmit-target"), label, ElementBounds.Fixed(rightX, y, columnWidth, 26))
            .AddDropDown(channelValues, channelNames, Math.Max(0, Array.IndexOf(channelValues, config.SelectedChannelId)), OnChannelChanged, ElementBounds.Fixed(x, y + 28, columnWidth, 30), "channel")
            .AddDropDown(transmitValues, transmitNames, Math.Max(0, Array.IndexOf(transmitValues, TransmitCode(config.TransmitTarget))), OnTransmitChanged, ElementBounds.Fixed(rightX, y + 28, columnWidth, 30), "transmit")
            .AddStaticText(SVCLang.Get("label-channel-volume"), label, ElementBounds.Fixed(x, y += 76, 310, 30))
            .AddDirectorSlider(value => { controller.SetChannelVolumeFromSettings(value); return true; }, ElementBounds.Fixed(rightX, y, columnWidth, 28), "channelVolume")
            .AddStaticText(SVCLang.Get("ui-section-player"), section, ElementBounds.Fixed(x, y += 52, 700, 28))
            .AddStaticText(SVCLang.Get("ui-target-player"), label, ElementBounds.Fixed(x, y += 38, 310, 30))
            .AddDropDown(playerValues, playerNames, Math.Max(0, Array.IndexOf(playerValues, selectedPlayerUid)), OnPlayerChanged, ElementBounds.Fixed(rightX, y, columnWidth, 30), "selectedPlayer")
            .AddStaticText(SVCLang.Get("label-player-volume"), label, ElementBounds.Fixed(x, y += 44, 310, 30))
            .AddDirectorSlider(OnSelectedPlayerVolumeChanged, ElementBounds.Fixed(rightX, y, 292, 28), "selectedPlayerVolume")
            .AddSwitch(OnSelectedPlayerMuteChanged, ElementBounds.Fixed(rightX + 304, y, 28, 28), "selectedPlayerMute");

        composer.GetDropDown("channel").Enabled = true;
        ConfigureSlider(composer, "channelVolume", (int)Math.Round(config.ChannelOutputVolume * 100), 0, 200, "%");
        bool hasPlayer = !string.IsNullOrWhiteSpace(selectedPlayerUid);
        ConfigureSlider(composer, "selectedPlayerVolume", hasPlayer ? controller.GetPlayerVolumePercent(selectedPlayerUid) : 100, 0, 200, "%");
        composer.GetDirectorSlider("selectedPlayerVolume")!.Enabled = hasPlayer;
        composer.GetSwitch("selectedPlayerMute").SetValue(hasPlayer && controller.IsPlayerMuted(selectedPlayerUid));
        composer.GetSwitch("selectedPlayerMute").Enabled = hasPlayer;

        List<string> actions = BuildChannelActions(channels);
        if (!actions.Contains(selectedChannelAction, StringComparer.Ordinal))
        {
            selectedChannelAction = actions[0];
        }
        y += 52;
        composer
            .AddStaticText(SVCLang.Get("label-channel-manage"), section, ElementBounds.Fixed(x, y, 700, 28))
            .AddDropDown(actions.ToArray(), actions.Select(action => SVCLang.Get("channel-action-" + action)).ToArray(), Math.Max(0, actions.IndexOf(selectedChannelAction)), OnChannelActionChanged, ElementBounds.Fixed(x, y += 38, 500, 30), "channelAction")
            .AddDirectorButton(SVCLang.Get("button-apply"), ExecuteChannelAction, ElementBounds.Fixed(522, y, 194, 32), EnumButtonStyle.Normal, "applyChannelAction");
        composer.GetButton("applyChannelAction").Enabled = selectedChannelAction != "none";

        VoiceSettingsChannelOption? selectedChannel = channels.Cast<VoiceSettingsChannelOption?>().FirstOrDefault(channel => channel?.Id == config.SelectedChannelId);
        bool canRename = selectedChannel is { ExternallyManaged: false, LocalRole: VoiceChannelRole.Owner };
        if (canRename)
        {
            if (string.IsNullOrWhiteSpace(renameText))
            {
                renameText = selectedChannel?.Name ?? string.Empty;
            }
            composer
                .AddStaticText(SVCLang.Get("label-channel-name"), label, ElementBounds.Fixed(x, y += 48, 310, 30))
                .AddTextInput(ElementBounds.Fixed(rightX, y, 226, 30), OnRenameTextChanged, CairoFont.TextInput().WithFontSize(14f), "channelRename")
                .AddDirectorButton(SVCLang.Get("button-rename-channel"), RenameSelectedChannel, ElementBounds.Fixed(610, y, 106, 30), EnumButtonStyle.Normal, "renameChannel");
            composer.GetTextInput("channelRename").SetValue(renameText);
            composer.GetTextInput("channelRename").SetMaxLength(VoiceProtocol.MaxControlStringLength);
        }

        y += 56;
        composer
            .AddStaticText(SVCLang.Get("setting-voice-players"), section, ElementBounds.Fixed(x, y, 700, 28))
            .AddStaticText(SVCLang.Get("setting-voice-player-column"), detail, ElementBounds.Fixed(x, y += 32, 220, 24))
            .AddStaticText(SVCLang.Get("setting-voice-volume-column"), detail, ElementBounds.Fixed(250, y, 320, 24))
            .AddStaticText(SVCLang.Get("setting-voice-mute-column"), detail, ElementBounds.Fixed(650, y, 66, 24));
        y += 28;

        if (players.Length == 0)
        {
            composer.AddStaticText(SVCLang.Get("player-none"), detail, ElementBounds.Fixed(x, y, 700, 30));
            return y + 48;
        }

        for (int index = 0; index < players.Length; index++)
        {
            VoiceSettingsPlayerOption player = players[index];
            string sliderKey = "playerVolume" + index;
            string muteKey = "playerMute" + index;
            composer
                .AddStaticText(Truncate(player.Name, 30), label, ElementBounds.Fixed(x, y, 220, 30), "playerName" + index)
                .AddDirectorSlider(value => SetPlayerVolume(player.Id, value), ElementBounds.Fixed(250, y, 350, 28), sliderKey)
                .AddSwitch(value => controller.SetPlayerMutedFromSettings(player.Id, value), ElementBounds.Fixed(680, y, 28, 28), muteKey);
            ConfigureSlider(composer, sliderKey, controller.GetPlayerVolumePercent(player.Id), 0, 200, "%");
            composer.GetSwitch(muteKey).SetValue(controller.IsPlayerMuted(player.Id));
            y += 40;
        }
        return y + 18;
    }

    private double AddStatusPage(GuiComposer composer)
    {
        const double x = 18;
        double y = 16;
        CairoFont section = CairoFont.WhiteSmallText().WithFontSize(15f);
        DirectorTableRow[] statusRows = ParseTableRows(
            controller.BuildSettingsStatus(),
            SVCLang.Get("summary-line-commands"));
        double statusTableHeight = GuiElementDirectorTable.RequiredHeight(statusRows, 710, 220);
        composer
            .AddStaticText(SVCLang.Get("label-current-status"), section, ElementBounds.Fixed(x, y, 700, 28))
            .AddDirectorTable(
                SVCLang.Get("ui-table-field"),
                SVCLang.Get("ui-table-value"),
                statusRows,
                ElementBounds.Fixed(x, y += 34, 710, statusTableHeight),
                220,
                "statusTable");

        y += statusTableHeight + 20;
        DirectorTableRow[] diagnosticRows = ParseTableRows(controller.BuildSettingsDiagnostics());
        double diagnosticTableHeight = GuiElementDirectorTable.RequiredHeight(diagnosticRows, 710, 220);
        composer
            .AddStaticText(SVCLang.Get("label-diagnostics"), section, ElementBounds.Fixed(x, y, 700, 28))
            .AddDirectorTable(
                SVCLang.Get("ui-table-field"),
                SVCLang.Get("ui-table-value"),
                diagnosticRows,
                ElementBounds.Fixed(x, y += 34, 710, diagnosticTableHeight),
                220,
                "diagnosticsTable");

        y += diagnosticTableHeight + 20;
        DirectorTableRow[] commandRows = BuildCommandTableRows();
        double commandTableHeight = GuiElementDirectorTable.RequiredHeight(commandRows, 710, 300);
        composer
            .AddStaticText(SVCLang.Get("ui-command-reference"), section, ElementBounds.Fixed(x, y, 700, 28))
            .AddDirectorTable(
                SVCLang.Get("ui-table-command"),
                SVCLang.Get("ui-table-description"),
                commandRows,
                ElementBounds.Fixed(x, y += 34, 710, commandTableHeight),
                300,
                "commandTable")
            .AddDirectorButton(
                SVCLang.Get("button-refresh-status"),
                RefreshStatus,
                ElementBounds.Fixed(x, y += commandTableHeight + 18, 190, 34),
                EnumButtonStyle.Normal,
                "refreshStatus");
        return y + 52;
    }

    private static DirectorTableRow[] ParseTableRows(string summary, params string[] excludedLines)
    {
        List<DirectorTableRow> rows = new();
        string[] lines = (summary ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (excludedLines.Any(excluded => string.Equals(excluded, line, StringComparison.Ordinal)))
            {
                continue;
            }

            int separator = FindTableSeparator(line);
            if (separator > 0 && separator < line.Length - 1)
            {
                rows.Add(new DirectorTableRow(
                    line[..separator].Trim(),
                    line[(separator + 1)..].Trim()));
                continue;
            }

            rows.Add(new DirectorTableRow(
                SVCLang.Get(index == 0 ? "ui-table-summary-row" : "ui-table-detail-row"),
                line));
        }

        return rows.ToArray();
    }

    private static int FindTableSeparator(string line)
    {
        int colon = line.IndexOf(':');
        int wideColon = line.IndexOf('：');
        int equals = line.IndexOf('=');
        int separator = colon < 0 ? wideColon : wideColon < 0 ? colon : Math.Min(colon, wideColon);
        if (separator < 0 || (equals >= 0 && equals < separator))
        {
            separator = equals;
        }
        return separator;
    }

    private static DirectorTableRow[] BuildCommandTableRows()
    {
        return new[]
        {
            new DirectorTableRow("/svc status", SVCLang.Get("command-table-status")),
            new DirectorTableRow("/svc volume <0-200>", SVCLang.Get("command-table-volume")),
            new DirectorTableRow("/svc volumeplayer <player> <0-200>", SVCLang.Get("command-table-volumeplayer")),
            new DirectorTableRow("/svc mute <player>", SVCLang.Get("command-table-mute")),
            new DirectorTableRow("/svc unmute <player>", SVCLang.Get("command-table-unmute")),
            new DirectorTableRow("/svc bind", SVCLang.Get("command-table-bind")),
            new DirectorTableRow("/svc unbind", SVCLang.Get("command-table-unbind")),
            new DirectorTableRow("/svc squad", SVCLang.Get("command-table-squad")),
            new DirectorTableRow("/svc accept", SVCLang.Get("command-table-accept")),
            new DirectorTableRow("/svc decline", SVCLang.Get("command-table-decline")),
            new DirectorTableRow("/svc diag", SVCLang.Get("command-table-diag")),
            new DirectorTableRow("/svc adminmute|adminunmute|forceblock|unforceblock <player|UID>", SVCLang.Get("command-table-admin-control")),
            new DirectorTableRow("/svc adminmutes", SVCLang.Get("command-table-admin-list"))
        };
    }

    private double AddAdminPage(GuiComposer composer)
    {
        const double leftX = 18;
        const double rightX = 380;
        const double columnWidth = 338;
        double leftY = 14;
        double rightY = 14;
        CairoFont section = CairoFont.WhiteSmallText().WithFontSize(15f);
        CairoFont label = CairoFont.WhiteSmallText().WithFontSize(14f);
        CairoFont detail = CairoFont.WhiteDetailText().WithFontSize(13f);
        CairoFont input = CairoFont.TextInput().WithFontSize(14f);

        VoiceSettingsChannelOption[] channels = controller.BuildChannelOptions();
        VoiceSettingsPlayerOption[] players = controller.BuildPlayerOptions();
        NormalizePlayer(players);
        NormalizeAdminChannel(channels);
        string[] playerValues = players.Length == 0 ? new[] { string.Empty } : players.Select(player => player.Id).ToArray();
        string[] playerNames = players.Length == 0 ? new[] { SVCLang.Get("player-none") } : players.Select(player => player.Name).ToArray();
        string[] channelValues = channels.Length == 0 ? new[] { string.Empty } : channels.Select(channel => channel.Id).ToArray();
        string[] channelNames = channels.Length == 0 ? new[] { SVCLang.Get("channel-none") } : channels.Select(channel => channel.Name).ToArray();
        string[] adminActions = BuildAdminChannelActions(channels);
        string[] createKinds = { "civilization", "command", "diplomacy", "staff", "broadcast", "radio" };

        composer
            .AddStaticText(SVCLang.Get("ui-section-admin-target"), section, ElementBounds.Fixed(leftX, leftY, columnWidth, 28))
            .AddStaticText(SVCLang.Get("ui-target-player"), label, ElementBounds.Fixed(leftX, leftY += 36, columnWidth, 26))
            .AddDropDown(playerValues, playerNames, Math.Max(0, Array.IndexOf(playerValues, selectedPlayerUid)), OnPlayerChanged, ElementBounds.Fixed(leftX, leftY + 28, columnWidth, 30), "adminPlayer")
            .AddStaticText(SVCLang.Get("ui-section-temporary-actions"), section, ElementBounds.Fixed(leftX, leftY += 76, columnWidth, 28))
            .AddDirectorButton(SVCLang.Get("channel-action-tempmute"), () => ExecuteModeration("tempmute"), ElementBounds.Fixed(leftX, leftY += 36, 160, 34), EnumButtonStyle.Normal, "tempmute")
            .AddDirectorButton(SVCLang.Get("channel-action-deafen"), () => ExecuteModeration("deafen"), ElementBounds.Fixed(leftX + 174, leftY, 164, 34), EnumButtonStyle.Normal, "deafen")
            .AddStaticText(SVCLang.Get("ui-section-persistent-actions"), section, ElementBounds.Fixed(leftX, leftY += 52, columnWidth, 28))
            .AddDirectorButton(SVCLang.Get("channel-action-adminmute"), () => ExecuteModeration("adminmute"), ElementBounds.Fixed(leftX, leftY += 36, 160, 34), EnumButtonStyle.Normal, "adminmute")
            .AddDirectorButton(SVCLang.Get("channel-action-adminunmute"), () => ExecuteModeration("adminunmute"), ElementBounds.Fixed(leftX + 174, leftY, 164, 34), EnumButtonStyle.Normal, "adminunmute")
            .AddDirectorButton(SVCLang.Get("channel-action-forceblock"), () => ExecuteModeration("forceblock"), ElementBounds.Fixed(leftX, leftY += 46, 160, 34), EnumButtonStyle.Normal, "forceblock")
            .AddDirectorButton(SVCLang.Get("channel-action-unforceblock"), () => ExecuteModeration("unforceblock"), ElementBounds.Fixed(leftX + 174, leftY, 164, 34), EnumButtonStyle.Normal, "unforceblock")
            .AddStaticText(SVCLang.Get("ui-admin-warning"), detail, ElementBounds.Fixed(leftX, leftY += 52, columnWidth, 44))
            .AddDirectorButton(SVCLang.Get("button-refresh-status"), RefreshStatus, ElementBounds.Fixed(leftX, leftY += 54, 180, 34), EnumButtonStyle.Small, "adminRefresh");

        bool hasTarget = !string.IsNullOrWhiteSpace(selectedPlayerUid);
        foreach (string key in new[] { "tempmute", "deafen", "adminmute", "adminunmute", "forceblock", "unforceblock" })
        {
            composer.GetButton(key).Enabled = hasTarget;
        }

        composer
            .AddStaticText(SVCLang.Get("label-channel-manage"), section, ElementBounds.Fixed(rightX, rightY, columnWidth, 28))
            .AddStaticText(SVCLang.Get("label-current-channel"), label, ElementBounds.Fixed(rightX, rightY += 36, columnWidth, 26))
            .AddDropDown(channelValues, channelNames, Math.Max(0, Array.IndexOf(channelValues, selectedAdminChannelId)), OnAdminChannelChanged, ElementBounds.Fixed(rightX, rightY + 28, columnWidth, 30), "adminChannel")
            .AddStaticText(SVCLang.Get("ui-action-select"), label, ElementBounds.Fixed(rightX, rightY += 72, columnWidth, 26))
            .AddDropDown(adminActions, adminActions.Select(action => SVCLang.Get("channel-action-" + action)).ToArray(), Math.Max(0, Array.IndexOf(adminActions, selectedAdminAction)), OnAdminActionChanged, ElementBounds.Fixed(rightX, rightY + 28, 220, 30), "adminAction")
            .AddDirectorButton(SVCLang.Get("button-apply"), ExecuteAdminChannelAction, ElementBounds.Fixed(rightX + 232, rightY + 28, 106, 30), EnumButtonStyle.Normal, "adminApply")
            .AddStaticText(SVCLang.Get("label-channel-name"), section, ElementBounds.Fixed(rightX, rightY += 78, columnWidth, 28))
            .AddTextInput(ElementBounds.Fixed(rightX, rightY += 34, 220, 30), OnRenameTextChanged, input, "adminRenameInput")
            .AddDirectorButton(SVCLang.Get("button-rename-channel"), RenameAdminChannel, ElementBounds.Fixed(rightX + 232, rightY, 106, 30), EnumButtonStyle.Normal, "adminRename")
            .AddStaticText(SVCLang.Get("ui-section-create-channel"), section, ElementBounds.Fixed(rightX, rightY += 52, columnWidth, 28))
            .AddTextInput(ElementBounds.Fixed(rightX, rightY += 34, columnWidth, 30), OnCreateNameChanged, input, "createName")
            .AddDropDown(createKinds, createKinds.Select(kind => SVCLang.Get("channel-kind-" + kind)).ToArray(), Math.Max(0, Array.IndexOf(createKinds, selectedCreateKind)), OnCreateKindChanged, ElementBounds.Fixed(rightX, rightY += 42, 220, 30), "createKind")
            .AddDirectorButton(SVCLang.Get("channel-action-create"), CreateChannel, ElementBounds.Fixed(rightX + 232, rightY, 106, 30), EnumButtonStyle.Normal, "createChannel");

        composer.GetTextInput("adminRenameInput").SetValue(renameText);
        composer.GetTextInput("adminRenameInput").SetMaxLength(VoiceProtocol.MaxControlStringLength);
        composer.GetTextInput("createName").SetValue(createName);
        composer.GetTextInput("createName").SetMaxLength(VoiceProtocol.MaxControlStringLength);
        composer.GetTextInput("createName").SetPlaceHolderText(SVCLang.Get("placeholder-channel-name"));
        composer.GetButton("adminApply").Enabled = CanExecuteAdminAction();
        composer.GetButton("adminRename").Enabled = CanRenameAdminChannel(channels);
        composer.GetButton("createChannel").Enabled = !string.IsNullOrWhiteSpace(createName);
        return Math.Max(leftY, rightY) + 60;
    }

    private static void AddSwitchRow(GuiComposer composer, double x, ref double y, string text, string key, bool value, Action<bool> changed)
    {
        composer
            .AddStaticText(text, CairoFont.WhiteSmallText().WithFontSize(14f), ElementBounds.Fixed(x, y, 260, 30))
            .AddSwitch(changed, ElementBounds.Fixed(x + 286, y, 28, 28), key);
        composer.GetSwitch(key).SetValue(value);
        y += 42;
    }

    private static void ConfigureSlider(
        GuiComposer composer,
        string key,
        int value,
        int minimum,
        int maximum,
        string suffix = "")
    {
        GuiElementDirectorSlider slider = composer.GetDirectorSlider(key)
            ?? throw new InvalidOperationException($"Settings slider '{key}' was not found.");
        slider.OnSliderTooltip = suffix.Length == 0 ? null : current => current + suffix;
        slider.SetValues(value, minimum, maximum, 1);
    }

    private List<string> BuildChannelActions(VoiceSettingsChannelOption[] channels)
    {
        bool hasPlayer = !string.IsNullOrWhiteSpace(selectedPlayerUid);
        VoiceSettingsChannelOption? selected = channels.Cast<VoiceSettingsChannelOption?>().FirstOrDefault(channel => channel?.Id == config.SelectedChannelId);
        bool hasChannel = selected.HasValue;
        VoiceChannelRole role = selected?.LocalRole ?? VoiceChannelRole.Banned;
        bool external = selected?.ExternallyManaged ?? false;
        bool canInvite = controller.HasServerControl
            || !channels.Any(channel => channel.Kind == VoiceChannelKind.Squad)
            || channels.Any(channel => channel.Kind == VoiceChannelKind.Squad && channel.LocalRole >= VoiceChannelRole.Officer);
        List<string> actions = new();
        if (canInvite && hasPlayer) actions.Add("invite");
        if (hasChannel && !external) actions.Add("leave");
        if (hasChannel && role >= VoiceChannelRole.Officer && hasPlayer)
        {
            actions.AddRange(new[] { "mute", "unmute", "ban", "unban" });
            if (!external) actions.Add("remove");
        }
        if (hasChannel && role == VoiceChannelRole.Owner)
        {
            if (hasPlayer && !external) actions.AddRange(new[] { "listenonly", "member", "officer" });
            actions.AddRange(new[] { "lock", "unlock" });
            if (!external) actions.Add("disband");
        }
        if (actions.Count == 0) actions.Add("none");
        return actions;
    }

    private string[] BuildAdminChannelActions(VoiceSettingsChannelOption[] channels)
    {
        VoiceSettingsChannelOption? selected = channels.Cast<VoiceSettingsChannelOption?>().FirstOrDefault(channel => channel?.Id == selectedAdminChannelId);
        List<string> actions = new() { "mute", "unmute", "ban", "unban", "lock", "unlock" };
        if (selected is { ExternallyManaged: false })
        {
            actions.AddRange(new[] { "add", "remove", "listenonly", "member", "officer", "disband" });
        }
        if (!actions.Contains(selectedAdminAction, StringComparer.Ordinal))
        {
            selectedAdminAction = actions[0];
        }
        return actions.ToArray();
    }

    private bool SelectPage(VoiceSettingsPage page)
    {
        if (page == VoiceSettingsPage.Admin && !controller.HasServerControl)
        {
            return false;
        }
        selectedPage = page;
        scrollPosition = 0;
        QueueCompose();
        return true;
    }

    private void OnInputDeviceChanged(string value, bool selected)
    {
        if (selected) controller.SetInputDeviceFromSettings(value);
    }

    private void OnChannelChanged(string value, bool selected)
    {
        if (!selected) return;
        config.SelectedChannelId = value;
        renameText = string.Empty;
        selectedChannelAction = "invite";
        controller.SelectChannelFromSettings(value);
        QueueCompose();
    }

    private void OnTransmitChanged(string value, bool selected)
    {
        if (selected) controller.SetTransmitTargetFromSettings(value);
    }

    private void OnPlayerChanged(string value, bool selected)
    {
        if (!selected) return;
        selectedPlayerUid = value;
        QueueCompose();
    }

    private bool OnSelectedPlayerVolumeChanged(int value)
    {
        if (!string.IsNullOrWhiteSpace(selectedPlayerUid)) controller.SetPlayerVolumeFromSettings(selectedPlayerUid, value);
        return true;
    }

    private void OnSelectedPlayerMuteChanged(bool value)
    {
        if (!string.IsNullOrWhiteSpace(selectedPlayerUid)) controller.SetPlayerMutedFromSettings(selectedPlayerUid, value);
    }

    private bool SetPlayerVolume(string playerUid, int value)
    {
        controller.SetPlayerVolumeFromSettings(playerUid, value);
        return true;
    }

    private void OnChannelActionChanged(string value, bool selected)
    {
        if (selected) selectedChannelAction = value;
    }

    private bool ExecuteChannelAction()
    {
        if (selectedChannelAction == "none") return false;
        VoiceChannelRole role = selectedChannelAction switch
        {
            "listenonly" => VoiceChannelRole.ListenOnly,
            "officer" => VoiceChannelRole.Officer,
            _ => VoiceChannelRole.Member
        };
        string action = selectedChannelAction is "listenonly" or "member" or "officer" ? "role" : selectedChannelAction;
        controller.ManageSelectedChannel(action, config.SelectedChannelId, selectedPlayerUid, string.Empty, role);
        return true;
    }

    private bool RenameSelectedChannel()
    {
        if (string.IsNullOrWhiteSpace(renameText) || string.IsNullOrWhiteSpace(config.SelectedChannelId)) return false;
        controller.ManageSelectedChannel("rename", config.SelectedChannelId, name: renameText);
        return true;
    }

    private bool ExecuteModeration(string action)
    {
        if (!controller.HasServerControl || string.IsNullOrWhiteSpace(selectedPlayerUid)) return false;
        controller.ManageSelectedChannel(action, string.Empty, selectedPlayerUid);
        return true;
    }

    private void OnAdminChannelChanged(string value, bool selected)
    {
        if (!selected) return;
        selectedAdminChannelId = value;
        renameText = controller.BuildChannelOptions().FirstOrDefault(channel => channel.Id == value).Name ?? string.Empty;
        QueueCompose();
    }

    private void OnAdminActionChanged(string value, bool selected)
    {
        if (selected)
        {
            selectedAdminAction = value;
            SetButtonEnabled("adminApply", CanExecuteAdminAction());
        }
    }

    private bool ExecuteAdminChannelAction()
    {
        if (!controller.HasServerControl || !CanExecuteAdminAction()) return false;
        VoiceChannelRole role = selectedAdminAction switch
        {
            "listenonly" => VoiceChannelRole.ListenOnly,
            "officer" => VoiceChannelRole.Officer,
            _ => VoiceChannelRole.Member
        };
        string action = selectedAdminAction is "listenonly" or "member" or "officer" ? "role" : selectedAdminAction;
        controller.ManageSelectedChannel(action, selectedAdminChannelId, selectedPlayerUid, string.Empty, role);
        return true;
    }

    private bool RenameAdminChannel()
    {
        if (!controller.HasServerControl || string.IsNullOrWhiteSpace(renameText)) return false;
        controller.ManageSelectedChannel("rename", selectedAdminChannelId, name: renameText);
        return true;
    }

    private void OnRenameTextChanged(string value)
    {
        renameText = value;
        SetButtonEnabled("adminRename", CanRenameAdminChannel(controller.BuildChannelOptions()));
    }

    private void OnCreateNameChanged(string value)
    {
        createName = value;
        SetButtonEnabled("createChannel", controller.HasServerControl && !string.IsNullOrWhiteSpace(value));
    }

    private void OnCreateKindChanged(string value, bool selected)
    {
        if (selected) selectedCreateKind = value;
    }

    private bool CreateChannel()
    {
        if (!controller.HasServerControl || string.IsNullOrWhiteSpace(createName)) return false;
        controller.ManageSelectedChannel("create-" + selectedCreateKind, string.Empty, name: createName);
        createName = string.Empty;
        SingleComposer?.GetTextInput("createName")?.SetValue(string.Empty);
        SetButtonEnabled("createChannel", false);
        return true;
    }

    private bool CanExecuteAdminAction()
    {
        return controller.HasServerControl
            && !string.IsNullOrWhiteSpace(selectedAdminChannelId)
            && (!VoiceSettingsActionPolicy.RequiresTarget(selectedAdminAction) || !string.IsNullOrWhiteSpace(selectedPlayerUid));
    }

    private bool CanRenameAdminChannel(VoiceSettingsChannelOption[] channels)
    {
        return controller.HasServerControl
            && !string.IsNullOrWhiteSpace(renameText)
            && channels.Any(channel => channel.Id == selectedAdminChannelId && !channel.ExternallyManaged);
    }

    private void NormalizePlayer(VoiceSettingsPlayerOption[] players)
    {
        if (!players.Any(player => player.Id == selectedPlayerUid))
        {
            selectedPlayerUid = players.FirstOrDefault().Id ?? string.Empty;
        }
    }

    private void NormalizeAdminChannel(VoiceSettingsChannelOption[] channels)
    {
        if (channels.Any(channel => channel.Id == selectedAdminChannelId)) return;
        selectedAdminChannelId = channels.FirstOrDefault(channel => channel.Id == config.SelectedChannelId).Id
            ?? channels.FirstOrDefault().Id
            ?? string.Empty;
        renameText = channels.FirstOrDefault(channel => channel.Id == selectedAdminChannelId).Name ?? string.Empty;
    }

    private bool RefreshStatus()
    {
        controller.RequestSettingsRefresh();
        QueueCompose();
        return true;
    }

    private void SetButtonEnabled(string key, bool enabled)
    {
        if (SingleComposer?.TryGetButton(key) is { } button)
        {
            button.Enabled = enabled;
        }
    }

    private void OnScroll(float value)
    {
        scrollPosition = Math.Clamp(
            value,
            0f,
            (float)Math.Max(0d, contentHeight - ViewportHeight));
        if (contentBounds == null) return;
        contentBounds.fixedY = -scrollPosition;
        contentBounds.CalcWorldBounds();
        if (!suppressScrollCallback)
        {
            SingleComposer?.ReCompose();
        }
    }

    private void QueueCompose()
    {
        if (composeQueued) return;
        composeQueued = true;
        capi.Event.EnqueueMainThreadTask(() =>
        {
            composeQueued = false;
            if (IsOpened()) Compose();
        }, "simplevoicechat-settings-recompose");
    }

    private void OnClose()
    {
        TryClose();
    }

    private static string GetPageName(VoiceSettingsPage page)
    {
        return page switch
        {
            VoiceSettingsPage.Channels => SVCLang.Get("tab-channels"),
            VoiceSettingsPage.Status => SVCLang.Get("tab-status"),
            VoiceSettingsPage.Admin => SVCLang.Get("tab-admin"),
            _ => SVCLang.Get("tab-audio")
        };
    }

    private static string GetPageTitle(VoiceSettingsPage page)
    {
        return page switch
        {
            VoiceSettingsPage.Channels => SVCLang.Get("ui-channels-title"),
            VoiceSettingsPage.Status => SVCLang.Get("ui-status-title"),
            VoiceSettingsPage.Admin => SVCLang.Get("ui-admin-title"),
            _ => SVCLang.Get("ui-audio-title")
        };
    }

    private static string TransmitCode(VoiceTransmitTarget target)
    {
        return target switch
        {
            VoiceTransmitTarget.SelectedChannel => "channel",
            VoiceTransmitTarget.ProximityAndChannel => "both",
            _ => "proximity"
        };
    }

    private static string FormatChannelKind(VoiceChannelKind kind)
    {
        return kind switch
        {
            VoiceChannelKind.Civilization => SVCLang.Get("channel-kind-civilization"),
            VoiceChannelKind.Command => SVCLang.Get("channel-kind-command"),
            VoiceChannelKind.Diplomacy => SVCLang.Get("channel-kind-diplomacy"),
            VoiceChannelKind.Staff => SVCLang.Get("channel-kind-staff"),
            VoiceChannelKind.Broadcast => SVCLang.Get("channel-kind-broadcast"),
            VoiceChannelKind.Radio => SVCLang.Get("channel-kind-radio"),
            _ => SVCLang.Get("channel-kind-squad")
        };
    }

    private static string Truncate(string value, int maximumLength)
    {
        return value.Length <= maximumLength ? value : value[..Math.Max(1, maximumLength - 3)] + "...";
    }
}
