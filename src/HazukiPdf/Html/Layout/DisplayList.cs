using HazukiPdf.Html.Css;
using HazukiPdf.Html.Fonts;
using HazukiPdf.Html.Images;

namespace HazukiPdf.Html.Layout;

/// <summary>
/// A drawing operation in layout coordinates: x is measured from the left edge of the page
/// content area, y runs downward continuously through all pages (page n starts at n × content height).
/// </summary>
internal abstract record DrawOp
{
    /// <summary>The top edge, which decides the page the operation is drawn on.</summary>
    public abstract double Top { get; }
}

internal sealed record RectOp(double X, double Y, double Width, double Height, Rgb Color) : DrawOp
{
    public override double Top => Y;
}

/// <summary>A dashed or dotted line, used for borders that are not solid.</summary>
internal sealed record LineOp(double X1, double Y1, double X2, double Y2, double LineWidth, Rgb Color, BorderStyle Style) : DrawOp
{
    public override double Top => Math.Min(Y1, Y2) - LineWidth / 2;
}

internal sealed record TextOp(
    double X, double Baseline, StandardFont Font, double FontSize, Rgb Color, string Text,
    double WordSpacing, double CharSpacing) : DrawOp
{
    public override double Top => Baseline - Font.Ascent * FontSize / 1000;
}

internal sealed record ImageOp(double X, double Y, double Width, double Height, ImageData Image) : DrawOp
{
    public override double Top => Y;
}

internal sealed record ShapeOp(double CenterX, double CenterY, double Radius, MarkerShape Shape, Rgb Color) : DrawOp
{
    public override double Top => CenterY - Radius;
}

/// <summary>A clickable area. <see cref="Target"/> is a URI or "#id" for an internal link.</summary>
internal sealed record LinkOp(double X, double Y, double Width, double Height, string Target) : DrawOp
{
    public override double Top => Y;
}

/// <summary>The result of layout: drawing operations plus named anchor positions.</summary>
internal sealed class DisplayList
{
    public List<DrawOp> Ops { get; } = [];

    public Dictionary<string, (double X, double Y)> Anchors { get; } = new(StringComparer.Ordinal);

    public void Add(DrawOp op) => Ops.Add(op);
}
