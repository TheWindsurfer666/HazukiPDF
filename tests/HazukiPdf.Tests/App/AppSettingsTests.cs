using HazukiPdf.WinForms.Services;

namespace HazukiPdf.Tests.App;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hazuki-settings-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RoundTripsThroughDisk()
    {
        string path = Path.Combine(_dir, "sub", "settings.json");
        var settings = new AppSettings
        {
            CompressionLevel = 2,
            UseCompressOutputFolder = true,
            CompressOutputFolder = @"C:\out",
            Suffix = "_small",
            HtmlOutputFolder = @"D:\pdfs",
        };

        Assert.True(settings.Save(path));
        var loaded = AppSettings.Load(path);

        Assert.Equal(2, loaded.CompressionLevel);
        Assert.True(loaded.UseCompressOutputFolder);
        Assert.Equal(@"C:\out", loaded.CompressOutputFolder);
        Assert.Equal("_small", loaded.Suffix);
        Assert.Equal(@"D:\pdfs", loaded.HtmlOutputFolder);
    }

    [Fact]
    public void MissingOrDamagedFileGivesDefaults()
    {
        Assert.Equal(1, AppSettings.Load(Path.Combine(_dir, "none.json")).CompressionLevel);

        string damaged = Path.Combine(_dir, "bad.json");
        File.WriteAllText(damaged, "{ not json");
        var settings = AppSettings.Load(damaged);
        Assert.Equal(OutputPathPlanner.DefaultSuffix, settings.Suffix);
        Assert.False(settings.UseCompressOutputFolder);
    }
}
