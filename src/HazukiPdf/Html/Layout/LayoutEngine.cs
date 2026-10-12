using HazukiPdf.Html.Css;

namespace HazukiPdf.Html.Layout;

/// <summary>
/// Lays out the box tree into a <see cref="DisplayList"/>. Pages are modelled as one continuous
/// strip that is cut every <c>pageHeight</c> points; lines, images and table rows that would cross
/// a cut are moved to the next page, while backgrounds and borders are split across it.
/// </summary>
internal sealed partial class LayoutEngine(double pageHeight)
{
    private const double Epsilon = 0.01;

    private DisplayList _list = new();
    private bool _paginate = true;
    private ListMarker? _pendingMarker;
    private double _markerX;

    public DisplayList Layout(BlockBox root, double width)
    {
        _list = new DisplayList();
        var cursor = new Cursor();
        LayoutBlock(root, 0, width, cursor);
        return _list;
    }

    /// <summary>The vertical position in the flow plus the margins waiting to collapse.</summary>
    private sealed class Cursor
    {
        private double _positive;
        private double _negative;

        public double Y;

        public double Pending => _positive + _negative;

        public void AddMargin(double margin)
        {
            if (margin >= 0)
                _positive = Math.Max(_positive, margin);
            else
                _negative = Math.Min(_negative, margin);
        }

        public void Flush()
        {
            Y += Pending;
            Discard();
        }

        public void Discard() => _positive = _negative = 0;
    }

    private int PageOf(double y) => (int)Math.Floor(y / pageHeight + 1e-9);

    private double NextPageTop(double y) => (PageOf(y) + 1) * pageHeight;

    /// <summary>Moves the cursor to the next page when something of this height would cross a page cut.</summary>
    private bool FitOnPage(Cursor c, double height)
    {
        if (!_paginate || height > pageHeight || height <= 0)
            return false;
        if (PageOf(c.Y) == PageOf(c.Y + height - Epsilon))
            return false;
        c.Y = NextPageTop(c.Y);
        return true;
    }

    private void ForcePageBreak(Cursor c)
    {
        if (!_paginate)
            return;
        c.Discard();
        double offset = c.Y - PageOf(c.Y) * pageHeight;
        if (c.Y > Epsilon && offset > Epsilon)
            c.Y = NextPageTop(c.Y);
    }

    private void LayoutBlock(BlockBox box, double x, double availableWidth, Cursor c)
    {
        var s = box.Style;
        if (s.BreakBefore)
            ForcePageBreak(c);

        var edges = BoxEdges.Of(s, availableWidth);
        double contentWidth = s.Width.IsAuto
            ? availableWidth - edges.MarginLeft - edges.MarginRight - edges.HorizontalExtras
            : s.Width.Resolve(availableWidth);
        if (!s.MaxWidth.IsAuto)
            contentWidth = Math.Min(contentWidth, s.MaxWidth.Resolve(availableWidth));
        contentWidth = Math.Max(0, contentWidth);
        double borderBoxWidth = contentWidth + edges.HorizontalExtras;
        double marginLeft = PlaceHorizontally(s, edges, availableWidth, borderBoxWidth);

        double boxX = x + marginLeft;
        double contentX = boxX + edges.BorderLeft + edges.PaddingLeft;

        c.AddMargin(edges.MarginTop);
        bool decorated = s.BackgroundColor is not null || edges.HasBorder;
        bool separatesTop = decorated || edges.BorderTop + edges.PaddingTop > 0;
        if (separatesTop)
        {
            c.Flush();
            // Keep the top edge with at least the first line of content.
            FitOnPage(c, edges.BorderTop + edges.PaddingTop + s.LineHeight);
        }
        int opIndex = _list.Ops.Count;
        double top = c.Y + (separatesTop ? 0 : c.Pending);
        if (box.Id is not null)
            _list.Anchors.TryAdd(box.Id, (boxX, top));
        c.Y += edges.BorderTop + edges.PaddingTop;

        if (box.Marker is not null)
        {
            _pendingMarker = box.Marker;
            _markerX = contentX;
        }

        LayoutContents(box, contentX, contentWidth, c);

        if (box.Marker is not null && ReferenceEquals(_pendingMarker, box.Marker))
        {
            // Nothing in the item produced a line; put the marker where the first line would be.
            c.Flush();
            var font = box.Marker.Style.Font;
            DrawMarker(box.Marker, contentX, c.Y + font.Ascent * box.Marker.Style.FontSize / 1000);
            c.Y += box.Marker.Style.LineHeight;
        }

        bool hasHeight = !s.Height.IsAuto && s.Height.Percent == 0 || !s.MinHeight.IsAuto && s.MinHeight.Percent == 0;
        if (decorated || hasHeight || edges.BorderBottom + edges.PaddingBottom > 0)
        {
            c.Flush();
            double contentTop = top + edges.BorderTop + edges.PaddingTop;
            double minHeight = Math.Max(s.Height.IsAuto ? 0 : s.Height.Points, s.MinHeight.IsAuto ? 0 : s.MinHeight.Points);
            c.Y = Math.Max(c.Y, contentTop + minHeight);
            c.Y += edges.PaddingBottom + edges.BorderBottom;
        }

        if (decorated && s.Visible)
            _list.Ops.InsertRange(opIndex, Decorations(boxX, top, borderBoxWidth, c.Y - top, s, centered: false));

        c.AddMargin(edges.MarginBottom);
        if (s.BreakAfter)
            ForcePageBreak(c);
    }

