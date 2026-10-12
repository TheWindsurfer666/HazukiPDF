using HazukiPdf.Objects;
using HazukiPdf.Parsing;

namespace HazukiPdf.Optimization;

/// <summary>
/// Removes unused glyphs from embedded TrueType fonts. Some PDF producers (for example Microsoft Print to PDF)
/// embed complete fonts of several hundred kilobytes for a few lines of text.
/// </summary>
/// <remarks>
/// Only composite fonts that address glyphs directly (Type0, Identity-H/V encoding, CIDFontType2 with an
/// identity CIDToGIDMap) are subset, because for those the codes in the text are the glyph IDs. All content
/// streams are scanned for the glyphs shown; when anything cannot be read, or a font may be used to fill in
/// form fields later, the font is left alone.
/// </remarks>
internal static class FontSubsetter
{
    public static int Run(PdfDocument document)
    {
        var usage = FontUsageScanner.Scan(document);
        if (usage == null)
        {
            return 0;
        }

        // Group eligible fonts by their font program; a program shared with any other kind of font is left alone.
        var users = new Dictionary<PdfStream, List<PdfDictionary>>(ReferenceEqualityComparer.Instance);
        var eligibleDescriptors = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var obj in document.Objects.Values)
        {
            if (obj is PdfDictionary font && obj is not PdfStream && font.GetName("Type") == "Font" &&
                font.GetName("Subtype") == "Type0" && GetProgram(document, font, out var descriptor) is { } program)
            {
                if (!users.TryGetValue(program, out var list))
                {
                    users[program] = list = new List<PdfDictionary>();
                }

                list.Add(font);
                eligibleDescriptors.Add(descriptor!);
            }
        }

        // A descriptor that is not reached through an eligible Type0 font marks its program as shared.
        foreach (var obj in document.Objects.Values)
        {
            if (obj is PdfDictionary dict && obj is not PdfStream && dict.GetName("Type") == "FontDescriptor" &&
                !eligibleDescriptors.Contains(dict) && document.Get(dict, "FontFile2") is PdfStream program)
            {
                users.Remove(program);
            }
        }

        // Simple fonts that share a descriptor, and fonts written inline in resources, are not covered above.
        var known = new HashSet<PdfDictionary>(users.Values.SelectMany(l => l), ReferenceEqualityComparer.Instance);
        foreach (var obj in document.Objects.Values)
        {
            if (obj is PdfDictionary font && obj is not PdfStream && font.GetName("Type") == "Font" &&
                font.GetName("Subtype") is not ("Type0" or "CIDFontType2") &&
                document.Get(font, "FontDescriptor") is PdfDictionary simpleDescriptor &&
                document.Get(simpleDescriptor, "FontFile2") is PdfStream simpleProgram)
            {
                users.Remove(simpleProgram);
            }
        }

        foreach (var font in usage.Keys)
        {
            if (!known.Contains(font) && GetProgram(document, font, out _) is { } program)
            {
                users.Remove(program);
            }
        }

        foreach (var font in FormFieldFonts(document))
        {
            if (font.GetName("Subtype") == "Type0" && GetProgram(document, font, out _) is { } program)
            {
                users.Remove(program);
            }
        }

        int changed = 0;
        foreach (var (program, fonts) in users)
        {
            // Every font that uses the program must have been seen, or it may be drawn somewhere this scan missed.
            var glyphs = new HashSet<int>();
            bool allSeen = true;
            foreach (var font in fonts)
            {
                if (usage.TryGetValue(font, out var used))
                {
                    glyphs.UnionWith(used);
                }
                else
                {
                    allSeen = false;
                }
            }

            if (allSeen && TrySubset(document, program, fonts, glyphs))
            {
                changed++;
            }
        }

