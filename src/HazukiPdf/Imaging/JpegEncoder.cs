namespace HazukiPdf.Imaging;

/// <summary>
/// Sequential JPEG encoder with per-image optimized Huffman tables. Encodes grayscale and RGB pixels, and can
/// re-encode the coefficients of an existing JPEG losslessly (any component count), which typically saves
/// 5 to 15% on files written with the standard tables.
/// </summary>
internal static class JpegEncoder
{
    /// <summary>Encodes interleaved 8-bit samples (1 = gray, 3 = RGB) as a JPEG.</summary>
    public static byte[] Encode(byte[] pixels, int width, int height, int components, int quality)
    {
        if (components is not (1 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(components), "Only grayscale and RGB images can be encoded.");
        }

        if (width <= 0 || height <= 0 || width > 65535 || height > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be between 1 and 65535.");
        }

        if (pixels.Length < (long)width * height * components)
        {
            throw new ArgumentException("Not enough pixel data.", nameof(pixels));
        }

        var lumaQ = ToInts(JpegCommon.ScaleQuantization(JpegCommon.LuminanceQuantization, quality));
        var chromaQ = ToInts(JpegCommon.ScaleQuantization(JpegCommon.ChrominanceQuantization, quality));
        bool subsample = components == 3 && quality < 90;
        int maxFactor = subsample ? 2 : 1;
        int mcusX = (width + (8 * maxFactor) - 1) / (8 * maxFactor);
        int mcusY = (height + (8 * maxFactor) - 1) / (8 * maxFactor);

        JpegComponent[] result;
        if (components == 1)
        {
            var plane = new float[width * height];
            for (int i = 0; i < plane.Length; i++)
            {
                plane[i] = pixels[i] - 128f;
            }

            result = new[] { Quantize(1, 1, 1, plane, width, height, 1, lumaQ, mcusX, mcusY) };
        }
        else
        {
            int count = width * height;
            var yPlane = new float[count];
            var cbPlane = new float[count];
            var crPlane = new float[count];
            for (int i = 0; i < count; i++)
            {
                float r = pixels[i * 3];
                float g = pixels[(i * 3) + 1];
                float b = pixels[(i * 3) + 2];
                yPlane[i] = (0.299f * r) + (0.587f * g) + (0.114f * b) - 128f;
                cbPlane[i] = (-0.168736f * r) - (0.331264f * g) + (0.5f * b);
                crPlane[i] = (0.5f * r) - (0.418688f * g) - (0.081312f * b);
            }

            result = new[]
            {
                Quantize(1, maxFactor, maxFactor, yPlane, width, height, 1, lumaQ, mcusX, mcusY),
                Quantize(2, 1, 1, cbPlane, width, height, maxFactor, chromaQ, mcusX, mcusY),
                Quantize(3, 1, 1, crPlane, width, height, maxFactor, chromaQ, mcusX, mcusY),
            };
        }

        return Write(new JpegCoefficients(width, height, result, null, false));
    }

    private static int[] ToInts(byte[] table) => table.Select(b => (int)b).ToArray();

    /// <summary>Transforms and quantizes one component; <paramref name="scale"/> pixels are averaged per sample.</summary>
    private static JpegComponent Quantize(int id, int h, int v, float[] plane, int width, int height, int scale, int[] q, int mcusX, int mcusY)
    {
        int blocksPerLine = mcusX * h;
        int blocksPerColumn = mcusY * v;
        var coefficients = new short[blocksPerLine * blocksPerColumn * 64];
        Span<float> block = stackalloc float[64];
        for (int by = 0; by < blocksPerColumn; by++)
        {
            for (int bx = 0; bx < blocksPerLine; bx++)
            {
                FillBlock(plane, width, height, bx * 8 * scale, by * 8 * scale, scale, block);
                JpegCommon.ForwardDct(block);
                int offset = ((by * blocksPerLine) + bx) * 64;
                for (int i = 0; i < 64; i++)
                {
                    int natural = JpegCommon.ZigZag[i];
                    int limit = i == 0 ? 2047 : 1023;
                    coefficients[offset + i] = (short)Math.Clamp((int)MathF.Round(block[natural] / q[natural]), -limit, limit);
                }
            }
        }

        return new JpegComponent(id, h, v, q, blocksPerLine, blocksPerColumn, coefficients);
    }

