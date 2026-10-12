using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace HazukiPdf.Tests.Html;

/// <summary>Builds small PNG files for tests.</summary>
internal static class TestImages
{
    /// <summary>
    /// Creates an 8-bit PNG. <paramref name="pixel"/> returns the samples of one pixel for the color type
    /// (0 gray, 2 RGB, 3 palette index, 4 gray+alpha, 6 RGBA). Rows use a mix of filters.
    /// </summary>
    public static byte[] Png(int width, int height, int colorType, Func<int, int, byte[]> pixel,
        byte[]? palette = null, byte[]? transparency = null, int bitDepth = 8)
    {
        int channels = colorType switch { 0 or 3 => 1, 2 => 3, 4 => 2, _ => 4 };
        int stride = (width * channels * bitDepth + 7) / 8;
        int bpp = Math.Max(1, channels * bitDepth / 8);
        var raw = new MemoryStream();
        var previous = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            var row = new byte[stride];
            for (int x = 0; x < width; x++)
            {
                var samples = pixel(x, y);
                for (int c = 0; c < channels; c++)
                {
                    int sampleIndex = x * channels + c;
                    if (bitDepth == 8)
                    {
                        row[sampleIndex] = samples[c];
                    }
                    else if (bitDepth == 16)
                    {
                        row[sampleIndex * 2] = samples[c];
                        row[sampleIndex * 2 + 1] = samples[c];
                    }
                    else
                    {
                        int bit = sampleIndex * bitDepth;
                        row[bit >> 3] |= (byte)(samples[c] << (8 - bitDepth - (bit & 7)));
                    }
                }
            }

            int filter = y % 5;
            raw.WriteByte((byte)filter);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
                int predicted = filter switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => 0,
                };
                raw.WriteByte((byte)(row[i] - predicted));
            }
            previous = row;
        }

        var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = (byte)bitDepth;
        header[9] = (byte)colorType;
        Chunk(output, "IHDR", header);
        if (palette is not null)
            Chunk(output, "PLTE", palette);
        if (transparency is not null)
            Chunk(output, "tRNS", transparency);

        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw.ToArray());
        // Split the image data over two chunks, which decoders must join.
        var data = compressed.ToArray();
        Chunk(output, "IDAT", data[..(data.Length / 2)]);
        Chunk(output, "IDAT", data[(data.Length / 2)..]);
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(Encoding.ASCII.GetBytes(type));
        output.Write(data);
        output.Write(new byte[4]); // the decoder does not verify CRCs
    }
}
