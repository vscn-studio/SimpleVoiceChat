using Vintagestory.API.Client;
using VSRmlUi;

namespace SimpleVoiceChat.Gui;

public sealed class VoiceHud : VoiceRmlDialog
{
    private readonly Func<VoiceHudSnapshot> snapshotProvider;
    private readonly Func<bool> shouldShowProvider;
    private readonly Func<(int X, int Y)> positionProvider;
    private bool positionEditing;
    public double ReservedHeight => IsOpened() ? (Document!.GetElementById("voice-hud")!.Bounds.Height / Document.Viewport.Scale) + 18 : 0;
    protected override RmlDocumentOptions Options => new()
    {
        Mode = RmlWindowMode.Hud, DrawOrder = .09, CloseOnEscape = false, FocusOnOpen = false,
        Input = new() { ReceiveMouse = false, ReceiveKeyboard = false, UnlockMouse = false }
    };
    public VoiceHud(ICoreClientAPI api, Func<VoiceHudSnapshot> snapshotProvider, Func<bool> shouldShowProvider, Func<(int X, int Y)>? positionProvider = null) : base(api)
    {
        this.snapshotProvider = snapshotProvider; this.shouldShowProvider = shouldShowProvider;
        this.positionProvider = positionProvider ?? (() => (0, 0));
    }
    protected override void OnTick(float dt) => Refresh();
    public void Refresh()
    {
        if (!shouldShowProvider() && !positionEditing) { TryClose(); return; }
        if (Document is null)
            Load("<div id='voice-hud' class='hud'><img id='hud-icon' class='hud-icon'/><div class='hud-info'><div id='hud-status' class='hud-status'></div><div id='hud-mode' class='hud-mode'></div><img id='hud-volume' class='hud-volume'/></div></div>", "hud");
        if (Document!.IsDisposed) return;
        var snapshot = snapshotProvider();
        string icon = snapshot.IconState switch
        {
            VoiceHudIconState.Muted => "svc_mic_muted", VoiceHudIconState.Whispering => "svc_whispering",
            VoiceHudIconState.Talking => "svc_talking", _ => "svc_voice_disabled"
        };
        Document.GetElementById("hud-icon")!.SetAttribute("src", "simplevoicechat:textures/gui/" + icon + ".png");
        Document.GetElementById("hud-status")!.Text = snapshot.Status;
        Document.GetElementById("hud-mode")!.Text = snapshot.Mode;
        Document.GetElementById("hud-volume")!.SetAttribute("src", $"simplevoicechat:textures/gui/volume/volume-{(int)Math.Round(Math.Clamp(snapshot.VoiceLevel, 0, 1) * 40):00}.png");
        RefreshLayout();
        if (!IsOpened()) TryOpen();
    }
    public void RefreshLayout()
    {
        if (Document is not { IsDisposed: false }) return;
        var (x, y) = positionProvider();
        var hud = Document.GetElementById("voice-hud")!;
        hud.SetProperty("right", N(18 - x) + "dp"); hud.SetProperty("bottom", N(34 - y) + "dp");
    }
    public void BeginPositionEditing() { positionEditing = true; Refresh(); }
    public void EndPositionEditing() { positionEditing = false; Refresh(); }
    public bool TryGetInteractionBounds(out double x, out double y, out double width, out double height)
    {
        x = y = width = height = 0;
        if (!IsOpened()) return false;
        var b = Document!.GetElementById("voice-hud")!.Bounds;
        x = b.X; y = b.Y; width = b.Width; height = b.Height; return true;
    }
}
