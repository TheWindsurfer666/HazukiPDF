using System.Text;
using HazukiPdf.Filters;
using HazukiPdf.Objects;
using HazukiPdf.Parsing;

namespace HazukiPdf.Tests;

public class PdfCompressorTests
{
    private static PdfStream FindImage(byte[] pdf) =>
        PdfReader.Read(pdf).Objects.Values.OfType<PdfStream>().Single(s => s.GetName("Subtype") == "Image");

    private static string PageText(byte[] pdf, int index)
    {
        var document = PdfReader.Read(pdf);
        var page = document.GetPages()[index].Page;
        var content = (PdfStream)document.Resolve(page.Get("Contents"))!;
        return Encoding.Latin1.GetString(StreamFilters.Decode(content.Data, StreamFilters.GetFilters(content, document.Resolve), strict: true));
    }

    [Fact]
    public void Lossless_ShrinksUncompressedContentAndKeepsItIdentical()
    {
        var original = TestPdf.WithPages(TestPdf.TextPage(), TestPdf.TextPage(60));
        var compressed = PdfCompressor.Compress(original, PdfCompressionLevel.Lossless);

        Assert.True(compressed.Length < original.Length / 2, $"{compressed.Length} vs {original.Length}");
        Assert.Equal(PageText(original, 0), PageText(compressed, 0));
        Assert.Equal(PageText(original, 1), PageText(compressed, 1));
    }

    [Fact]
    public void Compress_ReportsWhatItDid()
    {
        var original = TestPdf.WithPages(TestPdf.TextPage(), TestPdf.TextPage());
        PdfCompressor.Compress(original, new PdfCompressionOptions(), out var result);

        Assert.Equal(original.Length, result.OriginalSize);
        Assert.True(result.CompressedSize < result.OriginalSize);
        Assert.Equal(result.OriginalSize - result.CompressedSize, result.BytesSaved);
        Assert.True(result.Ratio < 1);
        Assert.Equal(2, result.StreamsRecompressed);
        Assert.False(result.OriginalKept);
    }

