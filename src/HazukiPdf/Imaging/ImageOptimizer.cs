using HazukiPdf.Filters;
using HazukiPdf.Objects;
using HazukiPdf.Optimization;

namespace HazukiPdf.Imaging;

internal readonly record struct ImageStats(int Recompressed, int Downsampled);

/// <summary>
/// Re-encodes image XObjects as JPEG, downsampling those drawn above the configured resolution.
/// Only 8-bit grayscale and RGB images are touched; masks, indexed, CMYK and other special images are left alone,
/// and an image is only replaced when the result is meaningfully smaller.
/// </summary>
internal static class ImageOptimizer
{
    // Downsample only when the image is noticeably above the target, to avoid a pointless generation loss.
    private const double DownsampleThreshold = 1.2;

    // Images smaller than this (in pixels) are usually icons or line art that JPEG would blur.
    private const int MinimumPixels = 32 * 32;

    // Images with fewer distinct colors (or gray levels) than this are diagrams or screenshots; JPEG makes them blurry.
    private const int MinimumDistinctColors = 256;
    private const int MinimumDistinctGrays = 64;

    public static ImageStats Run(PdfDocument document, PdfCompressionOptions options)
    {
        var resolutions = options.MaxImageDpi.HasValue ? ImageUsageScanner.Scan(document) : new Dictionary<int, ImageResolution>();
        var masks = CollectMaskImages(document);
        int recompressed = 0;
        int downsampled = 0;
        foreach (var (number, obj) in document.Objects.ToList())
        {
            if (obj is not PdfStream stream || stream.GetName("Subtype") != "Image" || masks.Contains(number))
            {
                continue;
            }

            resolutions.TryGetValue(number, out var resolution);
            bool hasResolution = resolutions.ContainsKey(number);
            try
            {
                var outcome = TryOptimize(document, stream, options, hasResolution ? resolution : null);
                if (outcome != Outcome.Unchanged)
                {
                    recompressed++;
                }

                if (outcome == Outcome.Downsampled)
                {
                    downsampled++;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException or OverflowException)
            {
                // Leave images this library cannot read exactly as they are.
            }
        }

        return new ImageStats(recompressed, downsampled);
    }

    private enum Outcome
    {
        Unchanged,
        Recompressed,
        Downsampled,
    }

    /// <summary>Images used as soft masks or stencil masks of other images must stay lossless.</summary>
    private static HashSet<int> CollectMaskImages(PdfDocument document)
    {
        var masks = new HashSet<int>();
        foreach (var obj in document.Objects.Values)
        {
            if (obj is PdfStream s && s.GetName("Subtype") == "Image")
            {
                if (s.Get("SMask") is PdfReference smask)
                {
                    masks.Add(smask.ObjectNumber);
                }

                if (s.Get("Mask") is PdfReference mask)
                {
                    masks.Add(mask.ObjectNumber);
                }
            }
            else if (obj is PdfDictionary d && d.GetName("Type") == "ExtGState" && d.Get("SMask") is PdfReference softMask)
            {
                masks.Add(softMask.ObjectNumber);
            }
        }

        return masks;
    }

    private static Outcome TryOptimize(PdfDocument document, PdfStream image, PdfCompressionOptions options, ImageResolution? resolution)
    {
        if (document.Get(image, "ImageMask") is PdfBoolean { Value: true } ||
            document.Get(image, "Mask") is PdfArray ||
            image.Get("SMaskInData") != null ||
            image.Get("F") != null)
        {
            return Outcome.Unchanged;
        }

        int width = (int)(document.Get(image, "Width").AsNumber() ?? 0);
        int height = (int)(document.Get(image, "Height").AsNumber() ?? 0);
        int bpc = (int)(document.Get(image, "BitsPerComponent").AsNumber() ?? 8);
        int components = ColorComponents(document, document.Get(image, "ColorSpace"), out bool isRgbSpace);
        if (width <= 0 || height <= 0 || components == 0 || bpc != 8 || (long)width * height < MinimumPixels ||
            (long)width * height > 100_000_000)
        {
            return Outcome.Unchanged;
        }

        var filters = StreamFilters.GetFilters(image, document.Resolve);
        byte[] pixels;
        bool sourceIsJpeg = filters.Count > 0 && filters[^1].Name == "DCTDecode";
        if (sourceIsJpeg)
        {
            var prefix = filters.Take(filters.Count - 1).ToList();
            if (prefix.Any(f => !StreamFilters.DecodableFilters.Contains(f.Name)))
            {
                return Outcome.Unchanged;
            }

            var jpeg = StreamFilters.Decode(image.Data, prefix, strict: true);
            int? colorTransform = filters[^1].Parameters?.Get("ColorTransform") is PdfInteger ct ? (int)ct.Value : null;
            var decoded = JpegDecoder.Decode(jpeg, colorTransform);
            if (decoded.Width != width || decoded.Height != height || decoded.Components != components)
            {
                return Outcome.Unchanged;
            }

            pixels = decoded.Pixels;
        }
        else
        {
            if (filters.Any(f => !StreamFilters.DecodableFilters.Contains(f.Name)))
            {
                return Outcome.Unchanged;
            }

            pixels = StreamFilters.Decode(image.Data, filters, strict: true);
            if (pixels.Length < (long)width * height * components)
            {
                return Outcome.Unchanged;
            }

            bool gray = components == 1 || IsGray(pixels, width * height, 0);
            if (CountColors(pixels, width * height, components) < (gray ? MinimumDistinctGrays : MinimumDistinctColors))
            {
                return Outcome.Unchanged;
            }
        }

        // Work out the target size.
        int newWidth = width;
        int newHeight = height;
        if (options.MaxImageDpi is int maxDpi && resolution is { } r && !HasMatte(document, image))
        {
            if (r.DpiX > maxDpi * DownsampleThreshold)
            {
                newWidth = Math.Max(1, (int)Math.Round(width * maxDpi / r.DpiX));
            }

            if (r.DpiY > maxDpi * DownsampleThreshold)
            {
                newHeight = Math.Max(1, (int)Math.Round(height * maxDpi / r.DpiY));
            }
        }

        bool resized = newWidth != width || newHeight != height;
        if (resized)
        {
            pixels = ImageResampler.Downsample(pixels, width, height, components, newWidth, newHeight);
        }

        int outComponents = components;
        bool toGray = components == 3 && isRgbSpace && image.Get("Decode") == null && IsGray(pixels, newWidth * newHeight, sourceIsJpeg ? 3 : 0);
        if (toGray)
        {
            pixels = ToGray(pixels, newWidth * newHeight);
            outComponents = 1;
        }

        var encoded = JpegEncoder.Encode(pixels, newWidth, newHeight, outComponents, options.JpegQuality);

        // Re-encoding costs quality, so it has to pay for itself.
        double required = resized ? 1.0 : 0.9;
        if (encoded.Length >= image.Data.Length * required)
        {
            return Outcome.Unchanged;
        }

        image.Data = encoded;
        StreamOptimizer.SetFilters(image, new[] { new FilterSpec("DCTDecode", null) });
        image.Set("Width", new PdfInteger(newWidth));
        image.Set("Height", new PdfInteger(newHeight));
        image.Set("BitsPerComponent", new PdfInteger(8));
        if (toGray)
        {
            image.Set("ColorSpace", PdfName.Get("DeviceGray"));
        }

        return resized ? Outcome.Downsampled : Outcome.Recompressed;
    }

    private static bool HasMatte(PdfDocument document, PdfStream image) =>
        document.Get(image, "SMask") is PdfStream smask && smask.Get("Matte") != null;

    /// <summary>Returns 1 or 3 for supported gray and RGB color spaces, otherwise 0.</summary>
    private static int ColorComponents(PdfDocument document, PdfObject? colorSpace, out bool isRgb)
    {
        isRgb = false;
        switch (colorSpace)
        {
            case PdfName name:
                if (name.Value is "DeviceGray" or "G")
                {
                    return 1;
                }

                if (name.Value is "DeviceRGB" or "RGB")
                {
                    isRgb = true;
                    return 3;
                }

                return 0;
            case PdfArray array when array.Count >= 2 && document.Resolve(array[0]) is PdfName family:
                switch (family.Value)
                {
                    case "CalGray":
                        return 1;
                    case "CalRGB":
                        isRgb = true;
                        return 3;
                    case "ICCBased" when document.Resolve(array[1]) is PdfStream profile:
                        int n = (int)(document.Get(profile, "N").AsNumber() ?? 0);
                        isRgb = n == 3;
                        return n is 1 or 3 ? n : 0;
                }

                return 0;
            default:
                return 0;
        }
    }

    private static int CountColors(byte[] pixels, int count, int components)
    {
        var colors = new HashSet<int>();
        int step = Math.Max(1, count / 20_000);
        for (int i = 0; i < count; i += step)
        {
            int o = i * components;
            int key = components == 1 ? pixels[o] : (pixels[o] << 16) | (pixels[o + 1] << 8) | pixels[o + 2];
            colors.Add(key);
            if (colors.Count >= MinimumDistinctColors)
            {
                break;
            }
        }

        return colors.Count;
    }

    private static bool IsGray(byte[] pixels, int count, int tolerance)
    {
        for (int i = 0; i < count; i++)
        {
            int o = i * 3;
            if (Math.Abs(pixels[o] - pixels[o + 1]) > tolerance || Math.Abs(pixels[o + 1] - pixels[o + 2]) > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] ToGray(byte[] pixels, int count)
    {
        var gray = new byte[count];
        for (int i = 0; i < count; i++)
        {
            gray[i] = pixels[(i * 3) + 1];
        }

        return gray;
    }
}
