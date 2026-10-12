using System.Text;
using HazukiPdf.Html;
using HazukiPdf.Html.Fonts;
using HazukiPdf.Imaging;
using HazukiPdf.Objects;

namespace HazukiPdf.Tests.Html;

public class HtmlToPdfConverterTests
{
    private static readonly HtmlToPdfOptions Letter = new() { PageSize = PageSize.Letter, Margins = PageMargins.Uniform(72) };

    [Fact]
    public void Convert_ProducesReadablePdfWithText()
    {
        var pdf = RenderedPdf.From("<html><head><title> My  Report </title></head><body><h1>Hello</h1><p>World &amp; more</p></body></html>");

        Assert.Single(pdf.Pages);
        Assert.Equal("Hello World & more", pdf.AllText());
        var (width, height) = pdf.MediaBox(0);
        Assert.Equal(595.28, width, 1);
        Assert.Equal(841.89, height, 1);
        Assert.Equal("My Report", ((PdfString)pdf.Info.Get("Title")!).AsText());
        Assert.Equal("HazukiPdf", ((PdfString)pdf.Info.Get("Producer")!).AsText());
    }

    [Fact]
    public void Convert_EmptyInputGivesOneBlankPage()
    {
        var pdf = RenderedPdf.From("");
        Assert.Single(pdf.Pages);
        Assert.Empty(pdf.Texts(0));
    }

    [Fact]
    public void Convert_IsDeterministicForAFixedCreationDate()
    {
        const string html = "<p>Same <b>input</b></p><table border=1><tr><td>1</td></tr></table>";
        Assert.Equal(RenderedPdf.From(html).Bytes, RenderedPdf.From(html).Bytes);
    }

    [Fact]
    public void Headings_UseBoldFontAndLargerSize()
    {
        var pdf = RenderedPdf.From("<h1>Title</h1><p>Body</p>");
        var texts = pdf.Texts(0);
        Assert.Equal("Helvetica-Bold", texts[0].Font);
        Assert.Equal(24, texts[0].Size, 2);
        Assert.Equal("Helvetica", texts[1].Font);
        Assert.Equal(12, texts[1].Size, 2);
        Assert.True(texts[0].Y > texts[1].Y);
    }

    [Theory]
    [InlineData("<b><i>x</i></b>", "Helvetica-BoldOblique")]
    [InlineData("<em>x</em>", "Helvetica-Oblique")]
    [InlineData("<span style=\"font-family: 'Times New Roman'\">x</span>", "Times-Roman")]
    [InlineData("<span style='font-family: Georgia; font-weight: 700; font-style: italic'>x</span>", "Times-BoldItalic")]
    [InlineData("<code>x</code>", "Courier")]
    [InlineData("<pre><strong>x</strong></pre>", "Courier-Bold")]
    public void FontStyles_MapToStandardFonts(string html, string expectedFont)
    {
        Assert.Equal(expectedFont, RenderedPdf.From(html).Texts(0).Single().Font);
    }

    [Fact]
    public void DefaultFontFamily_CanBeChanged()
    {
        var pdf = RenderedPdf.From("<p>x</p>", new HtmlToPdfOptions { DefaultFontFamily = "serif" });
        Assert.Equal("Times-Roman", pdf.Texts(0).Single().Font);
    }

    [Fact]
    public void Text_IsEncodedAsWinAnsi()
    {
        var pdf = RenderedPdf.From("<p>Smörgås – 5 € “quoted” • ok</p>");
        Assert.Equal("Smörgås – 5 € “quoted” • ok", pdf.AllText());
    }

    [Fact]
    public void Text_OutsideWinAnsiBecomesQuestionMark()
    {
        Assert.Equal("a ? b ? c", RenderedPdf.From("<p>a 漢 b 😀 c</p>").AllText());
    }

    [Fact]
    public void Whitespace_CollapsesExceptInPre()
    {
        var pdf = RenderedPdf.From("<p>  a \n\t  b  </p><pre>x   y\n  z</pre>");
        var texts = pdf.Texts(0).Select(t => t.Text).ToList();
        Assert.Equal(["a b", "x   y", "  z"], texts);
    }