    /// <summary>Resolves auto horizontal margins for a box narrower than its container.</summary>
    private static double PlaceHorizontally(ComputedStyle s, BoxEdges edges, double availableWidth, double borderBoxWidth)
    {
        double marginLeft = edges.MarginLeft;
        if (s.Width.IsAuto && s.MaxWidth.IsAuto && s.Display != Display.Table)
            return marginLeft;
        double free = availableWidth - borderBoxWidth - edges.MarginLeft - edges.MarginRight;
        if (free <= 0)
            return marginLeft;
        if (s.MarginLeft.IsAuto && s.MarginRight.IsAuto)
            return free / 2;
        if (s.MarginLeft.IsAuto)
            return free;
        return marginLeft;
    }

    private void LayoutContents(BlockBox box, double x, double width, Cursor c)
    {
        if (box.Inline is not null)
        {
            LayoutInline(box.Inline, box.Style, x, width, c);
            return;
        }
        foreach (var child in box.Children)
        {
            if (child is BlockBox block)
                LayoutBlock(block, x, width, c);
            else if (child is TableBox table)
                LayoutTable(table, x, width, c);
        }
    }

    private void DrawMarker(ListMarker marker, double contentX, double baseline)
    {
        _pendingMarker = null;
        var s = marker.Style;
        if (!s.Visible)
            return;
        double size = s.FontSize;
        if (marker.Shape != MarkerShape.None)
        {
            double radius = size * 0.17;
            _list.Add(new ShapeOp(contentX - size * 0.65, baseline - size * 0.3, radius, marker.Shape, s.Color));
        }
        else if (marker.Text is { } text)
        {
            var font = s.Font;
            double width = font.Measure(text, size);
            double gap = font.Measure(" ", size);
            _list.Add(new TextOp(contentX - width - gap, baseline, font, size, s.Color, text, 0, 0));
        }
    }

    /// <summary>Background and border drawing operations for a border box.</summary>
    private static List<DrawOp> Decorations(double x, double y, double w, double h, ComputedStyle s, bool centered,
        Rgb? backgroundOverride = null)
    {
        var ops = new List<DrawOp>();
        if ((s.BackgroundColor ?? backgroundOverride) is { } bg && w > 0 && h > 0)
            ops.Add(new RectOp(x, y, w, h, bg));

        var top = s.BorderTop;
        var right = s.BorderRight;
        var bottom = s.BorderBottom;
        var left = s.BorderLeft;
        if (centered)
        {
            // Collapsed borders straddle the grid line so neighbouring cells draw over each other.
            x -= left.LayoutWidth / 2;
            y -= top.LayoutWidth / 2;
            w += (left.LayoutWidth + right.LayoutWidth) / 2;
            h += (top.LayoutWidth + bottom.LayoutWidth) / 2;
        }

        if (top.IsVisible)
            AddSide(ops, top, x, y, x + w, y, horizontal: true, inward: 1);
        if (bottom.IsVisible)
            AddSide(ops, bottom, x, y + h, x + w, y + h, horizontal: true, inward: -1);
        if (left.IsVisible)
            AddSide(ops, left, x, y, x, y + h, horizontal: false, inward: 1);
        if (right.IsVisible)
            AddSide(ops, right, x + w, y, x + w, y + h, horizontal: false, inward: -1);
        return ops;
    }

    /// <summary>Adds one border side. (x1,y1)-(x2,y2) is the outer edge; inward is the direction of the box interior.</summary>
    private static void AddSide(List<DrawOp> ops, BorderSide side, double x1, double y1, double x2, double y2, bool horizontal, int inward)
    {
        double width = side.Width;
        switch (side.Style)
        {
            case BorderStyle.Dashed or BorderStyle.Dotted:
                double offset = inward * width / 2;
                ops.Add(horizontal
                    ? new LineOp(x1, y1 + offset, x2, y2 + offset, width, side.Color, side.Style)
                    : new LineOp(x1 + offset, y1, x2 + offset, y2, width, side.Color, side.Style));
                break;
            case BorderStyle.Double when width >= 2.25:
                double third = width / 3;
                AddRect(0, third);
                AddRect(2 * third, third);
                break;
            default:
                AddRect(0, width);
                break;
        }

        void AddRect(double from, double thickness)
        {
            if (horizontal)
            {
                double y = inward > 0 ? y1 + from : y1 - from - thickness;
                ops.Add(new RectOp(x1, y, x2 - x1, thickness, side.Color));
            }
            else
            {
                double x = inward > 0 ? x1 + from : x1 - from - thickness;
                ops.Add(new RectOp(x, y1, thickness, y2 - y1, side.Color));
            }
        }
    }

    /// <summary>Resolved margins, borders and padding of a box.</summary>
    private readonly record struct BoxEdges(
        double MarginTop, double MarginRight, double MarginBottom, double MarginLeft,
        double BorderTop, double BorderRight, double BorderBottom, double BorderLeft,
        double PaddingTop, double PaddingRight, double PaddingBottom, double PaddingLeft)
    {
        public double HorizontalExtras => BorderLeft + BorderRight + PaddingLeft + PaddingRight;

        public bool HasBorder => BorderTop + BorderRight + BorderBottom + BorderLeft > 0;

        public static BoxEdges Of(ComputedStyle s, double containerWidth) => new(
            s.MarginTop.Resolve(containerWidth), s.MarginRight.Resolve(containerWidth),
            s.MarginBottom.Resolve(containerWidth), s.MarginLeft.Resolve(containerWidth),
            s.BorderTop.LayoutWidth, s.BorderRight.LayoutWidth, s.BorderBottom.LayoutWidth, s.BorderLeft.LayoutWidth,
            s.PaddingTop.Resolve(containerWidth), s.PaddingRight.Resolve(containerWidth),
            s.PaddingBottom.Resolve(containerWidth), s.PaddingLeft.Resolve(containerWidth));
    }
}
