using System.Text;
using HazukiPdf.Objects;
using HazukiPdf.Parsing;
using HazukiPdf.Writing;

namespace HazukiPdf.Tests;

public class ParserWriterTests
{
    private static PdfObject? Parse(string text) => new PdfLexer(Encoding.Latin1.GetBytes(text)).ReadObject();

    [Fact]
    public void Lexer_ParsesAllObjectTypes()
    {
        var dict = Assert.IsType<PdfDictionary>(Parse("<< /Int 42 /Real -3.5 /Bool true /Null null /Name /A#20B /Ref 12 0 R /Arr [1 (x) <414>] >>"));
        Assert.Equal(42, ((PdfInteger)dict["Int"]!).Value);
        Assert.Equal(-3.5, ((PdfReal)dict["Real"]!).Value);
        Assert.True(((PdfBoolean)dict["Bool"]!).Value);
        Assert.Null(dict["Null"]);
        Assert.Equal("A B", ((PdfName)dict["Name"]!).Value);
        Assert.Equal(new PdfReference(12, 0), dict["Ref"]);
        var array = Assert.IsType<PdfArray>(dict["Arr"]);
        Assert.Equal(3, array.Count);
        Assert.Equal(new byte[] { 0x41, 0x40 }, ((PdfString)array[2]).Value);
    }

    [Fact]
    public void Lexer_DecodesLiteralStringEscapes()
    {
        var s = Assert.IsType<PdfString>(Parse("(a\\(b\\)c\\\\ \\101\\n (nested) \\\r\nend)"));
        Assert.Equal("a(b)c\\ A\n (nested) end", s.AsText());
    }

    [Fact]
    public void Writer_EscapesStringsAndNamesSoTheyRoundTrip()
    {
        var dict = new PdfDictionary();
        dict.Set("Odd Name#", new PdfString(Encoding.Latin1.GetBytes("p(a)r\\e\rn")));
        dict.Set("Bin", new PdfString(new byte[] { 0, 1, 2, 255, 254, 253, 252 }));
        dict.Set("Num", new PdfReal(0.1 + 0.2));
        var bytes = PdfWriter.Serialize(dict);
        var parsed = Assert.IsType<PdfDictionary>(new PdfLexer(bytes).ReadObject());
        Assert.Equal("p(a)r\\e\rn", ((PdfString)parsed["Odd Name#"]!).AsText());
        Assert.Equal(new byte[] { 0, 1, 2, 255, 254, 253, 252 }, ((PdfString)parsed["Bin"]!).Value);
        Assert.Equal(0.3, ((PdfReal)parsed["Num"]!).Value, 6);
        Assert.Contains("/Num 0.3", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_LoadsAllObjectsAndPages()
    {
        var pdf = TestPdf.WithPages(TestPdf.TextPage(), TestPdf.TextPage());
        var document = PdfReader.Read(pdf);
        Assert.Equal(2, document.GetPages().Count);
        Assert.Equal("1.4", document.Version);
    }

    [Fact]
    public void Reader_RecoversWhenTheCrossReferenceIsBroken()
    {
        var pdf = TestPdf.WithPages(TestPdf.TextPage());
        var text = Encoding.Latin1.GetString(pdf);
        int index = text.LastIndexOf("startxref", StringComparison.Ordinal);
        var broken = Encoding.Latin1.GetBytes(text[..index] + "startxref\n999999\n%%EOF\n");
        Assert.Single(PdfReader.Read(broken).GetPages());
    }

    [Fact]
    public void Reader_HandlesJunkBeforeTheHeader()
    {
        var pdf = TestPdf.WithPages(TestPdf.TextPage());
        var withJunk = Encoding.ASCII.GetBytes("garbage line\n").Concat(pdf).ToArray();
        Assert.Single(PdfReader.Read(withJunk).GetPages());
    }

    [Fact]
    public void Reader_FollowsIncrementalUpdates()
    {
        var pdf = TestPdf.WithPages("BT /F1 12 Tf (old) Tj ET");
        var text = Encoding.Latin1.GetString(pdf);
        int startxref = int.Parse(text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 9)..].Trim().Split('\n')[0]);

        // Append a new version of the content stream (object 4).
        var update = new StringBuilder();
        int offset = pdf.Length;
        var body = "BT /F1 12 Tf (new) Tj ET";
        update.Append($"4 0 obj\n<< /Length {body.Length} >>\nstream\n{body}\nendstream\nendobj\n");
        int xref = offset + update.Length;
        update.Append($"xref\n4 1\n{offset:D10} 00000 n \ntrailer\n<< /Size 6 /Root 1 0 R /Prev {startxref} >>\nstartxref\n{xref}\n%%EOF\n");
        var updated = pdf.Concat(Encoding.Latin1.GetBytes(update.ToString())).ToArray();

        var document = PdfReader.Read(updated);
        var content = Assert.IsType<PdfStream>(document.Objects[4]);
        Assert.Equal(body, Encoding.Latin1.GetString(content.Data));
    }

    [Fact]
    public void Reader_RejectsNonPdfInput()
    {
        Assert.Throws<PdfFormatException>(() => PdfReader.Read(Encoding.ASCII.GetBytes("hello, not a pdf")));
    }

    [Fact]
    public void Reader_RejectsEncryptedFiles()
    {
        var pdf = new TestPdf();
        int pages = pdf.Add("<< /Type /Pages /Kids [] /Count 0 >>");
        int catalog = pdf.Add($"<< /Type /Catalog /Pages {pages} 0 R >>");
        int encrypt = pdf.Add("<< /Filter /Standard /V 1 /R 2 /O <00> /U <00> /P -4 >>");
        var bytes = pdf.Build(catalog, extraTrailer: $"/Encrypt {encrypt} 0 R");
        Assert.Throws<PdfEncryptedException>(() => PdfReader.Read(bytes));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Writer_OutputCanBeReadBack(bool useObjectStreams)
    {
        var document = PdfReader.Read(TestPdf.WithPages(TestPdf.TextPage(), TestPdf.TextPage(5)));
        var bytes = PdfWriter.Write(document, new PdfWriterOptions { UseObjectStreams = useObjectStreams });
        var text = Encoding.Latin1.GetString(bytes);
        if (useObjectStreams)
        {
            Assert.StartsWith("%PDF-1.5", text, StringComparison.Ordinal);
            Assert.Contains("/ObjStm", text, StringComparison.Ordinal);
            Assert.Contains("/XRef", text, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("\nxref\n", text, StringComparison.Ordinal);
        }

        var reread = PdfReader.Read(bytes);
        Assert.Equal(2, reread.GetPages().Count);
        Assert.Equal(document.Objects.Count, reread.Objects.Count);
    }
}
