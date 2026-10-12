using HazukiPdf.Html.Css;

namespace HazukiPdf.Html.Layout;

internal sealed partial class LayoutEngine
{
    private void LayoutTable(TableBox table, double x, double availableWidth, Cursor c)
    {
        var s = table.Style;
        if (s.BreakBefore)
            ForcePageBreak(c);

        var edges = BoxEdges.Of(s, availableWidth);
        bool collapse = s.BorderCollapse;
        double spacing = collapse ? 0 : s.BorderSpacing;
        // Collapsed table borders straddle the outer grid line, so only half lies inside the table.
        double borderScale = collapse ? 0.5 : 1;
        double insetLeft = edges.BorderLeft * borderScale + (collapse ? 0 : edges.PaddingLeft);
        double insetRight = edges.BorderRight * borderScale + (collapse ? 0 : edges.PaddingRight);
        double insetTop = edges.BorderTop * borderScale + (collapse ? 0 : edges.PaddingTop);
        double insetBottom = edges.BorderBottom * borderScale + (collapse ? 0 : edges.PaddingBottom);

        var rows = table.AllRows.ToList();
        int columnCount = rows.Count == 0 ? 0 : rows.Max(r => r.Cells.Sum(cell => cell.ColSpan));
        var columns = MeasureColumns(table, columnCount, collapse);
        double spacingTotal = spacing * (columnCount + 1);
        double minGrid = columns.Min.Sum() + spacingTotal;

        double gridWidth;
        if (!s.Width.IsAuto)
        {
            // A table's width includes its borders and padding.
            gridWidth = s.Width.Resolve(availableWidth) - insetLeft - insetRight;
        }
        else
        {
            double room = availableWidth - edges.MarginLeft - edges.MarginRight - insetLeft - insetRight;
            gridWidth = Math.Min(room, columns.Max.Sum() + spacingTotal);
        }
        gridWidth = Math.Max(gridWidth, minGrid);
        double[] widths = DistributeColumns(columns, gridWidth - spacingTotal, fixedTableWidth: !s.Width.IsAuto);
        double tableWidth = gridWidth + insetLeft + insetRight;
        double tableX = x + PlaceHorizontally(s, edges, availableWidth, tableWidth);

        if (table.Caption is not null)
        {
            c.AddMargin(edges.MarginTop);
            LayoutBlock(table.Caption, tableX, tableWidth, c);
            c.Flush();
        }
        else
        {
            c.AddMargin(edges.MarginTop);
            c.Flush();
        }

        // Start the table on a new page if its first rows would not fit here.
        double firstRows = insetTop + spacing;
        foreach (var row in table.HeaderRows)
            firstRows += MeasureRow(row, widths, spacing, collapse) + spacing;
        if (table.BodyRows.Concat(table.FooterRows).FirstOrDefault() is { } firstBody)
            firstRows += MeasureRow(firstBody, widths, spacing, collapse);
        FitOnPage(c, firstRows);

        int opIndex = _list.Ops.Count;
        double top = c.Y;
        if (table.Id is not null)
            _list.Anchors.TryAdd(table.Id, (tableX, top));
        double gridX = tableX + insetLeft;
        c.Y += insetTop + spacing;

        double headerHeight = 0;
        foreach (var row in table.HeaderRows)
        {
            double before = c.Y;
            LayoutRow(row, gridX, widths, spacing, collapse, c);
            headerHeight += c.Y - before;
        }

        int lastPage = PageOf(c.Y - Epsilon);
        foreach (var row in table.BodyRows.Concat(table.FooterRows))
        {
            double height = MeasureRow(row, widths, spacing, collapse);
            FitOnPage(c, height);
            bool newPage = _paginate && PageOf(c.Y) > lastPage;
            lastPage = PageOf(c.Y);
            if (newPage && table.HeaderRows.Count > 0 && headerHeight + height <= pageHeight)
            {
                if (c.Y - lastPage * pageHeight > Epsilon)
                {
                    // The row spacing pushed us past the cut; restart at the top of the page.
                    c.Y = lastPage * pageHeight;
                }
                // Repeat the header rows at the top of the new page.
                foreach (var header in table.HeaderRows)
                    LayoutRow(header, gridX, widths, spacing, collapse, c);
            }
            LayoutRow(row, gridX, widths, spacing, collapse, c);
        }

        c.Y += insetBottom;
        if (s.Visible)
            _list.Ops.InsertRange(opIndex, Decorations(tableX, top, tableWidth, c.Y - top, s, centered: false));

        c.AddMargin(edges.MarginBottom);
        if (s.BreakAfter)
            ForcePageBreak(c);
    }

