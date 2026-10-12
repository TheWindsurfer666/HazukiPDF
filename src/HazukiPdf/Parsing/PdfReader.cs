using System.Text;
using HazukiPdf.Filters;
using HazukiPdf.Objects;

namespace HazukiPdf.Parsing;

/// <summary>
/// Loads a PDF file into a <see cref="PdfDocument"/>. Reads cross-reference tables and streams (following
/// incremental updates), object streams, and falls back to scanning the whole file when the cross-reference
/// data is missing or damaged.
/// </summary>
internal sealed class PdfReader
{
    private readonly byte[] _data;
    private readonly int _headerOffset;
    private readonly Dictionary<int, XrefEntry> _xref = new();
    private readonly Dictionary<int, PdfObject?> _cache = new();
    private readonly HashSet<int> _loading = new();
    private readonly Dictionary<int, Dictionary<int, PdfObject>> _objectStreams = new();
    private Dictionary<int, int>? _scannedOffsets;
    private PdfDictionary _trailer = new();

    private PdfReader(byte[] data)
    {
        _data = data;
        _headerOffset = Math.Max(0, IndexOf(data, "%PDF-"u8, 0, Math.Min(data.Length, 1024)));
    }

    private readonly record struct XrefEntry(bool Compressed, long Offset, int StreamNumber, int Index);

    public static PdfDocument Read(byte[] data)
    {
        if (IndexOf(data, "%PDF-"u8, 0, Math.Min(data.Length, 1024)) < 0)
        {
            throw new PdfFormatException("The input is not a PDF file (no %PDF- header found).");
        }

        return new PdfReader(data).Load();
    }

    private PdfDocument Load()
    {
        bool xrefOk;
        try
        {
            xrefOk = ReadXrefChain();
        }
        catch (Exception ex) when (ex is not PdfEncryptedException)
        {
            xrefOk = false;
        }

        if (!xrefOk || _trailer.Get("Root") == null)
        {
            Reconstruct();
        }

        if (_trailer.Get("Encrypt") != null)
        {
            throw new PdfEncryptedException();
        }

        var objects = new Dictionary<int, PdfObject>();
        foreach (int number in _xref.Keys.OrderBy(n => n))
        {
            PdfObject? obj;
            try
            {
                obj = GetObject(number);
            }
            catch (Exception ex) when (ex is not PdfEncryptedException)
            {
                obj = null;
            }

            // Old cross-reference and object streams are rebuilt by the writer.
            if (obj is PdfStream s && s.GetName("Type") is "XRef" or "ObjStm")
            {
                continue;
            }

            if (obj != null && obj is not PdfNull)
            {
                objects[number] = obj;
            }
        }

        var trailer = new PdfDictionary();
        foreach (var key in new[] { "Root", "Info", "ID" })
        {
            if (_trailer.Get(key) is { } value)
            {
                trailer.Set(key, value);
            }
        }

        if (trailer.Get("Root") is not PdfReference rootRef || !objects.ContainsKey(rootRef.ObjectNumber))
        {
            throw new PdfFormatException("The PDF has no document catalog.");
        }

        return new PdfDocument(objects, trailer, ReadVersion());
    }

    private string ReadVersion()
    {
        int start = _headerOffset + 5;
        int end = start;
        while (end < _data.Length && end < start + 8 && (char.IsAsciiDigit((char)_data[end]) || _data[end] == '.'))
        {
            end++;
        }

        string version = Encoding.ASCII.GetString(_data, start, end - start);
        return version.Length >= 3 ? version : "1.4";
    }

    // ---------------------------------------------------------------- cross-reference

