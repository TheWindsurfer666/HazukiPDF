namespace HazukiPdf.Imaging;

/// <summary>Baseline JPEG encoder (JFIF, standard Huffman tables) for grayscale and RGB images.</summary>
internal static class JpegEncoder
{
    private static readonly HuffmanCode[] DcLuminance = BuildCodes(JpegCommon.DcLuminanceBits, JpegCommon.DcLuminanceValues);
    private static readonly HuffmanCode[] DcChrominance = BuildCodes(JpegCommon.DcChrominanceBits, JpegCommon.DcChrominanceValues);
    private static readonly HuffmanCode[] AcLuminance = BuildCodes(JpegCommon.AcLuminanceBits, JpegCommon.AcLuminanceValues);
    private static readonly HuffmanCode[] AcChrominance = BuildCodes(JpegCommon.AcChrominanceBits, JpegCommon.AcChrominanceValues);

    private readonly record struct HuffmanCode(ushort Code, byte Length);

    /// <summary>Encodes interleaved 8-bit samples (1 = gray, 3 = RGB) as a baseline JPEG.</summary>
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

        var lumaQ = JpegCommon.ScaleQuantization(JpegCommon.LuminanceQuantization, quality);
        var chromaQ = JpegCommon.ScaleQuantization(JpegCommon.ChrominanceQuantization, quality);
        bool subsample = components == 3 && quality < 90;

        var output = new MemoryStream((width * height * components / 8) + 1024);
        WriteHeaders(output, width, height, components, lumaQ, chromaQ, subsample);

        var writer = new BitWriter(output);
        float[] lumaDivisors = Divisors(lumaQ);
        float[] chromaDivisors = Divisors(chromaQ);

        if (components == 1)
        {
            EncodeGray(pixels, width, height, writer, lumaDivisors);
        }
        else
        {
            EncodeColor(pixels, width, height, writer, lumaDivisors, chromaDivisors, subsample);
        }

