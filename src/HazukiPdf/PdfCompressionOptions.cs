namespace HazukiPdf;

/// <summary>Ready-made compression presets.</summary>
public enum PdfCompressionLevel
{
    /// <summary>
    /// Changes only how the file is stored, never how it looks: recompresses streams with maximum Deflate,
    /// packs objects into object streams and drops unused and duplicate objects.
    /// </summary>
    Lossless,

    /// <summary>
    /// Everything in <see cref="Lossless"/>, plus JPEG recompression of photos at quality 80, downsampling of images
    /// shown above 200 DPI, and removal of page thumbnails and private application data.
    /// </summary>
    Balanced,

    /// <summary>
    /// Everything in <see cref="Balanced"/>, but with JPEG quality 60, downsampling above 120 DPI, black-and-white
    /// scans downsampled to 200 DPI, font hinting and XMP metadata removed. Best for on-screen reading and email.
    /// </summary>
    Maximum,
}

/// <summary>
/// Fine-grained compression settings. Start from a preset with <see cref="FromLevel"/> and adjust individual
/// properties, or build one from scratch (a new instance equals the <see cref="PdfCompressionLevel.Lossless"/> preset).
/// </summary>
public sealed class PdfCompressionOptions
{
    private int _jpegQuality = 75;
    private int? _maxImageDpi = 150;
    private int? _maxBilevelImageDpi;

    /// <summary>Decode Flate, LZW, ASCII85, ASCIIHex and RunLength streams and re-encode them with maximum Deflate; compress uncompressed streams.</summary>
    public bool RecompressStreams { get; set; } = true;

    /// <summary>
    /// Losslessly re-encode JPEG images with Huffman tables optimized for each image (the pixels stay identical).
    /// </summary>
    public bool OptimizeJpegs { get; set; } = true;

    /// <summary>
    /// Losslessly re-encode black-and-white (1-bit) images, such as scanned pages, with JBIG2 when that is smaller
    /// (requires PDF 1.4 readers). The pixels stay identical.
    /// </summary>
    public bool UseJbig2 { get; set; } = true;

    /// <summary>
    /// Remove unused glyphs from embedded TrueType fonts that address glyphs directly. Some programs, such as
    /// Microsoft Print to PDF, embed whole fonts of several hundred kilobytes each.
    /// </summary>
    public bool SubsetFonts { get; set; } = true;

    /// <summary>
    /// Remove hinting from embedded TrueType fonts: instructions that only adjust how small text is drawn on
    /// screen. Shapes and printing are unaffected.
    /// </summary>
    public bool StripFontHinting { get; set; }

    /// <summary>
    /// Black-and-white images (such as scanned pages) displayed at a higher resolution than this are downsampled
    /// to it. Null (the default) keeps their resolution.
    /// </summary>
    public int? MaxBilevelImageDpi
    {
        get => _maxBilevelImageDpi;
        set => _maxBilevelImageDpi = value is null or >= 72
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "The maximum black-and-white image DPI must be at least 72.");
    }

    /// <summary>Pack small objects into compressed object streams and write a cross-reference stream (requires PDF 1.5 readers).</summary>
    public bool UseObjectStreams { get; set; } = true;

    /// <summary>Drop objects that nothing in the document refers to, such as leftovers from earlier edits.</summary>
    public bool RemoveUnusedObjects { get; set; } = true;

    /// <summary>Merge byte-identical objects (for example the same font or image embedded several times) into one.</summary>
    public bool DeduplicateObjects { get; set; } = true;

    /// <summary>Remove XMP metadata streams. Note that this breaks PDF/A conformance.</summary>
    public bool RemoveMetadata { get; set; }

    /// <summary>Remove the document information dictionary (title, author, producer and so on).</summary>
    public bool RemoveDocumentInfo { get; set; }

    /// <summary>Remove embedded page thumbnail images; viewers generate their own.</summary>
    public bool RemoveThumbnails { get; set; }

    /// <summary>Remove private application data (/PieceInfo) that editors such as Illustrator store in the file.</summary>
    public bool RemovePrivateApplicationData { get; set; }

    /// <summary>Re-encode 8-bit grayscale and RGB images as JPEG, downsampling them according to <see cref="MaxImageDpi"/>.</summary>
    public bool CompressImages { get; set; }

    /// <summary>JPEG quality from 1 (smallest) to 100 (best) used when <see cref="CompressImages"/> is on.</summary>
    public int JpegQuality
    {
        get => _jpegQuality;
        set => _jpegQuality = value is >= 1 and <= 100
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "JPEG quality must be between 1 and 100.");
    }

    /// <summary>
    /// Images displayed at a higher resolution than this are downsampled to it. Null disables downsampling.
    /// Only used when <see cref="CompressImages"/> is on.
    /// </summary>
    public int? MaxImageDpi
    {
        get => _maxImageDpi;
        set => _maxImageDpi = value is null or >= 10
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "The maximum image DPI must be at least 10.");
    }

    /// <summary>
    /// Password for encrypted files (user or owner password). When null, files that open without a password
    /// (most "protected" files only restrict printing or editing) are still read. Encrypted input is written
    /// out unencrypted.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>Returns a new options instance for a preset.</summary>
    public static PdfCompressionOptions FromLevel(PdfCompressionLevel level)
    {
        var options = new PdfCompressionOptions();
        switch (level)
        {
            case PdfCompressionLevel.Lossless:
                break;
            case PdfCompressionLevel.Balanced:
                options.CompressImages = true;
                options.JpegQuality = 80;
                options.MaxImageDpi = 200;
                options.RemoveThumbnails = true;
                options.RemovePrivateApplicationData = true;
                break;
            case PdfCompressionLevel.Maximum:
                options.CompressImages = true;
                options.JpegQuality = 60;
                options.MaxImageDpi = 120;
                options.RemoveThumbnails = true;
                options.RemovePrivateApplicationData = true;
                options.RemoveMetadata = true;
                options.StripFontHinting = true;
                options.MaxBilevelImageDpi = 200;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown compression level.");
        }

        return options;
    }
}