    private bool ReadXrefChain()
    {
        long offset = FindStartXref();
        if (offset < 0)
        {
            return false;
        }

        var visited = new HashSet<long>();
        bool first = true;
        while (offset >= 0 && visited.Add(offset))
        {
            long position = ResolveOffset(offset, "xref"u8) ?? ResolveObjectOffset(offset) ?? -1;
            if (position < 0)
            {
                return false;
            }

            PdfDictionary sectionTrailer;
            if (StartsWith((int)position, "xref"u8))
            {
                var entries = new List<(int Number, XrefEntry Entry)>();
                sectionTrailer = ReadXrefTable((int)position, entries);

                // In hybrid files the stream's entries take precedence over the table's placeholders.
                if (sectionTrailer.Get("XRefStm") is PdfInteger hybrid)
                {
                    long hybridPos = ResolveObjectOffset(hybrid.Value) ?? -1;
                    if (hybridPos >= 0)
                    {
                        ReadXrefStream((int)hybridPos);
                    }
                }

                foreach (var (number, entry) in entries)
                {
                    _xref.TryAdd(number, entry);
                }
            }
            else
            {
                sectionTrailer = ReadXrefStream((int)position);
            }

            if (first)
            {
                foreach (var entry in sectionTrailer.Entries)
                {
                    _trailer.Set(entry.Key.Value, entry.Value);
                }

                first = false;
            }
            else
            {
                foreach (var entry in sectionTrailer.Entries)
                {
                    if (_trailer.Get(entry.Key.Value) == null)
                    {
                        _trailer.Set(entry.Key.Value, entry.Value);
                    }
                }
            }

            offset = sectionTrailer.Get("Prev") is PdfInteger prev ? prev.Value : -1;
        }

        return _xref.Count > 0;
    }

    private long FindStartXref()
    {
        int searchStart = Math.Max(0, _data.Length - 4096);
        int index = LastIndexOf(_data, "startxref"u8, searchStart);
        if (index < 0)
        {
            index = LastIndexOf(_data, "startxref"u8, 0);
        }

        if (index < 0)
        {
            return -1;
        }

        var lexer = new PdfLexer(_data, index + 9);
        var token = lexer.Next();
        return token.Type == TokenType.Integer ? token.IntegerValue : -1;
    }

    /// <summary>Returns the absolute position of an offset, allowing for junk before the header.</summary>
    private long? ResolveOffset(long offset, ReadOnlySpan<byte> expected)
    {
        foreach (long candidate in new[] { offset, offset + _headerOffset })
        {
            if (candidate >= 0 && candidate < _data.Length)
            {
                int p = (int)candidate;
                while (p < _data.Length && PdfLexer.IsWhitespace(_data[p]))
                {
                    p++;
                }

                if (StartsWith(p, expected))
                {
                    return p;
                }
            }
        }

        return null;
    }

    private long? ResolveObjectOffset(long offset)
    {
        foreach (long candidate in new[] { offset, offset + _headerOffset })
        {
            if (candidate >= 0 && candidate < _data.Length && LooksLikeObjectHeader((int)candidate, out _))
            {
                return candidate;
            }
        }

        return null;
    }

    private bool LooksLikeObjectHeader(int position, out int objectNumber)
    {
        objectNumber = 0;
        var lexer = new PdfLexer(_data, position);
        var a = lexer.Next();
        var b = lexer.Next();
        var c = lexer.Next();
        if (a.Type == TokenType.Integer && b.Type == TokenType.Integer && c.Type == TokenType.Keyword && c.Text == "obj" &&
            a.IntegerValue is > 0 and < int.MaxValue)
        {
            objectNumber = (int)a.IntegerValue;
            return true;
        }

        return false;
    }

    private PdfDictionary ReadXrefTable(int position, List<(int Number, XrefEntry Entry)> entries)
    {
        var lexer = new PdfLexer(_data, position + 4);
        while (true)
        {
            var token = lexer.Next();
            if (token.Type == TokenType.Keyword && token.Text == "trailer")
            {
                return lexer.ReadObject() as PdfDictionary ?? new PdfDictionary();
            }

            if (token.Type != TokenType.Integer)
            {
                throw new PdfFormatException("Malformed cross-reference table.");
            }

            long start = token.IntegerValue;
            var countToken = lexer.Next();
            if (countToken.Type != TokenType.Integer)
            {
                throw new PdfFormatException("Malformed cross-reference table.");
            }

            long count = countToken.IntegerValue;
            for (long i = 0; i < count; i++)
            {
                var offsetToken = lexer.Next();
                var genToken = lexer.Next();
                var typeToken = lexer.Next();
                if (offsetToken.Type != TokenType.Integer || genToken.Type != TokenType.Integer || typeToken.Type != TokenType.Keyword)
                {
                    throw new PdfFormatException("Malformed cross-reference entry.");
                }

                long number = start + i;
                if (number <= 0 || number >= int.MaxValue)
                {
                    continue;
                }

                // A free entry masks definitions in older sections.
                entries.Add(((int)number, new XrefEntry(false, typeToken.Text == "n" ? offsetToken.IntegerValue : -1, 0, 0)));
            }
        }
    }

