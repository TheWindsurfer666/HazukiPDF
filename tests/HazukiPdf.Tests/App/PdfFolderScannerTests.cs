using HazukiPdf.WinForms.Services;

namespace HazukiPdf.Tests.App;

public sealed class PdfFolderScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hazuki-scan-{Guid.NewGuid():N}");

    public PdfFolderScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Touch(params string[] parts)
    {
        string path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "%PDF-1.4");
        return path;
    }

    [Fact]
    public void SkipsTheOutputFolderAndSuffixedFiles()
    {
        var original = Touch("6BM8.pdf");
        var nested = Touch("sub", "7199.pdf");
        Touch("6BM8_compressed.pdf");
        Touch("Krympta", "6BM8.pdf");
        Touch("Krympta", "6BM8_compressed.pdf");

        var found = PdfFolderScanner.Find(_root, Path.Combine(_root, "Krympta"), "_compressed", out int skipped);

        Assert.Equal(new[] { original, nested }.OrderBy(p => p), found.OrderBy(p => p));
        Assert.Equal(3, skipped);
    }

    [Fact]
    public void UsesACustomSuffixAndStillSkipsTheDefaultOne()
    {
        var original = Touch("a.pdf");
        Touch("a_small.pdf");
        Touch("b_compressed.pdf");

        var found = PdfFolderScanner.Find(_root, null, "_small", out int skipped);

        Assert.Equal(new[] { original }, found);
        Assert.Equal(2, skipped);
    }

    [Fact]
    public void AFolderWhoseNameStartsLikeTheOutputFolderIsNotSkipped()
    {
        var file = Touch("Krympta2", "x.pdf");
        Assert.False(PdfFolderScanner.IsEarlierOutput(file, Path.Combine(_root, "Krympta"), "_compressed"));
    }
}
