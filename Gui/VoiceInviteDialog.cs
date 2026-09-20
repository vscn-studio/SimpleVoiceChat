using Vintagestory.API.Client;
using SimpleVoiceChat.Networking;
using VSRmlUi;

namespace SimpleVoiceChat.Gui;

internal static class VoiceInvitePolicy
{
    public const long ResponseTimeoutMilliseconds = VoiceConstants.ChannelInviteTimeoutMilliseconds;

    public static bool HasExpired(long nowMilliseconds, long deadlineMilliseconds)
    {
        return deadlineMilliseconds > 0 && nowMilliseconds >= deadlineMilliseconds;
    }
}

public sealed class VoiceInviteDialog : VoiceRmlDialog
{
    private readonly Func<long> nowProvider;
    private readonly Func<bool> accept, decline;
    private readonly Func<int> offsetYProvider;
    private readonly Func<string> acceptShortcutProvider, declineShortcutProvider;
    private bool positionPreview, editing;
    private long deadline;
    protected override RmlDocumentOptions Options => new()
    {
        Mode = RmlWindowMode.Window, DrawOrder = .96, InputOrder = 1.2, CloseOnEscape = false,
        FocusOnOpen = false, UnlockMouse = false,
        Input = new() { ReceiveMouse = true, ReceiveKeyboard = false, UnlockMouse = false, CapturePointer = false }
    };
    public VoiceInviteDialog(ICoreClientAPI api, Func<long> nowProvider, Func<bool> accept, Func<bool> decline,
        Func<double> bottomReservedHeightProvider, Func<int>? offsetYProvider = null,
        Func<string>? acceptShortcutProvider = null, Func<string>? declineShortcutProvider = null) : base(api)
    {
        this.nowProvider = nowProvider; this.accept = accept; this.decline = decline;
        this.offsetYProvider = offsetYProvider ?? (() => 85);
        this.acceptShortcutProvider = acceptShortcutProvider ?? (() => "Ctrl+F8");
        this.declineShortcutProvider = declineShortcutProvider ?? (() => "F7");
    }
    public void ShowInvite(string inviter, string channelId, string channelName, int channelMemberCount,
        int channelMaxMembers, VoiceChannelVisibility channelVisibility, bool channelLocked, long deadline)
    {
        positionPreview = false; this.deadline = deadline;
        string members = channelMaxMembers > 0 ? SVCLang.Get("invite-members", channelMemberCount, channelMaxMembers) : SVCLang.Get("invite-members-count", channelMemberCount);
        string visibility = SVCLang.Get("channel-visibility-" + channelVisibility.ToString().ToLowerInvariant());
        if (channelLocked) visibility += " / " + SVCLang.Get("channel-locked");
        Load($"<div id='invite' class='invite'><div class='invite-title'>{E(SVCLang.Get("invite-title"))}</div>"
            + $"<div class='invite-detail'>{E(SVCLang.Get("invite-message", inviter))}</div><div class='invite-detail'>{E(SVCLang.Get("invite-channel", channelName))}</div>"
            + $"<div class='invite-detail'>{E(members + "  " + visibility)}</div><div class='invite-actions'><button id='decline'><span id='decline-label' class='button-label'></span></button><button id='accept' class='primary'><span id='accept-label' class='button-label'></span></button></div></div>", "invite");
        Document!.GetElementById("accept")!.On("click", _ => { if (!editing && !positionPreview && !VoiceInvitePolicy.HasExpired(nowProvider(), this.deadline)) accept(); });
        Document.GetElementById("decline")!.On("click", _ => { if (!editing && !positionPreview) decline(); });
        RefreshPosition(); RefreshInvite(); TryOpen();
    }
    protected override bool OnEscape() => false;
    protected override void OnTick(float dt)
    {
        if (!IsOpened()) return;
        if (!positionPreview && VoiceInvitePolicy.HasExpired(nowProvider(), deadline)) { Dismiss(); return; }
        RefreshInvite();
    }
    public void Dismiss() { deadline = 0; TryClose(); }
    public void RefreshInvite()
    {
        if (Document is not { IsDisposed: false }) return;
        long seconds = Math.Max(0, (deadline - nowProvider() + 999) / 1000);
        Document.GetElementById("decline-label")!.Text = SVCLang.Get("button-decline-invite-shortcut", declineShortcutProvider(), seconds);
        Document.GetElementById("accept-label")!.Text = SVCLang.Get("button-accept-invite-shortcut", acceptShortcutProvider());
    }
    public void RefreshPosition()
    {
        if (Document is not { IsDisposed: false }) return;
        var invite = Document.GetElementById("invite")!;
        invite.SetProperty("left", "18dp"); invite.SetProperty("top", N(18 + offsetYProvider()) + "dp");
    }
    public void BeginPositionEditing()
    {
        editing = true;
        if (IsOpened()) return;
        ShowInvite("Player", "preview", "Channel", 3, 16, VoiceChannelVisibility.Open, false, nowProvider() + 30000);
        positionPreview = true;
    }
    public void EndPositionEditing() { editing = false; if (positionPreview) Dismiss(); positionPreview = false; }
    public bool TryGetInteractionBounds(out double x, out double y, out double width, out double height)
    {
        x = y = width = height = 0;
        if (!IsOpened()) return false;
        var b = Document!.GetElementById("invite")!.Bounds;
        x = b.X; y = b.Y; width = b.Width; height = b.Height; return true;
    }
}