    [Fact]
    public void LongParagraph_WrapsWithinContentWidth()
    {
        string words = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor sit amet", 40));
        var pdf = RenderedPdf.From($"<p>{words}</p>", Letter);
        var lines = pdf.Texts(0);

        Assert.True(lines.Count > 5);
        Assert.Equal(words, string.Join(" ", lines.Select(l => l.Text.Trim())));
        double left = 72 + 6; // page margin + default body margin of 8px
        foreach (var line in lines)
        {
            Assert.Equal(left, line.X, 1);
            double width = StandardFont.Helvetica.Measure(line.Text.TrimEnd(), 12);
            Assert.True(line.X + width <= 612 - 72 - 6 + 0.01, $"line overflows: {line.Text}");
        }
        Assert.True(lines.Zip(lines.Skip(1)).All(p => p.First.Y > p.Second.Y));
    }

    [Fact]
    public void LongWord_IsBrokenToFit()
    {
        string word = new('W', 300);
        var pdf = RenderedPdf.From($"<p>{word}</p>", Letter);
        var lines = pdf.Texts(0);
        Assert.True(lines.Count > 1);
        Assert.Equal(word, string.Concat(lines.Select(l => l.Text)));
    }

    [Theory]
    [InlineData("center")]
    [InlineData("right")]
    public void TextAlign_MovesLines(string align)
    {
        var pdf = RenderedPdf.From($"<p style='text-align:{align}; margin:0'>abc</p><style>body {{ margin: 0 }}</style>", Letter);
        var line = pdf.Texts(0).Single();
        double width = StandardFont.Helvetica.Measure("abc", 12);
        double expected = align == "center" ? 72 + (468 - width) / 2 : 72 + 468 - width;
        Assert.Equal(expected, line.X, 1);
    }

    [Fact]
    public void Justify_SetsWordSpacingOnAllButTheLastLine()
    {
        string words = string.Join(" ", Enumerable.Repeat("justified text flows", 30));
        var pdf = RenderedPdf.From($"<p style='text-align:justify'>{words}</p>", Letter);
        string content = pdf.Content(0);
        int lastLine = content.LastIndexOf("BT ", StringComparison.Ordinal);
        Assert.Matches(@" (?!0 Tw)[\d.]+ Tw ", content[..lastLine]);
        Assert.Contains(" 0 Tw ", content[lastLine..]);
    }

    [Fact]
    public void ForcedPageBreaks_StartNewPages()
    {
        var pdf = RenderedPdf.From("<p>one</p><div style='page-break-before: always'>two</div><p style='break-after: page'>three</p><p>four</p>");
        Assert.Equal(3, pdf.Pages.Count);
        Assert.Equal("one", pdf.Texts(0).Single().Text);
        Assert.Equal(["two", "three"], pdf.Texts(1).Select(t => t.Text));
        Assert.Equal("four", pdf.Texts(2).Single().Text);
    }

    [Fact]
    public void BreakAtTheEnd_DoesNotAddABlankPage()
    {
        Assert.Single(RenderedPdf.From("<p style='page-break-after: always'>last</p>").Pages);
    }

    [Fact]
    public void LongDocument_FlowsOntoMorePagesInsideTheMargins()
    {
        string html = string.Concat(Enumerable.Range(1, 200).Select(i => $"<p>Paragraph {i}</p>"));
        var pdf = RenderedPdf.From(html, Letter);

        Assert.True(pdf.Pages.Count > 3);
        var all = Enumerable.Range(0, pdf.Pages.Count).SelectMany(pdf.Texts).ToList();
        Assert.Equal(Enumerable.Range(1, 200).Select(i => $"Paragraph {i}"), all.Select(t => t.Text));
        Assert.All(all, t => Assert.InRange(t.Y, 72, 792 - 72));
    }

    [Fact]
    public void PageRule_SetsSizeOrientationAndMargins()
    {
        var pdf = RenderedPdf.From("<style>@page { size: letter landscape; margin: 0.5in } body { margin: 0 }</style><p style='margin:0'>x</p>");
        var (width, height) = pdf.MediaBox(0);
        Assert.Equal(792, width, 1);
        Assert.Equal(612, height, 1);
        var text = pdf.Texts(0).Single();
        Assert.Equal(36, text.X, 1);
        Assert.True(text.Y < 612 - 36 && text.Y > 612 - 36 - 12);
    }