    private PdfDictionary ReadXrefStream(int position)
    {
        var lexer = new PdfLexer(_data, position);
        if (ParseIndirectObject(lexer, out _) is not PdfStream stream || stream.GetName("Type") != "XRef")
        {
            throw new PdfFormatException("Expected a cross-reference stream.");
        }

        var data = StreamFilters.Decode(stream.Data, StreamFilters.GetFilters(stream, o => o), strict: false);
        if (stream.Get("W") is not PdfArray w || w.Count < 3)
        {
            throw new PdfFormatException("Cross-reference stream has no /W.");
        }

        int[] widths = w.Items.Take(3).Select(x => (int)(x.AsNumber() ?? 0)).ToArray();
        int rowLength = widths.Sum();
        if (rowLength <= 0 || widths.Any(x => x is < 0 or > 8))
        {
            throw new PdfFormatException("Invalid cross-reference stream widths.");
        }

        var index = new List<(long Start, long Count)>();
        if (stream.Get("Index") is PdfArray indexArray)
        {
            for (int i = 0; i + 1 < indexArray.Count; i += 2)
            {
                index.Add(((long)(indexArray[i].AsNumber() ?? 0), (long)(indexArray[i + 1].AsNumber() ?? 0)));
            }
        }
        else
        {
            index.Add((0, (long)(stream.Get("Size").AsNumber() ?? 0)));
        }

        int pos = 0;
        foreach (var (start, count) in index)
        {
            for (long i = 0; i < count && pos + rowLength <= data.Length; i++)
            {
                long type = widths[0] == 0 ? 1 : ReadField(data, pos, widths[0]);
                long f2 = ReadField(data, pos + widths[0], widths[1]);
                long f3 = ReadField(data, pos + widths[0] + widths[1], widths[2]);
                pos += rowLength;
                long number = start + i;
                if (number <= 0 || number >= int.MaxValue || _xref.ContainsKey((int)number))
                {
                    continue;
                }

                switch (type)
                {
                    case 0:
                        _xref[(int)number] = new XrefEntry(false, -1, 0, 0);
                        break;
                    case 1:
                        _xref[(int)number] = new XrefEntry(false, f2, 0, 0);
                        break;
                    case 2:
                        _xref[(int)number] = new XrefEntry(true, 0, (int)f2, (int)f3);
                        break;
                }
            }
        }

        return stream;
    }

    private static long ReadField(byte[] data, int position, int width)
    {
        long value = 0;
        for (int i = 0; i < width; i++)
        {
            value = (value << 8) | data[position + i];
        }

        return value;
    }

    // ---------------------------------------------------------------- reconstruction

    /// <summary>Rebuilds the cross-reference by scanning for "n g obj" headers when the real one is unusable.</summary>
    private void Reconstruct()
    {
        _xref.Clear();
        _cache.Clear();
        var offsets = ScanObjectOffsets();
        foreach (var (number, offset) in offsets)
        {
            _xref[number] = new XrefEntry(false, offset, 0, 0);
        }

        var trailer = new PdfDictionary();

        // Objects inside object streams.
        foreach (var number in offsets.Keys.ToList())
        {
            if (GetObject(number) is PdfStream s && s.GetName("Type") == "ObjStm")
            {
                foreach (var (inner, index) in ListObjectStream(number))
                {
                    if (!_xref.ContainsKey(inner))
                    {
                        _xref[inner] = new XrefEntry(true, 0, number, index);
                    }
                }
            }
            else if (GetObject(number) is PdfStream xs && xs.GetName("Type") == "XRef")
            {
                MergeTrailer(trailer, xs);
            }
        }

        // Classic trailers; later ones win.
        int search = 0;
        while ((search = IndexOf(_data, "trailer"u8, search, _data.Length)) >= 0)
        {
            var lexer = new PdfLexer(_data, search + 7);
            if (lexer.ReadObject() is PdfDictionary dict)
            {
                MergeTrailer(trailer, dict);
            }

            search += 7;
        }

        if (trailer.Get("Root") == null)
        {
            foreach (var number in _xref.Keys.OrderBy(n => n))
            {
                if (SafeGet(number) is PdfDictionary d && d.GetName("Type") == "Catalog")
                {
                    trailer.Set("Root", new PdfReference(number, 0));
                    break;
                }
            }
        }

        _trailer = trailer;
    }

