using System.Text.Json;

namespace HazukiPdf.WinForms.Services;

/// <summary>Choices the app remembers between runs, stored as JSON in the user's application data folder.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Compression level index shown in the level list (0 Lossless, 1 Balanced, 2 Maximum).</summary>
    public int CompressionLevel { get; set; } = 1;

    /// <summary>True to save compressed files in <see cref="CompressOutputFolder"/>, false to save them next to the originals.</summary>
    public bool UseCompressOutputFolder { get; set; }

    /// <summary>Folder for compressed files.</summary>
    public string? CompressOutputFolder { get; set; }

    /// <summary>Text added to compressed file names.</summary>
    public string Suffix { get; set; } = OutputPathPlanner.DefaultSuffix;

    /// <summary>Folder the HTML to PDF save dialog opens in.</summary>
    public string? HtmlOutputFolder { get; set; }

    /// <summary>%AppData%\HazukiPDF\settings.json (or the platform's equivalent).</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HazukiPDF", "settings.json");

    /// <summary>Reads settings, falling back to defaults when the file is missing or unreadable.</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged settings file just means starting from defaults.
        }
        return new AppSettings();
    }

    /// <summary>Writes settings. Returns false when the file cannot be written; settings are a convenience, not worth an error.</summary>
    public bool Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
