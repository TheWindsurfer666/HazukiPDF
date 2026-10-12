using System.Buffers.Binary;
using System.IO.Compression;

namespace HazukiPdf.Html.Images;

/// <summary>
/// Decodes PNG images (all color types and bit depths, interlaced or not) to 8-bit samples
/// plus an optional alpha channel.
/// </summary>
internal static class PngDecoder
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(Signature);

    public static ImageData? Decode(byte[] data)
    {
        if (!IsPng(data))
            return null;

        int width = 0, height = 0, bitDepth = 0, colorType = -1, interlace = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var idat = new MemoryStream();

        int pos = 8;
        while (pos + 8 <= data.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            if (length < 0 || pos + 12 + (long)length > data.Length)
                return null;
            string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            var chunk = data.AsSpan(pos + 8, length);
            switch (type)
            {
                case "IHDR":
                    if (length < 13)
                        return null;
                    width = BinaryPrimitives.ReadInt32BigEndian(chunk);
                    height = BinaryPrimitives.ReadInt32BigEndian(chunk[4..]);
                    bitDepth = chunk[8];
                    colorType = chunk[9];
                    interlace = chunk[12];
                    break;
                case "PLTE":
                    palette = chunk.ToArray();
                    break;
                case "tRNS":
                    transparency = chunk.ToArray();
                    break;
                case "IDAT":
                    idat.Write(chunk);
                    break;
            }
            pos += 12 + length;
            if (type == "IEND")
                break;
        }

        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (width <= 0 || height <= 0 || channels == 0 || bitDepth is not (1 or 2 or 4 or 8 or 16) ||
            (long)width * height > 100_000_000 || (colorType == 3 && palette is null))
            return null;

        byte[] raw;
        try
        {
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            raw = output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }

        // Samples per pixel, each scaled to 8 bits (palette indices are kept as indices).
        var samples = new byte[(long)width * height * channels];
        if (interlace == 0)
        {
            if (!DecodePass(raw, 0, width, height, bitDepth, channels, colorType, samples, width, 0, 0, 1, 1, out _))
                return null;
        }
        else
        {
            int[] startX = [0, 4, 0, 2, 0, 1, 0], startY = [0, 0, 4, 0, 2, 0, 1];
            int[] stepX = [8, 8, 4, 4, 2, 2, 1], stepY = [8, 8, 8, 4, 4, 2, 2];
            int offset = 0;
            for (int pass = 0; pass < 7; pass++)
            {
                int pw = (width - startX[pass] + stepX[pass] - 1) / stepX[pass];
                int ph = (height - startY[pass] + stepY[pass] - 1) / stepY[pass];
                if (pw <= 0 || ph <= 0)
                    continue;
                if (!DecodePass(raw, offset, pw, ph, bitDepth, channels, colorType, samples, width,
                        startX[pass], startY[pass], stepX[pass], stepY[pass], out int consumed))
                    return null;
                offset += consumed;
            }
        }

        return BuildImage(width, height, colorType, bitDepth, samples, palette, transparency);
    }

    private static bool DecodePass(byte[] raw, int offset, int width, int height, int bitDepth, int channels, int colorType,
        byte[] samples, int fullWidth, int x0, int y0, int dx, int dy, out int consumed)
    {
        int bitsPerPixel = channels * bitDepth;
        int stride = (width * bitsPerPixel + 7) / 8;
        int bpp = Math.Max(1, bitsPerPixel / 8);
        consumed = (stride + 1) * height;
        if (offset + (long)consumed > raw.Length)
            return false;

        var previous = new byte[stride];
        var current = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int rowStart = offset + y * (stride + 1);
            byte filter = raw[rowStart];
            Array.Copy(raw, rowStart + 1, current, 0, stride);
            Unfilter(filter, current, previous, bpp);

            int outY = y0 + y * dy;
            for (int x = 0; x < width; x++)
            {
                int outIndex = ((outY * fullWidth) + x0 + x * dx) * channels;
                for (int c = 0; c < channels; c++)
                    samples[outIndex + c] = ReadSample(current, x * channels + c, bitDepth, colorType == 3);
            }
            (previous, current) = (current, previous);
        }
        return true;
    }

    private static byte ReadSample(byte[] row, int sampleIndex, int bitDepth, bool isIndex)
    {
        switch (bitDepth)
        {
            case 8:
                return row[sampleIndex];
            case 16:
                return row[sampleIndex * 2]; // keep the high byte
            default:
                int bitOffset = sampleIndex * bitDepth;
                int value = (row[bitOffset >> 3] >> (8 - bitDepth - (bitOffset & 7))) & ((1 << bitDepth) - 1);
                return isIndex ? (byte)value : (byte)(value * 255 / ((1 << bitDepth) - 1));
        }
    }

    private static void Unfilter(byte filter, byte[] line, byte[] prior, int bpp)
    {
        switch (filter)
        {
            case 1:
                for (int i = bpp; i < line.Length; i++)
                    line[i] += line[i - bpp];
                break;
            case 2:
                for (int i = 0; i < line.Length; i++)
                    line[i] += prior[i];
                break;
            case 3:
                for (int i = 0; i < line.Length; i++)
                    line[i] += (byte)(((i >= bpp ? line[i - bpp] : 0) + prior[i]) >> 1);
                break;
            case 4:
                for (int i = 0; i < line.Length; i++)
                {
                    int a = i >= bpp ? line[i - bpp] : 0, b = prior[i], c = i >= bpp ? prior[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    line[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }
                break;
        }
    }

    private static ImageData BuildImage(int width, int height, int colorType, int bitDepth, byte[] samples,
        byte[]? palette, byte[]? transparency)
    {
        int pixels = width * height;
        byte[]? alpha = null;
        byte[] color;
        bool gray;

        switch (colorType)
        {
            case 0: // gray
                gray = true;
                color = samples;
                if (transparency is { Length: >= 2 })
                {
                    int key = (transparency[0] << 8) | transparency[1];
                    byte key8 = bitDepth == 16 ? (byte)(key >> 8) : bitDepth == 8 ? (byte)key : (byte)(key * 255 / ((1 << bitDepth) - 1));
                    alpha = new byte[pixels];
                    for (int i = 0; i < pixels; i++)
                        alpha[i] = samples[i] == key8 ? (byte)0 : (byte)255;
                }
                break;
            case 4: // gray + alpha
                gray = true;
                color = new byte[pixels];
                alpha = new byte[pixels];
                for (int i = 0; i < pixels; i++)
                {
                    color[i] = samples[i * 2];
                    alpha[i] = samples[i * 2 + 1];
                }
                break;
            case 2: // RGB
                gray = false;
                color = samples;
                if (transparency is { Length: >= 6 })
                {
                    byte r = transparency[bitDepth == 16 ? 0 : 1], g = transparency[bitDepth == 16 ? 2 : 3], b = transparency[bitDepth == 16 ? 4 : 5];
                    alpha = new byte[pixels];
                    for (int i = 0; i < pixels; i++)
                        alpha[i] = samples[i * 3] == r && samples[i * 3 + 1] == g && samples[i * 3 + 2] == b ? (byte)0 : (byte)255;
                }
                break;
            case 6: // RGBA
                gray = false;
                color = new byte[pixels * 3];
                alpha = new byte[pixels];
                for (int i = 0; i < pixels; i++)
                {
                    color[i * 3] = samples[i * 4];
                    color[i * 3 + 1] = samples[i * 4 + 1];
                    color[i * 3 + 2] = samples[i * 4 + 2];
                    alpha[i] = samples[i * 4 + 3];
                }
                break;
            default: // palette
                gray = false;
                color = new byte[pixels * 3];
                int entries = palette!.Length / 3;
                if (transparency is { Length: > 0 })
                    alpha = new byte[pixels];
                for (int i = 0; i < pixels; i++)
                {
                    int index = samples[i];
                    if (index < entries)
                    {
                        color[i * 3] = palette[index * 3];
                        color[i * 3 + 1] = palette[index * 3 + 1];
                        color[i * 3 + 2] = palette[index * 3 + 2];
                    }
                    if (alpha is not null)
                        alpha[i] = index < transparency!.Length ? transparency[index] : (byte)255;
                }
                break;
        }

        if (alpha is not null && alpha.All(a => a == 255))
            alpha = null;

        return new ImageData
        {
            Width = width,
            Height = height,
            ColorSpace = gray ? "DeviceGray" : "DeviceRGB",
            Data = color,
            Alpha = alpha,
        };
    }
}