    /// <summary>Fills an 8x8 block from a plane, averaging factor x factor pixels per sample and clamping at edges.</summary>
    private static void FillBlock(float[] plane, int width, int height, int x0, int y0, int factor, Span<float> block)
    {
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                float sum = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int sy = Math.Min(y0 + (y * factor) + dy, height - 1);
                    for (int dx = 0; dx < factor; dx++)
                    {
                        int sx = Math.Min(x0 + (x * factor) + dx, width - 1);
                        sum += plane[(sy * width) + sx];
                    }
                }

                block[(y * 8) + x] = sum / (factor * factor);
            }
        }
    }

    /// <summary>Writes coefficients as a sequential JPEG with Huffman tables optimized for this image.</summary>
    public static byte[] Write(JpegCoefficients image)
    {
        var components = image.Components;
        int count = components.Length;
        int maxH = components.Max(c => c.H);
        int maxV = components.Max(c => c.V);

        // Component 0 uses table 0; the rest share table 1 (as in YCbCr images).
        var dcFrequencies = new long[2, 257];
        var acFrequencies = new long[2, 257];
        ForEachBlock(image, maxH, maxV, (componentIndex, coefficients, offset, previousDc) =>
        {
            int table = componentIndex == 0 ? 0 : 1;
            dcFrequencies[table, Category(coefficients[offset] - previousDc)]++;
            CountAc(coefficients, offset, acFrequencies, table);
        });

        var dcTables = new HuffmanTable?[2];
        var acTables = new HuffmanTable?[2];
        int tableCount = count == 1 ? 1 : 2;
        for (int t = 0; t < tableCount; t++)
        {
            dcTables[t] = HuffmanTable.Build(Row(dcFrequencies, t));
            acTables[t] = HuffmanTable.Build(Row(acFrequencies, t));
        }

        var output = new MemoryStream();
        output.Write(new byte[] { 0xFF, 0xD8 });
        if (image.AdobeSegment != null)
        {
            output.Write(image.AdobeSegment);
        }
        else if (count is 1 or 3)
        {
            output.Write(new byte[] { 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 });
        }

        // Quantization tables, shared between components that use identical ones.
        var tables = new List<int[]>();
        var tableIds = new int[count];
        for (int i = 0; i < count; i++)
        {
            int existing = tables.FindIndex(t => t.AsSpan().SequenceEqual(components[i].Quantization));
            if (existing < 0)
            {
                tables.Add(components[i].Quantization);
                existing = tables.Count - 1;
            }

            tableIds[i] = existing;
        }

        bool wide = tables.Any(t => t.Any(v => v > 255));
        for (int t = 0; t < tables.Count; t++)
        {
            WriteQuantization(output, t, tables[t], wide);
        }

        // SOF0 (baseline) unless 16-bit tables or more than two Huffman table sets require SOF1.
        int sofLength = 8 + (count * 3);
        byte sof = (byte)(wide || image.Extended || tables.Count > 2 ? 0xC1 : 0xC0);
        output.Write(new byte[] { 0xFF, sof, (byte)(sofLength >> 8), (byte)sofLength, 8, (byte)(image.Height >> 8), (byte)image.Height, (byte)(image.Width >> 8), (byte)image.Width, (byte)count });
        for (int i = 0; i < count; i++)
        {
            output.Write(new[] { (byte)components[i].Id, (byte)((components[i].H << 4) | components[i].V), (byte)tableIds[i] });
        }

        for (int t = 0; t < tableCount; t++)
        {
            WriteHuffman(output, 0x00 | t, dcTables[t]!);
            WriteHuffman(output, 0x10 | t, acTables[t]!);
        }

        int sosLength = 6 + (count * 2);
        output.Write(new byte[] { 0xFF, 0xDA, (byte)(sosLength >> 8), (byte)sosLength, (byte)count });
        for (int i = 0; i < count; i++)
        {
            int t = i == 0 ? 0 : 1;
            output.Write(new[] { (byte)components[i].Id, (byte)((t << 4) | t) });
        }

        output.Write(new byte[] { 0, 63, 0 });

        var writer = new BitWriter(output);
        ForEachBlock(image, maxH, maxV, (componentIndex, coefficients, offset, previousDc) =>
        {
            int t = componentIndex == 0 ? 0 : 1;
            EncodeBlock(coefficients, offset, previousDc, dcTables[t]!, acTables[t]!, writer);
        });

        writer.Flush();
        output.WriteByte(0xFF);
        output.WriteByte(0xD9);
        return output.ToArray();
    }

    private static long[] Row(long[,] table, int row)
    {
        var result = new long[257];
        for (int i = 0; i < 257; i++)
        {
            result[i] = table[row, i];
        }

        return result;
    }

    private delegate void BlockVisitor(int componentIndex, short[] coefficients, int offset, int previousDc);

    /// <summary>Visits blocks in scan order: block rows for one component, MCU order for several.</summary>
    private static void ForEachBlock(JpegCoefficients image, int maxH, int maxV, BlockVisitor visit)
    {
        var components = image.Components;
        var previous = new int[components.Length];
        if (components.Length == 1)
        {
            var c = components[0];
            int compWidth = ((image.Width * c.H) + maxH - 1) / maxH;
            int compHeight = ((image.Height * c.V) + maxV - 1) / maxV;
            int blocksX = Math.Min(c.BlocksPerLine, (compWidth + 7) / 8);
            int blocksY = Math.Min(c.BlocksPerColumn, (compHeight + 7) / 8);
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int offset = ((by * c.BlocksPerLine) + bx) * 64;
                    visit(0, c.Coefficients, offset, previous[0]);
                    previous[0] = c.Coefficients[offset];
                }
            }

            return;
        }

        int mcusX = (image.Width + (8 * maxH) - 1) / (8 * maxH);
        int mcusY = (image.Height + (8 * maxV) - 1) / (8 * maxV);
        for (int my = 0; my < mcusY; my++)
        {
            for (int mx = 0; mx < mcusX; mx++)
            {
                for (int i = 0; i < components.Length; i++)
                {
                    var c = components[i];
                    for (int v = 0; v < c.V; v++)
                    {
                        for (int h = 0; h < c.H; h++)
                        {
                            int bx = (mx * c.H) + h;
                            int by = (my * c.V) + v;
                            int offset = bx < c.BlocksPerLine && by < c.BlocksPerColumn ? ((by * c.BlocksPerLine) + bx) * 64 : -1;
                            if (offset < 0)
                            {
                                throw new InvalidDataException("Coefficient data does not cover the image.");
                            }

                            visit(i, c.Coefficients, offset, previous[i]);
                            previous[i] = c.Coefficients[offset];
                        }
                    }
                }
            }
        }
    }

    private static void CountAc(short[] coefficients, int offset, long[,] frequencies, int table)
    {
        int run = 0;
        for (int i = 1; i < 64; i++)
        {
            int value = coefficients[offset + i];
            if (value == 0)
            {
                run++;
                continue;
            }

            while (run > 15)
            {
                frequencies[table, 0xF0]++;
                run -= 16;
            }

            frequencies[table, (run << 4) | Category(value)]++;
            run = 0;
        }

        if (run > 0)
        {
            frequencies[table, 0]++;
        }
    }

    private static void EncodeBlock(short[] coefficients, int offset, int previousDc, HuffmanTable dc, HuffmanTable ac, BitWriter writer)
    {
        int diff = coefficients[offset] - previousDc;
        int category = Category(diff);
        writer.Write(dc.Codes[category], dc.Lengths[category]);
        if (category > 0)
        {
            writer.Write(Magnitude(diff, category), category);
        }

        int run = 0;
        for (int i = 1; i < 64; i++)
        {
            int value = coefficients[offset + i];
            if (value == 0)
            {
                run++;
                continue;
            }

            while (run > 15)
            {
                writer.Write(ac.Codes[0xF0], ac.Lengths[0xF0]);
                run -= 16;
            }

            int size = Category(value);
            int symbol = (run << 4) | size;
            writer.Write(ac.Codes[symbol], ac.Lengths[symbol]);
            writer.Write(Magnitude(value, size), size);
            run = 0;
        }

        if (run > 0)
        {
            writer.Write(ac.Codes[0], ac.Lengths[0]);
        }
    }

    private static int Category(int value)
    {
        value = Math.Abs(value);
        int bits = 0;
        while (value > 0)
        {
            bits++;
            value >>= 1;
        }

        return bits;
    }

    private static int Magnitude(int value, int size) => value >= 0 ? value : value + (1 << size) - 1;

    private static void WriteQuantization(Stream output, int id, int[] table, bool wide)
    {
        int length = 2 + 1 + (wide ? 128 : 64);
        output.Write(new byte[] { 0xFF, 0xDB, (byte)(length >> 8), (byte)length, (byte)((wide ? 0x10 : 0) | id) });
        for (int i = 0; i < 64; i++)
        {
            int value = Math.Clamp(table[JpegCommon.ZigZag[i]], 1, wide ? 65535 : 255);
            if (wide)
            {
                output.WriteByte((byte)(value >> 8));
            }

            output.WriteByte((byte)value);
        }
    }

    private static void WriteHuffman(Stream output, int classAndId, HuffmanTable table)
    {
        int length = 2 + 1 + 16 + table.Values.Length;
        output.Write(new byte[] { 0xFF, 0xC4, (byte)(length >> 8), (byte)length, (byte)classAndId });
        output.Write(table.Bits);
        output.Write(table.Values);
    }

    /// <summary>A Huffman table built from symbol frequencies (ITU-T T.81, Annex K.2).</summary>
    private sealed class HuffmanTable
    {
        private HuffmanTable(byte[] bits, byte[] values)
        {
            Bits = bits;
            Values = values;
            int code = 0;
            int k = 0;
            for (int length = 1; length <= 16; length++)
            {
                for (int i = 0; i < bits[length - 1]; i++)
                {
                    Codes[values[k]] = code++;
                    Lengths[values[k]] = length;
                    k++;
                }

                code <<= 1;
            }
        }

        public byte[] Bits { get; }

        public byte[] Values { get; }

        public int[] Codes { get; } = new int[256];

        public int[] Lengths { get; } = new int[256];

        public static HuffmanTable Build(long[] frequencies)
        {
            var freq = (long[])frequencies.Clone();
            if (!freq.AsSpan(0, 256).ContainsAnyExcept(0L))
            {
                freq[0] = 1; // an unused table still needs one code
            }

            freq[256] = 1; // reserves the all-ones code
            var codeSize = new int[257];
            var others = Enumerable.Repeat(-1, 257).ToArray();
            while (true)
            {
                int c1 = -1, c2 = -1;
                long v = long.MaxValue;
                for (int i = 0; i <= 256; i++)
                {
                    if (freq[i] > 0 && freq[i] <= v)
                    {
                        v = freq[i];
                        c1 = i;
                    }
                }

                v = long.MaxValue;
                for (int i = 0; i <= 256; i++)
                {
                    if (freq[i] > 0 && freq[i] <= v && i != c1)
                    {
                        v = freq[i];
                        c2 = i;
                    }
                }

                if (c2 < 0)
                {
                    break;
                }

                freq[c1] += freq[c2];
                freq[c2] = 0;
                codeSize[c1]++;
                while (others[c1] >= 0)
                {
                    c1 = others[c1];
                    codeSize[c1]++;
                }

                others[c1] = c2;
                codeSize[c2]++;
                while (others[c2] >= 0)
                {
                    c2 = others[c2];
                    codeSize[c2]++;
                }
            }

            var bits = new int[33];
            for (int i = 0; i <= 256; i++)
            {
                if (codeSize[i] > 0)
                {
                    bits[codeSize[i]]++;
                }
            }

            // Limit code lengths to 16 bits.
            for (int i = 32; i > 16; i--)
            {
                while (bits[i] > 0)
                {
                    int j = i - 2;
                    while (bits[j] == 0)
                    {
                        j--;
                    }

                    bits[i] -= 2;
                    bits[i - 1]++;
                    bits[j + 1] += 2;
                    bits[j]--;
                }
            }

            // Remove the reserved symbol's code.
            int last = 16;
            while (bits[last] == 0)
            {
                last--;
            }

            bits[last]--;

            var values = new List<byte>();
            for (int length = 1; length <= 32; length++)
            {
                for (int symbol = 0; symbol < 256; symbol++)
                {
                    if (codeSize[symbol] == length)
                    {
                        values.Add((byte)symbol);
                    }
                }
            }

            var bitCounts = new byte[16];
            for (int i = 1; i <= 16; i++)
            {
                bitCounts[i - 1] = (byte)bits[i];
            }

            return new HuffmanTable(bitCounts, values.ToArray());
        }
    }

    private sealed class BitWriter
    {
        private readonly Stream _output;
        private int _buffer;
        private int _count;

        public BitWriter(Stream output) => _output = output;

        public void Write(int value, int length)
        {
            if (length == 0)
            {
                return;
            }

            _buffer = (_buffer << length) | (value & ((1 << length) - 1));
            _count += length;
            while (_count >= 8)
            {
                byte b = (byte)(_buffer >> (_count - 8));
                _output.WriteByte(b);
                if (b == 0xFF)
                {
                    _output.WriteByte(0);
                }

                _count -= 8;
                _buffer &= (1 << _count) - 1;
            }
        }

        public void Flush()
        {
            if (_count > 0)
            {
                Write(0x7F, 8 - _count); // pad with one bits
            }
        }
    }
}
