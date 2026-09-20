using Vintagestory.API.Client;
using VSRmlUi;

namespace SimpleVoiceChat.Gui;

/// <summary>Displays the one time credential used by the LauncherGo voice page.</summary>
public sealed class VoiceWebMicrophoneDialog : VoiceRmlDialog
{
    protected override RmlDocumentOptions Options => new()
    {
        Mode = RmlWindowMode.Window, DrawOrder = .98, InputOrder = 1.3,
        FocusOnOpen = true, UnlockMouse = true,
        Input = new() { ReceiveMouse = true, ReceiveKeyboard = true, UnlockMouse = true, CapturePointer = true }
    };

    public VoiceWebMicrophoneDialog(ICoreClientAPI api) : base(api) { }

    public void ShowToken(string token, DateTimeOffset expiresAtUtc)
    {
        string expiry = expiresAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        Load($"<div id='web-mic' class='invite'><div class='invite-title'>网页麦克风</div>"
            + "<div class='invite-detail'>请打开 LauncherGo 的“语音”页面，将下面的 Token 粘贴到凭证栏。</div>"
            + $"<div class='invite-detail'><strong>{E(token)}</strong></div>"
            + $"<div class='invite-detail'>有效期至：{E(expiry)}</div>"
            + "<div class='invite-actions'><button id='close' class='primary'><span class='button-label'>知道了</span></button></div></div>", "web-microphone");
        Document!.GetElementById("close")!.On("click", _ => TryClose());
        TryOpen();
    }

    protected override bool OnEscape() { TryClose(); return true; }
}