    private PdfObject? SafeGet(int number)
    {
        try
        {
            return GetObject(number);
        }
        catch (Exception ex) when (ex is not PdfEncryptedException)
        {
            return null;
        }
    }

    private static void MergeTrailer(PdfDictionary target, PdfDictionary source)
    {
        foreach (var key in new[] { "Root", "Info", "ID", "Encrypt" })
        {
            if (source.Get(key) is { } value)
            {
                target.Set(key, value);
            }
        }
    }

    private Dictionary<int, int> ScanObjectOffsets()
    {
        if (_scannedOffsets != null)
        {
            return _scannedOffsets;
        }

        var result = new Dictionary<int, int>();
        int search = 0;
        while ((search = IndexOf(_data, "obj"u8, search, _data.Length)) >= 0)
        {
            int end = search + 3;
            bool boundaryAfter = end >= _data.Length || !PdfLexer.IsRegular(_data[end]);
            bool boundaryBefore = search > 0 && PdfLexer.IsWhitespace(_data[search - 1]);
            if (boundaryAfter && boundaryBefore)
            {
                // Walk back over "<num> <gen> ".
                int p = search - 1;
                while (p >= 0 && PdfLexer.IsWhitespace(_data[p]))
                {
                    p--;
                }

                while (p >= 0 && char.IsAsciiDigit((char)_data[p]))
                {
                    p--;
                }

                while (p >= 0 && PdfLexer.IsWhitespace(_data[p]))
                {
                    p--;
                }

                int numEnd = p + 1;
                while (p >= 0 && char.IsAsciiDigit((char)_data[p]))
                {
                    p--;
                }

                int numStart = p + 1;
                if (numStart < numEnd && (p < 0 || !PdfLexer.IsRegular(_data[p])) && LooksLikeObjectHeader(numStart, out int number))
                {
                    result[number] = numStart; // later definitions win, as with incremental updates
                }
            }

            search = end;
        }

        _scannedOffsets = result;
        return result;
    }

    // ---------------------------------------------------------------- objects

    private PdfObject? GetObject(int number)
    {
        if (_cache.TryGetValue(number, out var cached))
        {
            return cached;
        }

        if (!_xref.TryGetValue(number, out var entry) || (!entry.Compressed && entry.Offset < 0))
        {
            return null;
        }

        if (!_loading.Add(number))
        {
            return null; // circular /Length reference
        }

        try
        {
            PdfObject? obj = entry.Compressed ? GetFromObjectStream(entry.StreamNumber, number) : ReadObjectAt(number, entry.Offset);
            _cache[number] = obj;
            return obj;
        }
        finally
        {
            _loading.Remove(number);
        }
    }

    private PdfObject? ReadObjectAt(int number, long offset)
    {
        long? position = null;
        foreach (long candidate in new[] { offset, offset + _headerOffset })
        {
            if (candidate >= 0 && candidate < _data.Length && LooksLikeObjectHeader((int)candidate, out int found) && found == number)
            {
                position = candidate;
                break;
            }
        }

        if (position == null && ScanObjectOffsets().TryGetValue(number, out int scanned))
        {
            position = scanned;
        }

        if (position == null)
        {
            return null;
        }

        return ParseIndirectObject(new PdfLexer(_data, (int)position.Value), out _);
    }

    private PdfObject? ParseIndirectObject(PdfLexer lexer, out int number)
    {
        var num = lexer.Next();
        lexer.Next(); // generation
        lexer.Next(); // obj
        number = num.Type == TokenType.Integer ? (int)num.IntegerValue : 0;
        var obj = lexer.ReadObject();
        if (obj is not PdfDictionary dict)
        {
            return obj;
        }

        int afterDict = lexer.Position;
        var token = lexer.Next();
        if (token.Type != TokenType.Keyword || token.Text != "stream")
        {
            lexer.Position = afterDict;
            return dict;
        }

        int start = lexer.Position;
        if (start < _data.Length && _data[start] == '\r')
        {
            start++;
        }

        if (start < _data.Length && _data[start] == '\n')
        {
            start++;
        }

        byte[] data = ReadStreamData(dict, start, out int endPosition);
        lexer.Position = endPosition;
        return new PdfStream(dict, data);
    }

