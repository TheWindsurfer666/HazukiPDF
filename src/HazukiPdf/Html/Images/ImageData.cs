namespace HazukiPdf.Html.Images;

/// <summary>A decoded image ready to be written as a PDF image XObject.</summary>
internal sealed class ImageData
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>DeviceRGB, DeviceGray or DeviceCMYK.</summary>
    public required string ColorSpace { get; init; }

    /// <summary>
    /// For JPEG images the original file (written with DCTDecode); otherwise uncompressed 8-bit samples.
    /// </summary>
    public required byte[] Data { get; init; }

    public bool IsJpeg { get; init; }

    /// <summary>Adobe CMYK JPEGs store inverted values and need a /Decode array.</summary>
    public bool InvertedCmyk { get; init; }

    /// <summary>Optional 8-bit alpha channel, written as a soft mask.</summary>
    public byte[]? Alpha { get; init; }
}
