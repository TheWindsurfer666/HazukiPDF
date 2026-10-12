using HazukiPdf.Filters;
using HazukiPdf.Objects;
using HazukiPdf.Parsing;

namespace HazukiPdf.Optimization;

/// <summary>The lowest resolution an image is drawn at anywhere in the document, per axis, in dots per inch.</summary>
internal readonly record struct ImageResolution(double DpiX, double DpiY);

/// <summary>
/// Walks page content streams (and the forms, patterns and annotation appearances they use), tracking the
/// transformation matrix, to find how large each image XObject is drawn.
/// </summary>
internal sealed class ImageUsageScanner
{
    private const int MaxDepth = 12;

    private readonly PdfDocument _document;
    private readonly Dictionary<int, ImageResolution> _result = new();
    private readonly HashSet<int> _activeForms = new();

    private ImageUsageScanner(PdfDocument document) => _document = document;

    public static Dictionary<int, ImageResolution> Scan(PdfDocument document)
    {
        var scanner = new ImageUsageScanner(document);
        foreach (var page in document.GetPages())
        {
            var identity = Matrix.Identity;
            if (document.Get(page.Page, "Contents") is { } contents)
            {
                var data = ReadContents(document, contents);
                if (data != null)
                {
                    scanner.Run(data, page.Resources, identity, 0);
                }
            }

            scanner.ScanAnnotations(page.Page);
        }

        return scanner._result;
    }

    private void ScanAnnotations(PdfDictionary page)
    {
        if (_document.Get(page, "Annots") is not PdfArray annots)
        {
            return;
        }

        foreach (var item in annots.Items)
        {
            if (_document.Resolve(item) is not PdfDictionary annot ||
                _document.Get(annot, "AP") is not PdfDictionary ap ||
                _document.Get(annot, "Rect") is not PdfArray rect || rect.Count < 4)
            {
                continue;
            }

            var appearances = new List<PdfObject?>();
            foreach (var entry in ap.Entries)
            {
                var value = _document.Resolve(entry.Value);
                if (value is PdfStream)
                {
                    appearances.Add(entry.Value);
                }
                else if (value is PdfDictionary states)
                {
                    appearances.AddRange(states.Entries.Select(e => (PdfObject?)e.Value));
                }
            }

            foreach (var appearance in appearances)
            {
                if (_document.Resolve(appearance) is not PdfStream form || _document.Get(form, "BBox") is not PdfArray bbox || bbox.Count < 4)
                {
                    continue;
                }

                // Per ISO 32000-1 12.5.5 the transformed bounding box is scaled to fit the annotation rectangle.
                var formMatrix = ReadMatrix(_document.Get(form, "Matrix"));
                var (bx0, by0, bx1, by1) = TransformBox(formMatrix, Numbers(bbox));
                var r = Numbers(rect);
                double bw = bx1 - bx0;
                double bh = by1 - by0;
                if (bw <= 0 || bh <= 0)
                {
                    continue;
                }

                double sx = Math.Abs(r[2] - r[0]) / bw;
                double sy = Math.Abs(r[3] - r[1]) / bh;
                var fit = new Matrix(sx, 0, 0, sy, 0, 0);
                RunForm(appearance, form, null, fit, 0, applyFormMatrix: true);
            }
        }
    }

    private static (double X0, double Y0, double X1, double Y1) TransformBox(Matrix m, double[] box)
    {
        double[] xs = new double[4];
        double[] ys = new double[4];
        int i = 0;
        foreach (var (x, y) in new[] { (box[0], box[1]), (box[2], box[1]), (box[0], box[3]), (box[2], box[3]) })
        {
            xs[i] = (m.A * x) + (m.C * y) + m.E;
            ys[i] = (m.B * x) + (m.D * y) + m.F;
            i++;
        }

        return (xs.Min(), ys.Min(), xs.Max(), ys.Max());
    }

    private double[] Numbers(PdfArray array) =>
        array.Items.Select(x => _document.Resolve(x).AsNumber() ?? 0).Concat(new double[4]).Take(Math.Max(4, array.Count)).ToArray();

    private Matrix ReadMatrix(PdfObject? obj)
    {
        if (obj is PdfArray a && a.Count >= 6)
        {
            var n = Numbers(a);
            return new Matrix(n[0], n[1], n[2], n[3], n[4], n[5]);
        }

        return Matrix.Identity;
    }

    private static byte[]? ReadContents(PdfDocument document, PdfObject contents)
    {
        if (contents is PdfStream single)
        {
            return Decode(document, single);
        }

        if (contents is PdfArray parts)
        {
            using var buffer = new MemoryStream();
            foreach (var part in parts.Items)
            {
                if (document.Resolve(part) is PdfStream s && Decode(document, s) is { } data)
                {
                    buffer.Write(data);
                    buffer.WriteByte((byte)'\n');
                }
            }

            return buffer.ToArray();
        }

        return null;
    }

