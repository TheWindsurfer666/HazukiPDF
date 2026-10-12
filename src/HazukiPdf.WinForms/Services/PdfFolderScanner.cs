namespace HazukiPdf.WinForms.Services;

/// <summary>
/// Lists the PDFs in a folder for a batch, leaving out files this app wrote itself: anything inside the
/// output folder, and files whose name ends with the output suffix. Without this, running a batch twice on
/// the same folder compresses the previous run's results again.
/// </summary>
public static class PdfFolderScanner
{
    /// <param name="folder">Folder to search, including subfolders.</param>
    /// <param name="outputFolder">The batch output folder, if one is set.</param>
    /// <param name="suffix">The output file suffix, such as "_compressed".</param>
    /// <param name="skipped">How many PDFs were left out as earlier output.</param>
    public static List<string> Find(string folder, string? outputFolder, string? suffix, out int skipped)
    {
        var files = new List<string>();
        skipped = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*.pdf", SearchOption.AllDirectories))
        {
            if (IsEarlierOutput(file, outputFolder, suffix))
            {
                skipped++;
            }
            else
            {
                files.Add(file);
            }
        }

        return files;
    }

    /// <summary>True when <paramref name="path"/> lies inside the output folder or carries the output suffix.</summary>
    public static bool IsEarlierOutput(string path, string? outputFolder, string? suffix)
    {
        string full = Path.GetFullPath(path);
        if (!string.IsNullOrWhiteSpace(outputFolder))
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputFolder)) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        string name = Path.GetFileNameWithoutExtension(full);
        string effectiveSuffix = string.IsNullOrWhiteSpace(suffix) ? OutputPathPlanner.DefaultSuffix : suffix.Trim();
        return name.EndsWith(effectiveSuffix, StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(OutputPathPlanner.DefaultSuffix, StringComparison.OrdinalIgnoreCase);
    }
}