        return changed;
    }

    private static bool TrySubset(PdfDocument document, PdfStream program, List<PdfDictionary> fonts, HashSet<int> glyphs)
    {
        var data = ImageUsageScanner.Decode(document, program);
        if (data == null)
        {
            return false;
        }

        var subset = TrueTypeSubsetter.Subset(data, glyphs);
        if (subset == null || subset.Length >= data.Length * 0.9)
        {
            return false;
        }

        program.Data = subset;
        StreamOptimizer.SetFilters(program, Array.Empty<Filters.FilterSpec>());
        program.Set("Length1", new PdfInteger(subset.Length));

        // PDF/A files list the glyphs present in a CIDSet; keep it accurate.
        foreach (var font in fonts)
        {
            GetProgram(document, font, out var descriptor);
            if (descriptor != null && document.Get(descriptor, "CIDSet") is PdfStream cidSet)
            {
                int max = glyphs.Count == 0 ? 0 : glyphs.Max();
                var bits = new byte[(max / 8) + 1];
                foreach (int gid in glyphs)
                {
                    bits[gid / 8] |= (byte)(0x80 >> (gid % 8));
                }

                cidSet.Data = bits;
                StreamOptimizer.SetFilters(cidSet, Array.Empty<Filters.FilterSpec>());
            }
        }

        return true;
    }

    /// <summary>Returns the TrueType program of a Type0 font whose codes are glyph IDs, otherwise null.</summary>
    private static PdfStream? GetProgram(PdfDocument document, PdfDictionary font, out PdfDictionary? descriptor)
    {
        descriptor = null;
        if (font.GetName("Subtype") != "Type0" ||
            document.Get(font, "Encoding") is not PdfName { Value: "Identity-H" or "Identity-V" } ||
            document.Get(font, "DescendantFonts") is not PdfArray { Count: 1 } descendants ||
            document.Resolve(descendants[0]) is not PdfDictionary cidFont ||
            cidFont.GetName("Subtype") != "CIDFontType2")
        {
            return null;
        }

        var map = document.Get(cidFont, "CIDToGIDMap");
        if (map != null && map is not PdfName { Value: "Identity" })
        {
            return null;
        }

        descriptor = document.Get(cidFont, "FontDescriptor") as PdfDictionary;
        return descriptor == null ? null : document.Get(descriptor, "FontFile2") as PdfStream;
    }

    private static IEnumerable<PdfDictionary> FormFieldFonts(PdfDocument document)
    {
        if (document.Catalog is not { } catalog || document.Get(catalog, "AcroForm") is not PdfDictionary form ||
            document.Get(form, "DR") is not PdfDictionary resources || document.Get(resources, "Font") is not PdfDictionary fonts)
        {
            yield break;
        }

        foreach (var entry in fonts.Entries)
        {
            if (document.Resolve(entry.Value) is PdfDictionary font)
            {
                yield return font;
            }
        }
    }
}

/// <summary>
/// Finds the character codes shown with each font: walks page content, form XObjects, patterns, soft masks,
/// Type 3 glyph procedures and annotation appearances, following the current font through q/Q and into forms.
/// </summary>
internal sealed class FontUsageScanner
{
    private const int MaxDepth = 16;

    private readonly PdfDocument _document;
    private readonly Dictionary<PdfDictionary, HashSet<int>> _usage = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfStream> _active = new(ReferenceEqualityComparer.Instance);
    private bool _failed;

    private FontUsageScanner(PdfDocument document) => _document = document;

    /// <summary>Returns the codes used per font dictionary (two-byte codes), or null when some content could not be read.</summary>
    public static Dictionary<PdfDictionary, HashSet<int>>? Scan(PdfDocument document)
    {
        var scanner = new FontUsageScanner(document);
        foreach (var page in document.GetPages())
        {
            if (document.Get(page.Page, "Contents") is { } contents)
            {
                var data = ReadContents(document, contents);
                if (data == null)
                {
                    return null;
                }

                scanner.Run(data, page.Resources, null, 0);
            }

            scanner.ScanAnnotations(page.Page);
            if (scanner._failed)
            {
                return null;
            }
        }

        return scanner._usage;
    }

    private static byte[]? ReadContents(PdfDocument document, PdfObject contents)
    {
        if (contents is PdfStream single)
        {
            return ImageUsageScanner.Decode(document, single);
        }

        if (contents is not PdfArray parts)
        {
            return Array.Empty<byte>();
        }

        using var buffer = new MemoryStream();
        foreach (var part in parts.Items)
        {
            if (document.Resolve(part) is PdfStream s)
            {
                var data = ImageUsageScanner.Decode(document, s);
                if (data == null)
                {
                    return null;
                }

                buffer.Write(data);
                buffer.WriteByte((byte)'\n');
            }
        }

        return buffer.ToArray();
    }

    private void ScanAnnotations(PdfDictionary page)
    {
        if (_document.Get(page, "Annots") is not PdfArray annots)
        {
            return;
        }

        foreach (var item in annots.Items)
        {
            if (_document.Resolve(item) is not PdfDictionary annot || _document.Get(annot, "AP") is not PdfDictionary ap)
            {
                continue;
            }

            foreach (var entry in ap.Entries)
            {
                switch (_document.Resolve(entry.Value))
                {
                    case PdfStream form:
                        RunForm(form, null, null, 0);
                        break;
                    case PdfDictionary states:
                        foreach (var state in states.Entries)
                        {
                            if (_document.Resolve(state.Value) is PdfStream stateForm)
                            {
                                RunForm(stateForm, null, null, 0);
                            }
                        }

                        break;
                }
            }
        }
    }

