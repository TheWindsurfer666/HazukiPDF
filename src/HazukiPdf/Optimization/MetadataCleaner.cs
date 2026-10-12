using HazukiPdf.Objects;

namespace HazukiPdf.Optimization;

/// <summary>Removes optional data that does not affect how pages look. Orphaned objects are dropped by the writer.</summary>
internal static class MetadataCleaner
{
    /// <summary>Returns true when anything was removed.</summary>
    public static bool Run(PdfDocument document, PdfCompressionOptions options)
    {
        bool removed = options.RemoveDocumentInfo && document.Trailer.Remove("Info");
        if (!options.RemoveMetadata && !options.RemoveThumbnails && !options.RemovePrivateApplicationData)
        {
            return removed;
        }

        foreach (var obj in document.Objects.Values.ToList())
        {
            if (obj is PdfDictionary dict)
            {
                removed |= Clean(dict, options, 0);
            }
        }

        return removed;
    }

    private static bool Clean(PdfDictionary dict, PdfCompressionOptions options, int depth)
    {
        if (depth > 64)
        {
            return false;
        }

        bool removed = false;
        if (options.RemoveMetadata)
        {
            removed |= dict.Remove("Metadata");
        }

        if (options.RemovePrivateApplicationData)
        {
            removed |= dict.Remove("PieceInfo");
        }

        if (options.RemoveThumbnails && dict.GetName("Type") == "Page")
        {
            removed |= dict.Remove("Thumb");
        }

        foreach (var entry in dict.Entries.ToList())
        {
            switch (entry.Value)
            {
                case PdfDictionary child:
                    removed |= Clean(child, options, depth + 1);
                    break;
                case PdfArray array:
                    foreach (var item in array.Items)
                    {
                        if (item is PdfDictionary itemDict)
                        {
                            removed |= Clean(itemDict, options, depth + 1);
                        }
                    }

                    break;
            }
        }

        return removed;
    }
}