        writer.Flush();
        output.WriteByte(0xFF);
        output.WriteByte(0xD9);
        return output.ToArray();
    }

    private static float[] Divisors(byte[] table)
    {
        var result = new float[64];
        for (int i = 0; i < 64; i++)
        {
            result[i] = table[i];
        }

        return result;
    }

    private static void EncodeGray(byte[] pixels, int width, int height, BitWriter writer, float[] q)
    {
        Span<float> block = stackalloc float[64];
        int previousDc = 0;
        for (int by = 0; by < height; by += 8)
        {
            for (int bx = 0; bx < width; bx += 8)
            {
                for (int y = 0; y < 8; y++)
                {
                    int sy = Math.Min(by + y, height - 1);
                    for (int x = 0; x < 8; x++)
                    {
                        int sx = Math.Min(bx + x, width - 1);
                        block[(y * 8) + x] = pixels[(sy * width) + sx] - 128f;
                    }
                }

                previousDc = EncodeBlock(block, q, previousDc, DcLuminance, AcLuminance, writer);
            }
        }
    }

    private static void EncodeColor(byte[] pixels, int width, int height, BitWriter writer, float[] lq, float[] cq, bool subsample)
    {
        // Convert the whole image to YCbCr planes first.
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

        Span<float> block = stackalloc float[64];
        int dcY = 0, dcCb = 0, dcCr = 0;
        int mcu = subsample ? 16 : 8;
        for (int my = 0; my < height; my += mcu)
        {
            for (int mx = 0; mx < width; mx += mcu)
            {
                if (subsample)
                {
                    for (int sub = 0; sub < 4; sub++)
                    {
                        FillBlock(yPlane, width, height, mx + ((sub & 1) * 8), my + ((sub >> 1) * 8), 1, block);
                        dcY = EncodeBlock(block, lq, dcY, DcLuminance, AcLuminance, writer);
                    }

                    FillBlock(cbPlane, width, height, mx, my, 2, block);
                    dcCb = EncodeBlock(block, cq, dcCb, DcChrominance, AcChrominance, writer);
                    FillBlock(crPlane, width, height, mx, my, 2, block);
                    dcCr = EncodeBlock(block, cq, dcCr, DcChrominance, AcChrominance, writer);
                }
                else
                {
                    FillBlock(yPlane, width, height, mx, my, 1, block);
                    dcY = EncodeBlock(block, lq, dcY, DcLuminance, AcLuminance, writer);
                    FillBlock(cbPlane, width, height, mx, my, 1, block);
                    dcCb = EncodeBlock(block, cq, dcCb, DcChrominance, AcChrominance, writer);
                    FillBlock(crPlane, width, height, mx, my, 1, block);
                    dcCr = EncodeBlock(block, cq, dcCr, DcChrominance, AcChrominance, writer);
                }
            }
        }
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

    private static int EncodeBlock(Span<float> block, float[] q, int previousDc, HuffmanCode[] dc, HuffmanCode[] ac, BitWriter writer)
    {
        JpegCommon.ForwardDct(block);
        Span<int> coefficients = stackalloc int[64];
        for (int i = 0; i < 64; i++)
        {
            int natural = JpegCommon.ZigZag[i];
            int limit = i == 0 ? 2047 : 1023;
            coefficients[i] = Math.Clamp((int)MathF.Round(block[natural] / q[natural]), -limit, limit);
        }

        int diff = coefficients[0] - previousDc;
        int category = Category(diff);
        writer.Write(dc[category].Code, dc[category].Length);
        if (category > 0)
        {
            writer.Write(Magnitude(diff, category), category);
        }

        int run = 0;
        for (int i = 1; i < 64; i++)
        {
            int value = coefficients[i];
            if (value == 0)
            {
                run++;
                continue;
            }

            while (run > 15)
            {
                writer.Write(ac[0xF0].Code, ac[0xF0].Length);
                run -= 16;
            }

            int size = Category(value);
            int symbol = (run << 4) | size;
            writer.Write(ac[symbol].Code, ac[symbol].Length);
            writer.Write(Magnitude(value, size), size);
            run = 0;
        }

        if (run > 0)
        {
            writer.Write(ac[0].Code, ac[0].Length);
        }

        return coefficients[0];
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

    private static HuffmanCode[] BuildCodes(byte[] bits, byte[] values)
    {
        var codes = new HuffmanCode[256];
        int code = 0;
        int k = 0;
        for (int length = 1; length <= 16; length++)
        {
            for (int i = 0; i < bits[length - 1]; i++)
            {
                codes[values[k++]] = new HuffmanCode((ushort)code, (byte)length);
                code++;
            }

            code <<= 1;
        }

        return codes;
    }

    private static void WriteHeaders(Stream output, int width, int height, int components, byte[] lumaQ, byte[] chromaQ, bool subsample)
    {
        output.Write(new byte[] { 0xFF, 0xD8 });

        // JFIF APP0
        output.Write(new byte[] { 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 });

        WriteQuantization(output, 0, lumaQ);
        if (components == 3)
        {
            WriteQuantization(output, 1, chromaQ);
        }

        // SOF0
        int sofLength = 8 + (components * 3);
        output.Write(new byte[] { 0xFF, 0xC0, (byte)(sofLength >> 8), (byte)sofLength, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)components });
        if (components == 1)
        {
            output.Write(new byte[] { 1, 0x11, 0 });
        }
        else
        {
            output.Write(new byte[] { 1, (byte)(subsample ? 0x22 : 0x11), 0, 2, 0x11, 1, 3, 0x11, 1 });
        }

        WriteHuffman(output, 0x00, JpegCommon.DcLuminanceBits, JpegCommon.DcLuminanceValues);
        WriteHuffman(output, 0x10, JpegCommon.AcLuminanceBits, JpegCommon.AcLuminanceValues);
        if (components == 3)
        {
            WriteHuffman(output, 0x01, JpegCommon.DcChrominanceBits, JpegCommon.DcChrominanceValues);
            WriteHuffman(output, 0x11, JpegCommon.AcChrominanceBits, JpegCommon.AcChrominanceValues);
        }

        // SOS
        int sosLength = 6 + (components * 2);
        output.Write(new byte[] { 0xFF, 0xDA, (byte)(sosLength >> 8), (byte)sosLength, (byte)components });
        if (components == 1)
        {
            output.Write(new byte[] { 1, 0x00 });
        }
        else
        {
            output.Write(new byte[] { 1, 0x00, 2, 0x11, 3, 0x11 });
        }

        output.Write(new byte[] { 0, 63, 0 });
    }

    private static void WriteQuantization(Stream output, int id, byte[] table)
    {
        output.Write(new byte[] { 0xFF, 0xDB, 0, 67, (byte)id });
        for (int i = 0; i < 64; i++)
        {
            output.WriteByte(table[JpegCommon.ZigZag[i]]);
        }
    }

    private static void WriteHuffman(Stream output, int classAndId, byte[] bits, byte[] values)
    {
        int length = 2 + 1 + 16 + values.Length;
        output.Write(new byte[] { 0xFF, 0xC4, (byte)(length >> 8), (byte)length, (byte)classAndId });
        output.Write(bits);
        output.Write(values);
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