    private readonly record struct CellPlacement(TableCellBox Cell, double X, double Width);

    private static List<CellPlacement> PlaceCells(TableRowBox row, double[] widths, double gridX, double spacing)
    {
        var placements = new List<CellPlacement>();
        double x = gridX + spacing;
        int column = 0;
        foreach (var cell in row.Cells)
        {
            if (column >= widths.Length)
                break;
            int span = Math.Min(cell.ColSpan, widths.Length - column);
            double width = spacing * (span - 1);
            for (int i = 0; i < span; i++)
                width += widths[column + i];
            placements.Add(new CellPlacement(cell, x, width));
            x += width + spacing;
            column += span;
        }
        return placements;
    }

    private static (double Top, double Right, double Bottom, double Left) CellInsets(TableCellBox cell, double width, bool collapse)
    {
        var s = cell.Style;
        double scale = collapse ? 0.5 : 1;
        return (
            s.BorderTop.LayoutWidth * scale + s.PaddingTop.Resolve(width),
            s.BorderRight.LayoutWidth * scale + s.PaddingRight.Resolve(width),
            s.BorderBottom.LayoutWidth * scale + s.PaddingBottom.Resolve(width),
            s.BorderLeft.LayoutWidth * scale + s.PaddingLeft.Resolve(width));
    }

    /// <summary>Height of a row, measured without pagination.</summary>
    private double MeasureRow(TableRowBox row, double[] widths, double spacing, bool collapse)
    {
        double height = 0;
        foreach (var placement in PlaceCells(row, widths, 0, spacing))
            height = Math.Max(height, MeasureCellHeight(placement, collapse));
        return Math.Max(height, RowMinHeight(row));
    }

    private static double RowMinHeight(TableRowBox row) =>
        !row.Style.Height.IsAuto && row.Style.Height.Percent == 0 ? row.Style.Height.Points : 0;

    private double MeasureCellHeight(CellPlacement placement, bool collapse)
    {
        var saved = (_list, _paginate, _pendingMarker, _markerX);
        _list = new DisplayList();
        _paginate = false;
        _pendingMarker = null;
        try
        {
            var insets = CellInsets(placement.Cell, placement.Width, collapse);
            var cursor = new Cursor();
            LayoutContents(placement.Cell, 0, Math.Max(0, placement.Width - insets.Left - insets.Right), cursor);
            cursor.Flush();
            double height = cursor.Y + insets.Top + insets.Bottom;
            var style = placement.Cell.Style;
            if (!style.Height.IsAuto && style.Height.Percent == 0)
                height = Math.Max(height, style.Height.Points + insets.Top + insets.Bottom);
            return height;
        }
        finally
        {
            (_list, _paginate, _pendingMarker, _markerX) = saved;
        }
    }

    private void LayoutRow(TableRowBox row, double gridX, double[] widths, double spacing, bool collapse, Cursor c)
    {
        var placements = PlaceCells(row, widths, gridX, spacing);
        var heights = placements.Select(p => MeasureCellHeight(p, collapse)).ToList();
        double rowHeight = Math.Max(heights.DefaultIfEmpty(0).Max(), RowMinHeight(row));
        double rowTop = c.Y;
        bool splits = PageOf(rowTop) != PageOf(rowTop + rowHeight - Epsilon);

        int opIndex = _list.Ops.Count;
        double bottom = rowTop + rowHeight;
        for (int i = 0; i < placements.Count; i++)
        {
            var (cell, cellX, cellWidth) = placements[i];
            var insets = CellInsets(cell, cellWidth, collapse);
            double offset = splits ? 0 : cell.Style.VerticalAlign switch
            {
                VerticalAlign.Middle => (rowHeight - heights[i]) / 2,
                VerticalAlign.Bottom => rowHeight - heights[i],
                _ => 0,
            };
            var cursor = new Cursor { Y = rowTop + offset + insets.Top };
            if (cell.Id is not null)
                _list.Anchors.TryAdd(cell.Id, (cellX, rowTop));
            LayoutContents(cell, cellX + insets.Left, Math.Max(0, cellWidth - insets.Left - insets.Right), cursor);
            cursor.Flush();
            bottom = Math.Max(bottom, cursor.Y + insets.Bottom - offset);
        }
        rowHeight = bottom - rowTop;

        var decorations = new List<DrawOp>();
        foreach (var (cell, cellX, cellWidth) in placements)
        {
            if (cell.Style.Visible)
                decorations.AddRange(Decorations(cellX, rowTop, cellWidth, rowHeight, cell.Style, collapse, row.Style.BackgroundColor));
        }
        _list.Ops.InsertRange(opIndex, decorations);
        c.Y = rowTop + rowHeight + spacing;
    }

