using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SimpleVoiceChat.Gui;

internal enum DirectorSurfaceKind
{
    Window,
    Card,
    OpaqueWindow,
    DockedWindow
}

internal readonly record struct DirectorTableRow(string Label, string Value);

internal sealed class GuiElementDirectorTable : GuiElement
{
    private const double HeaderHeight = 30d;
    private const double MinimumRowHeight = 38d;
    private const double CellHorizontalPadding = 9d;
    private const double CellVerticalPadding = 7d;
    private const double LineGap = 2d;

    private readonly string labelHeader;
    private readonly string valueHeader;
    private readonly IReadOnlyList<DirectorTableRow> rows;
    private readonly double labelWidth;

    public GuiElementDirectorTable(
        ICoreClientAPI capi,
        ElementBounds bounds,
        string labelHeader,
        string valueHeader,
        IReadOnlyList<DirectorTableRow> rows,
        double labelWidth)
        : base(capi, bounds)
    {
        this.labelHeader = labelHeader ?? string.Empty;
        this.valueHeader = valueHeader ?? string.Empty;
        this.rows = rows;
        this.labelWidth = Math.Max(80d, labelWidth);
    }

    public static double RequiredHeight(
        IReadOnlyList<DirectorTableRow> rows,
        double tableWidth,
        double labelWidth)
    {
        using ImageSurface surface = new(Format.Argb32, 1, 1);
        using Context context = new(surface);
        CairoFont rowFont = CairoFont.WhiteDetailText().WithFontSize(12.5f);
        double dividerWidth = Math.Min(labelWidth, Math.Max(80d, tableWidth - 80d));
        double valueWidth = tableWidth - dividerWidth;
        double[] rowHeights = CalculateRowHeights(
            context,
            rowFont,
            rows,
            dividerWidth - CellHorizontalPadding * 2d,
            valueWidth - CellHorizontalPadding * 2d);
        return HeaderHeight + rowHeights.Sum();
    }

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        _ = surface;
        Bounds.CalcWorldBounds();
        double x = Bounds.drawX;
        double y = Bounds.drawY;
        double width = Bounds.OuterWidth;
        double height = Bounds.OuterHeight;
        double dividerWidth = Math.Min(labelWidth, Math.Max(80d, width - 80d));
        double dividerX = x + dividerWidth;
        CairoFont rowFont = CairoFont.WhiteDetailText().WithFontSize(12.5f);
        double[] rowHeights = CalculateRowHeights(
            context,
            rowFont,
            rows,
            dividerWidth - CellHorizontalPadding * 2d,
            width - dividerWidth - CellHorizontalPadding * 2d);

        DirectorGuiTheme.RoundedRectangle(
            context,
            x + 0.5d,
            y + 0.5d,
            width - 1d,
            height - 1d,
            DirectorGuiTheme.ScaledCornerRadius);
        context.SetSourceRGBA(
            DirectorGuiTheme.SurfaceR,
            DirectorGuiTheme.SurfaceG,
            DirectorGuiTheme.SurfaceB,
            0.98d);
        context.Fill();

        context.Save();
        DirectorGuiTheme.RoundedRectangle(
            context,
            x + 0.5d,
            y + 0.5d,
            width - 1d,
            height - 1d,
            DirectorGuiTheme.ScaledCornerRadius);
        context.Clip();

        context.Rectangle(x, y, width, HeaderHeight);
        context.SetSourceRGBA(
            DirectorGuiTheme.AccentR,
            DirectorGuiTheme.AccentG,
            DirectorGuiTheme.AccentB,
            0.3d);
        context.Fill();

        double rowY = y + HeaderHeight;
        for (int index = 0; index < rowHeights.Length; index++)
        {
            if (index % 2 == 1)
            {
                context.Rectangle(x, rowY, width, rowHeights[index]);
                context.SetSourceRGBA(
                    DirectorGuiTheme.RaisedR,
                    DirectorGuiTheme.RaisedG,
                    DirectorGuiTheme.RaisedB,
                    0.5d);
                context.Fill();
            }
            rowY += rowHeights[index];
        }

        context.Restore();

