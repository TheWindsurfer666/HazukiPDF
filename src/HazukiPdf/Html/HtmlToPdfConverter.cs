using HazukiPdf.Html.Css;
using HazukiPdf.Html.Dom;
using HazukiPdf.Html.Images;
using HazukiPdf.Html.Layout;
using HazukiPdf.Html.Pdf;

namespace HazukiPdf.Html;

/// <summary>
/// Converts HTML with CSS to PDF using only the .NET base class library. Text is set in the
/// standard 14 PDF fonts, so the output needs no embedded fonts. See the README for the supported
/// subset of HTML and CSS.
/// </summary>
public static class HtmlToPdfConverter
{
    /// <summary>Converts an HTML document to PDF and returns the file contents.</summary>
    public static byte[] Convert(string html, HtmlToPdfOptions? options = null)
    {
        using var output = new MemoryStream();
        Convert(html, output, options);
        return output.ToArray();
    }

    /// <summary>Converts an HTML document to PDF, writing the file to <paramref name="output"/>.</summary>
    /// <returns>The number of pages written.</returns>
    public static int Convert(string html, Stream output, HtmlToPdfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(output);
        options ??= new HtmlToPdfOptions();

        var document = HtmlParser.Parse(html);
        EnsureBody(document);

        // Style sheets: <style> elements in document order, then the caller's extra CSS.
        int order = 0;
        var sheets = new List<StyleSheet>();
        foreach (var style in document.Descendants().Where(e => e.TagName == "style"))
        {
            string media = (style.GetAttribute("media") ?? "all").ToLowerInvariant();
            if (media.Contains("all") || media.Contains("print") || media.Trim().Length == 0)
                sheets.Add(StyleSheet.Parse(style.TextContent, ref order));
        }
        if (!string.IsNullOrWhiteSpace(options.UserStyleSheet))
            sheets.Add(StyleSheet.Parse(options.UserStyleSheet, ref order));

        var (pageSize, margins) = ResolvePage(options, sheets);
        double contentWidth = pageSize.Width - margins.Left - margins.Right;
        double contentHeight = pageSize.Height - margins.Top - margins.Bottom;
        if (contentWidth < 10 || contentHeight < 10)
            throw new ArgumentException("The page margins leave no room for content.", nameof(options));

        var resolver = new StyleResolver(sheets, options.DefaultFontSize);
        var rootStyle = new ComputedStyle
        {
            Display = Display.Block,
            FontSize = options.DefaultFontSize,
            FontFamily = StyleResolver.ParseFontFamily(options.DefaultFontFamily, Fonts.FontFamily.Helvetica),
        };
        foreach (var child in document.ChildElements)
            resolver.Resolve(child, rootStyle);

        var builder = new BoxBuilder(resolver, new ImageLoader(options));
        var root = builder.BuildRoot(document, rootStyle);

        var displayList = new LayoutEngine(contentHeight).Layout(root, contentWidth);

        string? title = options.Title ?? document.Descendants().FirstOrDefault(e => e.TagName == "title")?.TextContent;
        var info = new DocumentInfo(title is null ? null : CollapseWhitespace(title), options.Author,
            options.CreationDate ?? DateTimeOffset.Now);

        var renderer = new PdfRenderer(pageSize.Width, pageSize.Height, margins);
        renderer.Render(displayList, output, PageBackground(document, resolver), info, options.CompressContent);
        return renderer.PageCount;
    }

    /// <summary>Converts an HTML file. Relative image paths resolve against the file's directory.</summary>
    /// <returns>The number of pages written.</returns>
    public static int ConvertFile(string htmlPath, string pdfPath, HtmlToPdfOptions? options = null)
    {
        options ??= new HtmlToPdfOptions();
        options.BaseDirectory ??= Path.GetDirectoryName(Path.GetFullPath(htmlPath));
        string html = File.ReadAllText(htmlPath);
        using var output = File.Create(pdfPath);
        return Convert(html, output, options);
    }

    private static (PageSize Size, PageMargins Margins) ResolvePage(HtmlToPdfOptions options, List<StyleSheet> sheets)
    {
        var size = options.PageSize;
        bool landscape = options.Landscape;
        var m = options.Margins;
        double top = m.Top, right = m.Right, bottom = m.Bottom, left = m.Left;

        foreach (var decl in sheets.SelectMany(s => s.PageDeclarations))
        {
            string value = decl.Value.Trim().ToLowerInvariant();
            switch (decl.Property)
            {
                case "size":
                    foreach (string token in CssValues.SplitWhitespace(value))
                    {
                        if (token == "landscape")
                            landscape = true;
                        else if (token == "portrait")
                            landscape = false;
                        else if (NamedSize(token) is { } named)
                            size = named;
                    }
                    var lengths = CssValues.SplitWhitespace(value)
                        .Select(t => CssValues.TryParseLength(t, 12, 12, 0, out double v) && !CssValues.IsPercentage(t) ? v : (double?)null)
                        .Where(v => v is > 0)
                        .Select(v => v!.Value)
                        .ToList();
                    if (lengths.Count == 1)
                        size = new PageSize(lengths[0], lengths[0]);
                    else if (lengths.Count >= 2)
                        size = new PageSize(lengths[0], lengths[1]);
                    break;
                case "margin-top": top = PageLength(value, top, size.Height); break;
                case "margin-right": right = PageLength(value, right, size.Width); break;
                case "margin-bottom": bottom = PageLength(value, bottom, size.Height); break;
                case "margin-left": left = PageLength(value, left, size.Width); break;
            }
        }

        if (landscape && size.Height > size.Width)
            size = size.Rotate();
        return (size, new PageMargins(top, right, bottom, left));

        static double PageLength(string value, double fallback, double percentBase) =>
            CssValues.TryParseLength(value, 12, 12, percentBase, out double v) && v >= 0 ? v : fallback;

        static PageSize? NamedSize(string token) => token switch
        {
            "a3" => PageSize.A3,
            "a4" => PageSize.A4,
            "a5" => PageSize.A5,
            "letter" => PageSize.Letter,
            "legal" => PageSize.Legal,
            _ => null,
        };
    }

    /// <summary>Like browsers, a background on the root or body element paints the whole page.</summary>
    private static Rgb? PageBackground(HtmlElement document, StyleResolver resolver)
    {
        var html = document.ChildElements.FirstOrDefault(e => e.TagName == "html");
        if (html is not null && resolver.GetStyle(html).BackgroundColor is { } rootBackground)
            return rootBackground;
        var body = (html ?? document).ChildElements.FirstOrDefault(e => e.TagName == "body");
        return body is null ? null : resolver.GetStyle(body).BackgroundColor;
    }

    /// <summary>
    /// Browsers always create a &lt;body&gt;; content written without one gets one here, so its
    /// default margin and any <c>body</c> rules apply.
    /// </summary>
    private static void EnsureBody(HtmlElement document)
    {
        if (document.Descendants().Any(e => e.TagName == "body"))
            return;
        var container = document.ChildElements.FirstOrDefault(e => e.TagName == "html") ?? document;
        var body = new HtmlElement("body");
        foreach (var node in container.Children.ToList())
        {
            if (node is HtmlElement { TagName: "head" or "title" or "style" or "meta" or "link" or "base" })
                continue;
            container.Children.Remove(node);
            body.AppendChild(node);
        }
        container.AppendChild(body);
    }

    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