    private sealed record ColumnMeasure(double[] Min, double[] Max, bool[] Fixed, double[] Percent);

    private ColumnMeasure MeasureColumns(TableBox table, int columnCount, bool collapse)
    {
        var min = new double[columnCount];
        var max = new double[columnCount];
        var isFixed = new bool[columnCount];
        var percent = new double[columnCount];
        var spanned = new List<(int Start, int Span, double Min, double Max)>();

        foreach (var row in table.AllRows)
        {
            int column = 0;
            foreach (var cell in row.Cells)
            {
                if (column >= columnCount)
                    break;
                int span = Math.Min(cell.ColSpan, columnCount - column);
                var (contentMin, contentMax) = MeasureBlockContent(cell);
                var insets = CellInsets(cell, 0, collapse);
                double extras = insets.Left + insets.Right;
                double cellMin = contentMin + extras, cellMax = contentMax + extras;
                var width = cell.Style.Width;
                bool cellFixed = !width.IsAuto && width.Percent == 0;
                if (cellFixed)
                    cellMax = Math.Max(cellMin, width.Points + extras);

                if (span == 1)
                {
                    min[column] = Math.Max(min[column], cellMin);
                    if (cellFixed)
                    {
                        max[column] = isFixed[column] ? Math.Max(max[column], cellMax) : cellMax;
                        isFixed[column] = true;
                    }
                    else if (!isFixed[column])
                    {
                        max[column] = Math.Max(max[column], cellMax);
                    }
                    if (!width.IsAuto && width.Percent > 0)
                        percent[column] = Math.Max(percent[column], width.Percent);
                }
                else
                {
                    spanned.Add((column, span, cellMin, cellMax));
                }
                column += span;
            }
        }

        double spacing = collapse ? 0 : table.Style.BorderSpacing;
        foreach (var (start, span, cellMin, cellMax) in spanned)
        {
            double currentMin = spacing * (span - 1), currentMax = spacing * (span - 1);
            for (int i = start; i < start + span; i++)
            {
                currentMin += min[i];
                currentMax += max[i];
            }
            for (int i = start; i < start + span; i++)
            {
                if (cellMin > currentMin)
                    min[i] += (cellMin - currentMin) / span;
                if (cellMax > currentMax)
                    max[i] += (cellMax - currentMax) / span;
            }
        }
        for (int i = 0; i < columnCount; i++)
            max[i] = Math.Max(max[i], min[i]);
        return new ColumnMeasure(min, max, isFixed, percent);
    }

    private static double[] DistributeColumns(ColumnMeasure columns, double available, bool fixedTableWidth)
    {
        int n = columns.Min.Length;
        var widths = (double[])columns.Min.Clone();
        for (int i = 0; i < n; i++)
        {
            if (columns.Percent[i] > 0)
                widths[i] = Math.Max(widths[i], columns.Percent[i] * available / 100);
        }

        double remaining = available - widths.Sum();
        if (remaining <= Epsilon)
            return widths;

        // Grow auto columns towards their preferred width.
        double gap = 0;
        for (int i = 0; i < n; i++)
        {
            if (columns.Percent[i] == 0)
                gap += Math.Max(0, columns.Max[i] - widths[i]);
        }
        if (gap > 0)
        {
            double factor = Math.Min(1, remaining / gap);
            for (int i = 0; i < n; i++)
            {
                if (columns.Percent[i] == 0)
                {
                    double grow = Math.Max(0, columns.Max[i] - widths[i]) * factor;
                    widths[i] += grow;
                    remaining -= grow;
                }
            }
        }
        if (remaining <= Epsilon || !fixedTableWidth && gap > 0)
            return widths;

        // The table is wider than its content wants: share the rest, preferring auto-width columns.
        var targets = Enumerable.Range(0, n).Where(i => !columns.Fixed[i] && columns.Percent[i] == 0).ToList();
        if (targets.Count == 0)
            targets = Enumerable.Range(0, n).ToList();
        double weight = targets.Sum(i => widths[i]);
        foreach (int i in targets)
            widths[i] += weight > 0 ? remaining * widths[i] / weight : remaining / targets.Count;
        return widths;
    }