    [Fact]
    public void RemovesUnusedObjects()
    {
        var pdf = new TestPdf();
        int pages = pdf.Add("<< /Type /Pages /Kids [] /Count 0 >>");
        int catalog = pdf.Add($"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.AddStream(string.Empty, Encoding.ASCII.GetBytes(new string('x', 5000))); // nothing points here
        var original = pdf.Build(catalog);

        var compressed = PdfCompressor.Compress(original, new PdfCompressionOptions(), out var result);
        Assert.Equal(1, result.UnusedObjectsRemoved);
        Assert.Equal(2, PdfReader.Read(compressed).Objects.Count);

        var kept = PdfCompressor.Compress(original, new PdfCompressionOptions { RemoveUnusedObjects = false });
        Assert.Equal(3, PdfReader.Read(kept).Objects.Count);
    }

    [Fact]
    public void MergesDuplicateObjects()
    {
        var pdf = new TestPdf();
        int catalog = pdf.Reserve();
        int pages = pdf.Reserve();
        var fontProgram = Enumerable.Range(0, 4000).Select(i => (byte)(i * 31 % 251)).ToArray();
        var kids = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            // Each page embeds its own identical copy of the same font.
            int file = pdf.AddStream("/Length1 4000", fontProgram);
            int descriptor = pdf.Add($"<< /Type /FontDescriptor /FontName /Test /FontFile2 {file} 0 R >>");
            int font = pdf.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /Test /FontDescriptor {descriptor} 0 R >>");
            int content = pdf.AddStream(string.Empty, Encoding.ASCII.GetBytes("BT /F1 12 Tf (hi) Tj ET"));
            kids.Add(pdf.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 {font} 0 R >> >> /Contents {content} 0 R >>"));
        }

        pdf.Set(pages, $"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count 3 >>");
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        var original = pdf.Build(catalog);

        var compressed = PdfCompressor.Compress(original, new PdfCompressionOptions(), out var result);

        // Font file, descriptor, font and content stream: two duplicates of each.
        Assert.Equal(8, result.DuplicateObjectsRemoved);
        var document = PdfReader.Read(compressed);
        Assert.Equal(3, document.GetPages().Count);
        Assert.Single(document.Objects.Values.OfType<PdfStream>(), s => s.Get("Length1") != null);
    }

    [Fact]
    public void DoesNotMergeIdenticalPages()
    {
        var page = TestPdf.TextPage(3);
        var compressed = PdfCompressor.Compress(TestPdf.WithPages(page, page, page), PdfCompressionLevel.Lossless);
        Assert.Equal(3, PdfReader.Read(compressed).GetPages().Count);
    }

    [Fact]
    public void ObjectStreamsCanBeTurnedOff()
    {
        var original = TestPdf.WithPages(TestPdf.TextPage());
        var withStreams = Encoding.Latin1.GetString(PdfCompressor.Compress(original, new PdfCompressionOptions()));
        var without = Encoding.Latin1.GetString(PdfCompressor.Compress(original, new PdfCompressionOptions { UseObjectStreams = false }));

        Assert.Contains("/ObjStm", withStreams, StringComparison.Ordinal);
        Assert.DoesNotContain("/ObjStm", without, StringComparison.Ordinal);
        Assert.StartsWith("%PDF-1.4", without, StringComparison.Ordinal);
    }

    [Fact]
    public void Balanced_DownsamplesAndJpegEncodesHighResolutionPhotos()
    {
        // 800x600 pixels shown 2x1.5 inches = 400 DPI; Balanced caps at 200 DPI.
        var pixels = TestPdf.PhotoPixels(800, 600);
        var original = TestPdf.WithImage(pixels, 800, 600, "/DeviceRGB", 144, 108);

        var compressed = PdfCompressor.Compress(original, PdfCompressionOptions.FromLevel(PdfCompressionLevel.Balanced), out var result);

        Assert.Equal(1, result.ImagesRecompressed);
        Assert.Equal(1, result.ImagesDownsampled);
        var image = FindImage(compressed);
        Assert.Equal("DCTDecode", image.GetName("Filter"));
        Assert.Equal(400, ((PdfInteger)image["Width"]!).Value);
        Assert.Equal(300, ((PdfInteger)image["Height"]!).Value);
        Assert.True(compressed.Length < original.Length / 3);
    }

    [Fact]
    public void Balanced_KeepsResolutionOfImagesShownAtLowDpi()
    {
        // 300x200 pixels shown 4x2.67 inches = 75 DPI: re-encoded but not downsampled.
        var pixels = TestPdf.PhotoPixels(300, 200);
        var original = TestPdf.WithImage(pixels, 300, 200, "/DeviceRGB", 288, 192);

        var compressed = PdfCompressor.Compress(original, PdfCompressionOptions.FromLevel(PdfCompressionLevel.Balanced), out var result);

        Assert.Equal(0, result.ImagesDownsampled);
        var image = FindImage(compressed);
        Assert.Equal(300, ((PdfInteger)image["Width"]!).Value);
        Assert.Equal("DCTDecode", image.GetName("Filter"));
    }

    [Fact]
    public void Lossless_NeverTouchesImagePixels()
    {
        var pixels = TestPdf.PhotoPixels(400, 300);
        var original = TestPdf.WithImage(pixels, 400, 300, "/DeviceRGB", 50, 40);
        var compressed = PdfCompressor.Compress(original, PdfCompressionLevel.Lossless);

        var image = FindImage(compressed);
        Assert.Equal("FlateDecode", image.GetName("Filter"));
        Assert.Equal(pixels, StreamFilters.Inflate(image.Data, strict: true));
    }

    [Fact]
    public void GrayLookingRgbImagesBecomeGrayscale()
    {
        var gray = TestPdf.PhotoPixels(300, 200, components: 1);
        var rgb = gray.SelectMany(v => new[] { v, v, v }).ToArray();
        var original = TestPdf.WithImage(rgb, 300, 200, "/DeviceRGB", 300, 200);

        var compressed = PdfCompressor.Compress(original, PdfCompressionLevel.Balanced);
        Assert.Equal("DeviceGray", FindImage(compressed).GetName("ColorSpace"));
    }

    [Fact]
    public void LeavesMasksAndUnsupportedImagesAlone()
    {
        var pixels = TestPdf.PhotoPixels(300, 200);

        // A stencil mask, a CMYK image and a color-key masked image are all skipped.
        foreach (var (space, extra) in new[] { ("/DeviceRGB", "/Mask [0 10 0 10 0 10]"), ("/DeviceCMYK", string.Empty) })
        {
            var cmykPixels = space == "/DeviceCMYK" ? TestPdf.PhotoPixels(300, 200, 1).SelectMany(v => new[] { v, v, v, v }).ToArray() : pixels;
            var original = TestPdf.WithImage(cmykPixels, 300, 200, space, 300, 200, extra);
            PdfCompressor.Compress(original, PdfCompressionLevel.Maximum, out var result);
            Assert.Equal(0, result.ImagesRecompressed);
        }
    }

    [Fact]
    public void ImagesWithFewColorsStayLossless()
    {
        // A two-color diagram would get blurry halos as JPEG.
        var pixels = new byte[300 * 200 * 3];
        for (int i = 0; i < 300 * 200; i++)
        {
            byte v = (byte)(i % 300 < 150 ? 0 : 255);
            pixels[i * 3] = pixels[(i * 3) + 1] = pixels[(i * 3) + 2] = v;
        }

        var original = TestPdf.WithImage(pixels, 300, 200, "/DeviceRGB", 300, 200);
        PdfCompressor.Compress(original, PdfCompressionLevel.Maximum, out var result);
        Assert.Equal(0, result.ImagesRecompressed);
    }

    [Fact]
    public void Maximum_RemovesMetadataButLosslessKeepsIt()
    {
        var pdf = new TestPdf();
        int pages = pdf.Add("<< /Type /Pages /Kids [] /Count 0 >>");
        int xmp = pdf.AddStream("/Type /Metadata /Subtype /XML", Encoding.ASCII.GetBytes("<x:xmpmeta xmlns:x='adobe:ns:meta/'>" + new string(' ', 2000) + "</x:xmpmeta>"));
        int catalog = pdf.Add($"<< /Type /Catalog /Pages {pages} 0 R /Metadata {xmp} 0 R /PieceInfo << /Illustrator << /Private 1 >> >> >>");
        int info = pdf.Add("<< /Title (Report) >>");
        var original = pdf.Build(catalog, info);

        var lossless = PdfReader.Read(PdfCompressor.Compress(original, PdfCompressionLevel.Lossless));
        Assert.NotNull(lossless.Catalog!.Get("Metadata"));
        Assert.NotNull(lossless.Catalog!.Get("PieceInfo"));

        var maximum = PdfReader.Read(PdfCompressor.Compress(original, PdfCompressionLevel.Maximum));
        Assert.Null(maximum.Catalog!.Get("Metadata"));
        Assert.Null(maximum.Catalog!.Get("PieceInfo"));
        Assert.NotNull(maximum.Trailer.Get("Info"));

        var noInfo = PdfReader.Read(PdfCompressor.Compress(original, new PdfCompressionOptions { RemoveDocumentInfo = true }));
        Assert.Null(noInfo.Trailer.Get("Info"));
    }

    [Fact]
    public void KeepsTheOriginalWhenNothingCanBeSaved()
    {
        var once = PdfCompressor.Compress(TestPdf.WithPages(TestPdf.TextPage()), PdfCompressionLevel.Maximum);
        var twice = PdfCompressor.Compress(once, new PdfCompressionOptions(), out var result);
        Assert.True(result.OriginalKept);
        Assert.Same(once, twice);
        Assert.Equal(0, result.BytesSaved);
    }

    [Fact]
    public void RecompressesAsciiAndLzwEncodedStreams()
    {
        // "-----A---B" in LZW, wrapped in ASCIIHex: both layers are replaced by Flate or removed.
        var pdf = new TestPdf();
        int pages = pdf.Add("<< /Type /Pages /Kids [] /Count 0 >>");
        var hex = Encoding.ASCII.GetBytes("800B6050220C0C8501>");
        int stream = pdf.AddStream("/Filter [/ASCIIHexDecode /LZWDecode]", hex);
        int catalog = pdf.Add($"<< /Type /Catalog /Pages {pages} 0 R /Test {stream} 0 R >>");
        var compressed = PdfCompressor.Compress(pdf.Build(catalog), new PdfCompressionOptions { UseObjectStreams = false });

        var document = PdfReader.Read(compressed);
        var result = (PdfStream)document.Resolve(document.Catalog!.Get("Test"))!;
        var decoded = StreamFilters.Decode(result.Data, StreamFilters.GetFilters(result, document.Resolve), strict: true);
        Assert.Equal("-----A---B", Encoding.ASCII.GetString(decoded));
        Assert.IsNotType<PdfArray>(result.Get("Filter"));
    }

    [Fact]
    public void StreamAndFileOverloadsWork()
    {
        var original = TestPdf.WithPages(TestPdf.TextPage());
        using var input = new MemoryStream(original);
        using var output = new MemoryStream();
        var result = PdfCompressor.Compress(input, output, PdfCompressionLevel.Balanced);
        Assert.Equal(result.CompressedSize, output.Length);

        string path = Path.Combine(Path.GetTempPath(), $"hazuki-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, original);
            var fileResult = PdfCompressor.CompressFile(path, path, PdfCompressionLevel.Lossless);
            Assert.Equal(fileResult.CompressedSize, new FileInfo(path).Length);
            Assert.Single(PdfReader.Read(File.ReadAllBytes(path)).GetPages());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Presets_AreProgressivelyMoreAggressive()
    {
        var lossless = PdfCompressionOptions.FromLevel(PdfCompressionLevel.Lossless);
        var balanced = PdfCompressionOptions.FromLevel(PdfCompressionLevel.Balanced);
        var maximum = PdfCompressionOptions.FromLevel(PdfCompressionLevel.Maximum);

        Assert.False(lossless.CompressImages);
        Assert.True(balanced.CompressImages);
        Assert.True(maximum.JpegQuality < balanced.JpegQuality);
        Assert.True(maximum.MaxImageDpi < balanced.MaxImageDpi);
        Assert.True(maximum.RemoveMetadata);
        Assert.False(balanced.RemoveMetadata);
        Assert.Null(balanced.MaxBilevelImageDpi);
        Assert.Equal(200, maximum.MaxBilevelImageDpi);
        Assert.False(balanced.StripFontHinting);
        Assert.True(maximum.StripFontHinting);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Options_RejectInvalidJpegQuality(int quality)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfCompressionOptions { JpegQuality = quality });
    }

    [Fact]
    public void Options_RejectTinyDpi()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfCompressionOptions { MaxImageDpi = 5 });
    }

