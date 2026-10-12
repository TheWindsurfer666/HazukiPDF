using System.Buffers.Binary;

namespace HazukiPdf.Optimization;

/// <summary>
/// Shrinks a TrueType font program to the glyphs a document uses. Glyph IDs stay the same (unused glyphs become
/// empty), so text that refers to glyphs by ID keeps working without any change to the content streams.
/// Tables only used for layout or by font editors are dropped.
/// </summary>
internal static class TrueTypeSubsetter
{
    // Tables a PDF renderer can use. Everything else (GSUB, GPOS, kern, DSIG, hdmx, ...) only matters for
    // text layout or font installation.
    private static readonly HashSet<string> KeptTables = new(StringComparer.Ordinal)
    {
        "head", "hhea", "maxp", "hmtx", "loca", "glyf", "cvt ", "fpgm", "prep", "cmap", "OS/2", "name", "post",
        "gasp", "vhea", "vmtx",
    };

    private const int ArgsAreWords = 0x0001;
    private const int HaveScale = 0x0008;
    private const int MoreComponents = 0x0020;
    private const int HaveXYScale = 0x0040;
    private const int HaveTwoByTwo = 0x0080;

    /// <summary>
    /// Returns the subset font, or null when the data is not a plain TrueType font this method understands.
    /// <paramref name="glyphs"/> receives the composite glyph components that had to be kept as well.
    /// </summary>
    public static byte[]? Subset(byte[] font, HashSet<int> glyphs)
    {
        if (font.Length < 12)
        {
            return null;
        }

        uint version = BinaryPrimitives.ReadUInt32BigEndian(font);
        if (version != 0x00010000 && version != 0x74727565)
        {
            return null; // CFF-flavoured OpenType or a collection
        }

        int numTables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        var tables = new Dictionary<string, (int Offset, int Length)>(StringComparer.Ordinal);
        for (int i = 0; i < numTables; i++)
        {
            int record = 12 + (i * 16);
            if (record + 16 > font.Length)
            {
                return null;
            }

            string tag = System.Text.Encoding.ASCII.GetString(font, record, 4);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 8));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 12));
            if (offset < 0 || length < 0 || (long)offset + length > font.Length)
            {
                return null;
            }

            tables[tag] = (offset, length);
        }

        if (!tables.TryGetValue("head", out var head) || head.Length < 54 ||
            !tables.TryGetValue("maxp", out var maxp) || maxp.Length < 6 ||
            !tables.TryGetValue("loca", out var loca) || !tables.TryGetValue("glyf", out var glyf))
        {
            return null;
        }

        int numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(maxp.Offset + 4));
        bool longLoca = BinaryPrimitives.ReadInt16BigEndian(font.AsSpan(head.Offset + 50)) != 0;
        if (loca.Length < (numGlyphs + 1) * (longLoca ? 4 : 2))
        {
            return null;
        }

        int GlyphOffset(int gid) => longLoca
            ? (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(loca.Offset + (gid * 4)))
            : BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(loca.Offset + (gid * 2))) * 2;

        // Keep .notdef and every component of a kept composite glyph.
        glyphs.Add(0);
        glyphs.RemoveWhere(g => g < 0 || g >= numGlyphs);
        var pending = new Stack<int>(glyphs);
        while (pending.Count > 0)
        {
            int gid = pending.Pop();
            int start = GlyphOffset(gid);
            int end = GlyphOffset(gid + 1);
            if (end - start < 10 || glyf.Offset + end > glyf.Offset + glyf.Length)
            {
                continue;
            }

            var glyph = font.AsSpan(glyf.Offset + start, end - start);
            if (BinaryPrimitives.ReadInt16BigEndian(glyph) >= 0)
            {
                continue;
            }

            int p = 10;
            while (p + 4 <= glyph.Length)
            {
                int flags = BinaryPrimitives.ReadUInt16BigEndian(glyph[p..]);
                int component = BinaryPrimitives.ReadUInt16BigEndian(glyph[(p + 2)..]);
                if (component < numGlyphs && glyphs.Add(component))
                {
                    pending.Push(component);
                }

                p += 4 + ((flags & ArgsAreWords) != 0 ? 4 : 2);
                p += (flags & HaveScale) != 0 ? 2 : (flags & HaveXYScale) != 0 ? 4 : (flags & HaveTwoByTwo) != 0 ? 8 : 0;
                if ((flags & MoreComponents) == 0)
                {
                    break;
                }
            }
        }

        // New glyf and (long) loca tables.
        using var newGlyf = new MemoryStream();
        var newLoca = new byte[(numGlyphs + 1) * 4];
        for (int gid = 0; gid < numGlyphs; gid++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(gid * 4), (uint)newGlyf.Length);
            if (!glyphs.Contains(gid))
            {
                continue;
            }

            int start = GlyphOffset(gid);
            int end = GlyphOffset(gid + 1);
            if (end <= start || end > glyf.Length)
            {
                continue;
            }

            newGlyf.Write(font, glyf.Offset + start, end - start);
            while (newGlyf.Length % 4 != 0)
            {
                newGlyf.WriteByte(0);
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(numGlyphs * 4), (uint)newGlyf.Length);

        var output = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (tag, range) in tables)
        {
            if (KeptTables.Contains(tag))
            {
                output[tag] = font.AsSpan(range.Offset, range.Length).ToArray();
            }
        }

        output["glyf"] = newGlyf.ToArray();
        output["loca"] = newLoca;
        var newHead = output["head"];
        BinaryPrimitives.WriteInt16BigEndian(newHead.AsSpan(50), 1);
        BinaryPrimitives.WriteUInt32BigEndian(newHead.AsSpan(8), 0);
        if (output.TryGetValue("post", out var post) && post.Length >= 32)
        {
            // Format 3: no glyph names, which can be a large part of a font.
            var post3 = post[..32];
            BinaryPrimitives.WriteUInt32BigEndian(post3, 0x00030000);
            output["post"] = post3;
        }

        return Assemble(output);
    }

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        int count = tables.Count;
        int searchRange = 1;
        int entrySelector = 0;
        while (searchRange * 2 <= count)
        {
            searchRange *= 2;
            entrySelector++;
        }

        searchRange *= 16;
        int offset = 12 + (count * 16);
        int total = offset + tables.Values.Sum(t => (t.Length + 3) & ~3);
        var font = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(font, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(4), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(10), (ushort)((count * 16) - searchRange));

        int record = 12;
        int headOffset = -1;
        foreach (var (tag, data) in tables)
        {
            System.Text.Encoding.ASCII.GetBytes(tag, font.AsSpan(record));
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(record + 4), Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(record + 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(record + 12), (uint)data.Length);
            data.CopyTo(font, offset);
            if (tag == "head")
            {
                headOffset = offset;
            }

            offset += (data.Length + 3) & ~3;
            record += 16;
        }

        if (headOffset >= 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(headOffset + 8), 0xB1B0AFBA - Checksum(font));
        }

        return font;
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            sum += BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i));
        }

        if (i < data.Length)
        {
            Span<byte> last = stackalloc byte[4];
            data.AsSpan(i).CopyTo(last);
            sum += BinaryPrimitives.ReadUInt32BigEndian(last);
        }

        return sum;
    }
}
