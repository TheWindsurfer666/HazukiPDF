using HazukiPdf.WinForms.Services;

namespace HazukiPdf.Tests.App;

public sealed class BatchCompressorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hazuki-batch-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [Fact]
    public async Task CompressesEveryFileAndKeepsGoingPastFailures()
    {
        string first = WriteFile("one.pdf", TestPdf.WithPages(TestPdf.TextPage(), TestPdf.TextPage()));
        string broken = WriteFile("broken.pdf", "this is not a pdf"u8.ToArray());
        string second = WriteFile("two.pdf", TestPdf.WithPages(TestPdf.TextPage(80)));
        string[] files = [first, broken, second];
        var reported = new List<FileCompressionResult>();
        var started = new List<string>();

        var results = await BatchCompressor.CompressAsync(
            files,
            PdfCompressionOptions.FromLevel(PdfCompressionLevel.Lossless),
            new OutputPathPlanner(files, null, "_compressed"),
            new SyncProgress<FileCompressionResult>(r => { lock (reported) reported.Add(r); }),
            new SyncProgress<string>(f => { lock (started) started.Add(f); }),
            maxParallelism: 2);

        Assert.Equal(files, results.Select(r => r.InputPath));
        Assert.Equal(3, reported.Count);
        Assert.Equal(files.Order(), started.Order());

        var ok = results[0];
        Assert.True(ok.Succeeded);
        Assert.Equal(Path.Combine(_dir, "one_compressed.pdf"), ok.OutputPath);
        Assert.Equal(new FileInfo(first).Length, ok.OriginalSize);
        Assert.Equal(new FileInfo(ok.OutputPath!).Length, ok.CompressedSize);
        Assert.True(ok.PercentSaved > 30, $"saved {ok.PercentSaved}%");

        var bad = results[1];
        Assert.False(bad.Succeeded);
        Assert.Null(bad.OutputPath);
        Assert.NotNull(bad.Error);
        Assert.False(File.Exists(Path.Combine(_dir, "broken_compressed.pdf")));
        Assert.Equal(0, bad.BytesSaved);

        Assert.True(results[2].Succeeded);

        var summary = BatchSummary.From(results);
        Assert.Equal(3, summary.Files);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(results[0].OriginalSize + results[2].OriginalSize, summary.OriginalSize);
        Assert.Equal(results[0].CompressedSize + results[2].CompressedSize, summary.CompressedSize);
        Assert.True(summary.PercentSaved > 0);
    }

    [Fact]
    public async Task OriginalsAreNotModified()
    {
        var bytes = TestPdf.WithPages(TestPdf.TextPage());
        string file = WriteFile("keep.pdf", bytes);

        await BatchCompressor.CompressAsync([file], new PdfCompressionOptions(), new OutputPathPlanner([file], null, ""));

        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.True(File.Exists(Path.Combine(_dir, "keep_compressed.pdf")));
    }

    [Fact]
    public async Task MissingFileIsReportedAsFailure()
    {
        string missing = Path.Combine(_dir, "gone.pdf");
        var results = await BatchCompressor.CompressAsync([missing], new PdfCompressionOptions(), new OutputPathPlanner([missing], null, null));

        Assert.False(Assert.Single(results).Succeeded);
    }

    [Fact]
    public async Task CancelledBatchReturnsOnlyFinishedFiles()
    {
        string file = WriteFile("a.pdf", TestPdf.WithPages(TestPdf.TextPage()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var results = await BatchCompressor.CompressAsync([file, file], new PdfCompressionOptions(),
            new OutputPathPlanner([file], null, null), cancellationToken: cts.Token);

        Assert.Empty(results);
    }
}
