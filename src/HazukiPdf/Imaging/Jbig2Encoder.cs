using System.Buffers.Binary;

namespace HazukiPdf.Imaging;

/// <summary>
/// Lossless JBIG2 encoder (ITU-T T.88) for black-and-white images: one generic region coded with the MQ arithmetic
/// coder and template 0, written as the embedded stream that the PDF JBIG2Decode filter reads.
/// Scanned text pages usually come out a third to a half smaller than with Flate or CCITT Group 4.
/// </summary>
internal static class Jbig2Encoder
{
    /// <summary>
    /// Encodes a 1-bit image. Rows are packed MSB first and padded to whole bytes, as in a PDF image stream.
    /// The JBIG2Decode filter reverses the bits, so the output decodes back to exactly these bytes.
    /// </summary>
    public static byte[] Encode(byte[] packed, int width, int height)
    {
        int stride = (width + 7) / 8;
        if (width <= 0 || height <= 0 || packed.Length < (long)stride * height)
        {
            throw new ArgumentException("The image data is shorter than its size says.", nameof(packed));
        }

        var coded = CodeGenericRegion(packed, width, height, stride);

        using var output = new MemoryStream(coded.Length + 64);

        // Page information segment (7.4.8).
        var page = new byte[19];
        BinaryPrimitives.WriteInt32BigEndian(page.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(page.AsSpan(4), height);
        page[16] = 0x01; // page is eventually lossless, default pixel value 0 (white), combination operator OR
        WriteSegmentHeader(output, 0, 48, page.Length);
        output.Write(page);

        // Immediate lossless generic region segment (7.4.6), template 0 with the nominal adaptive pixels.
        var region = new byte[18 + 8];
        BinaryPrimitives.WriteInt32BigEndian(region.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(region.AsSpan(4), height);
        region[17] = 0x00; // not MMR, template 0, no typical prediction
        region[18] = 3;
        region[19] = 0xFF; // (3, -1)
        region[20] = 0xFD;
        region[21] = 0xFF; // (-3, -1)
        region[22] = 2;
        region[23] = 0xFE; // (2, -2)
        region[24] = 0xFE;
        region[25] = 0xFE; // (-2, -2)
        WriteSegmentHeader(output, 1, 39, region.Length + coded.Length);
        output.Write(region);
        output.Write(coded);
        return output.ToArray();
    }

    private static void WriteSegmentHeader(Stream output, int number, int type, int length)
    {
        Span<byte> header = stackalloc byte[11];
        BinaryPrimitives.WriteInt32BigEndian(header, number);
        header[4] = (byte)type; // one-byte page association
        header[5] = 0; // refers to no other segments
        header[6] = 1; // page 1
        BinaryPrimitives.WriteInt32BigEndian(header[7..], length);
        output.Write(header);
    }

    private static byte[] CodeGenericRegion(byte[] packed, int width, int height, int stride)
    {
        // JBIG2 calls 1 black, the PDF filter hands out 1 for white: store the inverse of the PDF samples.
        // Each row gets four zero pixels of padding on both sides so the template never reads out of range.
        const int pad = 4;
        int w = width + (2 * pad);
        var rows = new byte[3][];
        for (int i = 0; i < 3; i++)
        {
            rows[i] = new byte[w];
        }

        var coder = new MqEncoder(1 << 16);
        for (int y = 0; y < height; y++)
        {
            var above2 = rows[(y + 1) % 3];
            var above = rows[(y + 2) % 3];
            var current = rows[y % 3];
            int rowStart = y * stride;
            for (int x = 0; x < width; x++)
            {
                current[x + pad] = (byte)(((packed[rowStart + (x >> 3)] >> (7 - (x & 7))) & 1) ^ 1);
            }

            for (int x = 0; x < width; x++)
            {
                int p = x + pad;
                int context =
                    (above2[p - 2] << 15) | (above2[p - 1] << 14) | (above2[p] << 13) | (above2[p + 1] << 12) | (above2[p + 2] << 11) |
                    (above[p - 3] << 10) | (above[p - 2] << 9) | (above[p - 1] << 8) | (above[p] << 7) | (above[p + 1] << 6) |
                    (above[p + 2] << 5) | (above[p + 3] << 4) |
                    (current[p - 4] << 3) | (current[p - 3] << 2) | (current[p - 2] << 1) | current[p - 1];
                coder.Encode(context, current[p]);
            }
        }

        return coder.Finish();
    }
}

/// <summary>The MQ arithmetic encoder of ITU-T T.88 Annex E.</summary>
internal sealed class MqEncoder
{
    private static readonly (ushort Qe, byte Nmps, byte Nlps, bool Switch)[] States =
    {
        (0x5601, 1, 1, true), (0x3401, 2, 6, false), (0x1801, 3, 9, false), (0x0AC1, 4, 12, false),
        (0x0521, 5, 29, false), (0x0221, 38, 33, false), (0x5601, 7, 6, true), (0x5401, 8, 14, false),
        (0x4801, 9, 14, false), (0x3801, 10, 14, false), (0x3001, 11, 17, false), (0x2401, 12, 18, false),
        (0x1C01, 13, 20, false), (0x1601, 29, 21, false), (0x5601, 15, 14, true), (0x5401, 16, 14, false),
        (0x5101, 17, 15, false), (0x4801, 18, 16, false), (0x3801, 19, 17, false), (0x3401, 20, 18, false),
        (0x3001, 21, 19, false), (0x2801, 22, 19, false), (0x2401, 23, 20, false), (0x2201, 24, 21, false),
        (0x1C01, 25, 22, false), (0x1801, 26, 23, false), (0x1601, 27, 24, false), (0x1401, 28, 25, false),
        (0x1201, 29, 26, false), (0x1101, 30, 27, false), (0x0AC1, 31, 28, false), (0x09C1, 32, 29, false),
        (0x08A1, 33, 30, false), (0x0521, 34, 31, false), (0x0441, 35, 32, false), (0x02A1, 36, 33, false),
        (0x0221, 37, 34, false), (0x0141, 38, 35, false), (0x0111, 39, 36, false), (0x0085, 40, 37, false),
        (0x0049, 41, 38, false), (0x0025, 42, 39, false), (0x0015, 43, 40, false), (0x0009, 44, 41, false),
        (0x0005, 45, 42, false), (0x0001, 45, 43, false), (0x5601, 46, 46, false),
    };

    private readonly byte[] _index;
    private readonly byte[] _mps;
    private readonly MemoryStream _output = new();
    private uint _a = 0x8000;
    private uint _c;
    private int _ct = 12;
    private int _b = -1; // the byte being built; -1 until the first byte goes out

    public MqEncoder(int contexts)
    {
        _index = new byte[contexts];
        _mps = new byte[contexts];
    }

    public void Encode(int context, int bit)
    {
        var state = States[_index[context]];
        uint qe = state.Qe;
        _a -= qe;
        if (bit == _mps[context])
        {
            if ((_a & 0x8000) != 0)
            {
                _c += qe;
                return;
            }

            if (_a < qe)
            {
                _a = qe;
            }
            else
            {
                _c += qe;
            }

            _index[context] = state.Nmps;
        }
        else
        {
            if (_a < qe)
            {
                _c += qe;
            }
            else
            {
                _a = qe;
            }

            if (state.Switch)
            {
                _mps[context] ^= 1;
            }

            _index[context] = state.Nlps;
        }

        do
        {
            _a <<= 1;
            _c <<= 1;
            if (--_ct == 0)
            {
                ByteOut();
            }
        }
        while ((_a & 0x8000) == 0);
    }

    public byte[] Finish()
    {
        // SETBITS
        uint temp = _c + _a;
        _c |= 0xFFFF;
        if (_c >= temp)
        {
            _c -= 0x8000;
        }

        _c <<= _ct;
        ByteOut();
        _c <<= _ct;
        ByteOut();
        Emit();
        if (_b != 0xFF)
        {
            _b = 0xFF;
            Emit();
        }

        _b = 0xAC;
        Emit();
        return _output.ToArray();
    }

    private void ByteOut()
    {
        if (_b == 0xFF)
        {
            Emit();
            _b = (int)(_c >> 20);
            _c &= 0xFFFFF;
            _ct = 7;
        }
        else if (_c < 0x8000000)
        {
            Emit();
            _b = (int)(_c >> 19);
            _c &= 0x7FFFF;
            _ct = 8;
        }
        else
        {
            _b++;
            if (_b == 0xFF)
            {
                _c &= 0x7FFFFFF;
                Emit();
                _b = (int)(_c >> 20);
                _c &= 0xFFFFF;
                _ct = 7;
            }
            else
            {
                Emit();
                _b = (int)(_c >> 19);
                _c &= 0x7FFFF;
                _ct = 8;
            }
        }
    }

    private void Emit()
    {
        if (_b >= 0)
        {
            _output.WriteByte((byte)_b);
        }
    }
}