        context.SetSourceRGBA(
            DirectorGuiTheme.BorderR,
            DirectorGuiTheme.BorderG,
            DirectorGuiTheme.BorderB,
            0.82d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.MoveTo(dividerX, y);
        context.LineTo(dividerX, y + height);
        context.MoveTo(x, y + HeaderHeight);
        context.LineTo(x + width, y + HeaderHeight);
        rowY = y + HeaderHeight;
        for (int index = 0; index < rowHeights.Length; index++)
        {
            rowY += rowHeights[index];
            context.MoveTo(x, rowY);
            context.LineTo(x + width, rowY);
        }
        context.Stroke();

        DirectorGuiTheme.RoundedRectangle(
            context,
            x + 0.5d,
            y + 0.5d,
            width - 1d,
            height - 1d,
            DirectorGuiTheme.ScaledCornerRadius);
        context.SetSourceRGBA(
            DirectorGuiTheme.BorderR,
            DirectorGuiTheme.BorderG,
            DirectorGuiTheme.BorderB,
            0.95d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.Stroke();

        CairoFont headerFont = CairoFont.WhiteSmallText().WithFontSize(12f);
        DrawCellText(
            context,
            headerFont,
            labelHeader,
            x + CellHorizontalPadding,
            y,
            dividerWidth - CellHorizontalPadding * 2d,
            HeaderHeight,
            1d,
            wrap: false);
        DrawCellText(
            context,
            headerFont,
            valueHeader,
            dividerX + CellHorizontalPadding,
            y,
            width - dividerWidth - CellHorizontalPadding * 2d,
            HeaderHeight,
            1d,
            wrap: false);

        rowY = y + HeaderHeight;
        for (int index = 0; index < rows.Count; index++)
        {
            DirectorTableRow row = rows[index];
            DrawCellText(
                context,
                rowFont,
                row.Label,
                x + CellHorizontalPadding,
                rowY,
                dividerWidth - CellHorizontalPadding * 2d,
                rowHeights[index],
                0.98d,
                wrap: true);
            DrawCellText(
                context,
                rowFont,
                row.Value,
                dividerX + CellHorizontalPadding,
                rowY,
                width - dividerWidth - CellHorizontalPadding * 2d,
                rowHeights[index],
                0.86d,
                wrap: true);
            rowY += rowHeights[index];
        }
    }

    private static double[] CalculateRowHeights(
        Context context,
        CairoFont font,
        IReadOnlyList<DirectorTableRow> rows,
        double labelWidth,
        double valueWidth)
    {
        font.SetupContext(context);
        if (rows.Count == 0)
        {
            return new[] { MinimumRowHeight };
        }

        FontExtents fontExtents = font.GetFontExtents();
        double lineHeight = Math.Max(1d, fontExtents.Height);
        double[] rowHeights = new double[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            int labelLines = WrapText(context, font, rows[index].Label, Math.Max(1d, labelWidth)).Count;
            int valueLines = WrapText(context, font, rows[index].Value, Math.Max(1d, valueWidth)).Count;
            int lineCount = Math.Max(labelLines, valueLines);
            double textHeight = lineCount * lineHeight + Math.Max(0, lineCount - 1) * LineGap;
            rowHeights[index] = Math.Max(MinimumRowHeight, textHeight + CellVerticalPadding * 2d);
        }
        return rowHeights;
    }

    private static void DrawCellText(
        Context context,
        CairoFont font,
        string text,
        double x,
        double y,
        double width,
        double height,
        double alpha,
        bool wrap)
    {
        font.SetupContext(context);
        FontExtents fontExtents = font.GetFontExtents();
        context.SetSourceRGBA(
            DirectorGuiTheme.TextR,
            DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB,
            alpha);
        if (!wrap)
        {
            string display = DirectorGuiTheme.Ellipsize(context, font, text, Math.Max(1d, width));
            TextExtents extents = context.TextExtents(display);
            context.MoveTo(
                x - extents.XBearing,
                y + (height - fontExtents.Height) / 2d + fontExtents.Ascent);
            context.ShowText(display);
            return;
        }

        IReadOnlyList<string> lines = WrapText(context, font, text, Math.Max(1d, width));
        double lineHeight = Math.Max(1d, fontExtents.Height);
        double textHeight = lines.Count * lineHeight + Math.Max(0, lines.Count - 1) * LineGap;
        double baseline = y + (height - textHeight) / 2d + fontExtents.Ascent;
        foreach (string line in lines)
        {
            TextExtents extents = context.TextExtents(line);
            context.MoveTo(x - extents.XBearing, baseline);
            context.ShowText(line);
            baseline += lineHeight + LineGap;
        }
    }

    private static IReadOnlyList<string> WrapText(
        Context context,
        CairoFont font,
        string text,
        double maxWidth)
    {
        List<string> lines = new();
        string[] paragraphs = (text ?? string.Empty).Replace("\r", string.Empty).Split('\n');
        foreach (string paragraph in paragraphs)
        {
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            string current = string.Empty;
            foreach (char character in paragraph)
            {
                string candidate = current + character;
                if (current.Length > 0 && context.TextExtents(candidate).Width > maxWidth)
                {
                    int breakAt = LastWhitespace(current);
                    if (breakAt > 0)
                    {
                        lines.Add(current[..breakAt].TrimEnd());
                        current = current[(breakAt + 1)..].TrimStart();
                    }
                    else
                    {
                        lines.Add(current);
                        current = string.Empty;
                    }

                    if (!char.IsWhiteSpace(character))
                    {
                        current += character;
                    }
                }
                else if (!char.IsWhiteSpace(character) || current.Length > 0)
                {
                    current += character;
                }
            }

            if (current.Length > 0)
            {
                lines.Add(current.TrimEnd());
            }
        }

        return lines.Count == 0 ? new[] { string.Empty } : lines;
    }

    private static int LastWhitespace(string value)
    {
        for (int index = value.Length - 1; index > 0; index--)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                return index;
            }
        }
        return -1;
    }
}

