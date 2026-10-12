namespace HazukiPdf.Objects;

/// <summary>An in-memory PDF: every indirect object keyed by object number, plus the trailer.</summary>
internal sealed class PdfDocument
{
    public PdfDocument(Dictionary<int, PdfObject> objects, PdfDictionary trailer, string version)
    {
        Objects = objects;
        Trailer = trailer;
        Version = version;
    }

    public Dictionary<int, PdfObject> Objects { get; }

    public PdfDictionary Trailer { get; }

    /// <summary>The header version, such as "1.4".</summary>
    public string Version { get; set; }

    public PdfDictionary? Catalog => Resolve(Trailer.Get("Root")) as PdfDictionary;

    /// <summary>Follows a reference to its object; a reference to a missing object is null.</summary>
    public PdfObject? Resolve(PdfObject? obj)
    {
        for (int i = 0; i < 32 && obj is PdfReference reference; i++)
        {
            obj = Objects.TryGetValue(reference.ObjectNumber, out var target) ? target : null;
        }

        return obj is PdfReference ? null : obj;
    }

    public PdfObject? Get(PdfDictionary dict, string key) => Resolve(dict.Get(key));

    public int NextObjectNumber() => Objects.Count == 0 ? 1 : Objects.Keys.Max() + 1;

    public PdfReference Add(PdfObject obj)
    {
        int number = NextObjectNumber();
        Objects[number] = obj;
        return new PdfReference(number, 0);
    }

    /// <summary>Lists every page dictionary in order, with inherited attributes resolved.</summary>
    public List<PdfPageInfo> GetPages()
    {
        var pages = new List<PdfPageInfo>();
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        if (Catalog != null && Get(Catalog, "Pages") is PdfDictionary root)
        {
            Walk(root, null, null, visited, pages, 0);
        }

        return pages;
    }

    private void Walk(PdfDictionary node, PdfDictionary? resources, PdfArray? mediaBox, HashSet<PdfDictionary> visited, List<PdfPageInfo> pages, int depth)
    {
        if (depth > 256 || !visited.Add(node))
        {
            return;
        }

        resources = Get(node, "Resources") as PdfDictionary ?? resources;
        mediaBox = Get(node, "MediaBox") as PdfArray ?? mediaBox;
        if (Get(node, "Kids") is PdfArray kids)
        {
            foreach (var kid in kids.Items)
            {
                if (Resolve(kid) is PdfDictionary child)
                {
                    Walk(child, resources, mediaBox, visited, pages, depth + 1);
                }
            }
        }
        else
        {
            pages.Add(new PdfPageInfo(node, resources));
        }
    }
}

internal sealed record PdfPageInfo(PdfDictionary Page, PdfDictionary? Resources);
