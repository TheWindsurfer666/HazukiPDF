using HazukiPdf.Filters;
using HazukiPdf.Objects;

namespace HazukiPdf.Optimization;

/// <summary>
/// Re-encodes streams with maximum Deflate. Filters this library can decode (Flate, LZW, ASCII85, ASCIIHex,
/// RunLength) are peeled off and replaced with a single Flate filter; filters it cannot decode (such as
/// DCTDecode) are kept as they are. A stream is only changed when the result is smaller.
/// </summary>
internal static class StreamOptimizer
{
    public static int Run(PdfDocument document)
    {
        int changed = 0;
        foreach (var obj in document.Objects.Values)
        {
            if (obj is PdfStream stream && TryRecompress(document, stream))
            {
                changed++;
            }
        }

        return changed;
    }

    public static bool TryRecompress(PdfDocument document, PdfStream stream)
    {
        // PDF/A requires metadata streams to stay unfiltered, and external file streams have no data here.
        if (stream.GetName("Type") == "Metadata" || stream.Get("F") != null)
        {
            return false;
        }

        var filters = StreamFilters.GetFilters(stream, document.Resolve);
        int prefix = 0;
        PdfDictionary? predictorParms = null;
        while (prefix < filters.Count && StreamFilters.DecodableFilters.Contains(filters[prefix].Name))
        {
            prefix++;
            if (StreamFilters.HasPredictor(filters[prefix - 1]))
            {
                predictorParms = filters[prefix - 1].Parameters;
                break;
            }
        }

        if (filters.Count > 0 && prefix == 0)
        {
            return false;
        }

        byte[] decoded = stream.Data;
        try
        {
            for (int i = 0; i < prefix; i++)
            {
                decoded = StreamFilters.DecodeOne(decoded, filters[i], strict: true, applyPredictor: false);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException)
        {
            return false; // damaged data: leave it exactly as it was
        }

        var remaining = filters.Skip(prefix).ToList();
        byte[] best = stream.Data;
        List<FilterSpec>? bestFilters = null;

        var flated = StreamFilters.Deflate(decoded);
        if (flated.Length < best.Length)
        {
            best = flated;
            bestFilters = new List<FilterSpec> { new("FlateDecode", predictorParms) };
            bestFilters.AddRange(remaining);
        }

        // Already-compressed data (such as JPEG) behind an ASCII filter: just drop the ASCII layer.
        if (predictorParms == null && remaining.Count > 0 && decoded.Length < best.Length)
        {
            best = decoded;
            bestFilters = remaining;
        }

        if (bestFilters == null)
        {
            return false;
        }

        stream.Data = best;
        SetFilters(stream, bestFilters);
        return true;
    }

    public static void SetFilters(PdfDictionary dict, IReadOnlyList<FilterSpec> filters)
    {
        if (filters.Count == 0)
        {
            dict.Remove("Filter");
            dict.Remove("DecodeParms");
            return;
        }

        if (filters.Count == 1)
        {
            dict.Set("Filter", PdfName.Get(filters[0].Name));
            dict.Set("DecodeParms", filters[0].Parameters);
            return;
        }

        dict.Set("Filter", new PdfArray(filters.Select(f => (PdfObject)PdfName.Get(f.Name))));
        if (filters.All(f => f.Parameters == null))
        {
            dict.Remove("DecodeParms");
        }
        else
        {
            dict.Set("DecodeParms", new PdfArray(filters.Select(f => (PdfObject?)f.Parameters ?? PdfNull.Instance)));
        }
    }
}