    [Fact]
    public void Options_LandscapeSwapsPageSize()
    {
        var (width, height) = RenderedPdf.From("<p>x</p>", new HtmlToPdfOptions { PageSize = PageSize.A5, Landscape = true }).MediaBox(0);
        Assert.True(width > height);
    }

    [Fact]
    public void Lists_DrawMarkers()
    {
        var pdf = RenderedPdf.From(
            "<ol><li>a</li><li>b</li></ol><ol start=4 type=i><li>c</li><li value=9>d</li></ol>" +
            "<ol style='list-style-type: upper-alpha'><li>e</li></ol><ul><li>f</li></ul>");
        var texts = pdf.Texts(0).Select(t => t.Text).ToList();
        Assert.Equal(["1.", "a", "2.", "b", "iv.", "c", "ix.", "d", "A.", "e", "f"], texts);

        // The bullet of the unordered list is drawn as a filled circle left of its text.
        var f = pdf.Texts(0).Last();
        Assert.Contains(" c ", pdf.Content(0));
        Assert.True(f.X > 36 + 6);
    }

    [Fact]
    public void ListMarker_SitsLeftOfTheItemText()
    {
        var texts = RenderedPdf.From("<ol><li>item</li></ol>").Texts(0);
        Assert.Equal(texts[0].Y, texts[1].Y, 2);
        Assert.True(texts[0].X < texts[1].X);
    }

    [Fact]
    public void Table_PlacesCellsInColumnsAndRows()
    {
        var pdf = RenderedPdf.From("<table><tr><td>a</td><td>b</td></tr><tr><td>c</td><td>d</td></tr></table>");
        var t = pdf.Texts(0).ToDictionary(x => x.Text);
        Assert.Equal(t["a"].Y, t["b"].Y, 2);
        Assert.Equal(t["a"].X, t["c"].X, 2);
        Assert.True(t["b"].X > t["a"].X);
        Assert.True(t["c"].Y < t["a"].Y);
    }

    [Fact]
    public void Table_WithWidthUsesFullWidthAndColspan()
    {
        var pdf = RenderedPdf.From(
            "<style>body{margin:0} table{width:100%;border-collapse:collapse} td{padding:0}</style>" +
            "<table><tr><td>a</td><td>b</td></tr><tr><td colspan=2 style='text-align:right'>wide</td></tr></table>",
            Letter);
        var wide = pdf.Texts(0).Single(t => t.Text == "wide");
        Assert.Equal(72 + 468 - StandardFont.Helvetica.Measure("wide", 12), wide.X, 1);
    }

    [Fact]
    public void Table_RepeatsHeaderRowsOnEveryPage()
    {
        string rows = string.Concat(Enumerable.Range(1, 120).Select(i => $"<tr><td>row {i}</td></tr>"));
        var pdf = RenderedPdf.From($"<table><thead><tr><th>Header</th></tr></thead><tbody>{rows}</tbody></table>", Letter);

        Assert.True(pdf.Pages.Count >= 3);
        for (int page = 0; page < pdf.Pages.Count; page++)
            Assert.Equal("Header", pdf.Texts(page)[0].Text);
        var bodyRows = Enumerable.Range(0, pdf.Pages.Count).SelectMany(pdf.Texts).Where(t => t.Text != "Header");
        Assert.Equal(Enumerable.Range(1, 120).Select(i => $"row {i}"), bodyRows.Select(t => t.Text));
    }

    [Fact]
    public void Table_RowsAreNotSplitAcrossPages()
    {
        string rows = string.Concat(Enumerable.Range(1, 60).Select(i => $"<tr><td>{i}<br>second line</td></tr>"));
        var pdf = RenderedPdf.From($"<table>{rows}</table>", Letter);
        for (int page = 0; page < pdf.Pages.Count; page++)
        {
            var texts = pdf.Texts(page);
            Assert.NotEqual("second line", texts[0].Text);
            Assert.Equal("second line", texts[^1].Text);
        }
    }

