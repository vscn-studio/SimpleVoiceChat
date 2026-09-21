using System.Globalization;
using System.Net;
using Vintagestory.API.Client;
using VSRmlUi;

namespace SimpleVoiceChat.Gui;

/// <summary>Owns a client-thread RmlUi document and its subscriptions for one world.</summary>
public abstract class VoiceRmlDialog : IDisposable
{
    protected readonly ICoreClientAPI capi;
    protected RmlDocument? Document;
    protected VoiceRmlForm? Form;
    private readonly long tick;
    private bool disposed;
    private bool dragging;
    private bool positioned;
    private float dragX, dragY, startLeft, startTop;
    protected bool PointerPressed { get; private set; }
    protected virtual RmlDocumentOptions Options => new() { Mode = RmlWindowMode.Window, DrawOrder = .48, InputOrder = .3 };
    protected virtual bool AllowWindowDrag => false;
    protected VoiceRmlDialog(ICoreClientAPI api)
    {
        capi = api;
        tick = api.Event.RegisterGameTickListener(dt =>
        {
            if (!disposed) OnTick(dt);
        }, 50);
    }

    public bool IsOpened() => !disposed && Document is { IsDisposed: false, IsVisible: true };
    public virtual bool TryOpen()
    {
        if (disposed || Document is not { IsDisposed: false }) return false;
        RecenterWindow();
        Document.Show();
        return true;
    }
    public virtual bool TryClose()
    {
        if (!IsOpened()) return false;
        Document!.Close();
        return true;
    }
    public void Toggle() { if (IsOpened()) TryClose(); else TryOpen(); }
    protected virtual void OnClosed() { }
    protected virtual void OnWindowClose() => TryClose();
    protected virtual bool OnEscape() { TryClose(); return true; }
    protected virtual bool HandleInput(RmlInputEvent input) => false;
    protected virtual void OnTick(float dt)
    {
        if (!IsOpened()) return;
        Form?.Update();
        if (positioned && Document!.GetElementById("window") is { } window)
        {
            var bounds = window.Bounds;
            float scale = Document.Viewport.Scale;
            window.SetProperty("left", N(Math.Clamp(bounds.X, 0, Math.Max(0, Document.Viewport.Width - bounds.Width)) / scale) + "dp");
            window.SetProperty("top", N(Math.Clamp(bounds.Y, 0, Math.Max(0, Document.Viewport.Height - bounds.Height)) / scale) + "dp");
        }
    }

    /// <summary>Restores the centered layout used when a regular dialog opens.</summary>
    protected void RecenterWindow()
    {
        if (Document?.GetElementById("window") is not { } window
            || window.ClassNames.Contains("overlay-host", StringComparison.Ordinal))
        {
            return;
        }

        positioned = false;
        window.SetProperty("left", "0dp");
        window.SetProperty("right", "0dp");
        window.SetProperty("top", "0dp");
        window.SetProperty("bottom", "0dp");
        window.SetProperty("margin", "auto");
    }