    private void RunForm(PdfStream form, PdfDictionary? parentResources, PdfDictionary? font, int depth)
    {
        if (depth > MaxDepth || !_active.Add(form))
        {
            return;
        }

        try
        {
            var data = ImageUsageScanner.Decode(_document, form);
            if (data == null)
            {
                _failed = true;
                return;
            }

            Run(data, _document.Get(form, "Resources") as PdfDictionary ?? parentResources, font, depth + 1);
        }
        finally
        {
            _active.Remove(form);
        }
    }

    private void Run(byte[] content, PdfDictionary? resources, PdfDictionary? font, int depth)
    {
        ScanResourceExtras(resources, depth);

        var lexer = new PdfLexer(content);
        var operands = new List<PdfObject>();
        var stack = new Stack<PdfDictionary?>();
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

                continue;
            }

            switch (token.Text)
            {
                case "q":
                    stack.Push(font);
                    break;
                case "Q":
                    if (stack.Count > 0)
                    {
                        font = stack.Pop();
                    }

                    break;
                case "Tf":
                    if (operands.Count >= 2 && operands[^2] is PdfName fontName)
                    {
                        font = resources != null && _document.Get(resources, "Font") is PdfDictionary fonts
                            ? _document.Resolve(fonts.Get(fontName.Value)) as PdfDictionary
                            : null;
                    }

                    break;
                case "gs":
                    if (operands.Count >= 1 && operands[^1] is PdfName gsName && resources != null &&
                        _document.Get(resources, "ExtGState") is PdfDictionary states &&
                        _document.Get(states, gsName.Value) is PdfDictionary gs && gs.Get("Font") != null)
                    {
                        _failed = true; // fonts set through a graphics state are rare; do not guess
                    }

                    break;
                case "Tj":
                case "'":
                    if (operands.Count >= 1 && operands[^1] is PdfString text)
                    {
                        Record(font, text);
                    }

                    break;
                case "\"":
                    if (operands.Count >= 3 && operands[^1] is PdfString quoted)
                    {
                        Record(font, quoted);
                    }

                    break;
                case "TJ":
                    if (operands.Count >= 1 && operands[^1] is PdfArray parts)
                    {
                        foreach (var part in parts.Items)
                        {
                            if (part is PdfString s)
                            {
                                Record(font, s);
                            }
                        }
                    }

                    break;
                case "Do":
                    if (operands.Count >= 1 && operands[^1] is PdfName xName && resources != null &&
                        _document.Get(resources, "XObject") is PdfDictionary xobjects &&
                        _document.Get(xobjects, xName.Value) is PdfStream xobject && xobject.GetName("Subtype") == "Form")
                    {
                        RunForm(xobject, resources, font, depth);
                    }

                    break;
                case "BI":
                    SkipInlineImage(lexer);
                    break;
            }

            operands.Clear();
        }
    }

    /// <summary>Patterns, soft masks and Type 3 glyphs named in the resources can show text too.</summary>
    private void ScanResourceExtras(PdfDictionary? resources, int depth)
    {
        if (resources == null)
        {
            return;
        }

        if (_document.Get(resources, "Pattern") is PdfDictionary patterns)
        {
            foreach (var entry in patterns.Entries)
            {
                if (_document.Resolve(entry.Value) is PdfStream pattern)
                {
                    RunForm(pattern, resources, null, depth);
                }
            }
        }

        if (_document.Get(resources, "ExtGState") is PdfDictionary states)
        {
            foreach (var entry in states.Entries)
            {
                if (_document.Resolve(entry.Value) is PdfDictionary gs && _document.Get(gs, "SMask") is PdfDictionary mask &&
                    _document.Get(mask, "G") is PdfStream group)
                {
                    RunForm(group, resources, null, depth);
                }
            }
        }

        if (_document.Get(resources, "Font") is PdfDictionary fonts)
        {
            foreach (var entry in fonts.Entries)
            {
                if (_document.Resolve(entry.Value) is PdfDictionary type3 && type3.GetName("Subtype") == "Type3" &&
                    _document.Get(type3, "CharProcs") is PdfDictionary procs)
                {
                    var glyphResources = _document.Get(type3, "Resources") as PdfDictionary ?? resources;
                    foreach (var proc in procs.Entries)
                    {
                        if (_document.Resolve(proc.Value) is PdfStream glyph)
                        {
                            RunForm(glyph, glyphResources, null, depth);
                        }
                    }
                }
            }
        }
    }

    private void Record(PdfDictionary? font, PdfString text)
    {
        if (font == null)
        {
            return;
        }

        if (!_usage.TryGetValue(font, out var codes))
        {
            _usage[font] = codes = new HashSet<int>();
        }

        var bytes = text.Value;
        for (int i = 0; i + 1 < bytes.Length; i += 2)
        {
            codes.Add((bytes[i] << 8) | bytes[i + 1]);
        }
    }

    private static void SkipInlineImage(PdfLexer lexer)
    {
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
