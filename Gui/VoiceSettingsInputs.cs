using Cairo;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace SimpleVoiceChat.Gui;

internal static class DirectorDropdownInputCapture
{
    private static GuiElementDirectorDropDown? openPopup;
    private static GuiElementDirectorDropDown? mouseCapture;

    internal static GuiElementDirectorDropDown? OpenPopup
    {
        get
        {
            if (openPopup?.IsPopupOpen != true)
            {
                openPopup = null;
            }
            return openPopup;
        }
    }

    internal static void RegisterOpen(GuiElementDirectorDropDown dropDown)
    {
        if (openPopup is { } previous && !ReferenceEquals(previous, dropDown))
        {
            previous.ClosePopup();
        }
        openPopup = dropDown;
    }

    internal static void CaptureMouse(GuiElementDirectorDropDown dropDown)
        => mouseCapture = dropDown;

    internal static GuiElementDirectorDropDown? TakeMouseCapture()
    {
        GuiElementDirectorDropDown? captured = mouseCapture;
        mouseCapture = null;
        return captured;
    }

    internal static void NotifyClosed(GuiElementDirectorDropDown dropDown)
    {
        if (ReferenceEquals(openPopup, dropDown))
        {
            openPopup = null;
        }
    }
}

[HarmonyPatch(typeof(GuiComposer), nameof(GuiComposer.OnMouseDown))]
internal static class DirectorGuiComposerDropdownCapturePatch
{
    [HarmonyPrefix]
    private static bool Prefix(
        GuiComposer __instance,
        MouseEvent mouseArgs,
        Dictionary<string, GuiElement> ___interactiveElements)
    {
        if (mouseArgs.Handled)
        {
            return false;
        }

        GuiElementDirectorDropDown? openDropdown = DirectorDropdownInputCapture.OpenPopup
            ?? ___interactiveElements.Values
                .OfType<GuiElementDirectorDropDown>()
                .FirstOrDefault(dropDown => dropDown.IsPopupOpen);
        if (openDropdown is null)
        {
            return true;
        }

        DirectorDropdownInputCapture.RegisterOpen(openDropdown);
        DirectorDropdownInputCapture.CaptureMouse(openDropdown);
        openDropdown.OnMouseDown(__instance.Api, mouseArgs);
        mouseArgs.Handled = true;
        return false;
    }
}

[HarmonyPatch(typeof(GuiComposer), nameof(GuiComposer.OnMouseUp))]
internal static class DirectorGuiComposerDropdownMouseUpCapturePatch
{
    [HarmonyPrefix]
    private static bool Prefix(GuiComposer __instance, MouseEvent mouse)
    {
        GuiElementDirectorDropDown? captured = DirectorDropdownInputCapture.TakeMouseCapture();
        if (captured is null)
        {
            return !mouse.Handled;
        }

        if (!captured.IsDisposedForCapture)
        {
            captured.OnMouseUp(__instance.Api, mouse);
        }
        mouse.Handled = true;
        return false;
    }
}

internal static class DirectorGuiTheme
{
    public const double CornerRadius = 4d;
    // Blue surface: #123B5C.
    public const double SurfaceR = 18d / 255d;
    public const double SurfaceG = 59d / 255d;
    public const double SurfaceB = 92d / 255d;
    public const double RaisedR = 0.065d;
    public const double RaisedG = 0.15d;
    public const double RaisedB = 0.19d;
    public const double BorderR = 0.16d;
    public const double BorderG = 0.34d;
    public const double BorderB = 0.44d;
    public const double AccentR = 54d / 255d;
    public const double AccentG = 117d / 255d;
    public const double AccentB = 150d / 255d;
    public const double DangerR = 0.62d;
    public const double DangerG = 0.14d;
    public const double DangerB = 0.1d;
    public const double DangerBorderR = 0.86d;
    public const double DangerBorderG = 0.3d;
    public const double DangerBorderB = 0.2d;
    public const double TextR = 0.9d;
    public const double TextG = 0.94d;
    public const double TextB = 0.96d;

    public static double ScaledCornerRadius => GuiElement.scaled(CornerRadius);

    public static void RoundedRectangle(
        Context context,
        double x,
        double y,
        double width,
        double height,
        double radius)
    {
        radius = Math.Max(0d, Math.Min(radius, Math.Min(width, height) / 2d));
        context.NewPath();
        context.Arc(x + width - radius, y + radius, radius, -Math.PI / 2d, 0d);
        context.Arc(x + width - radius, y + height - radius, radius, 0d, Math.PI / 2d);
        context.Arc(x + radius, y + height - radius, radius, Math.PI / 2d, Math.PI);
        context.Arc(x + radius, y + radius, radius, Math.PI, Math.PI * 1.5d);
        context.ClosePath();
    }

    public static string Ellipsize(Context context, CairoFont font, string value, double maxWidth)
    {
        value ??= string.Empty;
        if (maxWidth <= 0d)
        {
            return string.Empty;
        }

        font.SetupContext(context);
        if (font.GetTextExtents(value).Width <= maxWidth)
        {
            return value;
        }

        const string suffix = "...";
        int low = 0;
        int high = value.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            string candidate = value[..middle].TrimEnd() + suffix;
            if (font.GetTextExtents(candidate).Width <= maxWidth)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low <= 0 ? suffix : value[..low].TrimEnd() + suffix;
    }

    public static string Ellipsize(CairoFont font, string value, double maxWidth)
    {
        value ??= string.Empty;
        if (maxWidth <= 0d)
        {
            return string.Empty;
        }
        if (font.GetTextExtents(value).Width <= maxWidth)
        {
            return value;
        }

        const string suffix = "...";
        int low = 0;
        int high = value.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            string candidate = value[..middle].TrimEnd() + suffix;
            if (font.GetTextExtents(candidate).Width <= maxWidth)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low <= 0 ? suffix : value[..low].TrimEnd() + suffix;
    }
}

/// <summary>
/// VS Director dropdown field. The inherited API remains intact so existing
/// panels can continue to retrieve it as a GuiElementDropDown.
/// </summary>
internal sealed class GuiElementDirectorDropDown : GuiElementDropDown
{
    private LoadedTexture normalTexture;
    private LoadedTexture hoverFieldTexture;
    private LoadedTexture focusTexture;
    private LoadedTexture openTexture;
    private LoadedTexture disabledTexture;
    private LoadedTexture valueTexture;
    private int fieldStamp = int.MinValue;
    private int valueStamp = int.MinValue;
    private bool disposedForCapture;
    private bool visible = true;

    internal bool Visible
    {
        get => visible;
        set
        {
            if (visible == value)
            {
                return;
            }

            visible = value;
            if (!visible)
            {
                ClosePopup();
            }
        }
    }

    public GuiElementDirectorDropDown(
        ICoreClientAPI capi,
        string[] values,
        string[] names,
        int selectedIndex,
        SelectionChangedDelegate onSelectionChanged,
        ElementBounds bounds,
        CairoFont font,
        bool multiSelect)
        : base(capi, values, names, selectedIndex, onSelectionChanged, bounds, font, multiSelect)
    {
        normalTexture = new LoadedTexture(capi);
        hoverFieldTexture = new LoadedTexture(capi);
        focusTexture = new LoadedTexture(capi);
        openTexture = new LoadedTexture(capi);
        disabledTexture = new LoadedTexture(capi);
        valueTexture = new LoadedTexture(capi);

        listMenu.Dispose();
        ElementBounds menuBounds = bounds
            .ForkChildOffseted(-bounds.fixedX, -bounds.fixedY)
            .WithAlignment(EnumDialogArea.None);
        listMenu = new GuiElementDirectorListMenu(
            capi,
            values,
            names,
            selectedIndex,
            HandleSelectionChanged,
            menuBounds,
            font,
            multiSelect)
        {
            HoveredIndex = selectedIndex
        };
    }

