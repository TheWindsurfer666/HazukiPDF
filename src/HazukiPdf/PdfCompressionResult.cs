namespace HazukiPdf;

/// <summary>What a compression run did.</summary>
public sealed class PdfCompressionResult
{
    /// <summary>Size of the input in bytes.</summary>
    public long OriginalSize { get; init; }

    /// <summary>Size of the output in bytes.</summary>
    public long CompressedSize { get; init; }

    /// <summary>Bytes saved; zero when the original was kept, negative if removing data still grew the file.</summary>
    public long BytesSaved => OriginalSize - CompressedSize;

    /// <summary>Output size as a fraction of the input size (0.4 means the output is 40% of the original).</summary>
    public double Ratio => OriginalSize == 0 ? 1 : (double)CompressedSize / OriginalSize;

    /// <summary>
    /// True when the optimized file was not smaller than the input, so the input was returned unchanged.
    /// This does not happen when removal options (such as <see cref="PdfCompressionOptions.RemoveMetadata"/>) removed something.
    /// </summary>
    public bool OriginalKept { get; init; }

    /// <summary>Number of streams whose encoding was replaced with a smaller one.</summary>
    public int StreamsRecompressed { get; init; }

    /// <summary>Number of images re-encoded as JPEG (possibly downsampled).</summary>
    public int ImagesRecompressed { get; init; }

    /// <summary>Number of images that were downsampled.</summary>
    public int ImagesDownsampled { get; init; }

    /// <summary>Number of duplicate objects merged into an identical one.</summary>
    public int DuplicateObjectsRemoved { get; init; }

    /// <summary>Number of objects dropped because nothing referred to them.</summary>
    public int UnusedObjectsRemoved { get; init; }
}