internal sealed class GuiElementDirectorSurface : GuiElement
{
    private readonly DirectorSurfaceKind kind;

    public GuiElementDirectorSurface(
        ICoreClientAPI capi,
        ElementBounds bounds,
        DirectorSurfaceKind kind)
        : base(capi, bounds)
    {
        this.kind = kind;
    }

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        _ = surface;
        Bounds.CalcWorldBounds();
        bool insetPercentualBounds = Bounds.horizontalSizing is
            ElementSizing.Percentual or ElementSizing.PercentualSubstractFixed
            || Bounds.verticalSizing is
                ElementSizing.Percentual or ElementSizing.PercentualSubstractFixed;
        double x = insetPercentualBounds ? Bounds.drawX : Bounds.bgDrawX;
        double y = insetPercentualBounds ? Bounds.drawY : Bounds.bgDrawY;
        double width = insetPercentualBounds
            ? Bounds.InnerWidth - Bounds.absPaddingX * 2d
            : Bounds.OuterWidth;
        double height = insetPercentualBounds
            ? Bounds.InnerHeight - Bounds.absPaddingY * 2d
            : Bounds.OuterHeight;
        if (width <= 1d || height <= 1d)
        {
            return;
        }

        context.Save();
        bool docked = kind == DirectorSurfaceKind.DockedWindow;
        DirectorGuiTheme.RoundedRectangle(
            context,
            x + 0.5d,
            y + 0.5d,
            width - 1d,
            height - 1d,
            DirectorGuiTheme.ScaledCornerRadius);
        bool window = kind is DirectorSurfaceKind.Window
            or DirectorSurfaceKind.OpaqueWindow
            or DirectorSurfaceKind.DockedWindow;
        context.SetSourceRGBA(
            window ? DirectorGuiTheme.SurfaceR : DirectorGuiTheme.RaisedR,
            window ? DirectorGuiTheme.SurfaceG : DirectorGuiTheme.RaisedG,
            window ? DirectorGuiTheme.SurfaceB : DirectorGuiTheme.RaisedB,
            kind == DirectorSurfaceKind.OpaqueWindow ? 0.985d
                : docked ? 0.68d
                : kind == DirectorSurfaceKind.Window ? 0.52d : 0.46d);
        context.FillPreserve();
        context.SetSourceRGBA(
            DirectorGuiTheme.BorderR,
            DirectorGuiTheme.BorderG,
            DirectorGuiTheme.BorderB,
            window ? 0.72d : 0.38d);
        context.LineWidth = 1d;
        context.Stroke();
        context.Restore();
    }
}

internal sealed class GuiElementDirectorStaticClip : GuiElement
{
    private readonly bool begin;

    public GuiElementDirectorStaticClip(
        ICoreClientAPI capi,
        ElementBounds bounds,
        bool begin)
        : base(capi, bounds)
    {
        this.begin = begin;
    }

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        _ = surface;
        if (!begin)
        {
            context.Restore();
            return;
        }

        Bounds.CalcWorldBounds();
        context.Save();
        context.Rectangle(Bounds.drawX, Bounds.drawY, Bounds.OuterWidth, Bounds.OuterHeight);
        context.Clip();
    }
}

internal static class DirectorGuiSurfaceExtensions
{
    public static GuiComposer AddDirectorTable(
        this GuiComposer composer,
        string labelHeader,
        string valueHeader,
        IReadOnlyList<DirectorTableRow> rows,
        ElementBounds bounds,
        double labelWidth,
        string? key = null)
    {
        if (!composer.Composed)
        {
            composer.AddStaticElement(
                new GuiElementDirectorTable(
                    composer.Api,
                    bounds,
                    labelHeader,
                    valueHeader,
                    rows,
                    labelWidth),
                key);
        }
        return composer;
    }

