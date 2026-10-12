using System.Globalization;
using System.Text;
using HazukiPdf.Html.Css;
using HazukiPdf.Html.Fonts;
using HazukiPdf.Html.Images;
using HazukiPdf.Html.Layout;
using HazukiPdf.Filters;
using HazukiPdf.Objects;
using HazukiPdf.Writing;
using static HazukiPdf.Html.Pdf.PdfSyntax;

namespace HazukiPdf.Html.Pdf;

/// <summary>Cuts a <see cref="DisplayList"/> into pages and writes them as a PDF document.</summary>
internal sealed class PdfRenderer
{
    private readonly PdfDocument _document = new(new Dictionary<int, PdfObject>(), new PdfDictionary(), "1.7");
    private int _objectCount;
    private readonly Dictionary<StandardFont, (string Name, PdfReference Ref)> _fonts = [];
    private readonly Dictionary<ImageData, (string Name, PdfReference Ref)> _images = [];
    private readonly double _pageWidth;
    private readonly double _pageHeight;
    private readonly PageMargins _margins;
    private readonly double _contentHeight;

    public PdfRenderer(double pageWidth, double pageHeight, PageMargins margins)
    {
        _pageWidth = pageWidth;
        _pageHeight = pageHeight;
        _margins = margins;
        _contentHeight = pageHeight - margins.Top - margins.Bottom;
    }

    public int PageCount { get; private set; }