    public static byte[]? Decode(PdfDocument document, PdfStream stream)
    {
        try
        {
            return StreamFilters.Decode(stream.Data, StreamFilters.GetFilters(stream, document.Resolve), strict: false);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    private void RunForm(PdfObject? reference, PdfStream form, PdfDictionary? parentResources, Matrix ctm, int depth, bool applyFormMatrix)
    {
        int number = reference is PdfReference r ? r.ObjectNumber : -1;
        if (depth > MaxDepth || (number > 0 && !_activeForms.Add(number)))
        {
            return;
        }

        try
        {
            var matrix = applyFormMatrix ? ReadMatrix(_document.Get(form, "Matrix")).Multiply(ctm) : ctm;
            var resources = _document.Get(form, "Resources") as PdfDictionary ?? parentResources;
            var data = Decode(_document, form);
            if (data != null)
            {
                Run(data, resources, matrix, depth + 1);
            }
        }
        finally
        {
            if (number > 0)
            {
                _activeForms.Remove(number);
            }
        }
    }

    private void Run(byte[] content, PdfDictionary? resources, Matrix initial, int depth)
    {
        var lexer = new PdfLexer(content);
        var operands = new List<PdfObject>();
        var stack = new Stack<Matrix>();
        var ctm = initial;

        ScanPatterns(resources, initial, depth);

        while (true)
        {
            var token = lexer.Next();
            if (token.Type == TokenType.EndOfFile)
            {
                break;
            }

            if (token.Type != TokenType.Keyword)
            {
                if (token.Type is TokenType.ArrayEnd or TokenType.DictionaryEnd)
                {
                    continue;
                }

                var operand = lexer.ReadObject(token);
                if (operand != null)
                {
                    operands.Add(operand);
                }

                if (operands.Count > 64)
                {
                    operands.RemoveAt(0);
                }

                continue;
            }

            switch (token.Text)
            {
                case "q":
                    if (stack.Count < 256)
                    {
                        stack.Push(ctm);
                    }

                    break;
                case "Q":
                    if (stack.Count > 0)
                    {
                        ctm = stack.Pop();
                    }

                    break;
                case "cm":
                    if (operands.Count >= 6)
                    {
                        var n = operands.Skip(operands.Count - 6).Select(o => o.AsNumber() ?? 0).ToArray();
                        ctm = new Matrix(n[0], n[1], n[2], n[3], n[4], n[5]).Multiply(ctm);
                    }

                    break;
                case "Do":
                    if (operands.Count >= 1 && operands[^1] is PdfName name)
                    {
                        PaintXObject(name.Value, resources, ctm, depth);
                    }

                    break;
                case "BI":
                    SkipInlineImage(lexer);
                    break;
            }

            operands.Clear();
        }
    }

    private void ScanPatterns(PdfDictionary? resources, Matrix ctm, int depth)
    {
        if (resources == null || _document.Get(resources, "Pattern") is not PdfDictionary patterns)
        {
            return;
        }

        foreach (var entry in patterns.Entries)
        {
            if (_document.Resolve(entry.Value) is PdfStream pattern && StreamFilters.GetInt(pattern, "PatternType", 0) == 1)
            {
                RunForm(entry.Value, pattern, resources, ctm, depth, applyFormMatrix: true);
            }
        }
    }

    private void PaintXObject(string name, PdfDictionary? resources, Matrix ctm, int depth)
    {
        if (resources == null || _document.Get(resources, "XObject") is not PdfDictionary xobjects)
        {
            return;
        }

        var reference = xobjects.Get(name);
        if (_document.Resolve(reference) is not PdfStream xobject)
        {
            return;
        }

        string? subtype = xobject.GetName("Subtype");
        if (subtype == "Form")
        {
            RunForm(reference, xobject, resources, ctm, depth, applyFormMatrix: true);
            return;
        }

        if (subtype != "Image" || reference is not PdfReference imageRef)
        {
            return;
        }

        double width = _document.Get(xobject, "Width").AsNumber() ?? 0;
        double height = _document.Get(xobject, "Height").AsNumber() ?? 0;
        double shownWidth = Math.Sqrt((ctm.A * ctm.A) + (ctm.B * ctm.B)) / 72.0;
        double shownHeight = Math.Sqrt((ctm.C * ctm.C) + (ctm.D * ctm.D)) / 72.0;
        if (width <= 0 || height <= 0 || shownWidth < 1e-6 || shownHeight < 1e-6)
        {
            return;
        }

        var resolution = new ImageResolution(width / shownWidth, height / shownHeight);
        if (_result.TryGetValue(imageRef.ObjectNumber, out var existing))
        {
            resolution = new ImageResolution(Math.Min(existing.DpiX, resolution.DpiX), Math.Min(existing.DpiY, resolution.DpiY));
        }

        _result[imageRef.ObjectNumber] = resolution;
    }

    private static void SkipInlineImage(PdfLexer lexer)
    {
        // Skip the dictionary up to "ID", then the binary data up to a whitespace-delimited "EI".
        while (true)
        {
            var token = lexer.Next();
            if (token.Type == TokenType.EndOfFile || (token.Type == TokenType.Keyword && token.Text == "ID"))
            {
                break;
            }
        }

        var data = lexer.Data;
        int p = lexer.Position + 1;
        while (p + 1 < lexer.End)
        {
            if (data[p] == 'E' && data[p + 1] == 'I' && PdfLexer.IsWhitespace(data[p - 1]) &&
                (p + 2 >= lexer.End || PdfLexer.IsWhitespace(data[p + 2]) || PdfLexer.IsDelimiter(data[p + 2])))
            {
                lexer.Position = p + 2;
                return;
            }

            p++;
        }

        lexer.Position = lexer.End;
    }
}

internal readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
{
    public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

    /// <summary>Returns this × other (apply this first, then other), as PDF concatenates matrices.</summary>
    public Matrix Multiply(Matrix o) => new(
        (A * o.A) + (B * o.C),
        (A * o.B) + (B * o.D),
        (C * o.A) + (D * o.C),
        (C * o.B) + (D * o.D),
        (E * o.A) + (F * o.C) + o.E,
        (E * o.B) + (F * o.D) + o.F);
}