    [Fact]
    public void Backgrounds_AndBorders_AreFilledRectangles()
    {
        string content = RenderedPdf.From("<div style='background:#ff0000; border:2px solid #0000ff'>x</div>").Content(0);
        Assert.Contains("1 0 0 rg", content);
        Assert.Contains("0 0 1 rg", content);
        Assert.True(content.Split(" re f").Length - 1 >= 5); // background + four sides
        Assert.True(content.IndexOf("1 0 0 rg", StringComparison.Ordinal) < content.IndexOf("BT", StringComparison.Ordinal));
    }

    [Fact]
    public void BodyBackground_FillsTheWholePage()
    {
        string content = RenderedPdf.From("<body style='background:#000080'><p>x</p></body>").Content(0);
        Assert.StartsWith("0 0 0.502 rg 0 0 595.276 841.89 re f", content);
    }

    [Fact]
    public void TextDecorations_DrawLines()
    {
        string plain = RenderedPdf.From("<p>x</p>").Content(0);
        string underlined = RenderedPdf.From("<p><u>x</u> <s>y</s></p>").Content(0);
        Assert.DoesNotContain("re f", plain);
        Assert.Equal(2, underlined.Split(" re f").Length - 1);
    }

    [Fact]
    public void HiddenContent_IsNotDrawn()
    {
        var pdf = RenderedPdf.From("<p>shown</p><p style='display:none'>gone</p><script>alert(1)</script><p style='visibility:hidden'>invisible</p>");
        Assert.Equal("shown", pdf.AllText());
    }

    [Fact]
    public void Links_BecomeUriAnnotations()
    {
        var pdf = RenderedPdf.From("<p><a href='https://example.com/a b'>link</a> <a href='javascript:alert(1)'>bad</a></p>");
        var annotation = Assert.Single(pdf.Annotations(0));
        var action = (PdfDictionary)pdf.Document.Resolve(annotation.Get("A"))!;
        Assert.Equal("URI", action.GetName("S"));
        Assert.Equal("https://example.com/a%20b", ((PdfString)action.Get("URI")!).AsText());
    }

    [Fact]
    public void InternalLinks_PointAtTheTargetPage()
    {
        var pdf = RenderedPdf.From("<p><a href='#end'>jump</a></p><div style='page-break-before:always'><h2 id=end>End</h2></div>");
        var annotation = Assert.Single(pdf.Annotations(0));
        var destination = (PdfArray)pdf.Document.Resolve(annotation.Get("Dest"))!;
        var target = (PdfReference)destination[0];
        Assert.Same(pdf.Page(1), pdf.Document.Resolve(target));
    }

    [Fact]
    public void PngImage_IsEmbeddedWithSoftMask()
    {
        byte[] png = TestImages.Png(4, 3, colorType: 6, (x, y) => [(byte)(x * 60), (byte)(y * 80), 200, (byte)(x == 0 ? 0 : 255)]);
        var pdf = RenderedPdf.From($"<img src='data:image/png;base64,{Convert.ToBase64String(png)}' width=40>");

        var image = pdf.Images().Single();
        Assert.Equal(4, image.Get("Width").AsNumber());
        Assert.Equal(3, image.Get("Height").AsNumber());
        Assert.Equal("DeviceRGB", image.GetName("ColorSpace"));
        Assert.NotNull(image.Get("SMask"));
        Assert.Matches(@"q 30 0 0 22.5 [\d.]+ [\d.]+ cm /Im1 Do Q", pdf.Content(0));
    }

    [Fact]
    public void JpegImage_IsPassedThrough()
    {
        byte[] jpeg = JpegEncoder.Encode(TestPdf.PhotoPixels(16, 8), 16, 8, 3, 80);
        var pdf = RenderedPdf.From($"<p><img src='data:image/jpeg;base64,{Convert.ToBase64String(jpeg)}'></p>");

        var image = pdf.Images().Single();
        Assert.Equal("DCTDecode", image.GetName("Filter"));
        Assert.Equal(jpeg, image.Data);
        Assert.Matches(@"q 12 0 0 6 [\d.]+ [\d.]+ cm /Im1 Do Q", pdf.Content(0));
    }

