namespace HazukiPdf.Html.Images;

/// <summary>Reads the header of a JPEG file. The compressed data is passed through to the PDF untouched.</summary>
internal static class JpegReader
{
    public static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    public static ImageData? Read(byte[] data)
    {
        if (!IsJpeg(data))
            return null;

        bool adobe = false;
        int pos = 2;
        while (pos + 4 <= data.Length)
        {
            if (data[pos] != 0xFF)
                return null;
            byte marker = data[pos + 1];
            if (marker == 0xFF)
            {
                pos++; // fill byte
                continue;
            }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                pos += 2;
                continue;
            }
            int length = (data[pos + 2] << 8) | data[pos + 3];
            if (length < 2 || pos + 2 + length > data.Length)
                return null;

            if (marker == 0xEE && length >= 14 && data[pos + 4] == 'A' && data[pos + 5] == 'd' && data[pos + 6] == 'o' &&
                data[pos + 7] == 'b' && data[pos + 8] == 'e')
                adobe = true;

            bool isStartOfFrame = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isStartOfFrame)
            {
                if (length < 8)
                    return null;
                int precision = data[pos + 4];
                int height = (data[pos + 5] << 8) | data[pos + 6];
                int width = (data[pos + 7] << 8) | data[pos + 8];
                int components = data[pos + 9];
                if (precision != 8 || width == 0 || height == 0)
                    return null;
                string colorSpace = components switch
                {
                    1 => "DeviceGray",
                    3 => "DeviceRGB",
                    4 => "DeviceCMYK",
                    _ => "",
                };
                if (colorSpace.Length == 0)
                    return null;
                return new ImageData
                {
                    Width = width,
                    Height = height,
                    ColorSpace = colorSpace,
                    Data = data,
                    IsJpeg = true,
                    InvertedCmyk = components == 4 && adobe,
                };
            }
            pos += 2 + length;
        }
        return null;
    }
}
