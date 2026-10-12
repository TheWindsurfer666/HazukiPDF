using HazukiPdf.Imaging;

namespace HazukiPdf.Tests;

public class JpegTests
{
    // 48x40 RGB, 4:2:2 subsampling, restart interval of 2 MCUs; made with libjpeg from the formula in Expected().
    private const string RestartJpeg =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAIBAQEBAQIBAQECAgICAgQDAgICAgUEBAMEBgUGBgYFBgYGBwkIBgcJBwYGCAsICQoKCgoKBggLDAsKDAkKCgr/2wBDAQICAgICAgUDAwUKBwYHCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgr/wAARCAAoADADASEAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/90ABAAC/9oADAMBAAIRAxEAPwD8ivC3w/4X9x+ld/oPw/4X9x+lf6N5TmX+xrXocHC+b+9HU6fT/h/wP3H6VsWXw+6fuP0r8N8S8y9yWp/VXA+b/Dqf/9D4m0f4f/MP3H6V23hr4f8AKjyf0qXmX+379T/U7hTN/cjqePeFvh/wv7j9K7/Qvh9wv7n9K/t7Kcy/2Lfof4pcL5v70dT/0fmHT/h/wP3H6Vr2Xw/5H7j9K/avEzMvclqfpfBGb6x1N/Sfh/8AMP3H6V2vhr4f/Mo8j9K/lN5l/t+/U/rPhTN/cjqf/9L5s8LfD/7v7j9K7/Qvh/8AKP3H6V/TmU5l/sW/Q/lDhfN/ejqdNYfD/gfuP0rYsvh/0/cfpX4b4mZl7ktT+qeB83+DU//Ts6P8P/mH7j9K7Xw18PuVPkfpX8+vMv8Ab9+p/VvCeb+5HU8f8LfD/hf3H6V32g/D/hf3H6V/b2U5l/sW/Q/xS4Xzf3o6n//U9D0/4f8AA/cfpWxZfD7oPI/SvJ8TMy9yWp+UcD5v8OpvaR8P/mH7j9K7bw18P+V/cfpX8pvMv9v36n9acJ5v7kdT/9X07wt8P+F/cfpXf6F8PuB+4/SvcynMv9iWp/lfwvm/vR1On0/4fcD9x+la9l8P+R+4/Svw3xMzL3Zan9U8D5v8Op//1vurSfh/8wxB+ldr4Z+H/wAy/uP0r/P55l/t+/U7OE8392Op/9k=";

    private const string ProgressiveJpeg =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAIBAQEBAQIBAQECAgICAgQDAgICAgUEBAMEBgUGBgYFBgYGBwkIBgcJBwYGCAsICQoKCgoKBggLDAsKDAkKCgr/2wBDAQICAgICAgUDAwUKBwYHCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgr/wgARCAAoADADASIAAhEBAxEB/8QAGAABAQEBAQAAAAAAAAAAAAAABQYABAj/xAAXAQEBAQEAAAAAAAAAAAAAAAAHBggF/9oADAMBAAIQAxAAAAHyJQMvvvDmuyt64ZVlm3mipaj6Bl9PwBNdlZ2QyrLNutlKzHv7LODVOvaGVUGtila//8QAGBAAAwEBAAAAAAAAAAAAAAAAAAMFBAL/2gAIAQEAAQUCyzxE8XPOJ4meZp5lniJ4uecTxU8zTzLPETziecTxM8zTzLPETxc84nip5mnmWeIni55xPFTzNPP/xAAbEQACAgMBAAAAAAAAAAAAAAAFIQABAwQxEf/aAAgBAwEBPwEWXdOAy/HBJdU4LLunAhfjgouqcF7OX3sCbOVOCtnL5Tn/xAAfEQABAgcBAQAAAAAAAAAAAAAAAwUBBAYTISMxFRH/2gAIAQIBAT8BlHLT0qZyxHJ6W/pKOWnpUzliOT0t/SUXUslTLqfIl9S+f//EABUQAQEAAAAAAAAAAAAAAAAAAAAx/9oACAEBAAY/AoiIiIiIiIiIiIiIiIiIiP/EABkQAAMBAQEAAAAAAAAAAAAAAAAhMRABIP/aAAgBAQABPyGBlslhgYGGyXFnDAo3SwwMDLZLjAw8SS4u4f/aAAwDAQACAAMAAAAQn30hMtdce//EABcRAAMBAAAAAAAAAAAAAAAAAAAQIRH/2gAIAQMBAT8QAUYAFkQwmpR//8QAGxEAAgIDAQAAAAAAAAAAAAAAAJEBEBEhQTH/2gAIAQIBAT8QQUSWhFRJaMPs9IT19P/EAB0QAAMBAAIDAQAAAAAAAAAAAAAhwQExsRGh4fH/2gAIAQEAAT8QVERERknHIhI7HQVEXARkjskQkUdT8kUkVknFIhI/XUVERERknHnUUkdEVEXkickdkiHj1PgT/9k=";

