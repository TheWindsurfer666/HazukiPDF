using System.Globalization;
using System.Text;
using HazukiPdf.Filters;
using HazukiPdf.Objects;

namespace HazukiPdf.Writing;

internal sealed class PdfWriterOptions
{
    /// <summary>Pack non-stream objects into compressed object streams and write a cross-reference stream (PDF 1.5).</summary>
    public bool UseObjectStreams { get; init; } = true;

    /// <summary>Write only objects reachable from the trailer.</summary>
    public bool RemoveUnusedObjects { get; init; } = true;

    public int ObjectsPerStream { get; init; } = 200;
}

/// <summary>Serializes a <see cref="PdfDocument"/> as a complete, freshly numbered PDF file.</summary>
internal sealed class PdfWriter
{
    private readonly PdfDocument _document;
    private readonly PdfWriterOptions _options;
    private readonly Dictionary<int, int> _renumber = new();
    private readonly MemoryStream _out = new();
    private bool _needsSpace;

    private PdfWriter(PdfDocument document, PdfWriterOptions options)
    {
        _document = document;
        _options = options;
    }

    public static byte[] Write(PdfDocument document, PdfWriterOptions options) => Write(document, options, out _);

    public static byte[] Write(PdfDocument document, PdfWriterOptions options, out int objectsWritten)
    {
        var writer = new PdfWriter(document, options);
        var bytes = writer.WriteDocument();
        objectsWritten = writer._renumber.Count;
        return bytes;
    }

    /// <summary>Serializes a single object (references written as-is). Used to compare objects for equality.</summary>
    public static byte[] Serialize(PdfObject obj, Func<PdfReference, int>? mapReference = null)
    {
        var writer = new PdfWriter(new PdfDocument(new(), new PdfDictionary(), "1.4"), new PdfWriterOptions());
        writer.WriteObject(obj, mapReference ?? (r => r.ObjectNumber), includeStreamData: true);
        return writer._out.ToArray();
    }

    private byte[] WriteDocument()
    {
        var order = CollectObjects();
        for (int i = 0; i < order.Count; i++)
        {
            _renumber[order[i]] = i + 1;
        }

        string version = _document.Version;
        if (_options.UseObjectStreams && string.CompareOrdinal(version, "1.5") < 0)
        {
            version = "1.5";
        }

        WriteRaw($"%PDF-{version}\n");
        _out.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

        if (_options.UseObjectStreams)
        {
            WriteWithObjectStreams(order);
        }
        else
        {
            WriteClassic(order);
        }

        return _out.ToArray();
    }

    private List<int> CollectObjects()
    {
        if (!_options.RemoveUnusedObjects)
        {
            return _document.Objects.Keys.OrderBy(n => n).ToList();
        }

        var order = new List<int>();
        var seen = new HashSet<int>();
        var queue = new Queue<PdfObject>();
        foreach (var key in new[] { "Root", "Info" })
        {
            if (_document.Trailer.Get(key) is { } value)
            {
                queue.Enqueue(value);
            }
        }

        var work = new Stack<PdfObject>();
        while (queue.Count > 0)
        {
            work.Push(queue.Dequeue());
            while (work.Count > 0)
            {
                var obj = work.Pop();
                switch (obj)
                {
                    case PdfReference r:
                        if (_document.Objects.TryGetValue(r.ObjectNumber, out var target) && seen.Add(r.ObjectNumber))
                        {
                            order.Add(r.ObjectNumber);
                            queue.Enqueue(target);
                        }

                        break;
                    case PdfArray a:
                        for (int i = a.Count - 1; i >= 0; i--)
                        {
                            work.Push(a[i]);
                        }

                        break;
                    case PdfDictionary d:
                        foreach (var entry in d.Entries.Reverse())
                        {
                            work.Push(entry.Value);
                        }

                        break;
                }
            }
        }

        return order;
    }

    private int Map(PdfReference reference) => _renumber.TryGetValue(reference.ObjectNumber, out int n) ? n : 0;

    private void WriteClassic(List<int> order)
    {
        var offsets = new long[order.Count + 1];
        for (int i = 0; i < order.Count; i++)
        {
            offsets[i + 1] = _out.Position;
            WriteIndirect(i + 1, _document.Objects[order[i]]);
        }

        long xrefPosition = _out.Position;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {order.Count + 1}\n0000000000 65535 f\r\n");
        for (int i = 1; i <= order.Count; i++)
        {
            sb.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n\r\n");
        }

        WriteRaw(sb.ToString());
        WriteRaw("trailer\n");
        var trailer = BuildTrailer(order.Count + 1);
        WriteObject(trailer, Map, includeStreamData: false);
        WriteRaw(string.Create(CultureInfo.InvariantCulture, $"\nstartxref\n{xrefPosition}\n%%EOF\n"));
    }

