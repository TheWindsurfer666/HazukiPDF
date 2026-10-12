namespace HazukiPdf.WinForms.Services;

/// <summary>
/// Works out where each compressed file goes. Never returns an input file's path, and gives every
/// file in a batch its own output path even when two inputs share a name.
/// </summary>
public sealed class OutputPathPlanner
{
    /// <summary>Suffix used when none is given.</summary>
    public const string DefaultSuffix = "_compressed";

    private readonly string? _outputDirectory;
    private readonly string _suffix;
    private readonly HashSet<string> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="inputs">Every file in the batch, so no output can land on top of one of them.</param>
    /// <param name="outputDirectory">Folder for the results, or null to write each next to its original.</param>
    /// <param name="suffix">Text added before the extension. May be empty when the output folder differs.</param>
    public OutputPathPlanner(IEnumerable<string> inputs, string? outputDirectory, string? suffix)
    {
        foreach (var input in inputs)
            _inputs.Add(Path.GetFullPath(input));
        _outputDirectory = string.IsNullOrWhiteSpace(outputDirectory) ? null : Path.GetFullPath(outputDirectory);
        _suffix = suffix?.Trim() ?? "";
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            if (_suffix.Contains(c))
                throw new ArgumentException($"The suffix contains a character that is not allowed in file names: '{c}'.", nameof(suffix));
        }
    }

    /// <summary>Returns the output path for <paramref name="inputPath"/> and reserves it.</summary>
    public string Plan(string inputPath)
    {
        string fullInput = Path.GetFullPath(inputPath);
        string directory = _outputDirectory ?? Path.GetDirectoryName(fullInput)!;
        string name = Path.GetFileNameWithoutExtension(fullInput);
        string extension = Path.GetExtension(fullInput);
        if (extension.Length == 0)
            extension = ".pdf";

        string baseName = name + _suffix;
        string candidate = Path.Combine(directory, baseName + extension);
        if (IsInput(candidate))
        {
            baseName = name + DefaultSuffix;
            candidate = Path.Combine(directory, baseName + extension);
        }

        for (int n = 2; IsInput(candidate) || _taken.Contains(candidate); n++)
            candidate = Path.Combine(directory, $"{baseName} ({n}){extension}");

        _taken.Add(candidate);
        return candidate;
    }

    private bool IsInput(string path) => _inputs.Contains(path);
}
