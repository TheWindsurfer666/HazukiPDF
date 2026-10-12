using System.Buffers.Binary;
using System.Text;
using HazukiPdf.Imaging;
using HazukiPdf.Objects;
using HazukiPdf.Parsing;

namespace HazukiPdf.Tests;

public class Jbig2Tests
{
    [Theory]
    [InlineData(203, 117, 1)]
    [InlineData(64, 64, 2)]
    [InlineData(17, 300, 3)]
    public void Encode_DecodesBackToTheSamePixels(int width, int height, int seed)
    {
        var packed = ScanLikeBitmap(width, height, seed);
        var jbig2 = Jbig2Encoder.Encode(packed, width, height);
        Assert.Equal(packed, Jbig2TestDecoder.Decode(jbig2, width, height));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0xFF)]
    public void Encode_HandlesBlankPages(byte fill)
    {
        var packed = Enumerable.Repeat(fill, 13 * 40).ToArray();
        var jbig2 = Jbig2Encoder.Encode(packed, 100, 40);
        Assert.True(jbig2.Length < 100);
        var decoded = Jbig2TestDecoder.Decode(jbig2, 100, 40);
        for (int row = 0; row < 40; row++)
        {
            // Bits past the image width are padding and may differ.
            Assert.Equal(packed.AsSpan(row * 13, 12).ToArray(), decoded.AsSpan(row * 13, 12).ToArray());
            Assert.Equal(packed[(row * 13) + 12] & 0xF0, decoded[(row * 13) + 12] & 0xF0);
        }
    }

    [Fact]
    public void Compressor_StoresScannedPagesAsJbig2()
    {
        const int width = 600;
        const int height = 400;
        var packed = ScanLikeBitmap(width, height, 7);
        var pdf = TestPdf.WithImage(packed, width, height, "/DeviceGray", 300, 200, "/BitsPerComponent 1 /Decode [1 0]");

        var output = PdfCompressor.Compress(pdf, PdfCompressionLevel.Lossless, out var result);

        Assert.True(result.CompressedSize < pdf.Length);
        var image = FindImage(PdfReader.Read(output));
        Assert.Equal("JBIG2Decode", image.GetName("Filter"));
        Assert.Equal(packed, Jbig2TestDecoder.Decode(image.Data, width, height));
    }

    [Fact]
    public void Compressor_LeavesJbig2OffWhenAskedTo()
    {
        var packed = ScanLikeBitmap(600, 400, 7);
        var pdf = TestPdf.WithImage(packed, 600, 400, "/DeviceGray", 300, 200, "/BitsPerComponent 1");

        var output = PdfCompressor.Compress(pdf, new PdfCompressionOptions { UseJbig2 = false });

        Assert.Equal("FlateDecode", FindImage(PdfReader.Read(output)).GetName("Filter"));
    }

    private static PdfStream FindImage(PdfDocument document) =>
        document.Objects.Values.OfType<PdfStream>().Single(s => s.GetName("Subtype") == "Image");

    /// <summary>Lines of "text" (short dark runs) with some speckle noise, packed one bit per pixel, 1 = white, white padding.</summary>
    private static byte[] ScanLikeBitmap(int width, int height, int seed)
    {
        var random = new Random(seed);
        int stride = (width + 7) / 8;
        var packed = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            bool textLine = y % 14 is > 3 and < 11;
            for (int x = 0; x < stride * 8; x++)
            {
                bool black = x < width && ((textLine && (x / 3 * 7 % 11) < 5 && random.Next(9) > 0) || random.Next(200) == 0);
                if (!black)
                {
                    packed[(y * stride) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
        }

        return packed;
    }
}

/// <summary>
/// A minimal JBIG2 decoder for the subset the encoder writes (one generic region, template 0, no typical prediction),
/// written from ITU-T T.88 independently of the encoder so the two check each other.
/// </summary>
internal static class Jbig2TestDecoder
{
    private static readonly (int Qe, int Nmps, int Nlps, int Switch)[] Table =
    {
        (0x5601, 1, 1, 1), (0x3401, 2, 6, 0), (0x1801, 3, 9, 0), (0x0AC1, 4, 12, 0), (0x0521, 5, 29, 0), (0x0221, 38, 33, 0),
        (0x5601, 7, 6, 1), (0x5401, 8, 14, 0), (0x4801, 9, 14, 0), (0x3801, 10, 14, 0), (0x3001, 11, 17, 0), (0x2401, 12, 18, 0),
        (0x1C01, 13, 20, 0), (0x1601, 29, 21, 0), (0x5601, 15, 14, 1), (0x5401, 16, 14, 0), (0x5101, 17, 15, 0), (0x4801, 18, 16, 0),
        (0x3801, 19, 17, 0), (0x3401, 20, 18, 0), (0x3001, 21, 19, 0), (0x2801, 22, 19, 0), (0x2401, 23, 20, 0), (0x2201, 24, 21, 0),
        (0x1C01, 25, 22, 0), (0x1801, 26, 23, 0), (0x1601, 27, 24, 0), (0x1401, 28, 25, 0), (0x1201, 29, 26, 0), (0x1101, 30, 27, 0),
        (0x0AC1, 31, 28, 0), (0x09C1, 32, 29, 0), (0x08A1, 33, 30, 0), (0x0521, 34, 31, 0), (0x0441, 35, 32, 0), (0x02A1, 36, 33, 0),
        (0x0221, 37, 34, 0), (0x0141, 38, 35, 0), (0x0111, 39, 36, 0), (0x0085, 40, 37, 0), (0x0049, 41, 38, 0), (0x0025, 42, 39, 0),
        (0x0015, 43, 40, 0), (0x0009, 44, 41, 0), (0x0005, 45, 42, 0), (0x0001, 45, 43, 0), (0x5601, 46, 46, 0),
    };

    public static byte[] Decode(byte[] stream, int width, int height)
    {
        int p = 0;
        byte[]? region = null;
        while (p + 11 <= stream.Length)
        {
            int type = stream[p + 4] & 0x3F;
            int length = BinaryPrimitives.ReadInt32BigEndian(stream.AsSpan(p + 7));
            p += 11;
            if (type == 48)
            {
                Assert.Equal(width, BinaryPrimitives.ReadInt32BigEndian(stream.AsSpan(p)));
                Assert.Equal(height, BinaryPrimitives.ReadInt32BigEndian(stream.AsSpan(p + 4)));
            }
            else if (type == 39)
            {
                region = stream.AsSpan(p, length).ToArray();
            }

            p += length;
        }

        Assert.NotNull(region);
        Assert.Equal(0, region![17]); // arithmetic, template 0, no TPGDON
        var at = new (int X, int Y)[4];
        for (int i = 0; i < 4; i++)
        {
            at[i] = ((sbyte)region[18 + (2 * i)], (sbyte)region[19 + (2 * i)]);
        }

        var bitmap = new int[width * height];
        int Pixel(int x, int y) => x < 0 || y < 0 || x >= width ? 0 : bitmap[(y * width) + x];

        var decoder = new MqDecoder(region.AsSpan(26).ToArray());
        var index = new int[1 << 16];
        var mps = new int[1 << 16];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int context = 0;
                foreach (var (dx, dy) in new[]
                {
                    (at[3].X, at[3].Y), (-1, -2), (0, -2), (1, -2), (at[2].X, at[2].Y),
                    (at[1].X, at[1].Y), (-2, -1), (-1, -1), (0, -1), (1, -1), (2, -1), (at[0].X, at[0].Y),
                    (-4, 0), (-3, 0), (-2, 0), (-1, 0),
                })
                {
                    context = (context << 1) | Pixel(x + dx, y + dy);
                }

                bitmap[(y * width) + x] = decoder.Decode(index, mps, context);
            }
        }

        // The PDF filter hands out 0 for black.
        int stride = (width + 7) / 8;
        var packed = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (bitmap[(y * width) + x] == 0)
                {
                    packed[(y * stride) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }

            for (int x = width; x < stride * 8; x++)
            {
                packed[(y * stride) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }

        return packed;
    }

    private sealed class MqDecoder
    {
        private readonly byte[] _data;
        private int _bp;
        private uint _a;
        private uint _c;
        private int _ct;

        public MqDecoder(byte[] data)
        {
            _data = data;
            _c = (uint)At(0) << 16;
            ByteIn();
            _c <<= 7;
            _ct -= 7;
            _a = 0x8000;
        }

        private int At(int i) => i < _data.Length ? _data[i] : 0xFF;

        private void ByteIn()
        {
            if (At(_bp) == 0xFF)
            {
                if (At(_bp + 1) > 0x8F)
                {
                    _c += 0xFF00;
                    _ct = 8;
                }
                else
                {
                    _bp++;
                    _c += (uint)At(_bp) << 9;
                    _ct = 7;
                }
            }
            else
            {
                _bp++;
                _c += (uint)At(_bp) << 8;
                _ct = 8;
            }
        }

        public int Decode(int[] index, int[] mps, int cx)
        {
            var (qe, nmps, nlps, sw) = Table[index[cx]];
            int d;
            _a -= (uint)qe;
            if ((_c >> 16) < qe)
            {
                if (_a < qe)
                {
                    d = mps[cx];
                    index[cx] = nmps;
                }
                else
                {
                    d = 1 - mps[cx];
                    if (sw == 1)
                    {
                        mps[cx] = 1 - mps[cx];
                    }

                    index[cx] = nlps;
                }

                _a = (uint)qe;
                Renormalize();
            }
            else
            {
                _c -= (uint)qe << 16;
                if ((_a & 0x8000) != 0)
                {
                    return mps[cx];
                }

                if (_a < qe)
                {
                    d = 1 - mps[cx];
                    if (sw == 1)
                    {
                        mps[cx] = 1 - mps[cx];
                    }

                    index[cx] = nlps;
                }
                else
                {
                    d = mps[cx];
                    index[cx] = nmps;
                }

                Renormalize();
            }

            return d;
        }

        private void Renormalize()
        {
            do
            {
                if (_ct == 0)
                {
                    ByteIn();
                }

                _a <<= 1;
                _c <<= 1;
                _ct--;
            }
            while ((_a & 0x8000) == 0);
        }
    }
}
