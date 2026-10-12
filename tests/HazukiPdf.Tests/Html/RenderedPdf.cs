using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HazukiPdf.Filters;
using HazukiPdf.Html;
using HazukiPdf.Html.Fonts;
using HazukiPdf.Objects;
using HazukiPdf.Parsing;

namespace HazukiPdf.Tests.Html;

/// <summary>A text-showing operation found in a page's content stream.</summary>
internal sealed record ShownText(string Font, double Size, double X, double Y, string Text);

/// <summary>Converts HTML and reads the result back with the library's own PDF parser.</summary>
internal sealed partial class RenderedPdf
{
    private RenderedPdf(byte[] bytes)
    {
        Bytes = bytes;
        Document = PdfReader.Read(bytes);
        Pages = Document.GetPages();
    }

    public byte[] Bytes { get; }

    public PdfDocument Document { get; }

    public List<PdfPageInfo> Pages { get; }

    public static RenderedPdf From(string html, HtmlToPdfOptions? options = null)
    {
        options ??= new HtmlToPdfOptions();
        options.CreationDate ??= new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return new RenderedPdf(HtmlToPdfConverter.Convert(html, options));
    }

    public PdfDictionary Page(int index) => Pages[index].Page;

    /// <summary>The decoded content stream of a page.</summary>
    public string Content(int page)
    {
        var stream = (PdfStream)Document.Resolve(Page(page).Get("Contents"))!;
        var data = StreamFilters.Decode(stream.Data, StreamFilters.GetFilters(stream, Document.Resolve), strict: true);
        return Encoding.Latin1.GetString(data);
    }

    public List<ShownText> Texts(int page)
    {
        var result = new List<ShownText>();
        foreach (Match m in TextOperation().Matches(Content(page)))
        {
            result.Add(new ShownText(
                BaseFont(page, m.Groups["font"].Value),
                double.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
                DecodeWinAnsi(Unescape(m.Groups["text"].Value))));
        }
        return result;
    }

    /// <summary>All text on all pages, one entry per text operation, joined with spaces.</summary>
    public string AllText() => string.Join(" ", Enumerable.Range(0, Pages.Count).SelectMany(Texts).Select(t => t.Text.Trim()));

    public string BaseFont(int page, string resourceName)
    {
        var resources = (PdfDictionary)Document.Resolve(Page(page).Get("Resources"))!;
        var fonts = (PdfDictionary)Document.Resolve(resources.Get("Font"))!;
        var font = (PdfDictionary)Document.Resolve(fonts.Get(resourceName))!;
        return font.GetName("BaseFont")!;
    }

    public List<PdfStream> Images()
    {
        var resources = (PdfDictionary)Document.Resolve(Page(0).Get("Resources"))!;
        if (Document.Resolve(resources.Get("XObject")) is not PdfDictionary xobjects)
            return [];
        return xobjects.Entries.Select(e => (PdfStream)Document.Resolve(e.Value)!).ToList();
    }

    public List<PdfDictionary> Annotations(int page) =>
        Document.Resolve(Page(page).Get("Annots")) is PdfArray annots
            ? annots.Items.Select(a => (PdfDictionary)Document.Resolve(a)!).ToList()
            : [];

    public PdfDictionary Info => (PdfDictionary)Document.Resolve(Document.Trailer.Get("Info"))!;

    public (double Width, double Height) MediaBox(int page)
    {
        var box = (PdfArray)Document.Resolve(Page(page).Get("MediaBox"))!;
        return (box[2].AsNumber()!.Value, box[3].AsNumber()!.Value);
    }

    private static string Unescape(string literal)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < literal.Length; i++)
        {
            char c = literal[i];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            char next = literal[++i];
            if (next is >= '0' and <= '7')
            {
                sb.Append((char)Convert.ToInt32(literal.Substring(i, 3), 8));
                i += 2;
            }
            else
            {
                sb.Append(next);
            }
        }
        return sb.ToString();
    }

    private static string DecodeWinAnsi(string bytes)
    {
        var sb = new StringBuilder();
        foreach (char b in bytes)
        {
            char decoded = b;
            if (b is >= '\u0080' and <= '\u009F')
            {
                for (char c = 'Ā'; c < '￿'; c++)
                {
                    if (WinAnsiEncoding.Encode(c) == b && c is not ('-' or ' '))
                    {
                        decoded = c;
                        break;
                    }
                }
            }
            sb.Append(decoded);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"BT /(?<font>F\d+) (?<size>[\d.]+) Tf .*? (?<x>-?[\d.]+) (?<y>-?[\d.]+) Td \((?<text>(?:\\.|[^\\)])*)\) Tj ET")]
    private static partial Regex TextOperation();
}
