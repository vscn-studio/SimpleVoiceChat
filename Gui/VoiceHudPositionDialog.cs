using SimpleVoiceChat.Config;
using Vintagestory.API.Client;
using VSRmlUi;

namespace SimpleVoiceChat.Gui;

/// <summary>RML drag handles track the real HUDs and own pointer capture during positioning.</summary>
public sealed class VoiceHudPositionDialog : VoiceRmlDialog
{
    private readonly SimpleVoiceChatClientConfig config;
    private readonly VoiceHud hud;
    private readonly VoiceInviteDialog invite;
    private readonly Action<int, int, int> save;
    private readonly Action<bool>? editingChanged;
    private string? dragTarget;
    private float startX, startY;
    private int voiceX, voiceY, inviteY;
    protected override RmlDocumentOptions Options => new() { Mode = RmlWindowMode.Window, DrawOrder = .98, InputOrder = .2 };
    public VoiceHudPositionDialog(ICoreClientAPI api, SimpleVoiceChatClientConfig config, VoiceHud hud, VoiceInviteDialog invite,
        Action<int, int, int> save, Action<bool>? editingChanged = null) : base(api)
    { this.config = config; this.hud = hud; this.invite = invite; this.save = save; this.editingChanged = editingChanged; }
    public override bool TryOpen()
    {
        if (IsOpened()) return true;
        hud.BeginPositionEditing(); invite.BeginPositionEditing(); editingChanged?.Invoke(true);
        Load($"<div id='voice-handle' class='drag-handle'></div><div id='invite-handle' class='drag-handle'></div><button id='confirm' class='position-help primary'><span class='button-label'>{E(SVCLang.Get("button-confirm-hud-position"))}</span></button>", "hud-position");
        foreach (string target in new[] { "voice", "invite" })
        {
            var handle = Document!.GetElementById(target + "-handle")!;
            handle.On("mousedown", e =>
            {
                if (e.Button != 0) return;
                dragTarget = target; startX = e.MouseX; startY = e.MouseY;
                voiceX = config.VoiceHudOffsetX; voiceY = config.VoiceHudOffsetY; inviteY = config.VoiceInviteOffsetY;
                handle.CapturePointer();
            });
        }
        Document!.InputCancelled += () => dragTarget = null;
        Document.GetElementById("confirm")!.On("click", _ => TryClose());
        UpdateHandles(); return base.TryOpen();
    }
    internal void ConfirmFromSettings() => TryClose();
    protected override bool HandleInput(RmlInputEvent input)
    {
        if (dragTarget is null) return false;
        if (input.Kind == RmlInputKind.MouseUp && input.Button == 0)
        { dragTarget = null; Document!.ReleasePointer(); return false; }
        if (input.Kind != RmlInputKind.MouseMove) return false;
        float scale = Document!.Viewport.Scale;
        int dx = (int)Math.Round((input.X - startX) / scale), dy = (int)Math.Round((input.Y - startY) / scale);
        if (dragTarget == "voice")
        {
            config.VoiceHudOffsetX = Math.Clamp(voiceX + dx, -2000, 2000);
            config.VoiceHudOffsetY = Math.Clamp(voiceY + dy, -2000, 2000); hud.RefreshLayout();
        }
        else { config.VoiceInviteOffsetY = Math.Clamp(inviteY + dy, -2000, 2000); invite.RefreshPosition(); }
        UpdateHandles();
        return true;
    }
    protected override void OnTick(float dt) { if (IsOpened()) UpdateHandles(); }
    private void UpdateHandles()
    {
        if (Document is not { IsDisposed: false }) return;
        Place("voice-handle", hud.TryGetInteractionBounds(out double x, out double y, out double w, out double h), x, y, w, h);
        Place("invite-handle", invite.TryGetInteractionBounds(out x, out y, out w, out h), x, y, w, h);
    }
    private void Place(string id, bool visible, double x, double y, double w, double h)
    {
        var element = Document!.GetElementById(id)!;
        element.SetProperty("display", visible ? "block" : "none");
        element.SetProperty("left", N(x) + "px"); element.SetProperty("top", N(y) + "px");
        element.SetProperty("width", N(w) + "px"); element.SetProperty("height", N(h) + "px");
    }
    protected override void OnClosed()
    {
        dragTarget = null;
        save(config.VoiceHudOffsetX, config.VoiceHudOffsetY, config.VoiceInviteOffsetY);
        hud.EndPositionEditing(); invite.EndPositionEditing(); editingChanged?.Invoke(false);
    }
}