    private PdfDictionary BuildTrailer(int size)
    {
        var trailer = new PdfDictionary();
        trailer.Set("Size", new PdfInteger(size));
        foreach (var key in new[] { "Root", "Info", "ID" })
        {
            var value = _document.Trailer.Get(key);
            if (value is PdfReference r && Map(r) == 0)
            {
                continue;
            }

            trailer.Set(key, value);
        }

        return trailer;
    }

    private void WriteWithObjectStreams(List<int> order)
    {
        int count = order.Count;
        var type = new byte[count + 1];
        var field2 = new long[count + 1];
        var field3 = new long[count + 1];

        var compressible = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int newNumber = i + 1;
            var obj = _document.Objects[order[i]];
            if (obj is PdfStream)
            {
                type[newNumber] = 1;
                field2[newNumber] = _out.Position;
                WriteIndirect(newNumber, obj);
            }
            else
            {
                compressible.Add(newNumber);
            }
        }

        int nextNumber = count + 1;
        var streamEntries = new List<(int Number, long Offset)>();
        for (int start = 0; start < compressible.Count; start += _options.ObjectsPerStream)
        {
            var chunk = compressible.Skip(start).Take(_options.ObjectsPerStream).ToList();
            int streamNumber = nextNumber++;
            var header = new StringBuilder();
            var body = new MemoryStream();
            for (int i = 0; i < chunk.Count; i++)
            {
                int number = chunk[i];
                header.Append(CultureInfo.InvariantCulture, $"{number} {body.Length} ");
                var bytes = SerializeMapped(_document.Objects[order[number - 1]]);
                body.Write(bytes);
                body.WriteByte((byte)'\n');
                type[number] = 2;
                field2[number] = streamNumber;
                field3[number] = i;
            }

            var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
            var raw = new byte[headerBytes.Length + body.Length];
            headerBytes.CopyTo(raw, 0);
            body.ToArray().CopyTo(raw, headerBytes.Length);

            var dict = new PdfDictionary();
            dict.Set("Type", PdfName.Get("ObjStm"));
            dict.Set("N", new PdfInteger(chunk.Count));
            dict.Set("First", new PdfInteger(headerBytes.Length));
            dict.Set("Filter", PdfName.Get("FlateDecode"));
            streamEntries.Add((streamNumber, _out.Position));
            WriteIndirect(streamNumber, new PdfStream(dict, StreamFilters.Deflate(raw)));
        }

        int xrefNumber = nextNumber++;
        int size = nextNumber;
        Array.Resize(ref type, size);
        Array.Resize(ref field2, size);
        Array.Resize(ref field3, size);
        foreach (var (number, offset) in streamEntries)
        {
            type[number] = 1;
            field2[number] = offset;
        }

        long xrefPosition = _out.Position;
        type[xrefNumber] = 1;
        field2[xrefNumber] = xrefPosition;
        field3[0] = 65535;

        int w2 = BytesNeeded(field2.Max());
        int w3 = BytesNeeded(field3.Max());
        int rowLength = 1 + w2 + w3;
        var rows = new byte[size * rowLength];
        for (int n = 0; n < size; n++)
        {
            int p = n * rowLength;
            rows[p] = type[n];
            WriteField(rows, p + 1, w2, field2[n]);
            WriteField(rows, p + 1 + w2, w3, field3[n]);
        }

