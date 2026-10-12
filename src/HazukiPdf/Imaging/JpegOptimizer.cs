using HazukiPdf.Filters;
using HazukiPdf.Objects;
using HazukiPdf.Optimization;

namespace HazukiPdf.Imaging;

/// <summary>
/// Losslessly shrinks JPEG (DCTDecode) streams by re-encoding their coefficients with Huffman tables optimized
/// for each image and dropping extra marker segments (EXIF, embedded previews). The pixels do not change.
/// </summary>
internal static class JpegOptimizer
{
    public static int Run(PdfDocument document)
    {
        int changed = 0;
        foreach (var obj in document.Objects.Values)
        {
            if (obj is PdfStream stream && TryOptimize(document, stream))
            {
                changed++;
            }
        }

        return changed;
    }

    public static bool TryOptimize(PdfDocument document, PdfStream stream)
    {
        var filters = StreamFilters.GetFilters(stream, document.Resolve);
        if (filters.Count == 0 || filters[^1].Name != "DCTDecode" || stream.Get("F") != null)
        {
            return false;
        }

        var prefix = filters.Take(filters.Count - 1).ToList();
        if (prefix.Any(f => !StreamFilters.DecodableFilters.Contains(f.Name)))
        {
            return false;
        }

        try
        {
            var jpeg = StreamFilters.Decode(stream.Data, prefix, strict: true);
            var optimized = JpegEncoder.Write(JpegDecoder.DecodeCoefficients(jpeg));
            if (optimized.Length >= jpeg.Length)
            {
                return false;
            }

            stream.Data = optimized;
            StreamOptimizer.SetFilters(stream, new[] { filters[^1] });
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return false; // progressive, arithmetic-coded or damaged: leave as is
        }
    }
}
