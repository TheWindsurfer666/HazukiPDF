using HazukiPdf.Filters;
using HazukiPdf.Objects;
using HazukiPdf.Optimization;

namespace HazukiPdf.Imaging;

internal readonly record struct BilevelStats(int Recompressed, int Downsampled);

/// <summary>
/// Re-encodes black-and-white (1-bit) images, typically scanned pages, with lossless JBIG2, optionally downsampling
/// those shown above <see cref="PdfCompressionOptions.MaxBilevelImageDpi"/> first.
/// </summary>
internal static class BilevelImageOptimizer
{
    // Only switch when it pays: JBIG2 decodes slower than Flate.
    private const double RequiredRatio = 0.95;

    // Downsample only when the image is noticeably above the target.
    private const double DownsampleThreshold = 1.2;

    public static BilevelStats Run(PdfDocument document, PdfCompressionOptions options)
    {
        if (!options.UseJbig2 && options.MaxBilevelImageDpi == null)
        {
            return default;
        }

        var resolutions = options.MaxBilevelImageDpi.HasValue ? ImageUsageScanner.Scan(document) : new Dictionary<int, ImageResolution>();
        var masks = options.MaxBilevelImageDpi.HasValue ? ImageOptimizer.CollectMaskImages(document) : new HashSet<int>();
        int changed = 0;
        int downsampled = 0;
        bool usedJbig2 = false;
        foreach (var (number, obj) in document.Objects)
        {
            if (obj is not PdfStream stream || stream.GetName("Subtype") != "Image")
            {
                continue;
            }

            ImageResolution? resolution = masks.Contains(number) || !resolutions.TryGetValue(number, out var r) ? null : r;
            var outcome = TryOptimize(document, stream, options, resolution);
            if (outcome != Outcome.Unchanged)
            {
                changed++;
                usedJbig2 |= stream.GetName("Filter") == "JBIG2Decode";
            }

            if (outcome == Outcome.Downsampled)
            {
                downsampled++;
            }
        }

        if (usedJbig2 && string.CompareOrdinal(document.Version, "1.4") < 0)
        {
            document.Version = "1.4"; // JBIG2Decode arrived in PDF 1.4
        }

        return new BilevelStats(changed, downsampled);
    }

    private enum Outcome
    {
        Unchanged,
        Recompressed,
        Downsampled,
    }

    private static Outcome TryOptimize(PdfDocument document, PdfStream image, PdfCompressionOptions options, ImageResolution? resolution)
    {
        if (image.Get("F") != null || image.Get("SMaskInData") != null)
        {
            return Outcome.Unchanged;
        }

        bool stencil = document.Get(image, "ImageMask") is PdfBoolean { Value: true };
        int bpc = (int)(document.Get(image, "BitsPerComponent").AsNumber() ?? (stencil ? 1 : 0));
        if (bpc != 1 || (!stencil && !IsSingleComponent(document, document.Get(image, "ColorSpace"))))
        {
            return Outcome.Unchanged;
        }

        int width = (int)(document.Get(image, "Width").AsNumber() ?? 0);
        int height = (int)(document.Get(image, "Height").AsNumber() ?? 0);
        if (width < 16 || height < 16 || (long)width * height > 400_000_000)
        {
            return Outcome.Unchanged;
        }

        var filters = StreamFilters.GetFilters(image, document.Resolve);
        if (filters.Any(f => !StreamFilters.DecodableFilters.Contains(f.Name)))
        {
            return Outcome.Unchanged; // already CCITT, JBIG2 or something this library cannot decode
        }

        int newWidth = width;
        int newHeight = height;
        if (options.MaxBilevelImageDpi is int maxDpi && resolution is { } shown)
        {
            if (shown.DpiX > maxDpi * DownsampleThreshold)
            {
                newWidth = Math.Max(1, (int)Math.Round(width * maxDpi / shown.DpiX));
            }

            if (shown.DpiY > maxDpi * DownsampleThreshold)
            {
                newHeight = Math.Max(1, (int)Math.Round(height * maxDpi / shown.DpiY));
            }
        }

        bool resize = newWidth != width || newHeight != height;
        if (!resize && !options.UseJbig2)
        {
            return Outcome.Unchanged;
        }

        try
        {
            var packed = StreamFilters.Decode(image.Data, filters, strict: true);
            if (packed.Length < (long)((width + 7) / 8) * height)
            {
                return Outcome.Unchanged;
            }

            if (resize)
            {
                packed = Downsample(packed, width, height, newWidth, newHeight);
            }

            byte[] data = packed;
            var newFilters = Array.Empty<FilterSpec>();
            if (options.UseJbig2)
            {
                var encoded = Jbig2Encoder.Encode(packed, newWidth, newHeight);
                if (resize || encoded.Length < image.Data.Length * RequiredRatio)
                {
                    data = encoded;
                    newFilters = new[] { new FilterSpec("JBIG2Decode", null) };
                }
                else
                {
                    return Outcome.Unchanged;
                }
            }

            // Stored unfiltered here when JBIG2 is off; the stream optimizer deflates it afterwards.
            image.Data = data;
            StreamOptimizer.SetFilters(image, newFilters);
            image.Set("Width", new PdfInteger(newWidth));
            image.Set("Height", new PdfInteger(newHeight));
            return resize ? Outcome.Downsampled : Outcome.Recompressed;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return Outcome.Unchanged;
        }
    }

    /// <summary>Downsamples packed 1-bit rows by area averaging, then thresholds at half.</summary>
    private static byte[] Downsample(byte[] packed, int width, int height, int newWidth, int newHeight)
    {
        int stride = (width + 7) / 8;
        var levels = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                levels[(y * width) + x] = (packed[(y * stride) + (x >> 3)] & (0x80 >> (x & 7))) != 0 ? (byte)255 : (byte)0;
            }
        }

        var small = ImageResampler.Downsample(levels, width, height, 1, newWidth, newHeight);
        int newStride = (newWidth + 7) / 8;
        var result = new byte[newStride * newHeight];
        for (int y = 0; y < newHeight; y++)
        {
            for (int x = 0; x < newWidth; x++)
            {
                if (small[(y * newWidth) + x] >= 128)
                {
                    result[(y * newStride) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
        }

        return result;
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
