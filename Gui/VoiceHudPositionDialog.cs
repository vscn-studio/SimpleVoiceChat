using SimpleVoiceChat.Config;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SimpleVoiceChat.Gui;

/// <summary>
/// Transparent input layer over the real voice HUDs. It never draws a second
/// HUD: the existing VoiceHud and VoiceInviteDialog remain visible underneath.
/// </summary>
public sealed class VoiceHudPositionDialog : GuiDialog
{
    private readonly SimpleVoiceChatClientConfig config;
    private readonly VoiceHud hud;
    private readonly VoiceInviteDialog invite;
    private readonly Action<int, int, int> save;
    private readonly Action<bool>? editingChanged;
    private int frameWidth;
    private int frameHeight;
    private double scale = 1;
    private string? dragTarget;
    private double startX;
    private double startY;
    private int startVoiceX;
    private int startVoiceY;
    private int startInviteY;
    private bool committed;

    public VoiceHudPositionDialog(
        ICoreClientAPI capi,
        SimpleVoiceChatClientConfig config,
        VoiceHud hud,
        VoiceInviteDialog invite,
        Action<int, int, int> save,
        Action<bool>? editingChanged = null)
        : base(capi)
    {
        this.config = config;
        this.hud = hud;
        this.invite = invite;
        this.save = save;
        this.editingChanged = editingChanged;
    }

    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;
    public override bool DisableMouseGrab => true;
    // The settings dialog remains interactive so its button can be pressed a
    // second time to confirm. Mouse events over the HUD are still handled by
    // this higher-priority dialog.
    public override bool CaptureAllInputs() => dragTarget != null;
    public override bool CaptureRawMouse() => true;
    public override EnumDialogType DialogType => EnumDialogType.HUD;
    public override double DrawOrder => 0.98;
    // Handle HUD hit-testing before the settings window. The settings
    // composer bounds cover the whole window, so its base dialog handler can
    // otherwise consume clicks when the HUD overlaps that area.
    public override double InputOrder => 0.2;

    public override bool TryOpen()
    {
        frameWidth = capi.Render.FrameWidth;
        frameHeight = capi.Render.FrameHeight;
        dragTarget = null;
        committed = false;
        hud.BeginPositionEditing();
        invite.BeginPositionEditing();
        editingChanged?.Invoke(true);
        Compose();
        return base.TryOpen();
    }

    public override void OnRenderGUI(float deltaTime)
    {
        _ = deltaTime;
        if (frameWidth != capi.Render.FrameWidth || frameHeight != capi.Render.FrameHeight)
        {
            frameWidth = capi.Render.FrameWidth;
            frameHeight = capi.Render.FrameHeight;
            Compose();
        }
        base.OnRenderGUI(deltaTime);
    }

    public override bool OnEscapePressed()
    {
        CommitAndClose();
        return true;
    }

    public override void OnGuiClosed()
    {
        if (!committed)
        {
            save(config.VoiceHudOffsetX, config.VoiceHudOffsetY, config.VoiceInviteOffsetY);
            hud.EndPositionEditing();
            invite.EndPositionEditing();
            editingChanged?.Invoke(false);
        }
        committed = false;
        base.OnGuiClosed();
    }

    public override void OnMouseDown(MouseEvent args)
    {
        if (args.Button != EnumMouseButton.Left)
        {
            return;
        }

        if (TryContains(hud, args.X, args.Y))
        {
            dragTarget = "voice";
        }
        else if (TryContains(invite, args.X, args.Y))
        {
            dragTarget = "invite";
        }
        else
        {
            return;
        }

        startX = args.X;
        startY = args.Y;
        startVoiceX = config.VoiceHudOffsetX;
        startVoiceY = config.VoiceHudOffsetY;
        startInviteY = config.VoiceInviteOffsetY;
        args.Handled = true;
    }

    public override void OnMouseMove(MouseEvent args)
    {
        if (dragTarget == null)
        {
            return;
        }

        double scale = Math.Max(0.5, GuiElement.scaled(1));
        int dx = (int)Math.Round((args.X - startX) / scale);
        int dy = (int)Math.Round((args.Y - startY) / scale);
        if (dragTarget == "voice")
        {
            config.VoiceHudOffsetX = Clamp(startVoiceX + dx);
            config.VoiceHudOffsetY = Clamp(startVoiceY + dy);
            hud.RefreshLayout();
        }
        else
        {
            config.VoiceInviteOffsetY = Clamp(startInviteY + dy);
            invite.RefreshPosition();
        }

        args.Handled = true;
    }

    public override void OnMouseUp(MouseEvent args)
    {
        if (args.Button == EnumMouseButton.Left && dragTarget != null)
        {
            // Releasing the mouse ends the current drag only. The adjustment
            // mode stays active until the button is confirmed or ESC is used.
            dragTarget = null;
            args.Handled = true;
        }
    }

    internal void ConfirmFromSettings() => CommitAndClose();

    private void Compose()
    {
        scale = Math.Max(0.5, GuiElement.scaled(1));
        double width = Math.Max(1, frameWidth / scale);
        double height = Math.Max(1, frameHeight / scale);
        ElementBounds root = ElementBounds.Fixed(EnumDialogArea.LeftTop, 0, 0, width, height);
        ElementBounds drawBounds = ElementBounds.Fixed(0, 0, width, height);
        SingleComposer = capi.Gui.CreateCompo("simplevoicechat-hud-position", root)
            .AddDynamicCustomDraw(drawBounds, static (_, _, _) => { }, "input-layer")
            .Compose();
    }

    private void CommitAndClose()
    {
        if (dragTarget == null && !IsOpened())
        {
            return;
        }

        dragTarget = null;
        committed = true;
        save(config.VoiceHudOffsetX, config.VoiceHudOffsetY, config.VoiceInviteOffsetY);
        hud.EndPositionEditing();
        invite.EndPositionEditing();
        editingChanged?.Invoke(false);
        TryClose();
    }

    private static int Clamp(int value) => Math.Clamp(value, -2000, 2000);

    private static bool TryContains(VoiceHud element, double x, double y)
    {
        return element.TryGetInteractionBounds(out double left, out double top, out double width, out double height)
            && x >= left && x <= left + width && y >= top && y <= top + height;
    }

    private static bool TryContains(VoiceInviteDialog element, double x, double y)
    {
        return element.TryGetInteractionBounds(out double left, out double top, out double width, out double height)
            && x >= left && x <= left + width && y >= top && y <= top + height;
    }
}
