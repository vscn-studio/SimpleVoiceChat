using SimpleVoiceChat.Integration;
using Vintagestory.API.Client;
using VSRmlUi;

namespace SimpleVoiceChat.Gui;

internal sealed class VoiceSettingsExtensionDialog : VoiceRmlDialog
{
    private readonly VoiceSettingsExtensionWindow definition;
    private readonly Action closed;
    protected override RmlDocumentOptions Options => new() { Mode = RmlWindowMode.Modal, DrawOrder = .7, InputOrder = .1 };
    public VoiceSettingsExtensionDialog(ICoreClientAPI api, VoiceSettingsExtensionWindow definition, Action closed) : base(api)
    { this.definition = definition; this.closed = closed; }
    public override bool TryOpen()
    {
        double width = double.IsFinite(definition.Width) ? Math.Clamp(definition.Width, 360, 940) : 640;
        double height = double.IsFinite(definition.Height) ? Math.Clamp(definition.Height, 220, 650) : 420;
        VoiceRmlForm form = new(capi);
        try
        {
            definition.Compose(new(capi, form, ElementBounds.Fixed(0, 0, width - 40, height - 70), () => TryClose()));
        }
        catch (Exception ex)
        {
            capi.Logger.Warning("SimpleVoiceChat: extension window '{0}' failed: {1}", definition.Id, ex.Message);
        }
        Present(form, definition.Title, width, height - 70);
        return base.TryOpen();
    }
    protected override void OnClosed() => closed();
}
