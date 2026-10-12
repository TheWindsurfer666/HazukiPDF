using HazukiPdf.Filters;
using HazukiPdf.Objects;
using HazukiPdf.Optimization;

namespace HazukiPdf.Imaging;

/// <summary>
/// Re-encodes black-and-white (1-bit) images, typically scanned pages, with lossless JBIG2. The pixels do not change.
/// </summary>
internal static class BilevelImageOptimizer
{
    // Only switch when it pays: JBIG2 decodes slower than Flate.
    private const double RequiredRatio = 0.95;

    public static int Run(PdfDocument document)
    {
        int changed = 0;
        foreach (var obj in document.Objects.Values)
        {
            if (obj is PdfStream stream && stream.GetName("Subtype") == "Image" && TryOptimize(document, stream))
            {
                changed++;
            }
        }

        if (changed > 0 && string.CompareOrdinal(document.Version, "1.4") < 0)
        {
            document.Version = "1.4"; // JBIG2Decode arrived in PDF 1.4
        }

        return changed;
    }

    public static bool TryOptimize(PdfDocument document, PdfStream image)
    {
        if (image.Get("F") != null || image.Get("SMaskInData") != null)
        {
            return false;
        }

        bool stencil = document.Get(image, "ImageMask") is PdfBoolean { Value: true };
        int bpc = (int)(document.Get(image, "BitsPerComponent").AsNumber() ?? (stencil ? 1 : 0));
        if (bpc != 1 || (!stencil && !IsSingleComponent(document, document.Get(image, "ColorSpace"))))
        {
            return false;
        }

        int width = (int)(document.Get(image, "Width").AsNumber() ?? 0);
        int height = (int)(document.Get(image, "Height").AsNumber() ?? 0);
        if (width < 16 || height < 16 || (long)width * height > 400_000_000)
        {
            return false;
        }

        var filters = StreamFilters.GetFilters(image, document.Resolve);
        if (filters.Any(f => !StreamFilters.DecodableFilters.Contains(f.Name)))
        {
            return false; // already CCITT, JBIG2 or something this library cannot decode
        }

        try
        {
            var packed = StreamFilters.Decode(image.Data, filters, strict: true);
            if (packed.Length < (long)((width + 7) / 8) * height)
            {
                return false;
            }

            var encoded = Jbig2Encoder.Encode(packed, width, height);
            if (encoded.Length >= image.Data.Length * RequiredRatio)
            {
                return false;
            }

            image.Data = encoded;
            StreamOptimizer.SetFilters(image, new[] { new FilterSpec("JBIG2Decode", null) });
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static bool IsSingleComponent(PdfDocument document, PdfObject? colorSpace) => colorSpace switch
    {
        PdfName name => name.Value is "DeviceGray" or "G",
        PdfArray array when array.Count >= 2 && document.Resolve(array[0]) is PdfName family => family.Value switch
        {
            "CalGray" => true,
            "ICCBased" => document.Resolve(array[1]) is PdfStream profile && document.Get(profile, "N").AsNumber() == 1,
            "Indexed" or "I" => true, // a 1-bit index into a palette, still one sample per pixel
            _ => false,
        },
        _ => false,
    };
}