        var xref = BuildTrailer(size);
        xref.Set("Type", PdfName.Get("XRef"));
        xref.Set("W", new PdfArray(new PdfObject[] { new PdfInteger(1), new PdfInteger(w2), new PdfInteger(w3) }));
        xref.Set("Filter", PdfName.Get("FlateDecode"));
        var parms = new PdfDictionary();
        parms.Set("Predictor", new PdfInteger(12));
        parms.Set("Columns", new PdfInteger(rowLength));
        xref.Set("DecodeParms", parms);
        WriteIndirect(xrefNumber, new PdfStream(xref, StreamFilters.Deflate(Predictors.EncodePngUp(rows, rowLength))));
        WriteRaw(string.Create(CultureInfo.InvariantCulture, $"startxref\n{xrefPosition}\n%%EOF\n"));
    }

    private static int BytesNeeded(long value)
    {
        int n = 1;
        while (n < 8 && value >= 1L << (8 * n))
        {
            n++;
        }

        return n;
    }

    private static void WriteField(byte[] buffer, int position, int width, long value)
    {
        for (int i = width - 1; i >= 0; i--)
        {
            buffer[position + i] = (byte)value;
            value >>= 8;
        }
    }

    private byte[] SerializeMapped(PdfObject obj)
    {
        long start = _out.Position;
        _needsSpace = false;
        WriteObject(obj, Map, includeStreamData: false);
        var bytes = _out.GetBuffer().AsSpan((int)start, (int)(_out.Position - start)).ToArray();
        _out.SetLength(start);
        _out.Position = start;
        return bytes;
    }

    private void WriteIndirect(int number, PdfObject obj)
    {
        WriteRaw(string.Create(CultureInfo.InvariantCulture, $"{number} 0 obj\n"));
        _needsSpace = false;
        WriteObject(obj, Map, includeStreamData: true);
        WriteRaw("\nendobj\n");
    }

    private void WriteRaw(string text)
    {
        _out.Write(Encoding.Latin1.GetBytes(text));
        _needsSpace = false;
    }

    private void WriteToken(string text)
    {
        if (_needsSpace)
        {
            _out.WriteByte((byte)' ');
        }

        _out.Write(Encoding.Latin1.GetBytes(text));
        _needsSpace = true;
    }

    private void WriteDelimited(byte[] bytes)
    {
        _out.Write(bytes);
        _needsSpace = false;
    }

    private void WriteObject(PdfObject obj, Func<PdfReference, int> map, bool includeStreamData)
    {
        switch (obj)
        {
            case PdfNull:
                WriteToken("null");
                break;
            case PdfBoolean b:
                WriteToken(b.Value ? "true" : "false");
                break;
            case PdfInteger i:
                WriteToken(i.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case PdfReal r:
                WriteToken(r.Format());
                break;
            case PdfName n:
                WriteDelimited(EncodeName(n.Value));
                _needsSpace = true;
                break;
            case PdfString s:
                WriteDelimited(EncodeString(s));
                break;
            case PdfReference reference:
            {
                int number = map(reference);
                WriteToken(number > 0 ? string.Create(CultureInfo.InvariantCulture, $"{number} 0 R") : "null");
                break;
            }

            case PdfArray a:
                WriteDelimited("["u8.ToArray());
                foreach (var item in a.Items)
                {
                    WriteObject(item, map, includeStreamData);
                }

                WriteDelimited("]"u8.ToArray());
                break;
            case PdfStream stream:
            {
                var copy = new PdfDictionary();
                foreach (var entry in stream.Entries)
                {
                    copy.Set(entry.Key.Value, entry.Value);
                }

                copy.Set("Length", new PdfInteger(stream.Data.Length));
                WriteDictionary(copy, map, includeStreamData);
                if (includeStreamData)
                {
                    WriteRaw("\nstream\n");
                    _out.Write(stream.Data);
                    WriteRaw("\nendstream");
                }

                break;
            }

            case PdfDictionary d:
                WriteDictionary(d, map, includeStreamData);
                break;
        }
    }

    private void WriteDictionary(PdfDictionary d, Func<PdfReference, int> map, bool includeStreamData)
    {
        WriteDelimited("<<"u8.ToArray());
        foreach (var entry in d.Entries)
        {
            if (entry.Value is PdfReference r && map(r) == 0)
            {
                continue; // a reference to a missing object means the entry is absent
            }

            WriteDelimited(EncodeName(entry.Key.Value));
            _needsSpace = true;
            WriteObject(entry.Value, map, includeStreamData);
        }

        WriteDelimited(">>"u8.ToArray());
    }

    private static byte[] EncodeName(string name)
    {
        var result = new List<byte>(name.Length + 1) { (byte)'/' };
        foreach (char c in name)
        {
            byte b = (byte)c;
            if (b < 0x21 || b > 0x7E || b == '#' || Parsing.PdfLexer.IsDelimiter(b))
            {
                result.Add((byte)'#');
                result.AddRange(Encoding.ASCII.GetBytes(b.ToString("X2", CultureInfo.InvariantCulture)));
            }
            else
            {
                result.Add(b);
            }
        }

        return result.ToArray();
    }

    private static byte[] EncodeString(PdfString s)
    {
        var literal = new List<byte>(s.Value.Length + 2) { (byte)'(' };
        foreach (byte b in s.Value)
        {
            switch (b)
            {
                case (byte)'(' or (byte)')' or (byte)'\\':
                    literal.Add((byte)'\\');
                    literal.Add(b);
                    break;
                case (byte)'\r':
                    literal.Add((byte)'\\');
                    literal.Add((byte)'r');
                    break;
                default:
                    literal.Add(b);
                    break;
            }
        }

        literal.Add((byte)')');
        int hexLength = (s.Value.Length * 2) + 2;
        if (hexLength < literal.Count)
        {
            return Encoding.ASCII.GetBytes("<" + Convert.ToHexString(s.Value) + ">");
        }

        return literal.ToArray();
    }
}