    protected void Load(string body, string id, VoiceRmlForm? form = null)
    {
        bool visible = IsOpened();
        if (Document is { IsDisposed: false })
        {
            Document.Closed -= Closed;
            Document.Dispose();
        }
        Form?.Dispose();
        Form = form;
        var ui = capi.ModLoader.GetModSystem<RmlUiModSystem>().Service
            ?? throw new InvalidOperationException("SimpleVoiceChat requires the vsrmlui 1.0.1 client runtime.");
        ui.RegisterFont("simplevoicechat:fonts/tabler/tabler-icons.ttf", "svc-tabler");
        Document = ui.LoadDocumentFromString("simplevoicechat",
            "<rml><head><link type='text/rcss' href='simplevoicechat:dialog/voice.rcss'/>"
            + "<link type='text/rcss' href='vsrmlui:dialog/controls.rcss'/></head><body>" + body + "</body></rml>",
            "simplevoicechat:dialog/" + id + ".rml", Options);
        Document.Closed += Closed;
        Document.InputCancelled += () => { PointerPressed = false; dragging = false; };
        Document.InputFilter = input =>
        {
            if (input.Kind == RmlInputKind.KeyDown && input.Key == (int)GlKeys.Escape) return OnEscape();
            if (input.Kind == RmlInputKind.MouseDown) PointerPressed = true;
            if (input.Kind == RmlInputKind.MouseUp) { PointerPressed = false; if (dragging) { dragging = false; Document.ReleasePointer(); } }
            if (dragging && input.Kind == RmlInputKind.MouseMove)
            {
                positioned = true;
                var window = Document.GetElementById("window")!;
                var bounds = window.Bounds;
                float scale = Document.Viewport.Scale;
                window.SetProperty("left", N(Math.Clamp(startLeft + input.X - dragX, 0, Math.Max(0, Document.Viewport.Width - bounds.Width)) / scale) + "dp");
                window.SetProperty("top", N(Math.Clamp(startTop + input.Y - dragY, 0, Math.Max(0, Document.Viewport.Height - bounds.Height)) / scale) + "dp");
                window.SetProperty("margin", "0dp");
                return true;
            }
            return HandleInput(input);
        };
        form?.Bind(Document);
        if (AllowWindowDrag && Document.GetElementById("window-header") is { } header)
        {
            header.On("mousedown", e =>
            {
                if (e.Button != 0) return;
                var b = Document.GetElementById("window")!.Bounds;
                startLeft = b.X; startTop = b.Y; dragX = e.MouseX; dragY = e.MouseY;
                dragging = true; header.CapturePointer();
            });
        }
        if (visible) Document.Show();
    }

    protected void Present(VoiceRmlForm form, string title, double width, double height, bool overlayOnly = false)
    {
        double windowHeight = height + (overlayOnly ? 0 : 70);
        string windowClass = overlayOnly ? "window overlay-host" : "window";
        // Keep the document/host stable while replacing only the form. This preserves window position.
        if (Document is { IsDisposed: false } && Document.GetElementById("form") is { } content)
        {
            var scroll = Document.GetElementById("content")!.Bounds;
            string previousClass = Document.GetElementById("window")!.ClassNames;
            float previousWidth = Document.GetElementById("window")!.Bounds.Width;
            float previousHeight = Document.GetElementById("window")!.Bounds.Height;
            Form?.Dispose();
            Form = form;
            content.InnerRml = form.Markup;
            content.SetProperty("height", N(height) + "dp");
            Document.GetElementById("window-title")!.Text = title;
            Document.GetElementById("window")!.ClassNames = windowClass;
            Document.GetElementById("window")!.SetProperty("width", N(width) + "dp");
            Document.GetElementById("window")!.SetProperty("height", N(windowHeight) + "dp");
            if (!overlayOnly && (!previousClass.Equals(windowClass, StringComparison.Ordinal)
                || Math.Abs(previousWidth - width) > 1 || Math.Abs(previousHeight - windowHeight) > 1))
            {
                RecenterWindow();
            }
            form.Bind(Document);
            Document.GetElementById("content")!.SetScrollOffset(scroll.ScrollX, scroll.ScrollY);
            return;
        }
        Load($"<div id='window' class='{windowClass}' style='width:{N(width)}dp;height:{N(windowHeight)}dp;'>"
            + $"<div id='window-header'><span id='window-title'>{E(title)}</span></div>"
            + $"<button id='window-close' class='close' title='{E(SVCLang.Get("button-close"))}'><span class='icon'>&#xeb55;</span></button>"
            + $"<div id='content'><div id='form' style='height:{N(height)}dp'>{form.Markup}</div></div></div>", "window", form);
        Document!.GetElementById("window-close")!.On("click", _ => OnWindowClose());
    }

    private void Closed() { PointerPressed = false; dragging = false; OnClosed(); }
    public virtual void Dispose()
    {
        if (disposed) return;
        TryClose();
        disposed = true;
        capi.Event.UnregisterGameTickListener(tick);
        Form?.Dispose(); Form = null;
        Document?.Dispose(); Document = null;
    }
    internal static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
    internal static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
