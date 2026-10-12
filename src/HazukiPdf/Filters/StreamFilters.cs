using System.IO.Compression;
using HazukiPdf.Objects;

namespace HazukiPdf.Filters;

internal readonly record struct FilterSpec(string Name, PdfDictionary? Parameters);

/// <summary>Encoders and decoders for the standard stream filters (ISO 32000-1, section 7.4).</summary>
internal static class StreamFilters
{
    public static readonly HashSet<string> DecodableFilters = new(StringComparer.Ordinal)
    {
        "FlateDecode", "LZWDecode", "ASCIIHexDecode", "ASCII85Decode", "RunLengthDecode",
    };

    /// <summary>Reads /Filter and /DecodeParms into a list, normalizing inline-image abbreviations.</summary>
    public static List<FilterSpec> GetFilters(PdfDictionary dict, Func<PdfObject?, PdfObject?> resolve, bool inlineImage = false)
    {
        var result = new List<FilterSpec>();
        var filter = resolve(dict.Get("Filter") ?? (inlineImage ? dict.Get("F") : null));
        var parms = resolve(dict.Get("DecodeParms") ?? (inlineImage ? dict.Get("DP") : null));
        if (filter is PdfName single)
        {
            result.Add(new FilterSpec(NormalizeName(single.Value), resolve(parms is PdfArray pa && pa.Count > 0 ? pa[0] : parms) as PdfDictionary));
        }
        else if (filter is PdfArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (resolve(array[i]) is not PdfName name)
                {
                    continue;
                }

                PdfDictionary? p = parms switch
                {
                    PdfArray parray when i < parray.Count => resolve(parray[i]) as PdfDictionary,
                    PdfDictionary d when array.Count == 1 => d,
                    _ => null,
                };
                result.Add(new FilterSpec(NormalizeName(name.Value), p));
            }
        }

