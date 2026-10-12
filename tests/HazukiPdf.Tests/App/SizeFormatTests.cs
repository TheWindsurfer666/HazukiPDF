using System.Globalization;
using HazukiPdf.WinForms.Services;

namespace HazukiPdf.Tests.App;

public class SizeFormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5 * 1024 * 1024, "5.0 MB")]
    [InlineData(-2048, "-2.0 KB")]
    public void Bytes_PicksAUnit(long bytes, string expected)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, SizeFormat.Bytes(bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Theory]
    [InlineData(1000, 250, 75)]
    [InlineData(1000, 1000, 0)]
    [InlineData(0, 0, 0)]
    public void PercentSaved_IsShareOfOriginal(long original, long compressed, double expected) =>
        Assert.Equal(expected, SizeFormat.PercentSaved(original, compressed), 6);
}
