using Vintagestory.API.Client;
using VSRmlUi;

namespace SimpleVoiceChat.Gui;

/// <summary>Displays the one time credential used by the LauncherGo voice page.</summary>
public sealed class VoiceWebMicrophoneDialog : VoiceRmlDialog
{
    protected override bool AllowWindowDrag => false;
    protected override RmlDocumentOptions Options => new()
    {
        Mode = RmlWindowMode.Window, DrawOrder = .98, InputOrder = 1.3,
        FocusOnOpen = true, UnlockMouse = true,
        Input = new() { ReceiveMouse = true, ReceiveKeyboard = true, UnlockMouse = true, CapturePointer = true }
    };

    private readonly Func<bool> getCredential;
    private readonly Action? opening;
    private readonly Action? closed;
    private string currentToken = "";
    private bool returning;
    private bool restorePending;

    public VoiceWebMicrophoneDialog(ICoreClientAPI api, Func<bool> getCredential, Action? opening = null, Action? closed = null) : base(api)
    {
        this.getCredential = getCredential;
        this.opening = opening;
        this.closed = closed;
    }

    public void ShowToken(string token, DateTimeOffset expiresAtUtc)
    {
        bool wasOpen = IsOpened();
        currentToken = token;
        string expiry = expiresAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        if (Document is { IsDisposed: false })
        {
            Document.GetElementById("token")!.Value = token;
            Document.GetElementById("expiry")!.Text = $"一次性凭证，请在 {expiry} 前使用";
            Document.GetElementById("copy-status")!.Text = "";
            TryOpen();
            if (!wasOpen) { restorePending = true; opening?.Invoke(); }
            return;
        }
        Load("<div id='window' class='window web-token' style='width:560dp;height:260dp;'>"
            + "<div id='window-header'><span id='window-title'>网页麦克风</span></div>"
            + "<button id='window-close' class='close' title='关闭'><span class='icon'>&#xeb55;</span></button>"
            + "<div id='content'><div class='token-detail'>请打开浏览器中的语音网页，将下面的 Token 粘贴到凭证栏。</div>"
            + $"<input id='token' value='{E(token)}' readonly />"
            + $"<div id='expiry' class='token-detail'>一次性凭证，请在 {E(expiry)} 前使用</div>"
            + "<div id='copy-status' class='token-detail'></div>"
            + "<div class='token-actions'><button id='copy' class='primary'><span class='button-label'>复制 Token</span></button><button id='renew'><span class='button-label'>获取凭证</span></button><button id='close'><span class='button-label'>知道了</span></button></div>"
            + "</div></div>", "web-microphone");
        Document!.GetElementById("window-close")!.On("click", _ => TryClose());
        Document!.GetElementById("close")!.On("click", _ => TryClose());
        Document!.GetElementById("copy")!.On("click", _ =>
        {
            capi.Input.ClipboardText = currentToken;
            Document!.GetElementById("copy-status")!.Text = "Token 已复制到剪贴板。";
        });
        Document!.GetElementById("renew")!.On("click", _ => getCredential());
        TryOpen();
        // Keep an interactive window open throughout the handoff so the game never grabs the mouse.
        restorePending = true;
        opening?.Invoke();
    }

    public override bool TryClose()
    {
        if (!IsOpened() || returning) return false;
        returning = true;
        try { RestoreParent(); return base.TryClose(); }
        finally { returning = false; }
    }

    protected override bool OnEscape() { TryClose(); return true; }
    protected override void OnClosed() => RestoreParent();
    private void RestoreParent()
    {
        if (!restorePending) return;
        restorePending = false;
        closed?.Invoke();
    }
}