    public override bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value)
            {
                return;
            }

            enabled = value;
            fieldStamp = int.MinValue;
            valueStamp = int.MinValue;
            if (!enabled)
            {
                listMenu.OnFocusLost();
            }
        }
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        _ = ctx;
        _ = surface;
        Bounds.CalcWorldBounds();
        ComposeFieldTextures();
        ComposeValueTexture();
        ((GuiElementDirectorListMenu)listMenu).ComposeDirectorElements();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!Visible)
        {
            return;
        }

        EnsureTextures();
        bool hovered = Enabled && IsFieldPositionInside(api.Input.MouseX, api.Input.MouseY);
        LoadedTexture field = !Enabled
            ? disabledTexture
            : listMenu.IsOpened
                ? openTexture
                : HasFocus
                    ? focusTexture
                    : hovered ? hoverFieldTexture : normalTexture;

        bool clipField = InsideClipBounds is not null;
        if (clipField)
        {
            api.Render.PushScissor(InsideClipBounds, stacking: true);
        }
        try
        {
            api.Render.Render2DTexturePremultipliedAlpha(field.TextureId, Bounds);
            api.Render.Render2DTexturePremultipliedAlpha(valueTexture.TextureId, Bounds);
        }
        finally
        {
            if (clipField)
            {
                api.Render.PopScissor();
            }
        }

        // The popup intentionally renders after the field scissor is removed.
        // This keeps scrolled fields inside the viewport without cutting off
        // the expanded menu.
        listMenu.RenderInteractiveElements(deltaTime);
    }

    public override void OnMouseDown(ICoreClientAPI api, MouseEvent args)
    {
        if (!Visible || !Enabled)
        {
            return;
        }

        if (listMenu.IsOpened)
        {
            listMenu.OnMouseDown(api, args);
            return;
        }
        if (args.Button != EnumMouseButton.Left)
        {
            return;
        }

        listMenu.OnMouseDown(api, args);
        if (!args.Handled && !listMenu.IsOpened && IsFieldPositionInside(args.X, args.Y))
        {
            ((GuiElementDirectorListMenu)listMenu).OpenDirector();
            DirectorDropdownInputCapture.RegisterOpen(this);
            api.Gui.PlaySound("menubutton");
            args.Handled = true;
        }
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (Visible)
        {
            listMenu.OnMouseMove(api, args);
        }
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        if (!Visible)
        {
            return;
        }

        listMenu.OnMouseUp(api, args);
        args.Handled |= IsFieldPositionInside(args.X, args.Y);
    }

    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (!Visible || !Enabled)
        {
            return;
        }

        if (listMenu.IsOpened)
        {
            listMenu.OnMouseWheel(api, args);
            return;
        }
        if (!HasFocus)
        {
            return;
        }

        if (!IsFieldPositionInside(api.Input.MouseX, api.Input.MouseY) || listMenu.Values.Length == 0)
        {
            return;
        }

        int direction = args.delta <= 0 ? 1 : -1;
        int index = GameMath.Mod(listMenu.SelectedIndex + direction, listMenu.Values.Length);
        SetSelectedIndex(index);
        valueStamp = int.MinValue;
        args.SetHandled();
        onSelectionChanged?.Invoke(SelectedValue, true);
    }

    public override bool IsPositionInside(int posX, int posY)
        => Visible
            && (IsFieldPositionInside(posX, posY)
                || (listMenu.IsOpened && listMenu.IsPositionInside(posX, posY)));

    internal bool IsPopupOpen => listMenu.IsOpened;

    internal bool IsDisposedForCapture => disposedForCapture;

    internal void ClosePopup()
    {
        listMenu.OnFocusLost();
        DirectorDropdownInputCapture.NotifyClosed(this);
    }

    internal void SetDirectorList(string[] values, string[] names, int selectedIndex)
    {
        ((GuiElementDirectorListMenu)listMenu).SetDirectorList(values, names, selectedIndex);
        valueStamp = int.MinValue;
    }

    public override void Dispose()
    {
        disposedForCapture = true;
        DirectorDropdownInputCapture.NotifyClosed(this);
        normalTexture.Dispose();
        hoverFieldTexture.Dispose();
        focusTexture.Dispose();
        openTexture.Dispose();
        disabledTexture.Dispose();
        valueTexture.Dispose();
        base.Dispose();
    }

    private void HandleSelectionChanged(string value, bool selected)
    {
        valueStamp = int.MinValue;
        onSelectionChanged?.Invoke(value, selected);
    }

    private bool IsFieldPositionInside(int posX, int posY)
        => Bounds.PointInside(posX, posY)
            && (InsideClipBounds is null || InsideClipBounds.PointInside(posX, posY));

    private void EnsureTextures()
    {
        int currentFieldStamp = HashCode.Combine(
            Bounds.OuterWidthInt,
            Bounds.OuterHeightInt,
            Scale,
            Enabled);
        if (currentFieldStamp != fieldStamp)
        {
            ComposeFieldTextures();
        }

        int currentValueStamp = SelectionStamp();
        if (currentValueStamp != valueStamp)
        {
            ComposeValueTexture();
        }
    }

    private void ComposeFieldTextures()
    {
        Bounds.CalcWorldBounds();
        ComposeFieldTexture(ref normalTexture, FieldVisual.Normal);
        ComposeFieldTexture(ref hoverFieldTexture, FieldVisual.Hover);
        ComposeFieldTexture(ref focusTexture, FieldVisual.Focus);
        ComposeFieldTexture(ref openTexture, FieldVisual.Open);
        ComposeFieldTexture(ref disabledTexture, FieldVisual.Disabled);
        fieldStamp = HashCode.Combine(Bounds.OuterWidthInt, Bounds.OuterHeightInt, Scale, Enabled);
    }

    private void ComposeFieldTexture(ref LoadedTexture target, FieldVisual visual)
    {
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);

        double borderAlpha = visual == FieldVisual.Disabled ? 0.22d : 0.9d;
        double backgroundAlpha = visual == FieldVisual.Disabled ? 0.48d : 0.98d;
        double borderR = DirectorGuiTheme.BorderR;
        double borderG = DirectorGuiTheme.BorderG;
        double borderB = DirectorGuiTheme.BorderB;
        if (visual is FieldVisual.Focus or FieldVisual.Open)
        {
            borderR = DirectorGuiTheme.AccentR;
            borderG = DirectorGuiTheme.AccentG;
            borderB = DirectorGuiTheme.AccentB;
        }
        else if (visual == FieldVisual.Hover)
        {
            borderR += 0.12d;
            borderG += 0.14d;
            borderB += 0.14d;
        }

        DirectorGuiTheme.RoundedRectangle(context, 0.5d, 0.5d, width - 1d, height - 1d,
            DirectorGuiTheme.ScaledCornerRadius);
        context.SetSourceRGBA(
            DirectorGuiTheme.SurfaceR,
            DirectorGuiTheme.SurfaceG,
            DirectorGuiTheme.SurfaceB,
            backgroundAlpha);
        context.FillPreserve();
        context.SetSourceRGBA(borderR, borderG, borderB, borderAlpha);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.Stroke();

        double arrowWidth = Math.Min(width * 0.32d, GuiElement.scaled(27d));
        double separatorX = width - arrowWidth;
        context.SetSourceRGBA(borderR, borderG, borderB, visual == FieldVisual.Disabled ? 0.16d : 0.48d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.MoveTo(separatorX, GuiElement.scaled(5d));
        context.LineTo(separatorX, height - GuiElement.scaled(5d));
        context.Stroke();

        double cx = separatorX + arrowWidth / 2d;
        double cy = height / 2d;
        double half = Math.Min(GuiElement.scaled(4.5d), arrowWidth * 0.2d);
        double direction = visual == FieldVisual.Open ? -1d : 1d;
        context.SetSourceRGBA(
            DirectorGuiTheme.TextR,
            DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB,
            visual == FieldVisual.Disabled ? 0.3d : 0.88d);
        context.LineWidth = Math.Max(1.4d, GuiElement.scaled(1.5d));
        context.LineCap = LineCap.Round;
        context.LineJoin = LineJoin.Round;
        context.MoveTo(cx - half, cy - direction * half / 2d);
        context.LineTo(cx, cy + direction * half / 2d);
        context.LineTo(cx + half, cy - direction * half / 2d);
        context.Stroke();

        generateTexture(surface, ref target, linearMag: true);
    }

    private void ComposeValueTexture()
    {
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        CairoFont font = Font.Clone();
        font.Color = new[]
        {
            DirectorGuiTheme.TextR,
            DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB,
            Enabled ? 0.94d : 0.34d
        };
        font.SetupContext(context);

        IEnumerable<string> selectedNames = listMenu.SelectedIndices
            .Where(index => index >= 0 && index < listMenu.Names.Length)
            .Select(index => listMenu.Names[index]);
        string display = string.Join(", ", selectedNames);
        double left = GuiElement.scaled(9d);
        double arrowWidth = Math.Min(width * 0.32d, GuiElement.scaled(27d));
        double availableWidth = Math.Max(1d, width - arrowWidth - left - GuiElement.scaled(6d));
        display = DirectorGuiTheme.Ellipsize(context, font, display, availableWidth);
        TextExtents extents = font.GetTextExtents(display);
        double y = (height - extents.Height) / 2d - extents.YBearing;
        context.SetSourceRGBA(font.Color);
        context.MoveTo(left, y);
        context.ShowText(display);
        generateTexture(surface, ref valueTexture, linearMag: true);
        valueStamp = SelectionStamp();
    }

    private int SelectionStamp()
    {
        HashCode hash = new();
        hash.Add(Enabled);
        hash.Add(Bounds.OuterWidthInt);
        hash.Add(Bounds.OuterHeightInt);
        foreach (int index in listMenu.SelectedIndices)
        {
            hash.Add(index);
        }
        foreach (string name in listMenu.Names)
        {
            hash.Add(name, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    private enum FieldVisual
    {
        Normal,
        Hover,
        Focus,
        Open,
        Disabled
    }
}

/// <summary>
/// Custom option popup used by GuiElementDirectorDropDown. It renders only the
/// visible rows, keeping large camera/entity lists responsive.
/// </summary>
internal sealed class GuiElementDirectorListMenu : GuiElementListMenu
{
    private LoadedTexture menuTexture;
    private LoadedTexture rowHoverTexture;
    private LoadedTexture scrollbarThumbTexture;
    private int textureStamp = int.MinValue;
    private int rowHeightPx;
    private int searchHeightPx;
    private int listViewportHeightPx;
    private int viewportHeightPx;
    private int menuWidthPx;
    private int scrollbarWidthPx;
    private int[] filteredIndices = Array.Empty<int>();
    private string searchText = string.Empty;
    private int searchCaretIndex;
    private bool searchAllSelected;
    private ElementBounds? optionBounds;
    private bool scrollbarDragging;
    private double scrollbarGrabOffset;
    private bool wasExpanded;
    private bool consumeNextMouseUp;

    public GuiElementDirectorListMenu(
        ICoreClientAPI capi,
        string[] values,
        string[] names,
        int selectedIndex,
        SelectionChangedDelegate onSelectionChanged,
        ElementBounds bounds,
        CairoFont font,
        bool multiSelect)
        : base(capi, values, names, selectedIndex, onSelectionChanged, bounds, font, multiSelect)
    {
        menuTexture = new LoadedTexture(capi);
        rowHoverTexture = new LoadedTexture(capi);
        scrollbarThumbTexture = new LoadedTexture(capi);
        unscaledLineHeight = 28d;
        MaxHeight = 308;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        _ = ctx;
        _ = surface;
        ComposeDirectorElements();
    }

    public void OpenDirector()
    {
        ResetSearch();
        expanded = true;
        wasExpanded = false;
        scrollbarDragging = false;
        consumeNextMouseUp = false;
        ComposeDirectorElements();
    }

    internal void SetDirectorList(string[] values, string[] names, int selectedIndex)
    {
        if (values.Length != names.Length)
        {
            throw new ArgumentException("Values and names must have the same length.");
        }

        Values = values;
        Names = names;
        SelectedIndex = values.Length == 0 ? -1 : Math.Clamp(selectedIndex, 0, values.Length - 1);
        HoveredIndex = SelectedIndex;
        ResetSearch();
        textureStamp = int.MinValue;
    }

    public void ComposeDirectorElements()
    {
        Bounds.CalcWorldBounds();
        double scale = Math.Max(0.1d, Scale * RuntimeEnv.GUIScale);
        rowHeightPx = Math.Max(22, (int)Math.Round(unscaledLineHeight * scale));
        searchHeightPx = Math.Max(rowHeightPx, (int)Math.Round(32d * scale));
        scrollbarWidthPx = Math.Max(9, (int)Math.Round(10d * scale));
        RebuildFilter();
        expandedBoxHeight = Math.Max(rowHeightPx, filteredIndices.Length * rowHeightPx);
        int maximumPopupHeight = Math.Max(
            searchHeightPx + rowHeightPx,
            (int)Math.Round(MaxHeight * scale));
        listViewportHeightPx = Math.Max(
            rowHeightPx,
            Math.Min((int)Math.Round(expandedBoxHeight), maximumPopupHeight - searchHeightPx));
        viewportHeightPx = searchHeightPx + listViewportHeightPx;

        menuWidthPx = CalculateMenuWidth(scale);
        expandedBoxWidth = menuWidthPx;
        ClampScroll();
        UpdateVisibleBounds();
        ComposeVisibleMenuTexture();
        ComposeOverlayTextures();
        textureStamp = CurrentTextureStamp();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        _ = deltaTime;
        if (!expanded)
        {
            wasExpanded = false;
            return;
        }

        if (!wasExpanded)
        {
            ScrollSelectionIntoView();
            wasExpanded = true;
        }
        EnsureComposed();

        double x = visibleBounds.renderX;
        double y = visibleBounds.renderY;
        api.Render.Render2DTexturePremultipliedAlpha(
            menuTexture.TextureId,
            x,
            y,
            menuWidthPx,
            viewportHeightPx,
            310f);

        int hoveredRow = FilteredRowOf(HoveredIndex);
        if (hoveredRow >= 0 && optionBounds is not null)
        {
            double hoverY = optionBounds.renderY + hoveredRow * rowHeightPx - scrollOffY;
            api.Render.PushScissor(optionBounds);
            api.Render.Render2DTexturePremultipliedAlpha(
                rowHoverTexture.TextureId,
                x + 1d,
                hoverY + 1d,
                Math.Max(1, menuWidthPx - (HasScrollbar ? scrollbarWidthPx : 0) - 2),
                Math.Max(1, rowHeightPx - 2),
                312f);
            api.Render.PopScissor();
        }

        if (HasScrollbar)
        {
            (double thumbY, double thumbHeight) = ScrollbarThumbMetrics();
            api.Render.Render2DTexturePremultipliedAlpha(
                scrollbarThumbTexture.TextureId,
                x + menuWidthPx - scrollbarWidthPx + GuiElement.scaled(2d),
                y + searchHeightPx + thumbY,
                Math.Max(3d, scrollbarWidthPx - GuiElement.scaled(4d)),
                thumbHeight,
                314f);
        }
    }

    public override bool IsPositionInside(int posX, int posY)
        => expanded && visibleBounds is not null && visibleBounds.PointInside(posX, posY);

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (!expanded)
        {
            return;
        }
        EnsureComposed();

        if (scrollbarDragging)
        {
            MoveScrollbar(args.Y);
            args.Handled = true;
            return;
        }

        if (!visibleBounds.PointInside(args.X, args.Y))
        {
            return;
        }

        if (IsOverSearchRow(args.X, args.Y))
        {
            HoveredIndex = -1;
            args.Handled = true;
            return;
        }

        if (HasScrollbar && IsOverScrollbar(args.X, args.Y))
        {
            args.Handled = true;
            return;
        }

        if (optionBounds is null || !optionBounds.PointInside(args.X, args.Y))
        {
            return;
        }

        int row = (int)Math.Floor((args.Y - optionBounds.absY + scrollOffY) / rowHeightPx);
        if (row >= 0 && row < filteredIndices.Length)
        {
            HoveredIndex = filteredIndices[row];
            args.Handled = true;
        }
    }

    public override void OnMouseDown(ICoreClientAPI api, MouseEvent args)
    {
        if (!expanded)
        {
            return;
        }
        EnsureComposed();
        consumeNextMouseUp = true;

        if (args.Button != EnumMouseButton.Left)
        {
            if (!visibleBounds.PointInside(args.X, args.Y))
            {
                CloseMenu();
            }
            args.Handled = true;
            return;
        }

        if (!visibleBounds.PointInside(args.X, args.Y))
        {
            CloseMenu();
            if (Bounds.PointInside(args.X, args.Y))
            {
                api.Gui.PlaySound("menubutton");
            }
            args.Handled = true;
            return;
        }

        if (IsOverSearchRow(args.X, args.Y))
        {
            if (IsOverSearchClear(args.X, args.Y))
            {
                SetSearchText(string.Empty, 0);
            }
            else
            {
                searchCaretIndex = searchText.Length;
                searchAllSelected = false;
                textureStamp = int.MinValue;
            }
            args.Handled = true;
            return;
        }

        if (HasScrollbar && IsOverScrollbar(args.X, args.Y))
        {
            BeginScrollbarDrag(args.Y);
            args.Handled = true;
            return;
        }

        if (optionBounds is null || !optionBounds.PointInside(args.X, args.Y))
        {
            args.Handled = true;
            return;
        }

        int row = (int)Math.Floor((args.Y - optionBounds.absY + scrollOffY) / rowHeightPx);
        if (row < 0 || row >= filteredIndices.Length)
        {
            args.Handled = true;
            return;
        }

        int index = filteredIndices[row];
        HoveredIndex = index;
        if (multiSelect)
        {
            ToggleSelection(index);
        }
        else
        {
            SelectedIndex = index;
            onSelectionChanged?.Invoke(Values[index], true);
            CloseMenu();
        }

        textureStamp = int.MinValue;
        api.Gui.PlaySound("toggleswitch");
        args.Handled = true;
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        _ = api;
        if (consumeNextMouseUp)
        {
            consumeNextMouseUp = false;
            scrollbarDragging = false;
            args.Handled = true;
            return;
        }
        if (scrollbarDragging && args.Button == EnumMouseButton.Left)
        {
            scrollbarDragging = false;
            args.Handled = true;
        }
    }

    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (!expanded
            || !visibleBounds.PointInside(api.Input.MouseX, api.Input.MouseY))
        {
            return;
        }

        if (optionBounds is not null
            && optionBounds.PointInside(api.Input.MouseX, api.Input.MouseY)
            && HasScrollbar)
        {
            int direction = args.delta <= 0 ? 1 : -1;
            SetScroll(scrollOffY + direction * rowHeightPx);
        }
        args.SetHandled();
    }

    public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
    {
        if (!hasFocus)
        {
            return;
        }

        if (!expanded)
        {
            if (args.KeyCode is (int)GlKeys.Up or (int)GlKeys.Down)
            {
                OpenDirector();
                args.Handled = true;
            }
            return;
        }

        if (args.KeyCode == (int)GlKeys.Escape)
        {
            if (searchText.Length > 0)
            {
                SetSearchText(string.Empty, 0);
            }
            else
            {
                CloseMenu();
            }
            args.Handled = true;
            return;
        }

        if (args.CtrlPressed || args.CommandPressed)
        {
            switch ((GlKeys)args.KeyCode)
            {
                case GlKeys.A:
                    searchAllSelected = searchText.Length > 0;
                    searchCaretIndex = searchText.Length;
                    textureStamp = int.MinValue;
                    args.Handled = true;
                    return;
                case GlKeys.C:
                    if (searchText.Length > 0)
                    {
                        api.Forms.SetClipboardText(searchText);
                    }
                    args.Handled = true;
                    return;
                case GlKeys.X:
                    if (searchText.Length > 0)
                    {
                        api.Forms.SetClipboardText(searchText);
                        SetSearchText(string.Empty, 0);
                    }
                    args.Handled = true;
                    return;
                case GlKeys.V:
                    string clipboard = api.Forms.GetClipboardText() ?? string.Empty;
                    InsertSearchText(clipboard.Replace("\r", string.Empty).Replace("\n", string.Empty));
                    args.Handled = true;
                    return;
            }
        }

        switch ((GlKeys)args.KeyCode)
        {
            case GlKeys.Back:
                DeleteSearchBeforeCaret();
                args.Handled = true;
                return;
            case GlKeys.Delete:
                DeleteSearchAtCaret();
                args.Handled = true;
                return;
            case GlKeys.Left:
                searchAllSelected = false;
                searchCaretIndex = Math.Max(0, searchCaretIndex - 1);
                textureStamp = int.MinValue;
                args.Handled = true;
                return;
            case GlKeys.Right:
                searchAllSelected = false;
                searchCaretIndex = Math.Min(searchText.Length, searchCaretIndex + 1);
                textureStamp = int.MinValue;
                args.Handled = true;
                return;
            case GlKeys.Home:
                searchAllSelected = false;
                searchCaretIndex = 0;
                textureStamp = int.MinValue;
                args.Handled = true;
                return;
            case GlKeys.End:
                searchAllSelected = false;
                searchCaretIndex = searchText.Length;
                textureStamp = int.MinValue;
                args.Handled = true;
                return;
            case GlKeys.Up:
                MoveHovered(-1);
                args.Handled = true;
                return;
            case GlKeys.Down:
                MoveHovered(1);
                args.Handled = true;
                return;
            case GlKeys.PageUp:
                MoveHovered(-Math.Max(1, listViewportHeightPx / rowHeightPx));
                args.Handled = true;
                return;
            case GlKeys.PageDown:
                MoveHovered(Math.Max(1, listViewportHeightPx / rowHeightPx));
                args.Handled = true;
                return;
            case GlKeys.Enter:
            case GlKeys.KeypadEnter:
                SelectHovered();
                args.Handled = true;
                return;
        }

        if (args.KeyCode != (int)GlKeys.Tab)
        {
            args.Handled = true;
        }
    }

    public override void OnKeyPress(ICoreClientAPI api, KeyEvent args)
    {
        _ = api;
        if (!hasFocus
            || !expanded
            || args.CtrlPressed
            || args.CommandPressed
            || args.AltPressed
            || args.KeyChar == '\0'
            || char.IsControl(args.KeyChar))
        {
            return;
        }

        InsertSearchText(args.KeyChar.ToString());
        args.Handled = true;
    }

    public override void OnFocusLost()
    {
        base.OnFocusLost();
        CloseMenu();
    }

    public override void Dispose()
    {
        menuTexture.Dispose();
        rowHoverTexture.Dispose();
        scrollbarThumbTexture.Dispose();
        base.Dispose();
    }

    private bool HasScrollbar => expandedBoxHeight > listViewportHeightPx + 0.5d;

    private double MaxScroll => Math.Max(0d, expandedBoxHeight - listViewportHeightPx);

    private void EnsureComposed()
    {
        int stamp = CurrentTextureStamp();
        if (stamp != textureStamp)
        {
            ComposeDirectorElements();
        }
    }

    private int CalculateMenuWidth(double scale)
    {
        int minimum = Math.Max(1, Bounds.OuterWidthInt);
        double widest = minimum;
        using ImageSurface measureSurface = new(Format.Argb32, 1, 1);
        using Context measureContext = new(measureSurface);
        CairoFont font = Font.Clone();
        font.SetupContext(measureContext);
        foreach (string name in Names)
        {
            widest = Math.Max(widest, font.GetTextExtents(name ?? string.Empty).Width + 42d * scale);
        }
        widest = Math.Max(
            widest,
            font.GetTextExtents(SVCLang.Get("dropdown-search-placeholder")).Width + 68d * scale);

        int available = Math.Max(minimum,
            api.Render.FrameWidth - (int)Math.Max(0d, Bounds.absX) - (int)Math.Round(8d * scale));
        int preferredMaximum = Math.Max(minimum, (int)Math.Round(480d * scale));
        return Math.Max(minimum, Math.Min((int)Math.Ceiling(widest), Math.Min(available, preferredMaximum)));
    }

    private void UpdateVisibleBounds()
    {
        double below = api.Render.FrameHeight - (Bounds.absY + Bounds.OuterHeight);
        double above = Bounds.absY;
        double popupOffsetPx = below < viewportHeightPx + GuiElement.scaled(4d) && above > below
            ? -viewportHeightPx
            : Bounds.OuterHeight;

        visibleBounds = Bounds.FlatCopy();
        visibleBounds.fixedY += popupOffsetPx / RuntimeEnv.GUIScale;
        visibleBounds.fixedWidth = menuWidthPx / (double)RuntimeEnv.GUIScale;
        visibleBounds.fixedHeight = viewportHeightPx / (double)RuntimeEnv.GUIScale;
        visibleBounds.CalcWorldBounds();

        optionBounds = visibleBounds.FlatCopy();
        optionBounds.fixedY += searchHeightPx / (double)RuntimeEnv.GUIScale;
        optionBounds.fixedHeight = listViewportHeightPx / (double)RuntimeEnv.GUIScale;
        optionBounds.CalcWorldBounds();
    }

    private void ComposeVisibleMenuTexture()
    {
        int width = Math.Max(1, menuWidthPx);
        int height = Math.Max(1, viewportHeightPx);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);

        DirectorGuiTheme.RoundedRectangle(context, 0.5d, 0.5d, width - 1d, height - 1d,
            DirectorGuiTheme.ScaledCornerRadius);
        context.SetSourceRGBA(
            DirectorGuiTheme.SurfaceR,
            DirectorGuiTheme.SurfaceG,
            DirectorGuiTheme.SurfaceB,
            0.995d);
        context.Fill();
        context.Save();
        DirectorGuiTheme.RoundedRectangle(context, 1d, 1d, width - 2d, height - 2d,
            GuiElement.scaled(3d));
        context.Clip();

        DrawSearchRow(context, width);

        context.SetSourceRGBA(
            DirectorGuiTheme.BorderR,
            DirectorGuiTheme.BorderG,
            DirectorGuiTheme.BorderB,
            0.52d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.MoveTo(GuiElement.scaled(5d), searchHeightPx - 0.5d);
        context.LineTo(width - GuiElement.scaled(5d), searchHeightPx - 0.5d);
        context.Stroke();

        context.Save();
        context.Rectangle(0d, searchHeightPx, width, listViewportHeightPx);
        context.Clip();

        CairoFont rowFont = Font.Clone();
        rowFont.Color = new[]
        {
            DirectorGuiTheme.TextR,
            DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB,
            0.96d
        };
        rowFont.SetupContext(context);
        FontExtents fontExtents = rowFont.GetFontExtents();
        double textBaselineOffset = (rowHeightPx - fontExtents.Height) / 2d + fontExtents.Ascent;
        int first = Math.Max(0, (int)Math.Floor(scrollOffY / rowHeightPx));
        int last = Math.Min(filteredIndices.Length - 1,
            (int)Math.Ceiling((scrollOffY + listViewportHeightPx) / rowHeightPx));
        double leftPadding = GuiElement.scaled(multiSelect ? 31d : 27d);
        double rightPadding = HasScrollbar ? scrollbarWidthPx + GuiElement.scaled(7d) : GuiElement.scaled(8d);

        HashSet<int> selected = SelectedIndices.ToHashSet();
        for (int row = first; row <= last; row++)
        {
            int index = filteredIndices[row];
            double rowY = searchHeightPx + row * rowHeightPx - scrollOffY;
            if (selected.Contains(index))
            {
                context.Rectangle(1d, rowY + 1d, width - 2d, rowHeightPx - 1d);
                context.SetSourceRGBA(
                    DirectorGuiTheme.AccentR,
                    DirectorGuiTheme.AccentG,
                    DirectorGuiTheme.AccentB,
                    0.18d);
                context.Fill();
                context.Rectangle(1d, rowY + GuiElement.scaled(4d), GuiElement.scaled(3d),
                    rowHeightPx - GuiElement.scaled(8d));
                context.SetSourceRGBA(
                    DirectorGuiTheme.AccentR,
                    DirectorGuiTheme.AccentG,
                    DirectorGuiTheme.AccentB,
                    0.95d);
                context.Fill();
            }

            context.SetSourceRGBA(
                DirectorGuiTheme.BorderR,
                DirectorGuiTheme.BorderG,
                DirectorGuiTheme.BorderB,
                0.28d);
            context.LineWidth = 1d;
            context.MoveTo(GuiElement.scaled(7d), rowY + rowHeightPx - 0.5d);
            context.LineTo(width - rightPadding, rowY + rowHeightPx - 0.5d);
            context.Stroke();

            DrawSelectionMarker(context, index, rowY, selected.Contains(index));
            double textWidth = Math.Max(1d, width - leftPadding - rightPadding);
            string display = DirectorGuiTheme.Ellipsize(context, rowFont, Names[index], textWidth);
            context.SetSourceRGBA(rowFont.Color);
            context.MoveTo(leftPadding, rowY + textBaselineOffset);
            context.ShowText(display);
        }

        if (filteredIndices.Length == 0)
        {
            string noMatches = SVCLang.Get("dropdown-no-matches");
            context.SetSourceRGBA(
                DirectorGuiTheme.TextR,
                DirectorGuiTheme.TextG,
                DirectorGuiTheme.TextB,
                0.48d);
            TextExtents extents = rowFont.GetTextExtents(noMatches);
            double baseline = searchHeightPx
                + (listViewportHeightPx - extents.Height) / 2d
                - extents.YBearing;
            context.MoveTo(Math.Max(GuiElement.scaled(8d), (width - extents.Width) / 2d), baseline);
            context.ShowText(noMatches);
        }

        if (HasScrollbar)
        {
            double trackX = width - scrollbarWidthPx;
            context.Rectangle(
                trackX,
                searchHeightPx + GuiElement.scaled(3d),
                scrollbarWidthPx - GuiElement.scaled(1d),
                listViewportHeightPx - GuiElement.scaled(6d));
            context.SetSourceRGBA(
                DirectorGuiTheme.RaisedR,
                DirectorGuiTheme.RaisedG,
                DirectorGuiTheme.RaisedB,
                0.86d);
            context.Fill();
        }

        context.Restore();
        context.Restore();
        DirectorGuiTheme.RoundedRectangle(context, 0.5d, 0.5d, width - 1d, height - 1d,
            DirectorGuiTheme.ScaledCornerRadius);
        context.SetSourceRGBA(
            DirectorGuiTheme.AccentR,
            DirectorGuiTheme.AccentG,
            DirectorGuiTheme.AccentB,
            0.72d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.Stroke();

        generateTexture(surface, ref menuTexture, linearMag: true);
    }

    private void ComposeOverlayTextures()
    {
        int hoverWidth = Math.Max(1, menuWidthPx - (HasScrollbar ? scrollbarWidthPx : 0));
        using (ImageSurface surface = new(Format.Argb32, hoverWidth, Math.Max(1, rowHeightPx)))
        using (Context context = new(surface))
        {
            DirectorGuiTheme.RoundedRectangle(context, 0d, 0d, hoverWidth, rowHeightPx,
                GuiElement.scaled(2d));
            context.SetSourceRGBA(
                DirectorGuiTheme.AccentR,
                DirectorGuiTheme.AccentG,
                DirectorGuiTheme.AccentB,
                0.16d);
            context.Fill();
            generateTexture(surface, ref rowHoverTexture, linearMag: true);
        }

        int thumbWidth = Math.Max(3, scrollbarWidthPx - (int)GuiElement.scaled(4d));
        using ImageSurface thumbSurface = new(Format.Argb32, thumbWidth, Math.Max(8, rowHeightPx));
        using Context thumbContext = new(thumbSurface);
        DirectorGuiTheme.RoundedRectangle(thumbContext, 0d, 0d, thumbWidth, rowHeightPx,
            Math.Max(1d, thumbWidth / 2d));
        thumbContext.SetSourceRGBA(
            DirectorGuiTheme.AccentR,
            DirectorGuiTheme.AccentG,
            DirectorGuiTheme.AccentB,
            0.82d);
        thumbContext.Fill();
        generateTexture(thumbSurface, ref scrollbarThumbTexture, linearMag: true);
    }

    private void DrawSelectionMarker(Context context, int index, double rowY, bool selected)
    {
        _ = index;
        double cx = GuiElement.scaled(14d);
        double cy = rowY + rowHeightPx / 2d;
        double radius = GuiElement.scaled(multiSelect ? 6d : 5d);
        if (multiSelect)
        {
            DirectorGuiTheme.RoundedRectangle(context, cx - radius, cy - radius, radius * 2d, radius * 2d,
                GuiElement.scaled(2d));
        }
        else
        {
            context.NewPath();
            context.Arc(cx, cy, radius, 0d, Math.PI * 2d);
            context.ClosePath();
        }
        context.SetSourceRGBA(
            selected ? DirectorGuiTheme.AccentR : DirectorGuiTheme.BorderR,
            selected ? DirectorGuiTheme.AccentG : DirectorGuiTheme.BorderG,
            selected ? DirectorGuiTheme.AccentB : DirectorGuiTheme.BorderB,
            selected ? 0.92d : 0.72d);
        if (selected)
        {
            context.Fill();
        }
        else
        {
            context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
            context.Stroke();
        }

        if (!selected || !multiSelect)
        {
            return;
        }
        context.SetSourceRGBA(
            DirectorGuiTheme.SurfaceR,
            DirectorGuiTheme.SurfaceG,
            DirectorGuiTheme.SurfaceB,
            0.95d);
        context.LineWidth = Math.Max(1.2d, GuiElement.scaled(1.4d));
        context.LineCap = LineCap.Round;
        context.MoveTo(cx - radius * 0.55d, cy);
        context.LineTo(cx - radius * 0.12d, cy + radius * 0.42d);
        context.LineTo(cx + radius * 0.62d, cy - radius * 0.48d);
        context.Stroke();
    }

    private void DrawSearchRow(Context context, int width)
    {
        double inset = GuiElement.scaled(4d);
        double fieldX = inset;
        double fieldY = inset;
        double fieldWidth = Math.Max(1d, width - inset * 2d);
        double fieldHeight = Math.Max(1d, searchHeightPx - inset * 2d);

        DirectorGuiTheme.RoundedRectangle(
            context,
            fieldX,
            fieldY,
            fieldWidth,
            fieldHeight,
            GuiElement.scaled(3d));
        context.SetSourceRGBA(
            DirectorGuiTheme.RaisedR,
            DirectorGuiTheme.RaisedG,
            DirectorGuiTheme.RaisedB,
            0.98d);
        context.FillPreserve();
        context.SetSourceRGBA(
            DirectorGuiTheme.AccentR,
            DirectorGuiTheme.AccentG,
            DirectorGuiTheme.AccentB,
            0.78d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.Stroke();

        double iconX = fieldX + GuiElement.scaled(12d);
        double iconY = fieldY + fieldHeight / 2d - GuiElement.scaled(1d);
        double iconRadius = GuiElement.scaled(4d);
        context.SetSourceRGBA(
            DirectorGuiTheme.TextR,
            DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB,
            0.62d);
        context.LineWidth = Math.Max(1.2d, GuiElement.scaled(1.25d));
        context.NewPath();
        context.Arc(iconX, iconY, iconRadius, 0d, Math.PI * 2d);
        context.Stroke();
        context.MoveTo(iconX + iconRadius * 0.72d, iconY + iconRadius * 0.72d);
        context.LineTo(iconX + iconRadius * 1.65d, iconY + iconRadius * 1.65d);
        context.Stroke();

        double clearCenterX = fieldX + fieldWidth - GuiElement.scaled(12d);
        double textLeft = fieldX + GuiElement.scaled(25d);
        double textRight = searchText.Length > 0
            ? clearCenterX - GuiElement.scaled(11d)
            : fieldX + fieldWidth - GuiElement.scaled(8d);
        double availableTextWidth = Math.Max(1d, textRight - textLeft);

        CairoFont searchFont = Font.Clone();
        searchFont.Color = new[]
        {
            DirectorGuiTheme.TextR,
            DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB,
            searchText.Length == 0 ? 0.46d : 0.96d
        };
        searchFont.SetupContext(context);
        FontExtents fontExtents = searchFont.GetFontExtents();
        double baseline = fieldY + (fieldHeight - fontExtents.Height) / 2d + fontExtents.Ascent;

        int visibleStart = 0;
        string display = searchText.Length == 0
            ? DirectorGuiTheme.Ellipsize(
                context,
                searchFont,
                SVCLang.Get("dropdown-search-placeholder"),
                availableTextWidth)
            : SearchDisplayText(context, searchFont, availableTextWidth, out visibleStart);
        TextExtents displayExtents = searchFont.GetTextExtents(display);
        if (searchAllSelected && searchText.Length > 0)
        {
            context.Rectangle(
                textLeft - GuiElement.scaled(2d),
                fieldY + GuiElement.scaled(3d),
                Math.Min(availableTextWidth + GuiElement.scaled(4d), displayExtents.XAdvance + GuiElement.scaled(4d)),
                fieldHeight - GuiElement.scaled(6d));
            context.SetSourceRGBA(
                DirectorGuiTheme.AccentR,
                DirectorGuiTheme.AccentG,
                DirectorGuiTheme.AccentB,
                0.34d);
            context.Fill();
        }

        context.SetSourceRGBA(searchFont.Color);
        context.MoveTo(textLeft, baseline);
        context.ShowText(display);

        if (!searchAllSelected)
        {
            double caretOffset;
            if (searchText.Length == 0)
            {
                caretOffset = 0d;
            }
            else if (searchCaretIndex <= visibleStart)
            {
                caretOffset = searchFont.GetTextExtents("…").XAdvance;
            }
            else
            {
                string prefix = visibleStart > 0 ? "…" : string.Empty;
                string beforeCaret = searchText.Substring(
                    visibleStart,
                    Math.Min(searchCaretIndex, searchText.Length) - visibleStart);
                caretOffset = searchFont.GetTextExtents(prefix + beforeCaret).XAdvance;
            }

            double caretX = Math.Clamp(
                textLeft + caretOffset,
                textLeft,
                textLeft + availableTextWidth);
            context.SetSourceRGBA(
                DirectorGuiTheme.AccentR,
                DirectorGuiTheme.AccentG,
                DirectorGuiTheme.AccentB,
                0.96d);
            context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
            context.MoveTo(caretX, fieldY + GuiElement.scaled(4d));
            context.LineTo(caretX, fieldY + fieldHeight - GuiElement.scaled(4d));
            context.Stroke();
        }

        if (searchText.Length > 0)
        {
            double half = GuiElement.scaled(3.5d);
            double clearCenterY = fieldY + fieldHeight / 2d;
            context.SetSourceRGBA(
                DirectorGuiTheme.TextR,
                DirectorGuiTheme.TextG,
                DirectorGuiTheme.TextB,
                0.7d);
            context.LineWidth = Math.Max(1.2d, GuiElement.scaled(1.25d));
            context.LineCap = LineCap.Round;
            context.MoveTo(clearCenterX - half, clearCenterY - half);
            context.LineTo(clearCenterX + half, clearCenterY + half);
            context.MoveTo(clearCenterX + half, clearCenterY - half);
            context.LineTo(clearCenterX - half, clearCenterY + half);
            context.Stroke();
        }
    }

    private string SearchDisplayText(
        Context context,
        CairoFont font,
        double maximumWidth,
        out int visibleStart)
    {
        visibleStart = 0;
        if (font.GetTextExtents(searchText).Width <= maximumWidth)
        {
            return searchText;
        }

        const string prefix = "…";
        for (int start = 1; start < searchText.Length; start++)
        {
            string candidate = prefix + searchText[start..];
            if (font.GetTextExtents(candidate).Width <= maximumWidth)
            {
                visibleStart = start;
                return candidate;
            }
        }

        visibleStart = searchText.Length;
        return prefix;
    }

    private void RebuildFilter()
    {
        int count = Math.Min(Values.Length, Names.Length);
        string[] terms = searchText.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<int> matches = new(count);
        for (int index = 0; index < count; index++)
        {
            if (terms.Length == 0 || MatchesSearch(Names[index], Values[index], terms))
            {
                matches.Add(index);
            }
        }

        filteredIndices = matches.ToArray();
        if (FilteredRowOf(HoveredIndex) < 0)
        {
            HoveredIndex = filteredIndices.Length == 0 ? -1 : filteredIndices[0];
        }
    }

    private static bool MatchesSearch(string? name, string? value, IEnumerable<string> terms)
    {
        name ??= string.Empty;
        value ??= string.Empty;
        foreach (string term in terms)
        {
            if (!name.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !value.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private void SetSearchText(string value, int caretIndex)
    {
        value ??= string.Empty;
        if (value.Length > 128)
        {
            value = value[..128];
        }

        searchText = value;
        searchCaretIndex = Math.Clamp(caretIndex, 0, searchText.Length);
        searchAllSelected = false;
        RebuildFilter();
        scrollOffY = 0d;
        HoveredIndex = searchText.Length == 0 && filteredIndices.Contains(SelectedIndex)
            ? SelectedIndex
            : filteredIndices.FirstOrDefault(-1);
        textureStamp = int.MinValue;
    }

    private void ResetSearch()
    {
        searchText = string.Empty;
        searchCaretIndex = 0;
        searchAllSelected = false;
        RebuildFilter();
        scrollOffY = 0d;
        HoveredIndex = filteredIndices.Contains(SelectedIndex)
            ? SelectedIndex
            : filteredIndices.FirstOrDefault(-1);
        textureStamp = int.MinValue;
    }

    private void InsertSearchText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        string sanitized = new(value.Where(character => !char.IsControl(character)).ToArray());
        if (sanitized.Length == 0)
        {
            return;
        }

        string current = searchAllSelected ? string.Empty : searchText;
        int caret = searchAllSelected ? 0 : Math.Clamp(searchCaretIndex, 0, current.Length);
        string next = current.Insert(caret, sanitized);
        SetSearchText(next, Math.Min(128, caret + sanitized.Length));
    }

    private void DeleteSearchBeforeCaret()
    {
        if (searchAllSelected)
        {
            SetSearchText(string.Empty, 0);
            return;
        }

        int caret = Math.Clamp(searchCaretIndex, 0, searchText.Length);
        if (caret <= 0)
        {
            return;
        }

        SetSearchText(searchText.Remove(caret - 1, 1), caret - 1);
    }

    private void DeleteSearchAtCaret()
    {
        if (searchAllSelected)
        {
            SetSearchText(string.Empty, 0);
            return;
        }

        int caret = Math.Clamp(searchCaretIndex, 0, searchText.Length);
        if (caret >= searchText.Length)
        {
            return;
        }

        SetSearchText(searchText.Remove(caret, 1), caret);
    }

    private void MoveHovered(int delta)
    {
        if (filteredIndices.Length == 0)
        {
            HoveredIndex = -1;
            return;
        }

        int row = FilteredRowOf(HoveredIndex);
        if (row < 0)
        {
            row = delta >= 0 ? 0 : filteredIndices.Length - 1;
        }
        else
        {
            row = Math.Clamp(row + delta, 0, filteredIndices.Length - 1);
        }

        HoveredIndex = filteredIndices[row];
        ScrollHoveredIntoView();
        textureStamp = int.MinValue;
    }

    private void SelectHovered()
    {
        int index = FilteredRowOf(HoveredIndex) >= 0
            ? HoveredIndex
            : filteredIndices.FirstOrDefault(-1);
        if (index < 0 || index >= Values.Length)
        {
            return;
        }

        if (multiSelect)
        {
            ToggleSelection(index);
        }
        else
        {
            SelectedIndex = index;
            onSelectionChanged?.Invoke(Values[index], true);
            CloseMenu();
        }

        api.Gui.PlaySound("toggleswitch");
        textureStamp = int.MinValue;
    }

    private void ToggleSelection(int index)
    {
        List<int> selected = SelectedIndices.ToList();
        bool wasSelected = selected.Remove(index);
        if (!wasSelected)
        {
            selected.Add(index);
            selected.Sort();
        }
        SelectedIndices = selected.ToArray();
        onSelectionChanged?.Invoke(Values[index], !wasSelected);
        textureStamp = int.MinValue;
    }

    private int FilteredRowOf(int originalIndex)
        => Array.IndexOf(filteredIndices, originalIndex);

    private bool IsOverSearchRow(int mouseX, int mouseY)
        => expanded
            && visibleBounds.PointInside(mouseX, mouseY)
            && (optionBounds is null || mouseY < optionBounds.absY);

    private bool IsOverSearchClear(int mouseX, int mouseY)
    {
        if (searchText.Length == 0 || !IsOverSearchRow(mouseX, mouseY))
        {
            return false;
        }

        double clearWidth = Math.Max(searchHeightPx, GuiElement.scaled(28d));
        return mouseX >= visibleBounds.absX + menuWidthPx - clearWidth;
    }

    private void CloseMenu()
    {
        expanded = false;
        scrollbarDragging = false;
        wasExpanded = false;
        ResetSearch();
    }

    private bool IsOverScrollbar(int mouseX, int mouseY)
        => optionBounds is not null
            && optionBounds.PointInside(mouseX, mouseY)
            && mouseX >= visibleBounds.absX + menuWidthPx - scrollbarWidthPx;

    private void BeginScrollbarDrag(int mouseY)
    {
        (double thumbY, double thumbHeight) = ScrollbarThumbMetrics();
        double localY = mouseY - (optionBounds?.absY ?? visibleBounds.absY);
        if (localY >= thumbY && localY <= thumbY + thumbHeight)
        {
            scrollbarGrabOffset = localY - thumbY;
        }
        else
        {
            scrollbarGrabOffset = thumbHeight / 2d;
            MoveScrollbar(mouseY);
        }
        scrollbarDragging = true;
    }

    private void MoveScrollbar(int mouseY)
    {
        (_, double thumbHeight) = ScrollbarThumbMetrics();
        double trackTravel = Math.Max(1d, listViewportHeightPx - thumbHeight);
        double desiredY = mouseY - (optionBounds?.absY ?? visibleBounds.absY) - scrollbarGrabOffset;
        SetScroll(Math.Clamp(desiredY / trackTravel, 0d, 1d) * MaxScroll);
    }

    private (double Y, double Height) ScrollbarThumbMetrics()
    {
        double height = Math.Max(GuiElement.scaled(22d),
            listViewportHeightPx * listViewportHeightPx / Math.Max(listViewportHeightPx, expandedBoxHeight));
        height = Math.Min(listViewportHeightPx, height);
        double travel = Math.Max(0d, listViewportHeightPx - height);
        double y = MaxScroll <= 0d ? 0d : scrollOffY / MaxScroll * travel;
        return (y, height);
    }

    private void ScrollSelectionIntoView()
    {
        if (filteredIndices.Length == 0)
        {
            HoveredIndex = -1;
            SetScroll(0d);
            return;
        }

        HoveredIndex = filteredIndices.Contains(SelectedIndex)
            ? SelectedIndex
            : filteredIndices[0];
        ScrollHoveredIntoView();
    }

    private void ScrollHoveredIntoView()
    {
        int row = FilteredRowOf(HoveredIndex);
        if (row < 0)
        {
            return;
        }

        double top = row * rowHeightPx;
        double bottom = top + rowHeightPx;
        if (top < scrollOffY)
        {
            SetScroll(top);
        }
        else if (bottom > scrollOffY + listViewportHeightPx)
        {
            SetScroll(bottom - listViewportHeightPx);
        }
    }

    private void SetScroll(double value)
    {
        double clamped = Math.Clamp(value, 0d, MaxScroll);
        if (Math.Abs(clamped - scrollOffY) < 0.5d)
        {
            return;
        }
        scrollOffY = clamped;
        textureStamp = int.MinValue;
    }

    private void ClampScroll()
        => scrollOffY = Math.Clamp(scrollOffY, 0d, MaxScroll);

    private int CurrentTextureStamp()
    {
        HashCode hash = new();
        hash.Add(Bounds.OuterWidthInt);
        hash.Add(Bounds.OuterHeightInt);
        hash.Add(api.Render.FrameWidth);
        hash.Add(api.Render.FrameHeight);
        hash.Add(Scale);
        hash.Add((int)Math.Round(scrollOffY));
        hash.Add(MaxHeight);
        hash.Add(multiSelect);
        hash.Add(searchText, StringComparer.Ordinal);
        hash.Add(searchCaretIndex);
        hash.Add(searchAllSelected);
        foreach (string value in Values)
        {
            hash.Add(value, StringComparer.Ordinal);
        }
        foreach (string name in Names)
        {
            hash.Add(name, StringComparer.Ordinal);
        }
        foreach (int index in SelectedIndices)
        {
            hash.Add(index);
        }
        return hash.ToHashCode();
    }
}

/// <summary>
/// Number input with a flat editor field and icon-only +/- steppers. Numeric
/// parsing, interval modifiers, keyboard editing, and wheel behavior remain in
/// GuiElementNumberInput.
/// </summary>
internal sealed class GuiElementDirectorNumberInput : GuiElementNumberInput
{
    private readonly Action? onFocusLost;
    private LoadedTexture buttonPressedTexture;
    private LoadedTexture disabledOverlayTexture;
    private int pressedStepper;

    public GuiElementDirectorNumberInput(
        ICoreClientAPI capi,
        ElementBounds bounds,
        Action<string>? onTextChanged,
        CairoFont font,
        Action? onFocusLost = null)
        : base(capi, bounds, onTextChanged, font)
    {
        this.onFocusLost = onFocusLost;
        buttonPressedTexture = new LoadedTexture(capi);
        disabledOverlayTexture = new LoadedTexture(capi);
    }

    public override void ComposeTextElements(Context ctx, ImageSurface surface)
    {
        // Let the game maintain its internal cursor/text textures on a scratch
        // surface. No vanilla static pixels are allowed onto the real dialog.
        using (ImageSurface scratchSurface = new(
            Format.Argb32,
            Math.Max(1, surface.Width),
            Math.Max(1, surface.Height)))
        using (Context scratchContext = new(scratchSurface))
        {
            bool requestedEnabled = enabled;
            try
            {
                enabled = true;
                base.ComposeTextElements(scratchContext, scratchSurface);
            }
            finally
            {
                enabled = requestedEnabled;
            }
        }
        Bounds.CalcWorldBounds();
        double x = Bounds.drawX;
        double y = Bounds.drawY;
        double width = Bounds.OuterWidth;
        double height = Bounds.OuterHeight;
        double stepWidth = GuiElement.scaled(17d) * Scale;
        double stepX = x + width - stepWidth;
        double halfHeight = height / 2d;

        DirectorGuiTheme.RoundedRectangle(ctx, x + 0.5d, y + 0.5d, width - 1d, height - 1d,
            DirectorGuiTheme.ScaledCornerRadius);
        ctx.SetSourceRGBA(
            DirectorGuiTheme.SurfaceR,
            DirectorGuiTheme.SurfaceG,
            DirectorGuiTheme.SurfaceB,
            1d);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(
            DirectorGuiTheme.BorderR,
            DirectorGuiTheme.BorderG,
            DirectorGuiTheme.BorderB,
            0.88d);
        ctx.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        ctx.Stroke();

        ctx.Save();
        DirectorGuiTheme.RoundedRectangle(ctx, x + 0.5d, y + 0.5d, width - 1d, height - 1d,
            DirectorGuiTheme.ScaledCornerRadius);
        ctx.Clip();
        ctx.Rectangle(stepX, y, stepWidth, height);
        ctx.SetSourceRGBA(
            DirectorGuiTheme.RaisedR,
            DirectorGuiTheme.RaisedG,
            DirectorGuiTheme.RaisedB,
            1d);
        ctx.Fill();
        ctx.Restore();

        ctx.SetSourceRGBA(
            DirectorGuiTheme.BorderR,
            DirectorGuiTheme.BorderG,
            DirectorGuiTheme.BorderB,
            0.66d);
        ctx.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        ctx.MoveTo(stepX, y + GuiElement.scaled(4d));
        ctx.LineTo(stepX, y + height - GuiElement.scaled(4d));
        ctx.MoveTo(stepX + GuiElement.scaled(3d), y + halfHeight);
        ctx.LineTo(x + width - GuiElement.scaled(3d), y + halfHeight);
        ctx.Stroke();

        DrawStepperIcon(ctx, stepX + stepWidth / 2d, y + halfHeight / 2d, plus: true, enabled: true);
        DrawStepperIcon(ctx, stepX + stepWidth / 2d, y + halfHeight + halfHeight / 2d,
            plus: false, enabled: true);

        GenerateFocusTexture();
        GenerateStepperStateTexture(ref buttonHighlightTexture, 0.18d);
        GenerateStepperStateTexture(ref buttonPressedTexture, 0.34d);
        GenerateDisabledOverlayTexture();
        highlightBounds = Bounds.CopyOffsetedSibling().WithFixedPadding(0d, 0d);
        highlightBounds.CalcWorldBounds();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        base.RenderInteractiveElements(deltaTime);
        if (!Enabled)
        {
            api.Render.Render2DTexturePremultipliedAlpha(disabledOverlayTexture.TextureId, Bounds);
            return;
        }
        if (pressedStepper == 0)
        {
            return;
        }

        double halfHeight = Bounds.OuterHeight / 2d - 1d;
        double y = pressedStepper > 0
            ? Bounds.renderY
            : Bounds.renderY + halfHeight + 1d;
        api.Render.Render2DTexturePremultipliedAlpha(
            buttonPressedTexture.TextureId,
            Bounds.renderX + Bounds.OuterWidth - GuiElement.scaled(17d) - 1d,
            y,
            GuiElement.scaled(17d),
            halfHeight);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        pressedStepper = 0;
        if (Enabled && args.Button == EnumMouseButton.Left && IsStepperPoint(args.X, args.Y))
        {
            pressedStepper = args.Y > Bounds.absY + Bounds.OuterHeight / 2d ? -1 : 1;
        }
        base.OnMouseDownOnElement(api, args);
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseUp(api, args);
        if (args.Button == EnumMouseButton.Left)
        {
            pressedStepper = 0;
        }
    }

    public override void OnFocusLost()
    {
        bool wasFocused = HasFocus;
        base.OnFocusLost();
        if (wasFocused)
        {
            onFocusLost?.Invoke();
        }
    }

    public override void Dispose()
    {
        buttonPressedTexture.Dispose();
        disabledOverlayTexture.Dispose();
        base.Dispose();
    }

    private bool IsStepperPoint(int x, int y)
        => x >= Bounds.absX + Bounds.OuterWidth - GuiElement.scaled(17d)
            && x <= Bounds.absX + Bounds.OuterWidth
            && y >= Bounds.absY
            && y <= Bounds.absY + Bounds.OuterHeight;

    private void GenerateFocusTexture()
    {
        using ImageSurface surface = new(
            Format.Argb32,
            Math.Max(1, Bounds.OuterWidthInt),
            Math.Max(1, Bounds.OuterHeightInt));
        using Context context = new(surface);
        DirectorGuiTheme.RoundedRectangle(context, 1d, 1d, surface.Width - 2d, surface.Height - 2d,
            DirectorGuiTheme.ScaledCornerRadius);
        context.SetSourceRGBA(
            DirectorGuiTheme.AccentR,
            DirectorGuiTheme.AccentG,
            DirectorGuiTheme.AccentB,
            0.95d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1.4d));
        context.Stroke();
        generateTexture(surface, ref highlightTexture, linearMag: true);
    }

    private void GenerateStepperStateTexture(ref LoadedTexture target, double alpha)
    {
        double halfHeight = Math.Max(1d, Bounds.OuterHeight / 2d - 1d);
        using ImageSurface surface = new(
            Format.Argb32,
            Math.Max(1, (int)Math.Ceiling(GuiElement.scaled(17d))),
            Math.Max(1, (int)Math.Ceiling(halfHeight)));
        using Context context = new(surface);
        context.SetSourceRGBA(
            DirectorGuiTheme.AccentR,
            DirectorGuiTheme.AccentG,
            DirectorGuiTheme.AccentB,
            alpha);
        context.Paint();
        generateTexture(surface, ref target, linearMag: true);
    }

    private void GenerateDisabledOverlayTexture()
    {
        using ImageSurface surface = new(
            Format.Argb32,
            Math.Max(1, Bounds.OuterWidthInt),
            Math.Max(1, Bounds.OuterHeightInt));
        using Context context = new(surface);
        DirectorGuiTheme.RoundedRectangle(context, 0.5d, 0.5d, surface.Width - 1d, surface.Height - 1d,
            DirectorGuiTheme.ScaledCornerRadius);
        context.SetSourceRGBA(
            DirectorGuiTheme.SurfaceR,
            DirectorGuiTheme.SurfaceG,
            DirectorGuiTheme.SurfaceB,
            0.58d);
        context.Fill();
        generateTexture(surface, ref disabledOverlayTexture, linearMag: true);
    }

    private static void DrawStepperIcon(Context context, double cx, double cy, bool plus, bool enabled)
    {
        double radius = GuiElement.scaled(3.8d);
        context.SetSourceRGBA(
            DirectorGuiTheme.TextR,
            DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB,
            enabled ? 0.88d : 0.27d);
        context.LineWidth = Math.Max(1.2d, GuiElement.scaled(1.35d));
        context.LineCap = LineCap.Round;
        context.MoveTo(cx - radius, cy);
        context.LineTo(cx + radius, cy);
        if (plus)
        {
            context.MoveTo(cx, cy - radius);
            context.LineTo(cx, cy + radius);
        }
        context.Stroke();
    }
}

internal sealed class GuiElementDirectorTextInput : GuiElementTextInput
{
    private readonly Action? onFocusLost;

    public GuiElementDirectorTextInput(
        ICoreClientAPI capi,
        ElementBounds bounds,
        Action<string>? onTextChanged,
        CairoFont font,
        Action? onFocusLost = null)
        : base(capi, bounds, onTextChanged, font)
    {
        this.onFocusLost = onFocusLost;
    }

    public override void OnFocusLost()
    {
        bool wasFocused = HasFocus;
        base.OnFocusLost();
        if (wasFocused)
        {
            onFocusLost?.Invoke();
        }
    }

    public override void ComposeTextElements(Context context, ImageSurface surface)
    {
        using (ImageSurface scratchSurface = new(
            Format.Argb32,
            Math.Max(1, surface.Width),
            Math.Max(1, surface.Height)))
        using (Context scratchContext = new(scratchSurface))
        {
            base.ComposeTextElements(scratchContext, scratchSurface);
        }

        Bounds.CalcWorldBounds();
        DirectorGuiTheme.RoundedRectangle(
            context,
            Bounds.drawX + 0.5d,
            Bounds.drawY + 0.5d,
            Bounds.OuterWidth - 1d,
            Bounds.OuterHeight - 1d,
            DirectorGuiTheme.ScaledCornerRadius);
        context.SetSourceRGBA(
            DirectorGuiTheme.SurfaceR,
            DirectorGuiTheme.SurfaceG,
            DirectorGuiTheme.SurfaceB,
            1d);
        context.FillPreserve();
        context.SetSourceRGBA(
            DirectorGuiTheme.BorderR,
            DirectorGuiTheme.BorderG,
            DirectorGuiTheme.BorderB,
            Enabled ? 0.88d : 0.35d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.Stroke();

        using ImageSurface focusSurface = new(
            Format.Argb32,
            Math.Max(1, Bounds.OuterWidthInt),
            Math.Max(1, Bounds.OuterHeightInt));
        using Context focusContext = new(focusSurface);
        DirectorGuiTheme.RoundedRectangle(focusContext, 1d, 1d,
            focusSurface.Width - 2d, focusSurface.Height - 2d, DirectorGuiTheme.ScaledCornerRadius);
        focusContext.SetSourceRGBA(
            DirectorGuiTheme.AccentR,
            DirectorGuiTheme.AccentG,
            DirectorGuiTheme.AccentB,
            0.95d);
        focusContext.LineWidth = Math.Max(1d, GuiElement.scaled(1.4d));
        focusContext.Stroke();
        generateTexture(focusSurface, ref highlightTexture, linearMag: true);
        highlightBounds = Bounds.CopyOffsetedSibling().WithFixedPadding(0d, 0d);
        highlightBounds.CalcWorldBounds();
    }
}

internal sealed class GuiElementDirectorTextArea : GuiElementTextArea
{
    private readonly Action? onFocusLost;

    public GuiElementDirectorTextArea(
        ICoreClientAPI capi,
        ElementBounds bounds,
        Action<string>? onTextChanged,
        CairoFont font,
        Action? onFocusLost = null)
        : base(capi, bounds, onTextChanged, font.WithLineHeightMultiplier(1.18d))
    {
        this.onFocusLost = onFocusLost;
        Autoheight = false;
    }

    public override void OnFocusLost()
    {
        bool wasFocused = HasFocus;
        base.OnFocusLost();
        if (wasFocused)
        {
            onFocusLost?.Invoke();
        }
    }

    public override void ComposeTextElements(Context context, ImageSurface surface)
    {
        using (ImageSurface scratchSurface = new(
            Format.Argb32,
            Math.Max(1, surface.Width),
            Math.Max(1, surface.Height)))
        using (Context scratchContext = new(scratchSurface))
        {
            base.ComposeTextElements(scratchContext, scratchSurface);
        }

        Bounds.CalcWorldBounds();
        DirectorGuiTheme.RoundedRectangle(
            context,
            Bounds.drawX + 0.5d,
            Bounds.drawY + 0.5d,
            Bounds.OuterWidth - 1d,
            Bounds.OuterHeight - 1d,
            DirectorGuiTheme.ScaledCornerRadius);
        context.SetSourceRGBA(
            DirectorGuiTheme.SurfaceR,
            DirectorGuiTheme.SurfaceG,
            DirectorGuiTheme.SurfaceB,
            1d);
        context.FillPreserve();
        context.SetSourceRGBA(
            HasFocus ? DirectorGuiTheme.AccentR : DirectorGuiTheme.BorderR,
            HasFocus ? DirectorGuiTheme.AccentG : DirectorGuiTheme.BorderG,
            HasFocus ? DirectorGuiTheme.AccentB : DirectorGuiTheme.BorderB,
            Enabled ? 0.9d : 0.35d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(HasFocus ? 1.4d : 1d));
        context.Stroke();
    }
}

internal sealed class GuiElementDirectorSwitch : GuiElementSwitch
{
    private LoadedTexture offTexture;
    private LoadedTexture enabledTexture;

    public GuiElementDirectorSwitch(
        ICoreClientAPI capi,
        Action<bool> onToggled,
        ElementBounds bounds,
        double size,
        double padding)
        : base(capi, onToggled, bounds, size, padding)
    {
        offTexture = new LoadedTexture(capi);
        enabledTexture = new LoadedTexture(capi);
    }

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        _ = context;
        _ = surface;
        Bounds.CalcWorldBounds();
        ComposeState(ref offTexture, false);
        ComposeState(ref enabledTexture, true);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        _ = deltaTime;
        api.Render.Render2DTexturePremultipliedAlpha(
            On ? enabledTexture.TextureId : offTexture.TextureId,
            Bounds);
    }

    public override void Dispose()
    {
        offTexture.Dispose();
        enabledTexture.Dispose();
        base.Dispose();
    }

    private void ComposeState(ref LoadedTexture texture, bool value)
    {
        using ImageSurface surface = new(
            Format.Argb32,
            Math.Max(1, Bounds.OuterWidthInt),
            Math.Max(1, Bounds.OuterHeightInt));
        using Context context = new(surface);
        DirectorGuiTheme.RoundedRectangle(context, 0.5d, 0.5d,
            surface.Width - 1d, surface.Height - 1d, DirectorGuiTheme.ScaledCornerRadius);
        context.SetSourceRGBA(
            value ? DirectorGuiTheme.AccentR : DirectorGuiTheme.SurfaceR,
            value ? DirectorGuiTheme.AccentG : DirectorGuiTheme.SurfaceG,
            value ? DirectorGuiTheme.AccentB : DirectorGuiTheme.SurfaceB,
            Enabled ? 0.96d : 0.35d);
        context.FillPreserve();
        context.SetSourceRGBA(
            DirectorGuiTheme.BorderR,
            DirectorGuiTheme.BorderG,
            DirectorGuiTheme.BorderB,
            Enabled ? 0.9d : 0.3d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.Stroke();
        if (value)
        {
            context.SetSourceRGBA(
                DirectorGuiTheme.TextR,
                DirectorGuiTheme.TextG,
                DirectorGuiTheme.TextB,
                Enabled ? 1d : 0.4d);
            context.LineWidth = Math.Max(1.5d, GuiElement.scaled(1.8d));
            context.LineCap = LineCap.Round;
            context.MoveTo(surface.Width * 0.25d, surface.Height * 0.52d);
            context.LineTo(surface.Width * 0.43d, surface.Height * 0.7d);
            context.LineTo(surface.Width * 0.76d, surface.Height * 0.3d);
            context.Stroke();
        }
        generateTexture(surface, ref texture, linearMag: true);
    }
}

internal sealed class GuiElementDirectorSlider : GuiElementControl
{
    private readonly ActionConsumable<int> onChanged;
    private LoadedTexture backgroundTexture;
    private LoadedTexture sliderTexture;
    private LoadedTexture valueTexture;
    private int minimum;
    private int maximum = 100;
    private int step = 1;
    private int current;
    private bool dragging;

    public GuiElementDirectorSlider(
        ICoreClientAPI capi,
        ActionConsumable<int> onChanged,
        ElementBounds bounds)
        : base(capi, bounds)
    {
        this.onChanged = onChanged;
        backgroundTexture = new LoadedTexture(capi);
        sliderTexture = new LoadedTexture(capi);
        valueTexture = new LoadedTexture(capi);
    }

    public SliderTooltipDelegate? OnSliderTooltip { get; set; }

    public override bool Focusable => Enabled;

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        _ = context;
        _ = surface;
        Bounds.CalcWorldBounds();
        using ImageSurface field = new(
            Format.Argb32,
            Math.Max(1, Bounds.OuterWidthInt),
            Math.Max(1, Bounds.OuterHeightInt));
        using Context fieldContext = new(field);
        DirectorGuiTheme.RoundedRectangle(fieldContext, 0.5d, 0.5d,
            field.Width - 1d, field.Height - 1d, DirectorGuiTheme.ScaledCornerRadius);
        fieldContext.SetSourceRGBA(
            DirectorGuiTheme.SurfaceR,
            DirectorGuiTheme.SurfaceG,
            DirectorGuiTheme.SurfaceB,
            1d);
        fieldContext.FillPreserve();
        fieldContext.SetSourceRGBA(
            DirectorGuiTheme.BorderR,
            DirectorGuiTheme.BorderG,
            DirectorGuiTheme.BorderB,
            0.88d);
        fieldContext.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        fieldContext.Stroke();
        generateTexture(field, ref backgroundTexture, linearMag: true);
        ComposeSliderTexture();
        ComposeValueTexture();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        _ = deltaTime;
        api.Render.Render2DTexturePremultipliedAlpha(backgroundTexture.TextureId, Bounds);
        double labelWidth = GuiElement.scaled(58d);
        api.Render.Render2DTexturePremultipliedAlpha(sliderTexture.TextureId, Bounds, 300f);
        api.Render.Render2DTexturePremultipliedAlpha(
            valueTexture.TextureId,
            Bounds.renderX + Bounds.OuterWidth - labelWidth,
            Bounds.renderY,
            labelWidth,
            Bounds.OuterHeight,
            301f);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (!Enabled || args.Button != EnumMouseButton.Left)
        {
            return;
        }
        dragging = true;
        args.Handled = SetFromMouse(args.X);
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (dragging)
        {
            args.Handled = SetFromMouse(args.X);
        }
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        if (args.Button == EnumMouseButton.Left)
        {
            dragging = false;
        }
    }

    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (Enabled && (HasFocus || Bounds.PointInside(api.Input.MouseX, api.Input.MouseY)))
        {
            double direction = args.deltaPrecise != 0f ? args.deltaPrecise : args.delta;
            if (direction == 0d)
            {
                return;
            }

            args.SetHandled(true);
            SetValue(current + Math.Sign(direction) * step, notify: true);
        }
    }

    public void SetValues(int value, int minValue, int maxValue, int stepValue)
    {
        minimum = Math.Min(minValue, maxValue);
        maximum = Math.Max(minValue + 1, maxValue);
        step = Math.Max(1, stepValue);
        if (!SetValue(value, notify: false))
        {
            return;
        }
        ComposeSliderTexture();
        ComposeValueTexture();
    }

    public bool SetValue(int value)
        => SetValue(value, notify: false);

    public override void Dispose()
    {
        backgroundTexture.Dispose();
        sliderTexture.Dispose();
        valueTexture.Dispose();
        base.Dispose();
    }

    private bool SetFromMouse(int mouseX)
    {
        double left = Bounds.absX + GuiElement.scaled(9d);
        double usable = Math.Max(GuiElement.scaled(24d),
            Bounds.OuterWidth - GuiElement.scaled(76d));
        double fraction = Math.Clamp((mouseX - left) / usable, 0d, 1d);
        int requested = minimum + (int)Math.Round((maximum - minimum) * fraction);
        return SetValue(requested, notify: true);
    }

    private bool SetValue(int requested, bool notify)
    {
        int snapped = minimum + (int)Math.Round((double)(requested - minimum) / step) * step;
        int next = Math.Clamp(snapped, minimum, maximum);
        if (next == current)
        {
            return true;
        }
        current = next;
        ComposeSliderTexture();
        ComposeValueTexture();
        return !notify || onChanged(current);
    }

    private void ComposeSliderTexture()
    {
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);

        double left = GuiElement.scaled(9d);
        double labelWidth = GuiElement.scaled(58d);
        double usable = Math.Max(
            GuiElement.scaled(24d),
            width - labelWidth - GuiElement.scaled(18d));
        double fraction = Math.Clamp(
            (double)(current - minimum) / Math.Max(1, maximum - minimum),
            0d,
            1d);
        double centerY = height / 2d;
        double trackHeight = GuiElement.scaled(6d);
        double handleWidth = GuiElement.scaled(10d);
        double handleHeight = GuiElement.scaled(18d);

        context.Rectangle(left, centerY - trackHeight / 2d, usable, trackHeight);
        context.SetSourceRGBA(
            DirectorGuiTheme.RaisedR,
            DirectorGuiTheme.RaisedG,
            DirectorGuiTheme.RaisedB,
            1d);
        context.Fill();

        context.Rectangle(
            left,
            centerY - trackHeight / 2d,
            Math.Max(trackHeight, usable * fraction),
            trackHeight);
        context.SetSourceRGBA(
            DirectorGuiTheme.AccentR,
            DirectorGuiTheme.AccentG,
            DirectorGuiTheme.AccentB,
            1d);
        context.Fill();

        double handleX = left + usable * fraction;
        context.Rectangle(
            handleX - handleWidth / 2d,
            centerY - handleHeight / 2d,
            handleWidth,
            handleHeight);
        context.SetSourceRGBA(
            DirectorGuiTheme.TextR,
            DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB,
            1d);
        context.Fill();

        generateTexture(surface, ref sliderTexture, linearMag: false);
    }

    private void ComposeValueTexture()
    {
        int width = Math.Max(1, (int)Math.Ceiling(GuiElement.scaled(58d)));
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        CairoFont font = CairoFont.WhiteSmallText();
        font.SetupContext(context);
        string text = OnSliderTooltip?.Invoke(current) ?? current.ToString();
        TextExtents extents = font.GetTextExtents(text);
        context.SetSourceRGBA(
            DirectorGuiTheme.TextR,
            DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB,
            Enabled ? 0.92d : 0.35d);
        context.MoveTo(Math.Max(2d, (width - extents.Width) / 2d - extents.XBearing),
            (height - extents.Height) / 2d - extents.YBearing);
        context.ShowText(text);
        generateTexture(surface, ref valueTexture, linearMag: true);
    }
}

internal static class DirectorGuiInputExtensions
{
    public static GuiComposer AddTextInput(
        this GuiComposer composer,
        ElementBounds bounds,
        Action<string>? onTextChanged,
        CairoFont? font = null,
        string? key = null,
        Action? onFocusLost = null)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorTextInput(
                    composer.Api,
                    bounds,
                    onTextChanged,
                    font ?? CairoFont.TextInput(),
                    onFocusLost),
                key);
        }
        return composer;
    }

    public static GuiComposer AddTextArea(
        this GuiComposer composer,
        ElementBounds bounds,
        Action<string>? onTextChanged,
        CairoFont? font = null,
        string? key = null,
        Action? onFocusLost = null)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorTextArea(
                    composer.Api,
                    bounds,
                    onTextChanged,
                    font ?? CairoFont.TextInput(),
                    onFocusLost),
                key);
        }
        return composer;
    }

    public static GuiComposer AddSwitch(
        this GuiComposer composer,
        Action<bool> onToggled,
        ElementBounds bounds,
        string? key = null,
        double size = 28d,
        double padding = 3d)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorSwitch(composer.Api, onToggled, bounds, size, padding),
                key);
        }
        return composer;
    }

    public static GuiComposer AddDirectorSlider(
        this GuiComposer composer,
        ActionConsumable<int> onChanged,
        ElementBounds bounds,
        string? key = null)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorSlider(composer.Api, onChanged, bounds),
                key);
        }
        return composer;
    }

    public static GuiElementDirectorSlider? GetDirectorSlider(this GuiComposer composer, string key)
        => composer.GetElement(key) as GuiElementDirectorSlider;
    public static GuiComposer AddDropDown(
        this GuiComposer composer,
        string[] values,
        string[] names,
        int selectedIndex,
        SelectionChangedDelegate onSelectionChanged,
        ElementBounds bounds,
        string? key = null)
        => AddDropDown(
            composer,
            values,
            names,
            selectedIndex,
            onSelectionChanged,
            bounds,
            CairoFont.WhiteSmallText(),
            key);

    public static GuiComposer AddDropDown(
        this GuiComposer composer,
        string[] values,
        string[] names,
        int selectedIndex,
        SelectionChangedDelegate onSelectionChanged,
        ElementBounds bounds,
        CairoFont font,
        string? key = null)
    {
        if (!composer.Composed)
        {
            selectedIndex = values.Length == 0 ? -1 : Math.Clamp(selectedIndex, 0, values.Length - 1);
            composer.AddInteractiveElement(
                new GuiElementDirectorDropDown(
                    composer.Api,
                    values,
                    names,
                    selectedIndex,
                    onSelectionChanged,
                    bounds,
                    font,
                    multiSelect: false),
                key);
        }
        return composer;
    }

    public static GuiComposer AddMultiSelectDropDown(
        this GuiComposer composer,
        string[] values,
        string[] names,
        int selectedIndex,
        SelectionChangedDelegate onSelectionChanged,
        ElementBounds bounds,
        string? key = null)
    {
        if (!composer.Composed)
        {
            selectedIndex = values.Length == 0 ? -1 : Math.Clamp(selectedIndex, -1, values.Length - 1);
            composer.AddInteractiveElement(
                new GuiElementDirectorDropDown(
                    composer.Api,
                    values,
                    names,
                    selectedIndex,
                    onSelectionChanged,
                    bounds,
                    CairoFont.WhiteSmallText(),
                    multiSelect: true),
                key);
        }
        return composer;
    }

    public static GuiComposer AddNumberInput(
        this GuiComposer composer,
        ElementBounds bounds,
        Action<string>? onTextChanged,
        CairoFont? font = null,
        string? key = null,
        Action? onFocusLost = null)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorNumberInput(
                    composer.Api,
                    bounds,
                    onTextChanged,
                    font ?? CairoFont.TextInput(),
                    onFocusLost),
                key);
        }
        return composer;
    }
}