    [Theory]
    [InlineData(1, 64, 48, 90)]
    [InlineData(3, 64, 48, 75)]
    [InlineData(3, 64, 48, 95)]
    [InlineData(3, 37, 23, 80)]
    [InlineData(1, 1, 1, 50)]
    public void EncodeThenDecode_ReproducesTheImage(int components, int width, int height, int quality)
    {
        var pixels = TestPdf.PhotoPixels(width, height, components);
        var jpeg = JpegEncoder.Encode(pixels, width, height, components, quality);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg[..2]);
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, jpeg[^2..]);

        var decoded = JpegDecoder.Decode(jpeg);
        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        Assert.Equal(components, decoded.Components);
        Assert.True(Psnr(pixels, decoded.Pixels) > 28, $"PSNR too low: {Psnr(pixels, decoded.Pixels):F1}");
    }

    [Fact]
    public void LowerQuality_GivesSmallerFiles()
    {
        var pixels = TestPdf.PhotoPixels(128, 128);
        int high = JpegEncoder.Encode(pixels, 128, 128, 3, 95).Length;
        int low = JpegEncoder.Encode(pixels, 128, 128, 3, 40).Length;
        Assert.True(low < high * 0.6, $"quality 40 gave {low} bytes, quality 95 gave {high}");
    }

    [Fact]
    public void Decoder_HandlesSubsamplingAndRestartMarkers()
    {
        var decoded = JpegDecoder.Decode(Convert.FromBase64String(RestartJpeg));
        Assert.Equal(48, decoded.Width);
        Assert.Equal(40, decoded.Height);
        Assert.Equal(3, decoded.Components);
        double error = 0;
        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 48; x++)
            {
                var expected = Expected(x, y);
                int o = ((y * 48) + x) * 3;
                for (int c = 0; c < 3; c++)
                {
                    error += Math.Abs(decoded.Pixels[o + c] - expected[c]);
                }
            }
        }

        Assert.True(error / (48 * 40 * 3) < 4, $"Mean error {error / (48 * 40 * 3):F2}");
    }

    [Fact]
    public void Decoder_RejectsProgressiveJpegs()
    {
        Assert.Throws<NotSupportedException>(() => JpegDecoder.Decode(Convert.FromBase64String(ProgressiveJpeg)));
    }

    [Fact]
    public void LosslessRewrite_KeepsPixelsAndShrinksLibjpegOutput()
    {
        var original = Convert.FromBase64String(RestartJpeg);
        var rewritten = JpegEncoder.Write(JpegDecoder.DecodeCoefficients(original));

        Assert.True(rewritten.Length < original.Length, $"{rewritten.Length} bytes is not smaller than {original.Length}");
        Assert.Equal(JpegDecoder.Decode(original).Pixels, JpegDecoder.Decode(rewritten).Pixels);
    }

    [Theory]
    [InlineData(1, 75)]
    [InlineData(3, 60)]
    [InlineData(3, 95)]
    public void LosslessRewrite_RoundTripsOwnOutput(int components, int quality)
    {
        var pixels = TestPdf.PhotoPixels(53, 41, components);
        var jpeg = JpegEncoder.Encode(pixels, 53, 41, components, quality);
        var rewritten = JpegEncoder.Write(JpegDecoder.DecodeCoefficients(jpeg));

        Assert.Equal(JpegDecoder.Decode(jpeg).Pixels, JpegDecoder.Decode(rewritten).Pixels);
        Assert.True(rewritten.Length <= jpeg.Length);
    }

    [Fact]
    public void LosslessRewrite_RejectsProgressiveJpegs()
    {
        Assert.ThrowsAny<Exception>(() => JpegDecoder.DecodeCoefficients(Convert.FromBase64String(ProgressiveJpeg)));
    }

    [Fact]
    public void Resampler_AveragesAreas()
    {
        byte[] pixels = { 0, 100, 200, 100, 0, 100, 200, 100 }; // 4x2 gray
        var result = ImageResampler.Downsample(pixels, 4, 2, 1, 2, 1);
        Assert.Equal(new byte[] { 50, 150 }, result);
    }

    private static int[] Expected(int x, int y) => new[] { x * 5, y * 6, 128 + (int)(60 * Math.Sin(x / 8.0)) };

    private static double Psnr(byte[] a, byte[] b)
    {
        double mse = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = a[i] - b[i];
            mse += d * d;
        }

        mse /= a.Length;
        return mse == 0 ? 100 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}