    [Fact]
    public void SameImageTwice_IsEmbeddedOnce()
    {
        string src = "data:image/png;base64," + Convert.ToBase64String(TestImages.Png(2, 2, 2, (_, _) => [1, 2, 3]));
        var pdf = RenderedPdf.From($"<img src='{src}'><img src='{src}'>");
        Assert.Single(pdf.Images());
        Assert.Equal(2, pdf.Content(0).Split("/Im1 Do").Length - 1);
    }

    [Fact]
    public void WideImage_IsScaledDownToTheContentWidth()
    {
        string src = "data:image/png;base64," + Convert.ToBase64String(TestImages.Png(2000, 10, 0, (_, _) => [128]));
        var pdf = RenderedPdf.From($"<style>body{{margin:0}}</style><img src='{src}'>", Letter);
        Assert.Matches(@"q 468 0 0 2.34 72 [\d.]+ cm /Im1 Do Q", pdf.Content(0));
    }

    [Fact]
    public void Images_LoadFromBaseDirectoryOnly()
    {
        string root = Path.Combine(Path.GetTempPath(), "hazuki-html-" + Guid.NewGuid().ToString("N"));
        string inside = Path.Combine(root, "site");
        Directory.CreateDirectory(Path.Combine(inside, "img"));
        try
        {
            byte[] png = TestImages.Png(1, 1, 0, (_, _) => [0]);
            File.WriteAllBytes(Path.Combine(inside, "img", "a.png"), png);
            File.WriteAllBytes(Path.Combine(root, "secret.png"), png);
            var options = new HtmlToPdfOptions { BaseDirectory = inside };

            Assert.Single(RenderedPdf.From("<img src='img/a.png'>", options).Images());
            Assert.Empty(RenderedPdf.From("<img src='../secret.png'>", options).Images());
            Assert.Empty(RenderedPdf.From($"<img src='{new Uri(Path.Combine(root, "secret.png")).AbsoluteUri}'>", options).Images());
            Assert.Empty(RenderedPdf.From("<img src='img/a.png'>").Images());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Images_UseTheResolverForRemoteUrls()
    {
        byte[] png = TestImages.Png(1, 1, 0, (_, _) => [0]);
        var requested = new List<string>();
        var options = new HtmlToPdfOptions
        {
            ResourceResolver = src =>
            {
                requested.Add(src);
                return src.StartsWith("https://", StringComparison.Ordinal) ? png : null;
            },
        };
        var pdf = RenderedPdf.From("<img src='https://example.com/logo.png'><img src='http://x/missing.png' alt='Missing logo'>", options);
        Assert.Single(pdf.Images());
        Assert.Equal(["https://example.com/logo.png", "http://x/missing.png"], requested);
        Assert.Equal("Missing logo", pdf.AllText());
    }

    [Fact]
    public void ConvertFile_ResolvesImagesNextToTheHtmlFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hazuki-html-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "pic.png"), TestImages.Png(2, 2, 2, (_, _) => [9, 9, 9]));
            File.WriteAllText(Path.Combine(dir, "doc.html"), "<h1>File</h1><img src=pic.png>", Encoding.UTF8);
            int pages = HtmlToPdfConverter.ConvertFile(Path.Combine(dir, "doc.html"), Path.Combine(dir, "doc.pdf"));
            Assert.Equal(1, pages);
            Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(dir, "doc.pdf")), 0, 5));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MalformedHtml_StillConverts()
    {
        var pdf = RenderedPdf.From("<div><p>one<table><tr><td>two<p>three</div></b></i><ul><li>four");
        Assert.Equal("one two three four", pdf.AllText());
    }

    [Fact]
    public void DeeplyNestedHtml_DoesNotOverflowTheStack()
    {
        string html = string.Concat(Enumerable.Repeat("<div><span>", 20000)) + "deep";
        Assert.Equal("deep", RenderedPdf.From(html).AllText());
    }

    [Fact]
    public void Margins_ThatLeaveNoRoom_Throw()
    {
        Assert.Throws<ArgumentException>(() =>
            HtmlToPdfConverter.Convert("<p>x</p>", new HtmlToPdfOptions { Margins = PageMargins.Uniform(400) }));
    }
}
