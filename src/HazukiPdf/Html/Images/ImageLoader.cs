namespace HazukiPdf.Html.Images;

/// <summary>
/// Resolves image sources to decoded images. Supports <c>data:</c> URIs, files under the
/// configured base directory, and anything a user-supplied resolver returns. Remote URLs are
/// never fetched by the converter itself.
/// </summary>
internal sealed class ImageLoader(HtmlToPdfOptions options)
{
    private readonly Dictionary<string, ImageData?> _cache = new(StringComparer.Ordinal);

    public ImageData? Load(string source)
    {
        source = source.Trim();
        if (source.Length == 0)
            return null;
        if (_cache.TryGetValue(source, out var cached))
            return cached;

        ImageData? image = null;
        try
        {
            byte[]? bytes = LoadBytes(source);
            if (bytes is not null)
                image = Decode(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            image = null;
        }
        _cache[source] = image;
        return image;
    }

    public static ImageData? Decode(byte[] bytes)
    {
        if (JpegReader.IsJpeg(bytes))
            return JpegReader.Read(bytes);
        if (PngDecoder.IsPng(bytes))
            return PngDecoder.Decode(bytes);
        return null;
    }

    private byte[]? LoadBytes(string source)
    {
        if (options.ResourceResolver?.Invoke(source) is { } resolved)
            return resolved;

        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return DecodeDataUri(source);

        if (options.BaseDirectory is null)
            return null;

        string path = source;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile)
                return null; // http, https, ftp, ...: only a resolver may fetch these
            path = uri.LocalPath;
        }
        else
        {
            int cut = path.IndexOfAny(['?', '#']);
            if (cut >= 0)
                path = path[..cut];
            path = Uri.UnescapeDataString(path);
        }

        // Only files inside the base directory may be read, so untrusted HTML cannot pull in other files.
        string baseDirectory = Path.GetFullPath(options.BaseDirectory);
        string fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
        string prefix = baseDirectory.EndsWith(Path.DirectorySeparatorChar) ? baseDirectory : baseDirectory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return null;
        return File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
    }

    private static byte[]? DecodeDataUri(string uri)
    {
        int comma = uri.IndexOf(',');
        if (comma < 0)
            return null;
        string meta = uri[5..comma];
        string payload = uri[(comma + 1)..];
        if (meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            payload = new string(payload.Where(c => !char.IsWhiteSpace(c)).ToArray());
            return Convert.FromBase64String(Uri.UnescapeDataString(payload));
        }
        return System.Text.Encoding.Latin1.GetBytes(Uri.UnescapeDataString(payload));
    }
}
