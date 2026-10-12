namespace HazukiPdf.WinForms.Services;

/// <summary>The outcome of compressing one file.</summary>
/// <param name="InputPath">The original file.</param>
/// <param name="OutputPath">Where the result was written, or null when compression failed.</param>
/// <param name="OriginalSize">Size of the original in bytes.</param>
/// <param name="CompressedSize">Size of the result in bytes (equal to the original when nothing could be saved).</param>
/// <param name="OriginalKept">True when the library could not make the file smaller and wrote the original bytes.</param>
/// <param name="Error">Why the file failed, or null on success.</param>
public sealed record FileCompressionResult(
    string InputPath,
    string? OutputPath,
    long OriginalSize,
    long CompressedSize,
    bool OriginalKept,
    string? Error)
{
    /// <summary>True when the file was compressed and written.</summary>
    public bool Succeeded => Error is null;

    /// <summary>Bytes saved, zero for failures.</summary>
    public long BytesSaved => Succeeded ? OriginalSize - CompressedSize : 0;

    /// <summary>Percentage of the original saved, from 0 to 100.</summary>
    public double PercentSaved => Succeeded ? SizeFormat.PercentSaved(OriginalSize, CompressedSize) : 0;
}

/// <summary>Totals for a finished batch.</summary>
public sealed record BatchSummary(int Files, int Failed, long OriginalSize, long CompressedSize)
{
    /// <summary>Percentage saved over the files that succeeded.</summary>
    public double PercentSaved => SizeFormat.PercentSaved(OriginalSize, CompressedSize);

    /// <summary>Totals the successful results; failed files only count towards <see cref="Failed"/>.</summary>
    public static BatchSummary From(IEnumerable<FileCompressionResult> results)
    {
        int files = 0, failed = 0;
        long original = 0, compressed = 0;
        foreach (var r in results)
        {
            files++;
            if (!r.Succeeded)
            {
                failed++;
                continue;
            }
            original += r.OriginalSize;
            compressed += r.CompressedSize;
        }
        return new BatchSummary(files, failed, original, compressed);
    }
}

/// <summary>
/// Compresses a list of PDF files on background threads. A failing file (damaged, encrypted,
/// locked) is reported in its result and does not stop the rest of the batch.
/// </summary>
public static class BatchCompressor
{
    /// <summary>Compresses each file to the path chosen by <paramref name="planner"/>.</summary>
    /// <param name="files">Files to compress.</param>
    /// <param name="options">Compression settings, shared by every file.</param>
    /// <param name="planner">Chooses each output path.</param>
    /// <param name="progress">Receives each result as soon as its file is done.</param>
    /// <param name="started">Receives each input path when work on that file begins.</param>
    /// <param name="maxParallelism">How many files to work on at once. Defaults to half the processors, at most 4.</param>
    /// <param name="cancellationToken">Stops starting new files; files already being compressed finish.</param>
    /// <returns>One result per input, in input order. Files skipped by cancellation are left out.</returns>
    public static async Task<IReadOnlyList<FileCompressionResult>> CompressAsync(
        IReadOnlyList<string> files,
        PdfCompressionOptions options,
        OutputPathPlanner planner,
        IProgress<FileCompressionResult>? progress = null,
        IProgress<string>? started = null,
        int? maxParallelism = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(planner);

        // Plan up front, in input order, so names are stable whatever order the files finish in.
        var outputs = files.Select(planner.Plan).ToArray();
        var results = new FileCompressionResult?[files.Count];
        int parallelism = maxParallelism ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, files.Count),
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                (i, _) =>
                {
                    started?.Report(files[i]);
                    var result = CompressOne(files[i], outputs[i], options);
                    results[i] = result;
                    progress?.Report(result);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Return what finished.
        }

        return results.Where(r => r is not null).Select(r => r!).ToArray();
    }

    /// <summary>Compresses one file, turning expected failures into an error result.</summary>
    public static FileCompressionResult CompressOne(string inputPath, string outputPath, PdfCompressionOptions options)
    {
        long originalSize = 0;
        try
        {
            originalSize = new FileInfo(inputPath).Length;
            var result = PdfCompressor.CompressFile(inputPath, outputPath, options);
            return new FileCompressionResult(inputPath, outputPath, result.OriginalSize, result.CompressedSize, result.OriginalKept, null);
        }
        catch (PdfEncryptedException)
        {
            return Failed("The file is encrypted.");
        }
        catch (PdfFormatException e)
        {
            return Failed("Not a valid PDF: " + e.Message);
        }
        catch (Exception e)
        {
            // One bad file must not stop a batch, whatever went wrong with it.
            return Failed(e.Message);
        }

        FileCompressionResult Failed(string error) => new(inputPath, null, originalSize, originalSize, false, error);
    }
}
