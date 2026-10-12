using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace HazukiPdf.WinForms.Services;

/// <summary>A downloaded web page, ready to hand to the HTML converter.</summary>
/// <param name="Html">The page's HTML.</param>
/// <param name="BaseUri">The address relative links resolve against (after redirects and any <c>&lt;base href&gt;</c>).</param>
/// <param name="StyleSheets">The CSS of the page's linked style sheets, in document order.</param>
public sealed record WebPage(string Html, Uri BaseUri, string StyleSheets);

/// <summary>
/// Downloads web pages and their resources with <see cref="HttpClient"/>. The converter itself
/// never touches the network, so this class fetches the HTML, inlines linked style sheets, and
/// supplies a resolver that downloads images on demand.
/// </summary>
public sealed partial class WebPageLoader(HttpClient http)
{
    static WebPageLoader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Largest HTML page or style sheet that will be downloaded.</summary>
    public const long MaxDocumentBytes = 10 * 1024 * 1024;

    /// <summary>Largest image that will be downloaded.</summary>
    public const long MaxImageBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Turns user input such as <c>example.com/page</c> into an absolute http or https address.
    /// Returns false for anything else (file paths, ftp, javascript: and so on).
    /// </summary>
    public static bool TryNormalizeUrl(string? text, out Uri uri)
    {
        uri = null!;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || !IsHttp(parsed) || string.IsNullOrEmpty(parsed.Host))
            return false;
        uri = parsed;
        return true;
    }

    /// <summary>Downloads a page and the style sheets it links to.</summary>
    public async Task<WebPage> LoadAsync(Uri url, CancellationToken cancellationToken = default)
    {
        if (!IsHttp(url))
            throw new ArgumentException("Only http and https addresses can be loaded.", nameof(url));

        var (bytes, finalUri, charset) = await DownloadAsync(url, MaxDocumentBytes, cancellationToken).ConfigureAwait(false);
        string html = DecodeText(bytes, charset);
        var baseUri = ResolveBase(html, finalUri);

        var css = new StringBuilder();
        foreach (var href in FindStyleSheetLinks(html))
        {
            if (!Uri.TryCreate(baseUri, href, out var cssUri) || !IsHttp(cssUri))
                continue;
            try
            {
                var (cssBytes, _, cssCharset) = await DownloadAsync(cssUri, MaxDocumentBytes, cancellationToken).ConfigureAwait(false);
                css.AppendLine(DecodeText(cssBytes, cssCharset));
            }
            catch (Exception e) when ((e is HttpRequestException or InvalidDataException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
            {
                // A missing style sheet should not stop the page from converting.
            }
        }

        return new WebPage(html, baseUri, css.ToString());
    }

    /// <summary>
    /// Returns a resolver for <see cref="Html.HtmlToPdfOptions.ResourceResolver"/> that downloads
    /// http and https images relative to <paramref name="baseUri"/>. It blocks while downloading, so
    /// run the conversion off the UI thread. <c>data:</c> URIs are left to the converter.
    /// </summary>
    public Func<string, byte[]?> CreateResourceResolver(Uri baseUri, CancellationToken cancellationToken = default)
    {
        return source =>
        {
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return null;
            if (!Uri.TryCreate(baseUri, source, out var uri) || !IsHttp(uri))
                return null;
            try
            {
                return DownloadAsync(uri, MaxImageBytes, cancellationToken).GetAwaiter().GetResult().Bytes;
            }
            catch (Exception e) when ((e is HttpRequestException or InvalidDataException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
            {
                return null; // the converter skips images it cannot load
            }
        };
    }

    /// <summary>The <c>href</c> of every <c>&lt;link rel="stylesheet"&gt;</c> for screen, print or all media.</summary>
    public static IReadOnlyList<string> FindStyleSheetLinks(string html)
    {
        var links = new List<string>();
        foreach (Match tag in LinkTag().Matches(html))
        {
            var attributes = ParseAttributes(tag.Value);
            if (!attributes.TryGetValue("rel", out var rel) || !rel.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Any(r => r.Equals("stylesheet", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (rel.Contains("alternate", StringComparison.OrdinalIgnoreCase))
                continue;
            if (attributes.TryGetValue("media", out var media) && !IsPrintableMedia(media))
                continue;
            if (attributes.TryGetValue("href", out var href) && href.Length > 0)
                links.Add(WebUtility.HtmlDecode(href));
        }
        return links;
    }

    /// <summary>The page address, or the first <c>&lt;base href&gt;</c> resolved against it.</summary>
    public static Uri ResolveBase(string html, Uri pageUri)
    {
        var tag = BaseTag().Match(html);
        if (tag.Success && ParseAttributes(tag.Value).TryGetValue("href", out var href)
            && Uri.TryCreate(pageUri, WebUtility.HtmlDecode(href), out var baseUri) && IsHttp(baseUri))
            return baseUri;
        return pageUri;
    }

    private static bool IsPrintableMedia(string media)
    {
        media = media.Trim().ToLowerInvariant();
        return media.Length == 0 || media.Contains("all") || media.Contains("print") || media.Contains("screen");
    }

    private static Dictionary<string, string> ParseAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Attribute().Matches(tag))
        {
            string value = m.Groups["dq"].Success ? m.Groups["dq"].Value
                : m.Groups["sq"].Success ? m.Groups["sq"].Value
                : m.Groups["uq"].Value;
            attributes.TryAdd(m.Groups["name"].Value, value.Trim());
        }
        return attributes;
    }

    private static bool IsHttp(Uri uri) => uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private async Task<(byte[] Bytes, Uri FinalUri, string? Charset)> DownloadAsync(Uri uri, long maxBytes, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException($"{uri} is larger than {SizeFormat.Bytes(maxBytes)}.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException($"{uri} is larger than {SizeFormat.Bytes(maxBytes)}.");
            buffer.Write(chunk, 0, read);
        }
        return (buffer.ToArray(), response.RequestMessage?.RequestUri ?? uri, response.Content.Headers.ContentType?.CharSet);
    }

    /// <summary>
    /// Decodes downloaded text: a byte order mark wins, then the HTTP charset, then a
    /// <c>&lt;meta charset&gt;</c> in the first bytes, then UTF-8.
    /// </summary>
    public static string DecodeText(byte[] bytes, string? charset)
    {
        var encoding = EncodingFromBom(bytes) ?? GetEncoding(charset);
        if (encoding is null)
        {
            string head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 2048));
            var meta = MetaCharset().Match(head);
            encoding = meta.Success ? GetEncoding(meta.Groups["cs"].Value) : null;
        }
        encoding ??= Encoding.UTF8;
        string text = encoding.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    private static Encoding? EncodingFromBom(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8;
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode;
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode;
        return null;
    }

    private static Encoding? GetEncoding(string? name)
    {
        name = name?.Trim().Trim('"', '\'');
        if (string.IsNullOrEmpty(name))
            return null;
        // Browsers treat these labels as windows-1252.
        if (name.Equals("iso-8859-1", StringComparison.OrdinalIgnoreCase) || name.Equals("latin1", StringComparison.OrdinalIgnoreCase)
            || name.Equals("us-ascii", StringComparison.OrdinalIgnoreCase))
            name = "windows-1252";
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTag();

    [GeneratedRegex(@"<base\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BaseTag();

    [GeneratedRegex(@"(?<name>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<uq>[^\s""'=<>`]+))")]
    private static partial Regex Attribute();

    [GeneratedRegex(@"<meta\b[^>]*charset\s*=\s*[""']?(?<cs>[-a-zA-Z0-9_:.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharset();
}
