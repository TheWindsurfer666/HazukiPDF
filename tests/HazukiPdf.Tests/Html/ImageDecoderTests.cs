using HazukiPdf.Html.Images;
using HazukiPdf.Imaging;

namespace HazukiPdf.Tests.Html;

public class ImageDecoderTests
{
    [Fact]
    public void Png_Rgb_DecodesEveryFilterType()
    {
        var png = TestImages.Png(7, 10, 2, (x, y) => [(byte)(x * 30), (byte)(y * 20), (byte)(x * y)]);
        var image = PngDecoder.Decode(png)!;

        Assert.Equal(7, image.Width);
        Assert.Equal(10, image.Height);
        Assert.Equal("DeviceRGB", image.ColorSpace);
        Assert.Null(image.Alpha);
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 7; x++)
            {
                int i = (y * 7 + x) * 3;
                Assert.Equal([(byte)(x * 30), (byte)(y * 20), (byte)(x * y)], image.Data[i..(i + 3)]);
            }
        }
    }

    [Fact]
    public void Png_RgbaSplitsOutAlpha()
    {
        var png = TestImages.Png(3, 2, 6, (x, y) => [10, 20, 30, (byte)(x * 100)]);
        var image = PngDecoder.Decode(png)!;
        Assert.Equal(new byte[] { 0, 100, 200, 0, 100, 200 }, image.Alpha);
        Assert.Equal(new byte[] { 10, 20, 30 }, image.Data[..3]);
    }

    [Fact]
    public void Png_OpaqueAlphaIsDropped()
    {
        var image = PngDecoder.Decode(TestImages.Png(2, 2, 6, (_, _) => [1, 2, 3, 255]))!;
        Assert.Null(image.Alpha);
    }

    [Fact]
    public void Png_GrayAndGrayAlpha()
    {
        var gray = PngDecoder.Decode(TestImages.Png(4, 1, 0, (x, _) => [(byte)(x * 50)]))!;
        Assert.Equal("DeviceGray", gray.ColorSpace);
        Assert.Equal(new byte[] { 0, 50, 100, 150 }, gray.Data);

        var grayAlpha = PngDecoder.Decode(TestImages.Png(2, 1, 4, (x, _) => [77, (byte)(x * 255)]))!;
        Assert.Equal(new byte[] { 77, 77 }, grayAlpha.Data);
        Assert.Equal(new byte[] { 0, 255 }, grayAlpha.Alpha);
    }

    [Fact]
    public void Png_PaletteWithTransparency()
    {
        byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255];
        var png = TestImages.Png(3, 1, 3, (x, _) => [(byte)x], palette, transparency: [0, 255]);
        var image = PngDecoder.Decode(png)!;
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255 }, image.Data);
        Assert.Equal(new byte[] { 0, 255, 255 }, image.Alpha);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Png_LowBitDepthPaletteImages(int bitDepth)
    {
        int colors = 1 << bitDepth;
        var palette = Enumerable.Range(0, colors).SelectMany(i => new[] { (byte)i, (byte)(i * 2), (byte)(i * 3) }).ToArray();
        var png = TestImages.Png(9, 3, 3, (x, y) => [(byte)((x + y) % colors)], palette, bitDepth: bitDepth);
        var image = PngDecoder.Decode(png)!;
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 9; x++)
            {
                int index = (x + y) % colors;
                Assert.Equal((byte)(index * 2), image.Data[(y * 9 + x) * 3 + 1]);
            }
        }
    }

    [Fact]
    public void Png_SixteenBitKeepsHighByte()
    {
        var image = PngDecoder.Decode(TestImages.Png(2, 2, 2, (x, _) => [(byte)(x + 1), 50, 60], bitDepth: 16))!;
        Assert.Equal(new byte[] { 1, 50, 60, 2, 50, 60 }, image.Data[..6]);
    }

    [Fact]
    public void Png_RejectsGarbage()
    {
        Assert.Null(PngDecoder.Decode([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]));
        Assert.Null(ImageLoader.Decode("GIF89a"u8.ToArray()));
    }

    [Fact]
    public void Jpeg_ReadsDimensionsAndComponents()
    {
        var rgb = JpegReader.Read(JpegEncoder.Encode(TestPdf.PhotoPixels(33, 17), 33, 17, 3, 75))!;
        Assert.Equal((33, 17, "DeviceRGB"), (rgb.Width, rgb.Height, rgb.ColorSpace));
        Assert.True(rgb.IsJpeg);

        var gray = JpegReader.Read(JpegEncoder.Encode(new byte[8 * 8], 8, 8, 1, 75))!;
        Assert.Equal("DeviceGray", gray.ColorSpace);
    }

    [Fact]
    public void Jpeg_RejectsTruncatedFile()
    {
        var jpeg = JpegEncoder.Encode(TestPdf.PhotoPixels(8, 8), 8, 8, 3, 75);
        Assert.Null(JpegReader.Read(jpeg[..6]));
    }
}