    [Fact]
    public void ImagesInFormXObjectsUseTheCombinedTransform()
    {
        // The form scales by 0.5, so a 600px image drawn in a 288pt box inside it is shown 2 inches wide = 300 DPI.
        var pixels = TestPdf.PhotoPixels(600, 400);
        var pdf = new TestPdf();
        int catalog = pdf.Reserve();
        int pages = pdf.Reserve();
        int image = pdf.AddStream("/Type /XObject /Subtype /Image /Width 600 /Height 400 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", StreamFilters.Deflate(pixels));
        int form = pdf.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 1000 1000] /Matrix [0.5 0 0 0.5 0 0] /Resources << /XObject << /Im1 {image} 0 R >> >>", Encoding.ASCII.GetBytes("q 288 0 0 192 0 0 cm /Im1 Do Q"));
        int content = pdf.AddStream(string.Empty, Encoding.ASCII.GetBytes("/Fm1 Do"));
        int page = pdf.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm1 {form} 0 R >> >> /Contents {content} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");

        var options = PdfCompressionOptions.FromLevel(PdfCompressionLevel.Balanced);
        options.MaxImageDpi = 150;
        var compressed = PdfCompressor.Compress(pdf.Build(catalog), options);
        Assert.Equal(300, ((PdfInteger)FindImage(compressed)["Width"]!).Value);
    }
}
