using HazukiPdf.Objects;

namespace HazukiPdf.Filters;

/// <summary>TIFF and PNG predictor functions used with FlateDecode and LZWDecode (ISO 32000-1, section 7.4.4.4).</summary>
internal static class Predictors
{
    public static byte[] Decode(byte[] data, PdfDictionary parms)
    {
        int predictor = StreamFilters.GetInt(parms, "Predictor", 1);
        if (predictor <= 1)
        {
            return data;
        }

        int colors = Math.Clamp(StreamFilters.GetInt(parms, "Colors", 1), 1, 64);
        int bpc = Math.Clamp(StreamFilters.GetInt(parms, "BitsPerComponent", 8), 1, 16);
        int columns = Math.Max(1, StreamFilters.GetInt(parms, "Columns", 1));
        int bytesPerPixel = Math.Max(1, (colors * bpc + 7) / 8);
        int rowLength = (int)Math.Min(int.MaxValue / 2, ((long)colors * bpc * columns + 7) / 8);

        if (predictor == 2)
        {
            return DecodeTiff(data, colors, bpc, rowLength);
        }

        return DecodePng(data, bytesPerPixel, rowLength);
    }

    private static byte[] DecodePng(byte[] data, int bpp, int rowLength)
    {
        int rows = (data.Length + rowLength) / (rowLength + 1);
        var output = new byte[rows * rowLength];
        var prior = new byte[rowLength];
        int outPos = 0;
        int inPos = 0;
        for (int r = 0; r < rows && inPos < data.Length; r++)
        {
            int type = data[inPos++];
            int n = Math.Min(rowLength, data.Length - inPos);
            var row = output.AsSpan(outPos, rowLength);
            data.AsSpan(inPos, n).CopyTo(row);
            inPos += n;
            switch (type)
            {
                case 1: // Sub
                    for (int i = bpp; i < rowLength; i++)
                    {
                        row[i] = (byte)(row[i] + row[i - bpp]);
                    }

                    break;
                case 2: // Up
                    for (int i = 0; i < rowLength; i++)
                    {
                        row[i] = (byte)(row[i] + prior[i]);
                    }

                    break;
                case 3: // Average
                    for (int i = 0; i < rowLength; i++)
                    {
                        int left = i >= bpp ? row[i - bpp] : 0;
                        row[i] = (byte)(row[i] + ((left + prior[i]) >> 1));
                    }

                    break;
                case 4: // Paeth
                    for (int i = 0; i < rowLength; i++)
                    {
                        int a = i >= bpp ? row[i - bpp] : 0;
                        int b = prior[i];
                        int c = i >= bpp ? prior[i - bpp] : 0;
                        row[i] = (byte)(row[i] + Paeth(a, b, c));
                    }

                    break;
            }

            row.CopyTo(prior);
            outPos += rowLength;
        }

        return outPos == output.Length ? output : output.AsSpan(0, outPos).ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }

    private static byte[] DecodeTiff(byte[] data, int colors, int bpc, int rowLength)
    {
        var output = (byte[])data.Clone();
        if (bpc == 8)
        {
            for (int start = 0; start + rowLength <= output.Length; start += rowLength)
            {
                for (int i = colors; i < rowLength; i++)
                {
                    output[start + i] = (byte)(output[start + i] + output[start + i - colors]);
                }
            }
        }
        else if (bpc == 16)
        {
            for (int start = 0; start + rowLength <= output.Length; start += rowLength)
            {
                for (int i = colors * 2; i + 1 < rowLength; i += 2)
                {
                    int prev = (output[start + i - (colors * 2)] << 8) | output[start + i - (colors * 2) + 1];
                    int cur = ((output[start + i] << 8) | output[start + i + 1]) + prev;
                    output[start + i] = (byte)(cur >> 8);
                    output[start + i + 1] = (byte)cur;
                }
            }
        }
        else
        {
            // Sub-byte samples: process bit by bit.
            int mask = (1 << bpc) - 1;
            int samplesPerRow = rowLength * 8 / bpc;
            for (int start = 0; start + rowLength <= output.Length; start += rowLength)
            {
                for (int s = colors; s < samplesPerRow; s++)
                {
                    int cur = ReadBits(output, start, s, bpc);
                    int left = ReadBits(output, start, s - colors, bpc);
                    WriteBits(output, start, s, bpc, (cur + left) & mask);
                }
            }
        }

        return output;
    }

    private static int ReadBits(byte[] data, int rowStart, int sample, int bpc)
    {
        int bit = sample * bpc;
        int value = 0;
        for (int i = 0; i < bpc; i++, bit++)
        {
            value = (value << 1) | ((data[rowStart + (bit >> 3)] >> (7 - (bit & 7))) & 1);
        }

        return value;
    }

    private static void WriteBits(byte[] data, int rowStart, int sample, int bpc, int value)
    {
        int bit = sample * bpc;
        for (int i = bpc - 1; i >= 0; i--, bit++)
        {
            int index = rowStart + (bit >> 3);
            int shift = 7 - (bit & 7);
            data[index] = (byte)((data[index] & ~(1 << shift)) | (((value >> i) & 1) << shift));
        }
    }

    /// <summary>Applies the PNG "Up" predictor to every row, as used for cross-reference streams.</summary>
    public static byte[] EncodePngUp(byte[] data, int rowLength)
    {
        int rows = data.Length / rowLength;
        var output = new byte[rows * (rowLength + 1)];
        for (int r = 0; r < rows; r++)
        {
            int o = r * (rowLength + 1);
            output[o] = 2;
            for (int i = 0; i < rowLength; i++)
            {
                int cur = data[(r * rowLength) + i];
                int up = r > 0 ? data[((r - 1) * rowLength) + i] : 0;
                output[o + 1 + i] = (byte)(cur - up);
            }
        }

        return output;
    }
}
