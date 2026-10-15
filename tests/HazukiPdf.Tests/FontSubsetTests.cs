using System.Buffers.Binary;
using System.Text;
using HazukiPdf.Filters;
using HazukiPdf.Objects;
using HazukiPdf.Optimization;
using HazukiPdf.Parsing;

namespace HazukiPdf.Tests;

public class FontSubsetTests
{
    [Fact]
    public void Subset_KeepsUsedGlyphsAndCompositeParts()
    {
        var font = BuildFont();
        var subset = TrueTypeSubsetter.Subset(font, new HashSet<int> { 2 });

        Assert.NotNull(subset);
        Assert.True(subset!.Length < font.Length / 2);
        Assert.Equal(Glyph(font, 0), Glyph(subset, 0));
        Assert.Empty(Glyph(subset, 1));
        Assert.Equal(Glyph(font, 2), Glyph(subset, 2)); // the composite glyph
        Assert.Equal(Glyph(font, 3), Glyph(subset, 3)); // its component
        Assert.Empty(Glyph(subset, 4));
        Assert.Equal(0xB1B0AFBAu, Checksum(subset));
    }

    [Fact]
    public void StripHinting_RemovesInstructionsButKeepsOutlines()
    {
        var font = BuildFont();
        var stripped = TrueTypeSubsetter.Subset(font, null, stripHinting: true);

        Assert.NotNull(stripped);
        Assert.False(HasTable(stripped!, "fpgm"));
        Assert.True(HasTable(font, "fpgm"));
        var glyph = Glyph(stripped!, 1);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(glyph.AsSpan(12))); // no instructions left
        Assert.Equal(Glyph(font, 1)[^5..], glyph[^5..]); // the same point
        Assert.Equal(Glyph(font, 2), Glyph(stripped!, 2));
        Assert.Equal(0xB1B0AFBAu, Checksum(stripped!));
    }

    [Fact]
    public void Compressor_StripsHintingOnlyWhenAsked()
    {
        var pdf = BuildPdf("BT /F1 12 Tf 72 700 Td <0002> Tj ET", formFieldFont: true);

        var maximum = FontProgram(PdfCompressor.Compress(pdf, PdfCompressionLevel.Maximum));
        var balanced = FontProgram(PdfCompressor.Compress(pdf, PdfCompressionLevel.Balanced));

        Assert.False(HasTable(maximum, "fpgm"));
        Assert.Equal(BuildFont(), balanced);
    }

    [Fact]
    public void Subset_RejectsCffFonts()
    {
        var font = BuildFont();
        Encoding.ASCII.GetBytes("OTTO").CopyTo(font, 0);
        Assert.Null(TrueTypeSubsetter.Subset(font, new HashSet<int> { 1 }));
    }

    [Fact]
    public void Compressor_SubsetsIdentityEncodedFonts()
    {
        var pdf = BuildPdf("BT /F1 12 Tf 72 700 Td <0002> Tj ET", formFieldFont: false);

        var output = PdfCompressor.Compress(pdf, PdfCompressionLevel.Lossless);

        var program = FontProgram(output);
        Assert.Empty(Glyph(program, 1));
        Assert.NotEmpty(Glyph(program, 2));
        Assert.NotEmpty(Glyph(program, 3));
    }

    [Fact]
    public void Compressor_FollowsTheFontIntoFormXObjects()
    {
        var pdf = BuildPdf("BT /F1 12 Tf 72 700 Td ET /Fm1 Do", formFieldFont: false, formContent: "BT <0001> Tj ET");

        var output = PdfCompressor.Compress(pdf, PdfCompressionLevel.Lossless);

        var program = FontProgram(output);
        Assert.NotEmpty(Glyph(program, 1));
        Assert.Empty(Glyph(program, 2));
    }

    [Fact]
    public void Compressor_LeavesFormFieldFontsWhole()
    {
        var pdf = BuildPdf("BT /F1 12 Tf 72 700 Td <0002> Tj ET", formFieldFont: true);

        var output = PdfCompressor.Compress(pdf, PdfCompressionLevel.Lossless);

        Assert.Equal(BuildFont(), FontProgram(output));
    }

    [Fact]
    public void Compressor_LeavesFontsWholeWhenSubsettingIsOff()
    {
        var pdf = BuildPdf("BT /F1 12 Tf 72 700 Td <0002> Tj ET", formFieldFont: false);

        var output = PdfCompressor.Compress(pdf, new PdfCompressionOptions { SubsetFonts = false });

        Assert.Equal(BuildFont(), FontProgram(output));
    }

    private static byte[] BuildPdf(string content, bool formFieldFont, string? formContent = null)
    {
        var pdf = new TestPdf();
        int catalog = pdf.Reserve();
        int pages = pdf.Reserve();
        var fontData = BuildFont();
        int program = pdf.AddStream($"/Length1 {fontData.Length} /Filter /FlateDecode", StreamFilters.Deflate(fontData));
        int descriptor = pdf.Add($"<< /Type /FontDescriptor /FontName /AAAAAA+Test /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 /FontFile2 {program} 0 R >>");
        int cidFont = pdf.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /AAAAAA+Test /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /CIDToGIDMap /Identity /DW 500 >>");
        int font = pdf.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /AAAAAA+Test /Encoding /Identity-H /DescendantFonts [{cidFont} 0 R] >>");
        string xobjects = string.Empty;
        if (formContent != null)
        {
            int form = pdf.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 612 792]", Encoding.ASCII.GetBytes(formContent));
            xobjects = $"/XObject << /Fm1 {form} 0 R >>";
        }

        int contents = pdf.AddStream(string.Empty, Encoding.ASCII.GetBytes(content));
        int page = pdf.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R >> {xobjects} >> /Contents {contents} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        string acroForm = formFieldFont ? $"/AcroForm << /Fields [] /DR << /Font << /F1 {font} 0 R >> >> >>" : string.Empty;
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R {acroForm} >>");
        return pdf.Build(catalog);
    }

    private static byte[] FontProgram(byte[] pdf)
    {
        var document = PdfReader.Read(pdf);
        var descriptor = document.Objects.Values.OfType<PdfDictionary>().Single(d => d.GetName("Type") == "FontDescriptor");
        var program = (PdfStream)document.Get(descriptor, "FontFile2")!;
        var data = StreamFilters.Decode(program.Data, StreamFilters.GetFilters(program, document.Resolve), strict: true);
        Assert.Equal(data.Length, (int)(program.Get("Length1").AsNumber() ?? 0));
        return data;
    }

    /// <summary>
    /// A TrueType font with five glyphs: 0 and 1 simple (1 carries a large instruction block), 2 a composite
    /// made of glyph 3, and 4 simple.
    /// </summary>
    private static byte[] BuildFont()
    {
        var glyphs = new[]
        {
            SimpleGlyph(0),
            SimpleGlyph(2000),
            CompositeGlyph(3),
            SimpleGlyph(10),
            SimpleGlyph(1500),
        };

        var glyf = new MemoryStream();
        var loca = new byte[(glyphs.Length + 1) * 2];
        for (int i = 0; i < glyphs.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(loca.AsSpan(i * 2), (ushort)(glyf.Length / 2));
            glyf.Write(glyphs[i]);
            if (glyf.Length % 2 != 0)
            {
                glyf.WriteByte(0);
            }
        }

        BinaryPrimitives.WriteUInt16BigEndian(loca.AsSpan(glyphs.Length * 2), (ushort)(glyf.Length / 2));

        var head = new byte[54];
        BinaryPrimitives.WriteUInt32BigEndian(head, 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12), 0x5F0F3CF5);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), 1000);
        var maxp = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(maxp, 0x00005000);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)glyphs.Length);
        var hhea = new byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(hhea, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)glyphs.Length);
        var hmtx = new byte[glyphs.Length * 4];
        var gsub = new byte[400]; // a layout table the subset drops
        var fpgm = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray(); // hinting

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["GSUB"] = gsub, ["fpgm"] = fpgm, ["glyf"] = glyf.ToArray(), ["head"] = head, ["hhea"] = hhea, ["hmtx"] = hmtx, ["loca"] = loca, ["maxp"] = maxp,
        };

        var font = new MemoryStream();
        var directory = new byte[12 + (16 * tables.Count)];
        BinaryPrimitives.WriteUInt32BigEndian(directory, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(directory.AsSpan(4), (ushort)tables.Count);
        font.Write(directory);
        int record = 12;
        foreach (var (tag, data) in tables)
        {
            Encoding.ASCII.GetBytes(tag).CopyTo(directory, record);
            BinaryPrimitives.WriteUInt32BigEndian(directory.AsSpan(record + 8), (uint)font.Length);
            BinaryPrimitives.WriteUInt32BigEndian(directory.AsSpan(record + 12), (uint)data.Length);
            font.Write(data);
            while (font.Length % 4 != 0)
            {
                font.WriteByte(0);
            }

            record += 16;
        }

        var result = font.ToArray();
        directory.CopyTo(result, 0);
        return result;
    }

    private static byte[] SimpleGlyph(int instructions)
    {
        var glyph = new byte[10 + 2 + 2 + instructions + 1 + 2 + 2];
        BinaryPrimitives.WriteInt16BigEndian(glyph, 1); // one contour
        BinaryPrimitives.WriteInt16BigEndian(glyph.AsSpan(6), 100);
        BinaryPrimitives.WriteInt16BigEndian(glyph.AsSpan(8), 100);
        BinaryPrimitives.WriteUInt16BigEndian(glyph.AsSpan(10), 0); // last point of the contour
        BinaryPrimitives.WriteUInt16BigEndian(glyph.AsSpan(12), (ushort)instructions);
        for (int i = 0; i < instructions; i++)
        {
            glyph[14 + i] = (byte)(i * 7);
        }

        glyph[14 + instructions] = 0x01; // on curve, 16-bit coordinates
        BinaryPrimitives.WriteInt16BigEndian(glyph.AsSpan(15 + instructions), 100);
        BinaryPrimitives.WriteInt16BigEndian(glyph.AsSpan(17 + instructions), 100);
        return glyph;
    }

    private static byte[] CompositeGlyph(int component)
    {
        var glyph = new byte[18];
        BinaryPrimitives.WriteInt16BigEndian(glyph, -1);
        BinaryPrimitives.WriteUInt16BigEndian(glyph.AsSpan(10), 0x0003); // word arguments, XY values, no more components
        BinaryPrimitives.WriteUInt16BigEndian(glyph.AsSpan(12), (ushort)component);
        BinaryPrimitives.WriteInt16BigEndian(glyph.AsSpan(14), 50);
        return glyph;
    }

    private static bool HasTable(byte[] font, string tag)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        return Enumerable.Range(0, count).Any(i => Encoding.ASCII.GetString(font, 12 + (i * 16), 4) == tag);
    }

    private static byte[] Glyph(byte[] font, int gid)
    {
        var tables = new Dictionary<string, (int Offset, int Length)>();
        int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            int r = 12 + (i * 16);
            tables[Encoding.ASCII.GetString(font, r, 4)] = ((int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(r + 8)), (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(r + 12)));
        }

        bool longLoca = BinaryPrimitives.ReadInt16BigEndian(font.AsSpan(tables["head"].Offset + 50)) != 0;
        int Offset(int g) => longLoca
            ? (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(tables["loca"].Offset + (g * 4)))
            : BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(tables["loca"].Offset + (g * 2))) * 2;
        int start = Offset(gid);
        int end = Offset(gid + 1);
        var glyph = font.AsSpan(tables["glyf"].Offset + start, end - start).ToArray();

        // Glyphs are padded in the table; compare without the padding.
        int length = glyph.Length;
        while (length > 0 && glyph[length - 1] == 0 && length > 10)
        {
            length--;
        }

        return glyph[..length];
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (int i = 0; i + 4 <= data.Length; i += 4)
        {
            sum += BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i));
        }

        return sum;
    }
}