    public static GuiComposer AddDirectorWindowSurface(
        this GuiComposer composer,
        ElementBounds bounds,
        string? key = null)
        => AddDirectorSurface(composer, bounds, DirectorSurfaceKind.Window, key);

    public static GuiComposer AddDirectorCard(
        this GuiComposer composer,
        ElementBounds bounds,
        string? key = null)
        => AddDirectorSurface(composer, bounds, DirectorSurfaceKind.Card, key);

    public static GuiComposer AddDirectorOpaqueWindowSurface(
        this GuiComposer composer,
        ElementBounds bounds,
        string? key = null)
        => AddDirectorSurface(composer, bounds, DirectorSurfaceKind.OpaqueWindow, key);

    public static GuiComposer AddDirectorDockedSurface(
        this GuiComposer composer,
        ElementBounds bounds,
        string? key = null)
        => AddDirectorSurface(composer, bounds, DirectorSurfaceKind.DockedWindow, key);

    public static GuiComposer BeginDirectorStaticClip(
        this GuiComposer composer,
        ElementBounds bounds,
        string? key = null)
    {
        if (!composer.Composed)
        {
            composer.AddStaticElement(
                new GuiElementDirectorStaticClip(composer.Api, bounds, begin: true),
                key);
        }
        return composer;
    }

    public static GuiComposer EndDirectorStaticClip(
        this GuiComposer composer,
        string? key = null)
    {
        if (!composer.Composed)
        {
            composer.AddStaticElement(
                new GuiElementDirectorStaticClip(
                    composer.Api,
                    ElementBounds.Fixed(0, 0, 1, 1),
                    begin: false),
                key);
        }
        return composer;
    }

    public static GuiComposer AddDirectorDialogHeader(
        this GuiComposer composer,
        string title,
        Action close,
        double width,
        string key = "directorHeader")
    {
        if (composer.Composed)
        {
            return composer;
        }

        composer.AddInteractiveElement(
            new GuiElementDirectorDragHandle(
                composer.Api,
                composer,
                ElementBounds.Fixed(8, 4, Math.Max(40d, width - 54d), 32)),
            key + "-drag");
        composer
            .AddStaticText(title, CairoFont.WhiteSmallishText(),
                ElementBounds.Fixed(14, 8, Math.Max(40d, width - 70d), 28), key + "-title")
            .AddDirectorIconButton(
                DirectorIcon.Close,
                SVCLang.Get("button-close"),
                () => { close(); return true; },
                ElementBounds.Fixed(width - 38d, 6, 30, 28),
                EnumButtonStyle.Small,
                key + "-close");
        return composer;
    }

    private static GuiComposer AddDirectorSurface(
        GuiComposer composer,
        ElementBounds bounds,
        DirectorSurfaceKind kind,
        string? key)
    {
        if (!composer.Composed)
        {
            composer.AddStaticElement(
                new GuiElementDirectorSurface(composer.Api, bounds, kind),
                key);
        }
        return composer;
    }
}

internal sealed class GuiElementDirectorDragHandle : GuiElementControl
{
    private readonly GuiComposer composer;
    private bool dragging;
    private int dragStartX;
    private int dragStartY;
    private double startOffsetX;
    private double startOffsetY;

    public GuiElementDirectorDragHandle(
        ICoreClientAPI capi,
        GuiComposer composer,
        ElementBounds bounds)
        : base(capi, bounds)
    {
        this.composer = composer;
        MouseOverCursor = "move";
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (!Enabled || args.Button != EnumMouseButton.Left)
        {
            return;
        }

        dragging = true;
        dragStartX = args.X;
        dragStartY = args.Y;
        startOffsetX = composer.Bounds.fixedOffsetX;
        startOffsetY = composer.Bounds.fixedOffsetY;
        args.Handled = true;
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (!dragging)
        {
            return;
        }

        double scale = Math.Max(0.001d, GuiElement.scaled(1d));
        composer.Bounds.WithFixedAlignmentOffset(
            startOffsetX + (args.X - dragStartX) / scale,
            startOffsetY + (args.Y - dragStartY) / scale);
        composer.Bounds.MarkDirtyRecursive();
        composer.Bounds.CalcWorldBounds();
        args.Handled = true;
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        if (args.Button == EnumMouseButton.Left && dragging)
        {
            dragging = false;
            args.Handled = true;
        }
    }
}