        return result;
    }

    public static string NormalizeName(string name) => name switch
    {
        "Fl" => "FlateDecode",
        "LZW" => "LZWDecode",
        "AHx" => "ASCIIHexDecode",
        "A85" => "ASCII85Decode",
        "RL" => "RunLengthDecode",
        "DCT" => "DCTDecode",
        "CCF" => "CCITTFaxDecode",
        _ => name,
    };

    /// <summary>Fully decodes stream data. Throws <see cref="NotSupportedException"/> for filters this library cannot decode.</summary>
    public static byte[] Decode(byte[] data, IReadOnlyList<FilterSpec> filters, bool strict)
    {
        foreach (var filter in filters)
        {
            data = DecodeOne(data, filter, strict, applyPredictor: true);
        }

        return data;
    }

    public static byte[] DecodeOne(byte[] data, FilterSpec filter, bool strict, bool applyPredictor)
    {
        byte[] result = filter.Name switch
        {
            "FlateDecode" => Inflate(data, strict),
            "LZWDecode" => LzwDecode(data, GetInt(filter.Parameters, "EarlyChange", 1)),
            "ASCIIHexDecode" => AsciiHexDecode(data),
            "ASCII85Decode" => Ascii85Decode(data),
            "RunLengthDecode" => RunLengthDecode(data),
            _ => throw new NotSupportedException($"The {filter.Name} filter is not supported."),
        };

        if (applyPredictor && filter.Name is "FlateDecode" or "LZWDecode" && filter.Parameters != null)
        {
            result = Predictors.Decode(result, filter.Parameters);
        }

        return result;
    }

    public static bool HasPredictor(FilterSpec filter) =>
        filter.Name is "FlateDecode" or "LZWDecode" && GetInt(filter.Parameters, "Predictor", 1) > 1;

    public static int GetInt(PdfDictionary? dict, string key, int fallback)
    {
        if (dict?.Get(key) is PdfInteger i)
        {
            return (int)Math.Clamp(i.Value, int.MinValue, int.MaxValue);
        }

        if (dict?.Get(key) is PdfReal r)
        {
            return (int)r.Value;
        }

        return fallback;
    }

    public static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream(data.Length / 2 + 64);
        using (var z = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            z.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Inflates zlib data. In strict mode any corruption throws; otherwise as much data as can be recovered is returned,
    /// which is what PDF viewers do for damaged streams.
    /// </summary>
    public static byte[] Inflate(byte[] data, bool strict)
    {
        try
        {
            using var input = new MemoryStream(data);
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(data.Length * 4);
            z.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException) when (!strict)
        {
            return InflateLenient(data);
        }
    }

    private static byte[] InflateLenient(byte[] data)
    {
        using var output = new MemoryStream();
        int offset = data.Length >= 2 && (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0 ? 2 : 0;
        try
        {
            using var input = new MemoryStream(data, offset, data.Length - offset);
            using var d = new DeflateStream(input, CompressionMode.Decompress);
            var buffer = new byte[4096];
            int read;
            while ((read = d.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
            }
        }
        catch (InvalidDataException)
        {
            // Keep what was recovered.
        }

        return output.ToArray();
    }

    public static byte[] LzwDecode(byte[] data, int earlyChange)
    {
        var output = new List<byte>(data.Length * 3);
        var table = new List<byte[]>(4096);
        void ResetTable()
        {
            table.Clear();
            for (int i = 0; i < 256; i++)
            {
                table.Add(new[] { (byte)i });
            }

            table.Add(Array.Empty<byte>()); // 256 clear
            table.Add(Array.Empty<byte>()); // 257 end
        }

        ResetTable();
        int codeLength = 9;
        int bitBuffer = 0;
        int bitCount = 0;
        byte[]? previous = null;
        foreach (byte b in data)
        {
            bitBuffer = ((bitBuffer & 0xFFFF) << 8) | b;
            bitCount += 8;
            while (bitCount >= codeLength)
            {
                int code = (bitBuffer >> (bitCount - codeLength)) & ((1 << codeLength) - 1);
                bitCount -= codeLength;
                if (code == 256)
                {
                    ResetTable();
                    codeLength = 9;
                    previous = null;
                    continue;
                }

                if (code == 257)
                {
                    return output.ToArray();
                }

                byte[] entry;
                if (code < table.Count)
                {
                    entry = table[code];
                    if (previous != null && table.Count < 4096)
                    {
                        table.Add(Concat(previous, entry[0]));
                    }
                }
                else if (previous != null)
                {
                    entry = Concat(previous, previous[0]);
                    if (table.Count < 4096)
                    {
                        table.Add(entry);
                    }
                }
                else
                {
                    throw new InvalidDataException("Invalid LZW code.");
                }

                output.AddRange(entry);
                previous = entry;
                int next = table.Count + earlyChange;
                codeLength = next >= 2048 ? 12 : next >= 1024 ? 11 : next >= 512 ? 10 : 9;
            }
        }

        return output.ToArray();
    }

    private static byte[] Concat(byte[] a, byte b)
    {
        var r = new byte[a.Length + 1];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        r[a.Length] = b;
        return r;
    }

    public static byte[] AsciiHexDecode(byte[] data)
    {
        var output = new List<byte>(data.Length / 2);
        int high = -1;
        foreach (byte b in data)
        {
            if (b == '>')
            {
                break;
            }

            if (!Parsing.PdfLexer.IsHexDigit(b))
            {
                continue;
            }

            int v = Parsing.PdfLexer.HexValue(b);
            if (high < 0)
            {
                high = v;
            }
            else
            {
                output.Add((byte)((high << 4) | v));
                high = -1;
            }
        }

        if (high >= 0)
        {
            output.Add((byte)(high << 4));
        }

        return output.ToArray();
    }

    public static byte[] Ascii85Decode(byte[] data)
    {
        var output = new List<byte>(data.Length);
        int start = 0;
        if (data.Length >= 2 && data[0] == '<' && data[1] == '~')
        {
            start = 2;
        }

        Span<byte> group = stackalloc byte[5];
        int count = 0;
        for (int i = start; i < data.Length; i++)
        {
            byte b = data[i];
            if (b == '~')
            {
                break;
            }

            if (Parsing.PdfLexer.IsWhitespace(b))
            {
                continue;
            }

            if (b == 'z' && count == 0)
            {
                output.AddRange(new byte[4]);
                continue;
            }

            if (b < '!' || b > 'u')
            {
                throw new InvalidDataException("Invalid ASCII85 character.");
            }

            group[count++] = (byte)(b - '!');
            if (count == 5)
            {
                uint value = 0;
                for (int j = 0; j < 5; j++)
                {
                    value = (value * 85) + group[j];
                }

                output.Add((byte)(value >> 24));
                output.Add((byte)(value >> 16));
                output.Add((byte)(value >> 8));
                output.Add((byte)value);
                count = 0;
            }
        }

        if (count > 1)
        {
            for (int j = count; j < 5; j++)
            {
                group[j] = 84;
            }

            uint value = 0;
            for (int j = 0; j < 5; j++)
            {
                value = (value * 85) + group[j];
            }

            for (int j = 0; j < count - 1; j++)
            {
                output.Add((byte)(value >> (24 - (8 * j))));
            }
        }

        return output.ToArray();
    }

    public static byte[] RunLengthDecode(byte[] data)
    {
        var output = new List<byte>(data.Length * 2);
        int i = 0;
        while (i < data.Length)
        {
            int length = data[i++];
            if (length == 128)
            {
                break;
            }

            if (length < 128)
            {
                int n = Math.Min(length + 1, data.Length - i);
                for (int j = 0; j < n; j++)
                {
                    output.Add(data[i + j]);
                }

                i += n;
            }
            else if (i < data.Length)
            {
                byte value = data[i++];
                for (int j = 0; j < 257 - length; j++)
                {
                    output.Add(value);
                }
            }
        }

        return output.ToArray();
    }
}
