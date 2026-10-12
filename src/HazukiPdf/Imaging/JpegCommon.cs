namespace HazukiPdf.Imaging;

/// <summary>One component of a JPEG as quantized DCT coefficients.</summary>
/// <param name="Id">Component identifier from the frame header.</param>
/// <param name="H">Horizontal sampling factor.</param>
/// <param name="V">Vertical sampling factor.</param>
/// <param name="Quantization">Quantization table in natural (row-major) order.</param>
/// <param name="BlocksPerLine">Blocks per row, padded to whole MCUs.</param>
/// <param name="BlocksPerColumn">Block rows, padded to whole MCUs.</param>
/// <param name="Coefficients">64 coefficients per block in zig-zag order, blocks in row-major order.</param>
internal sealed record JpegComponent(int Id, int H, int V, int[] Quantization, int BlocksPerLine, int BlocksPerColumn, short[] Coefficients);

/// <summary>A JPEG image as quantized coefficients, which can be re-encoded without any loss.</summary>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="Components">The color components, in frame order.</param>
/// <param name="AdobeSegment">The original APP14 "Adobe" marker segment, which tells readers how to interpret colors.</param>
/// <param name="Extended">True when the source used an extended (SOF1) frame.</param>
internal sealed record JpegCoefficients(int Width, int Height, JpegComponent[] Components, byte[]? AdobeSegment, bool Extended);

/// <summary>Tables and transforms shared by the baseline JPEG encoder and decoder (ITU-T T.81).</summary>
internal static class JpegCommon
{
    /// <summary>Maps the zig-zag position of a coefficient to its natural (row-major) index.</summary>
    public static readonly int[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    };

    public static readonly byte[] LuminanceQuantization =
    {
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    };

    public static readonly byte[] ChrominanceQuantization =
    {
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    };

    public static readonly byte[] DcLuminanceBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
    public static readonly byte[] DcLuminanceValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    public static readonly byte[] DcChrominanceBits = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
    public static readonly byte[] DcChrominanceValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    public static readonly byte[] AcLuminanceBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };

    public static readonly byte[] AcLuminanceValues =
    {
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
        0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
        0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    };

    public static readonly byte[] AcChrominanceBits = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };

    public static readonly byte[] AcChrominanceValues =
    {
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
        0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
        0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
        0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    };

    // Cosine[u * 8 + x] = C(u) / 2 * cos((2x + 1) u pi / 16)
    private static readonly float[] Cosine = BuildCosine();

    private static float[] BuildCosine()
    {
        var table = new float[64];
        for (int u = 0; u < 8; u++)
        {
            double cu = u == 0 ? 1 / Math.Sqrt(2) : 1;
            for (int x = 0; x < 8; x++)
            {
                table[(u * 8) + x] = (float)(cu / 2 * Math.Cos(((2 * x) + 1) * u * Math.PI / 16));
            }
        }

        return table;
    }

    /// <summary>Forward 8x8 DCT, in place, on level-shifted samples in natural order.</summary>
    public static void ForwardDct(Span<float> block)
    {
        Span<float> temp = stackalloc float[64];

        // Rows: temp[y, u] = sum_x c[u, x] * block[y, x]
        for (int y = 0; y < 8; y++)
        {
            for (int u = 0; u < 8; u++)
            {
                float sum = 0;
                for (int x = 0; x < 8; x++)
                {
                    sum += Cosine[(u * 8) + x] * block[(y * 8) + x];
                }

                temp[(y * 8) + u] = sum;
            }
        }

        // Columns: block[v, u] = sum_y c[v, y] * temp[y, u]
        for (int u = 0; u < 8; u++)
        {
            for (int v = 0; v < 8; v++)
            {
                float sum = 0;
                for (int y = 0; y < 8; y++)
                {
                    sum += Cosine[(v * 8) + y] * temp[(y * 8) + u];
                }

                block[(v * 8) + u] = sum;
            }
        }
    }

    /// <summary>Inverse 8x8 DCT, in place, producing level-shifted samples in natural order.</summary>
    public static void InverseDct(Span<float> block)
    {
        Span<float> temp = stackalloc float[64];

        // Columns: temp[y, u] = sum_v c[v, y] * block[v, u]
        for (int u = 0; u < 8; u++)
        {
            for (int y = 0; y < 8; y++)
            {
                float sum = 0;
                for (int v = 0; v < 8; v++)
                {
                    sum += Cosine[(v * 8) + y] * block[(v * 8) + u];
                }

                temp[(y * 8) + u] = sum;
            }
        }

        // Rows: block[y, x] = sum_u c[u, x] * temp[y, u]
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                float sum = 0;
                for (int u = 0; u < 8; u++)
                {
                    sum += Cosine[(u * 8) + x] * temp[(y * 8) + u];
                }

                block[(y * 8) + x] = sum;
            }
        }
    }

    /// <summary>Scales a standard quantization table for a quality from 1 to 100, as libjpeg does.</summary>
    public static byte[] ScaleQuantization(byte[] table, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        int scale = quality < 50 ? 5000 / quality : 200 - (quality * 2);
        var result = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            result[i] = (byte)Math.Clamp(((table[i] * scale) + 50) / 100, 1, 255);
        }

        return result;
    }
}
