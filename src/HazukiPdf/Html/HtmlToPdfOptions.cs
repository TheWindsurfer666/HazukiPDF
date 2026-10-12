namespace HazukiPdf.Html;

/// <summary>A page size in PDF points (1/72 inch).</summary>
/// <param name="Width">Page width in points.</param>
/// <param name="Height">Page height in points.</param>
public readonly record struct PageSize(double Width, double Height)
{
    /// <summary>ISO A3, 297 × 420 mm.</summary>
    public static PageSize A3 => FromMillimeters(297, 420);

    /// <summary>ISO A4, 210 × 297 mm.</summary>
    public static PageSize A4 => FromMillimeters(210, 297);

    /// <summary>ISO A5, 148 × 210 mm.</summary>
    public static PageSize A5 => FromMillimeters(148, 210);

    /// <summary>US Letter, 8.5 × 11 in.</summary>
    public static PageSize Letter => new(612, 792);

    /// <summary>US Legal, 8.5 × 14 in.</summary>
    public static PageSize Legal => new(612, 1008);

    /// <summary>Creates a page size from millimeters.</summary>
    public static PageSize FromMillimeters(double width, double height) => new(width * 72 / 25.4, height * 72 / 25.4);

    /// <summary>Creates a page size from inches.</summary>
    public static PageSize FromInches(double width, double height) => new(width * 72, height * 72);

    /// <summary>The same size with width and height swapped.</summary>
    public PageSize Rotate() => new(Height, Width);
}

/// <summary>Page margins in PDF points (1/72 inch).</summary>
/// <param name="Top">Top margin in points.</param>
/// <param name="Right">Right margin in points.</param>
/// <param name="Bottom">Bottom margin in points.</param>
/// <param name="Left">Left margin in points.</param>
public readonly record struct PageMargins(double Top, double Right, double Bottom, double Left)
{
    /// <summary>The same margin on all four sides.</summary>
    public static PageMargins Uniform(double points) => new(points, points, points, points);

    /// <summary>Creates margins from millimeters.</summary>
    public static PageMargins FromMillimeters(double top, double right, double bottom, double left) =>
        new(top * 72 / 25.4, right * 72 / 25.4, bottom * 72 / 25.4, left * 72 / 25.4);
}

/// <summary>Settings for <see cref="HtmlToPdfConverter"/>.</summary>
public sealed class HtmlToPdfOptions
{
    /// <summary>Page size. Defaults to A4. A CSS <c>@page { size: ... }</c> rule overrides it.</summary>
    public PageSize PageSize { get; set; } = PageSize.A4;

    /// <summary>Use the page size in landscape orientation.</summary>
    public bool Landscape { get; set; }

    /// <summary>Page margins. Defaults to 0.5 inch. A CSS <c>@page { margin: ... }</c> rule overrides it.</summary>
    public PageMargins Margins { get; set; } = PageMargins.Uniform(36);

    /// <summary>
    /// Directory that relative image paths resolve against. When null, only <c>data:</c> URIs and
    /// the <see cref="ResourceResolver"/> provide images. Files outside this directory are never read.
    /// </summary>
    public string? BaseDirectory { get; set; }

    /// <summary>
    /// Optional callback that returns the bytes for an image source (for example to fetch
    /// <c>https://</c> URLs or read from an embedded resource). Return null to fall back to the
    /// built-in handling.
    /// </summary>
    public Func<string, byte[]?>? ResourceResolver { get; set; }

    /// <summary>Extra CSS applied after the document's own style sheets.</summary>
    public string? UserStyleSheet { get; set; }

    /// <summary>Base font size in points for the root element. Defaults to 12pt (16px).</summary>
    public double DefaultFontSize { get; set; } = 12;

    /// <summary>
    /// Font family used when the document does not set one: a CSS generic family such as
    /// <c>sans-serif</c> (Helvetica), <c>serif</c> (Times) or <c>monospace</c> (Courier).
    /// </summary>
    public string DefaultFontFamily { get; set; } = "sans-serif";

    /// <summary>Document title. Defaults to the HTML &lt;title&gt;.</summary>
    public string? Title { get; set; }

    /// <summary>Document author written to the PDF metadata.</summary>
    public string? Author { get; set; }

    /// <summary>Creation date written to the PDF metadata. Defaults to the current time.</summary>
    public DateTimeOffset? CreationDate { get; set; }

    /// <summary>Compress page content with Flate. Disable to inspect the raw content streams.</summary>
    public bool CompressContent { get; set; } = true;
}
