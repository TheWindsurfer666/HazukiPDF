using System.Security.Cryptography;
using HazukiPdf.Objects;
using HazukiPdf.Writing;

namespace HazukiPdf.Optimization;

/// <summary>
/// Merges indirect objects that serialize to identical bytes, such as a font or image embedded once per page.
/// Runs repeatedly so that objects which differ only in references to duplicates are merged too.
/// </summary>
internal static class Deduplicator
{
    // Objects whose identity matters: a page or annotation listed twice is not the same as one listed once.
    private static readonly HashSet<string> IdentityTypes = new(StringComparer.Ordinal)
    {
        "Catalog", "Pages", "Page", "Annot", "Sig", "StructTreeRoot", "StructElem", "Outlines",
    };

    public static int Run(PdfDocument document)
    {
        int removed = 0;
        for (int pass = 0; pass < 16; pass++)
        {
            var replacements = FindDuplicates(document);
            if (replacements.Count == 0)
            {
                break;
            }

            foreach (int number in replacements.Keys)
            {
                document.Objects.Remove(number);
            }

            RewriteReferences(document, replacements);
            removed += replacements.Count;
        }

        return removed;
    }

    private static Dictionary<int, int> FindDuplicates(PdfDocument document)
    {
        var protectedNumbers = new HashSet<int>();
        foreach (var key in new[] { "Root", "Info" })
        {
            if (document.Trailer.Get(key) is PdfReference r)
            {
                protectedNumbers.Add(r.ObjectNumber);
            }
        }

        var firstByHash = new Dictionary<string, (int Number, byte[] Bytes)>(StringComparer.Ordinal);
        var replacements = new Dictionary<int, int>();
        foreach (int number in document.Objects.Keys.OrderBy(n => n))
        {
            var obj = document.Objects[number];
            if (protectedNumbers.Contains(number) || obj is PdfNull ||
                (obj is PdfDictionary d && d.GetName("Type") is { } type && IdentityTypes.Contains(type)))
            {
                continue;
            }

            var bytes = PdfWriter.Serialize(obj);
            string hash = Convert.ToBase64String(SHA256.HashData(bytes));
            if (firstByHash.TryGetValue(hash, out var first))
            {
                if (first.Bytes.AsSpan().SequenceEqual(bytes))
                {
                    replacements[number] = first.Number;
                }
            }
            else
            {
                firstByHash[hash] = (number, bytes);
            }
        }

        return replacements;
    }

    public static void RewriteReferences(PdfDocument document, IReadOnlyDictionary<int, int> replacements)
    {
        foreach (var obj in document.Objects.Values)
        {
            Rewrite(obj, replacements, 0);
        }

        Rewrite(document.Trailer, replacements, 0);
    }

    private static PdfObject Rewrite(PdfObject obj, IReadOnlyDictionary<int, int> replacements, int depth)
    {
        if (depth > 512)
        {
            return obj;
        }

        switch (obj)
        {
            case PdfReference r when replacements.TryGetValue(r.ObjectNumber, out int target):
                return new PdfReference(target, 0);
            case PdfArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    a[i] = Rewrite(a[i], replacements, depth + 1);
                }

                break;
            case PdfDictionary d:
                for (int i = 0; i < d.Count; i++)
                {
                    var value = d.EntryAt(i).Value;
                    var rewritten = Rewrite(value, replacements, depth + 1);
                    if (!ReferenceEquals(value, rewritten))
                    {
                        d.ReplaceValue(i, rewritten);
                    }
                }

                break;
        }

        return obj;
    }
}