    /// <summary>Minimum and preferred width of a block's content, used for table column sizing.</summary>
    private (double Min, double Max) MeasureBlockContent(BlockBox box)
    {
        if (box.Inline is not null)
            return MeasureInline(box.Inline, box.Style);

        double min = 0, max = 0;
        foreach (var child in box.Children)
        {
            double childMin, childMax;
            ComputedStyle s;
            if (child is BlockBox block)
            {
                s = block.Style;
                (childMin, childMax) = MeasureBlockContent(block);
                if (!s.Width.IsAuto && s.Width.Percent == 0)
                    childMin = childMax = s.Width.Points;
            }
            else if (child is TableBox table)
            {
                s = table.Style;
                (childMin, childMax) = MeasureTable(table);
            }
            else
            {
                continue;
            }
            double extras = Fixed(s.MarginLeft) + Fixed(s.MarginRight);
            if (child is BlockBox)
                extras += s.BorderLeft.LayoutWidth + s.BorderRight.LayoutWidth + Fixed(s.PaddingLeft) + Fixed(s.PaddingRight);
            min = Math.Max(min, childMin + extras);
            max = Math.Max(max, childMax + extras);
        }
        return (min, max);

        static double Fixed(CssLength length) => length.IsAuto || length.Percent != 0 ? 0 : length.Points;
    }

    private (double Min, double Max) MeasureTable(TableBox table)
    {
        var s = table.Style;
        bool collapse = s.BorderCollapse;
        var rows = table.AllRows.ToList();
        int columnCount = rows.Count == 0 ? 0 : rows.Max(r => r.Cells.Sum(cell => cell.ColSpan));
        var columns = MeasureColumns(table, columnCount, collapse);
        double spacing = (collapse ? 0 : s.BorderSpacing) * (columnCount + 1);
        double scale = collapse ? 0.5 : 1;
        double extras = (s.BorderLeft.LayoutWidth + s.BorderRight.LayoutWidth) * scale +
                        (collapse ? 0 : s.PaddingLeft.Resolve(0) + s.PaddingRight.Resolve(0));
        double min = columns.Min.Sum() + spacing + extras;
        double max = columns.Max.Sum() + spacing + extras;
        if (!s.Width.IsAuto && s.Width.Percent == 0)
            max = Math.Max(min, s.Width.Points);
        return (min, Math.Max(min, max));
    }

    private (double Min, double Max) MeasureInline(InlineContent content, ComputedStyle blockStyle)
    {
        var pieces = GetPieces(content);
        double min = 0, max = 0, line = blockStyle.TextIndent.Resolve(0), segment = 0;
        foreach (var p in pieces)
        {
            double width = p.Kind == PieceKind.Image ? ImageSize(p, double.PositiveInfinity).Width : p.Width;
            switch (p.Kind)
            {
                case PieceKind.Break:
                    max = Math.Max(max, line);
                    min = Math.Max(min, segment);
                    line = segment = 0;
                    break;
                case PieceKind.Space when p.CanBreak:
                    min = Math.Max(min, segment);
                    segment = 0;
                    line += width;
                    break;
                case PieceKind.Image:
                    min = Math.Max(min, Math.Max(segment, width));
                    segment = 0;
                    line += width;
                    break;
                default:
                    segment += width;
                    line += width;
                    break;
            }
        }
        min = Math.Max(min, segment);
        max = Math.Max(max, line);
        return (min, Math.Max(min, max));
    }
}
