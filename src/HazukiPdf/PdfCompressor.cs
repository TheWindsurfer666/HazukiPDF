using HazukiPdf.Imaging;
using HazukiPdf.Objects;
using HazukiPdf.Optimization;
using HazukiPdf.Parsing;
using HazukiPdf.Writing;

namespace HazukiPdf;

/// <summary>
/// Makes PDF files smaller. Uses only the .NET base class library.
/// </summary>
/// <example>
/// <code>
/// var result = PdfCompressor.CompressFile("in.pdf", "out.pdf", PdfCompressionLevel.Balanced);
/// Console.WriteLine($"Saved {result.BytesSaved} bytes");
/// </code>
/// </example>
public static class PdfCompressor
{
    /// <summary>Compresses a PDF held in memory using a preset.</summary>
    public static byte[] Compress(byte[] pdf, PdfCompressionLevel level) => Compress(pdf, PdfCompressionOptions.FromLevel(level), out _);

    /// <summary>Compresses a PDF held in memory using a preset and reports what was done.</summary>
    public static byte[] Compress(byte[] pdf, PdfCompressionLevel level, out PdfCompressionResult result) =>
        Compress(pdf, PdfCompressionOptions.FromLevel(level), out result);

    /// <summary>Compresses a PDF held in memory.</summary>
    public static byte[] Compress(byte[] pdf, PdfCompressionOptions? options = null) => Compress(pdf, options, out _);

    /// <summary>Compresses a PDF held in memory and reports what was done.</summary>
    public static byte[] Compress(byte[] pdf, PdfCompressionOptions? options, out PdfCompressionResult result)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        options ??= new PdfCompressionOptions();

        var document = PdfReader.Read(pdf, options.Password);
        int loaded = document.Objects.Count;

        bool removedData = MetadataCleaner.Run(document, options);

        int jpegs = options.OptimizeJpegs ? JpegOptimizer.Run(document) : 0;
        var bilevel = BilevelImageOptimizer.Run(document, options);
        int fonts = FontSubsetter.Run(document, options.SubsetFonts, options.StripFontHinting);
        var imageStats = options.CompressImages ? ImageOptimizer.Run(document, options) : default;

        int streams = options.RecompressStreams ? StreamOptimizer.Run(document) : 0;
        int duplicates = options.DeduplicateObjects ? Deduplicator.Run(document) : 0;

        var output = PdfWriter.Write(
            document,
            new PdfWriterOptions { UseObjectStreams = options.UseObjectStreams, RemoveUnusedObjects = options.RemoveUnusedObjects },
            out int written);

        // Never make a file bigger, unless the caller asked for data to be removed and some was.
        bool keepOriginal = output.Length >= pdf.Length && !removedData;
        result = new PdfCompressionResult
        {
            OriginalSize = pdf.Length,
            CompressedSize = keepOriginal ? pdf.Length : output.Length,
            OriginalKept = keepOriginal,
            StreamsRecompressed = keepOriginal ? 0 : streams + jpegs + bilevel.Recompressed + fonts,
            ImagesRecompressed = keepOriginal ? 0 : imageStats.Recompressed,
            ImagesDownsampled = keepOriginal ? 0 : imageStats.Downsampled + bilevel.Downsampled,
            DuplicateObjectsRemoved = keepOriginal ? 0 : duplicates,
            UnusedObjectsRemoved = keepOriginal ? 0 : Math.Max(0, loaded - duplicates - written),
        };

        return keepOriginal ? pdf : output;
    }

    /// <summary>Reads a PDF from <paramref name="input"/> and writes the compressed file to <paramref name="output"/>.</summary>
    public static PdfCompressionResult Compress(Stream input, Stream output, PdfCompressionLevel level) =>
        Compress(input, output, PdfCompressionOptions.FromLevel(level));

    /// <summary>Reads a PDF from <paramref name="input"/> and writes the compressed file to <paramref name="output"/>.</summary>
    public static PdfCompressionResult Compress(Stream input, Stream output, PdfCompressionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        var bytes = Compress(buffer.ToArray(), options, out var result);
        output.Write(bytes, 0, bytes.Length);
        return result;
    }

    /// <summary>Compresses a PDF file on disk. The output path may equal the input path.</summary>
    public static PdfCompressionResult CompressFile(string inputPath, string outputPath, PdfCompressionLevel level) =>
        CompressFile(inputPath, outputPath, PdfCompressionOptions.FromLevel(level));

    /// <summary>Compresses a PDF file on disk. The output path may equal the input path.</summary>
    public static PdfCompressionResult CompressFile(string inputPath, string outputPath, PdfCompressionOptions? options = null)
    {
        var bytes = Compress(File.ReadAllBytes(inputPath), options, out var result);
        File.WriteAllBytes(outputPath, bytes);
        return result;
    }
}