    private byte[] ReadStreamData(PdfDictionary dict, int start, out int endPosition)
    {
        long length = -1;
        var lengthObj = dict.Get("Length");
        if (lengthObj is PdfReference lengthRef)
        {
            lengthObj = GetObject(lengthRef.ObjectNumber);
        }

        if (lengthObj is PdfInteger li)
        {
            length = li.Value;
        }

        if (length >= 0 && start + length <= _data.Length)
        {
            int p = (int)(start + length);
            while (p < _data.Length && PdfLexer.IsWhitespace(_data[p]))
            {
                p++;
            }

            if (StartsWith(p, "endstream"u8))
            {
                endPosition = p + 9;
                return _data.AsSpan(start, (int)length).ToArray();
            }
        }

        // /Length is missing or wrong: search for the end marker.
        int end = IndexOf(_data, "endstream"u8, start, _data.Length);
        if (end < 0)
        {
            endPosition = _data.Length;
            return _data.AsSpan(start).ToArray();
        }

        endPosition = end + 9;
        int dataEnd = end;
        if (dataEnd > start && _data[dataEnd - 1] == '\n')
        {
            dataEnd--;
        }

        if (dataEnd > start && _data[dataEnd - 1] == '\r')
        {
            dataEnd--;
        }

        return _data.AsSpan(start, dataEnd - start).ToArray();
    }

    private PdfObject? GetFromObjectStream(int streamNumber, int number)
    {
        if (!_objectStreams.TryGetValue(streamNumber, out var objects))
        {
            objects = new Dictionary<int, PdfObject>();
            _objectStreams[streamNumber] = objects;
            if (GetObject(streamNumber) is PdfStream stream)
            {
                var (data, pairs, first) = DecodeObjectStream(stream);
                foreach (var (objNumber, offset) in pairs)
                {
                    if (first + offset >= data.Length || objects.ContainsKey(objNumber))
                    {
                        continue;
                    }

                    var lexer = new PdfLexer(data, first + offset);
                    var obj = lexer.ReadObject();
                    if (obj != null)
                    {
                        objects[objNumber] = obj;
                    }
                }
            }
        }

        return objects.TryGetValue(number, out var result) ? result : null;
    }

    private List<(int Number, int Index)> ListObjectStream(int streamNumber)
    {
        var result = new List<(int, int)>();
        try
        {
            if (GetObject(streamNumber) is PdfStream stream)
            {
                var (_, pairs, _) = DecodeObjectStream(stream);
                for (int i = 0; i < pairs.Count; i++)
                {
                    result.Add((pairs[i].Number, i));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or PdfFormatException)
        {
            // Ignore damaged object streams during reconstruction.
        }

        return result;
    }

    private (byte[] Data, List<(int Number, int Offset)> Pairs, int First) DecodeObjectStream(PdfStream stream)
    {
        var data = StreamFilters.Decode(stream.Data, StreamFilters.GetFilters(stream, o => o is PdfReference r ? GetObject(r.ObjectNumber) : o), strict: false);
        int n = (int)(stream.Get("N").AsNumber() ?? 0);
        int first = (int)(stream.Get("First").AsNumber() ?? 0);
        var lexer = new PdfLexer(data);
        var pairs = new List<(int, int)>();
        for (int i = 0; i < n; i++)
        {
            var a = lexer.Next();
            var b = lexer.Next();
            if (a.Type != TokenType.Integer || b.Type != TokenType.Integer)
            {
                break;
            }

            pairs.Add(((int)a.IntegerValue, (int)b.IntegerValue));
        }

        return (data, pairs, first);
    }

    // ---------------------------------------------------------------- byte helpers

    private bool StartsWith(int position, ReadOnlySpan<byte> value) =>
        position >= 0 && position + value.Length <= _data.Length && _data.AsSpan(position, value.Length).SequenceEqual(value);

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> value, int start, int end)
    {
        if (start >= end)
        {
            return -1;
        }

        int index = data.AsSpan(start, Math.Min(end, data.Length) - start).IndexOf(value);
        return index < 0 ? -1 : start + index;
    }

    private static int LastIndexOf(byte[] data, ReadOnlySpan<byte> value, int start)
    {
        int index = data.AsSpan(start).LastIndexOf(value);
        return index < 0 ? -1 : start + index;
    }
}