    public void Render(DisplayList list, Stream output, Rgb? pageBackground, DocumentInfo info, bool compress)
    {
        // Assign every operation to its page; rectangles that cross a page cut are split.
        var pages = new List<List<DrawOp>>();
        foreach (var op in list.Ops)
        {
            if (op is RectOp rect)
            {
                foreach (var (page, piece) in SplitRect(rect))
                    PageList(page).Add(piece);
            }
            else
            {
                PageList(PageOf(op.Top)).Add(op);
            }
        }
        if (pages.Count == 0)
            pages.Add([]);
        PageCount = pages.Count;

        var pagesDict = new PdfDictionary();
        var pagesRef = Add(pagesDict);
        var pageDicts = pages.Select(_ => new PdfDictionary()).ToList();
        var pageRefs = pageDicts.Select(d => Add(d)).ToList();
        var resources = new PdfDictionary();
        var resourcesRef = Add(resources);

        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.MD5);
        for (int i = 0; i < pages.Count; i++)
        {
            var annotations = new PdfArray();
            byte[] content = BuildContent(pages[i], i, pageBackground, list, pageRefs, annotations);
            hash.AppendData(content);

            var page = pageDicts[i];
            page.Set("Type", PdfName.Get("Page"));
            page.Set("Parent", pagesRef);
            page.Set("MediaBox", new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfReal(_pageWidth), new PdfReal(_pageHeight)]));
            page.Set("Resources", resourcesRef);
            page.Set("Contents", AddStream(new PdfDictionary(), content, compress));
            if (annotations.Count > 0)
                page.Set("Annots", annotations);
        }

        pagesDict.Set("Type", PdfName.Get("Pages"));
        pagesDict.Set("Kids", new PdfArray(pageRefs));
        pagesDict.Set("Count", new PdfInteger(pages.Count));

        resources.Set("ProcSet", new PdfArray(new[] { "PDF", "Text", "ImageB", "ImageC" }.Select(n => (PdfObject)PdfName.Get(n))));
        if (_fonts.Count > 0)
        {
            var fonts = new PdfDictionary();
            foreach (var (name, reference) in _fonts.Values)
                fonts.Set(name, reference);
            resources.Set("Font", fonts);
        }
        if (_images.Count > 0)
        {
            var xobjects = new PdfDictionary();
            foreach (var (name, reference) in _images.Values)
                xobjects.Set(name, reference);
            resources.Set("XObject", xobjects);
        }

        var catalog = new PdfDictionary();
        catalog.Set("Type", PdfName.Get("Catalog"));
        catalog.Set("Pages", pagesRef);
        _document.Trailer.Set("Root", Add(catalog));
        _document.Trailer.Set("Info", Add(info.ToDictionary()));
        var id = new PdfString(hash.GetHashAndReset(), isHex: true);
        _document.Trailer.Set("ID", new PdfArray([id, id]));

        byte[] file = PdfWriter.Write(_document, new PdfWriterOptions { UseObjectStreams = true, RemoveUnusedObjects = false });
        output.Write(file);

        List<DrawOp> PageList(int page)
        {
            while (pages.Count <= page)
                pages.Add([]);
            return pages[page];
        }
    }

    private int PageOf(double y) => Math.Max(0, (int)Math.Floor(y / _contentHeight + 1e-9));

    private IEnumerable<(int Page, RectOp Piece)> SplitRect(RectOp rect)
    {
        double y = rect.Y, bottom = rect.Y + rect.Height;
        while (y < bottom - 0.001)
        {
            int page = PageOf(y);
            double pageBottom = (page + 1) * _contentHeight;
            double end = Math.Min(bottom, pageBottom);
            if (end - y > 0.001)
                yield return (page, rect with { Y = y, Height = end - y });
            y = end;
        }
    }

    /// <summary>Converts a flow y position on the given page to PDF user space (origin at bottom left).</summary>
    private double PdfY(double y, int page) => _pageHeight - _margins.Top - (y - page * _contentHeight);

    private double PdfX(double x) => _margins.Left + x;

    /// <summary>Adds an indirect object. Numbers are handed out here because documents can hold many thousands of link annotations.</summary>
    private PdfReference Add(PdfObject obj)
    {
        _document.Objects[++_objectCount] = obj;
        return new PdfReference(_objectCount, 0);
    }

    private PdfReference AddStream(PdfDictionary dictionary, byte[] data, bool compress)
    {
        if (compress)
        {
            data = StreamFilters.Deflate(data);
            dictionary.Set("Filter", PdfName.Get("FlateDecode"));
        }
        return Add(new PdfStream(dictionary, data));
    }

    private byte[] BuildContent(List<DrawOp> ops, int page, Rgb? background, DisplayList list, List<PdfReference> pageRefs,
        PdfArray annotations)
    {
        var sb = new StringBuilder();
        if (background is { } bg)
            sb.Append($"{Color(bg)} rg 0 0 {Num(_pageWidth)} {Num(_pageHeight)} re f\n");

        double wordSpacing = 0, charSpacing = 0;
        foreach (var op in ops)
        {
            switch (op)
            {
                case RectOp r:
                    sb.Append($"{Color(r.Color)} rg {Num(PdfX(r.X))} {Num(PdfY(r.Y + r.Height, page))} {Num(r.Width)} {Num(r.Height)} re f\n");
                    break;

                case TextOp t:
                    string font = FontName(t.Font);
                    sb.Append($"BT /{font} {Num(t.FontSize)} Tf {Color(t.Color)} rg ");
                    if (Math.Abs(t.WordSpacing - wordSpacing) > 0.0005)
                    {
                        wordSpacing = t.WordSpacing;
                        sb.Append($"{Num(wordSpacing)} Tw ");
                    }
                    if (Math.Abs(t.CharSpacing - charSpacing) > 0.0005)
                    {
                        charSpacing = t.CharSpacing;
                        sb.Append($"{Num(charSpacing)} Tc ");
                    }
                    sb.Append($"{Num(PdfX(t.X))} {Num(PdfY(t.Baseline, page))} Td ");
                    sb.Append(LiteralString(WinAnsiEncoding.Encode(t.Text)));
                    sb.Append(" Tj ET\n");
                    break;

                case LineOp l:
                    string dash = l.Style == BorderStyle.Dotted
                        ? $"[0 {Num(l.LineWidth * 2)}] 0 d 1 J"
                        : $"[{Num(l.LineWidth * 3)} {Num(l.LineWidth * 3)}] 0 d 0 J";
                    sb.Append($"q {Num(l.LineWidth)} w {Color(l.Color)} RG {dash} ");
                    sb.Append($"{Num(PdfX(l.X1))} {Num(PdfY(l.Y1, page))} m {Num(PdfX(l.X2))} {Num(PdfY(l.Y2, page))} l S Q\n");
                    break;

                case ImageOp im:
                    string image = ImageName(im.Image);
                    sb.Append($"q {Num(im.Width)} 0 0 {Num(im.Height)} {Num(PdfX(im.X))} {Num(PdfY(im.Y + im.Height, page))} cm /{image} Do Q\n");
                    break;

                case ShapeOp shape:
                    AppendShape(sb, shape, page);
                    break;

                case LinkOp link:
                    if (BuildLink(link, page, list, pageRefs) is { } annotation)
                        annotations.Add(Add(annotation));
                    break;
            }
        }
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private void AppendShape(StringBuilder sb, ShapeOp shape, int page)
    {
        double cx = PdfX(shape.CenterX), cy = PdfY(shape.CenterY, page), r = shape.Radius;
        if (shape.Shape == MarkerShape.Square)
        {
            double half = r * 0.9;
            sb.Append($"{Color(shape.Color)} rg {Num(cx - half)} {Num(cy - half)} {Num(half * 2)} {Num(half * 2)} re f\n");
            return;
        }

        bool outline = shape.Shape == MarkerShape.Circle;
        double radius = outline ? r * 0.85 : r;
        const double k = 0.5523; // control point offset for a circle drawn with four Béziers
        double o = radius * k;
        sb.Append(outline ? $"q {Num(r * 0.3)} w {Color(shape.Color)} RG " : $"{Color(shape.Color)} rg ");
        sb.Append($"{Num(cx + radius)} {Num(cy)} m ");
        sb.Append($"{Num(cx + radius)} {Num(cy + o)} {Num(cx + o)} {Num(cy + radius)} {Num(cx)} {Num(cy + radius)} c ");
        sb.Append($"{Num(cx - o)} {Num(cy + radius)} {Num(cx - radius)} {Num(cy + o)} {Num(cx - radius)} {Num(cy)} c ");
        sb.Append($"{Num(cx - radius)} {Num(cy - o)} {Num(cx - o)} {Num(cy - radius)} {Num(cx)} {Num(cy - radius)} c ");
        sb.Append($"{Num(cx + o)} {Num(cy - radius)} {Num(cx + radius)} {Num(cy - o)} {Num(cx + radius)} {Num(cy)} c ");
        sb.Append(outline ? "S Q\n" : "f\n");
    }

    private PdfDictionary? BuildLink(LinkOp link, int page, DisplayList list, List<PdfReference> pageRefs)
    {
        var annotation = new PdfDictionary();
        annotation.Set("Type", PdfName.Get("Annot"));
        annotation.Set("Subtype", PdfName.Get("Link"));
        annotation.Set("Rect", Numbers(PdfX(link.X), PdfY(link.Y + link.Height, page), PdfX(link.X + link.Width), PdfY(link.Y, page)));
        annotation.Set("Border", Numbers(0, 0, 0));
        string target = link.Target;

        if (target.StartsWith('#'))
        {
            string id = Uri.UnescapeDataString(target[1..]);
            if (!list.Anchors.TryGetValue(id, out var anchor))
                return null;
            int targetPage = Math.Min(PageOf(anchor.Y), pageRefs.Count - 1);
            annotation.Set("Dest", new PdfArray(
            [
                pageRefs[targetPage], PdfName.Get("XYZ"), new PdfReal(PdfX(anchor.X)), new PdfReal(PdfY(anchor.Y, targetPage)), PdfNull.Instance,
            ]));
            return annotation;
        }

        // Only well-formed absolute URIs with harmless schemes become link actions.
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "mailto" or "ftp" or "tel"))
            return null;
        var action = new PdfDictionary();
        action.Set("S", PdfName.Get("URI"));
        action.Set("URI", new PdfString(Encoding.ASCII.GetBytes(uri.AbsoluteUri)));
        annotation.Set("A", action);
        return annotation;
    }

    private static PdfArray Numbers(params double[] values) =>
        new(values.Select(v => (PdfObject)new PdfReal(Math.Round(v, 3))));

    private string FontName(StandardFont font)
    {
        if (!_fonts.TryGetValue(font, out var entry))
        {
            string name = "F" + (_fonts.Count + 1).ToString(CultureInfo.InvariantCulture);
            var dict = new PdfDictionary();
            dict.Set("Type", PdfName.Get("Font"));
            dict.Set("Subtype", PdfName.Get("Type1"));
            dict.Set("BaseFont", PdfName.Get(font.BaseFont));
            dict.Set("Encoding", PdfName.Get("WinAnsiEncoding"));
            entry = (name, Add(dict));
            _fonts[font] = entry;
        }
        return entry.Name;
    }

    private string ImageName(ImageData image)
    {
        if (_images.TryGetValue(image, out var entry))
            return entry.Name;

        string name = "Im" + (_images.Count + 1).ToString(CultureInfo.InvariantCulture);
        var dict = ImageDictionary(image.Width, image.Height, image.ColorSpace);
        PdfReference reference;
        if (image.IsJpeg)
        {
            if (image.InvertedCmyk)
                dict.Set("Decode", Numbers(1, 0, 1, 0, 1, 0, 1, 0));
            dict.Set("Filter", PdfName.Get("DCTDecode"));
            reference = Add(new PdfStream(dict, image.Data));
        }
        else
        {
            if (image.Alpha is not null)
                dict.Set("SMask", AddStream(ImageDictionary(image.Width, image.Height, "DeviceGray"), image.Alpha, compress: true));
            reference = AddStream(dict, image.Data, compress: true);
        }
        _images[image] = (name, reference);
        return name;
    }

    private static PdfDictionary ImageDictionary(int width, int height, string colorSpace)
    {
        var dict = new PdfDictionary();
        dict.Set("Type", PdfName.Get("XObject"));
        dict.Set("Subtype", PdfName.Get("Image"));
        dict.Set("Width", new PdfInteger(width));
        dict.Set("Height", new PdfInteger(height));
        dict.Set("BitsPerComponent", new PdfInteger(8));
        dict.Set("ColorSpace", PdfName.Get(colorSpace));
        return dict;
    }

    private static string Color(Rgb c) => $"{Num(c.R / 255.0)} {Num(c.G / 255.0)} {Num(c.B / 255.0)}";
}

/// <summary>Values for the document information dictionary.</summary>
internal sealed record DocumentInfo(string? Title, string? Author, DateTimeOffset CreationDate)
{
    public PdfDictionary ToDictionary()
    {
        var dict = new PdfDictionary();
        dict.Set("Producer", new PdfString(Encoding.ASCII.GetBytes("HazukiPdf")));
        if (!string.IsNullOrWhiteSpace(Title))
            dict.Set("Title", new PdfString(PdfSyntax.TextStringBytes(Title.Trim())));
        if (!string.IsNullOrWhiteSpace(Author))
            dict.Set("Author", new PdfString(PdfSyntax.TextStringBytes(Author.Trim())));
        var offset = CreationDate.Offset;
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        string date = "D:" + CreationDate.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) +
                      $"{sign}{Math.Abs(offset.Hours):D2}'{Math.Abs(offset.Minutes):D2}'";
        dict.Set("CreationDate", new PdfString(Encoding.ASCII.GetBytes(date)));
        return dict;
    }
}
