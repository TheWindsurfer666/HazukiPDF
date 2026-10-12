using System.Net;
using System.Text;
using HazukiPdf.Html;
using HazukiPdf.WinForms.Services;

namespace HazukiPdf.Tests.App;

public class WebPageLoaderTests
{
    /// <summary>Serves canned responses by absolute URL; anything else is a 404.</summary>
    private sealed class FakeHandler(Dictionary<string, (string ContentType, byte[] Body)> responses) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.AbsoluteUri;
            lock (Requests)
                Requests.Add(url);
            if (!responses.TryGetValue(url, out var r))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            var content = new ByteArrayContent(r.Body);
            content.Headers.TryAddWithoutValidation("Content-Type", r.ContentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }

    private static (WebPageLoader Loader, FakeHandler Handler) Create(Dictionary<string, (string, byte[])> responses)
    {
        var handler = new FakeHandler(responses);
        return (new WebPageLoader(new HttpClient(handler)), handler);
    }

    private static (string, byte[]) Text(string contentType, string body) => (contentType, Encoding.UTF8.GetBytes(body));

    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("  http://example.com/a?b=1 ", "http://example.com/a?b=1")]
    [InlineData("https://example.com/page", "https://example.com/page")]
    public void TryNormalizeUrl_AcceptsWebAddresses(string input, string expected)
    {
        Assert.True(WebPageLoader.TryNormalizeUrl(input, out var uri));
        Assert.Equal(expected, uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///C:/secret.html")]
    [InlineData("javascript://alert(1)")]
    public void TryNormalizeUrl_RejectsEverythingElse(string? input) =>
        Assert.False(WebPageLoader.TryNormalizeUrl(input, out _));

    [Fact]
    public void FindStyleSheetLinks_SkipsAlternateAndNonPrintMedia()
    {
        const string html = """
            <link rel="stylesheet" href="a.css">
            <LINK REL='Stylesheet' HREF='/b.css?x=1&amp;y=2' media="screen, print">
            <link rel=stylesheet href=c.css media=all>
            <link rel="alternate stylesheet" href="alt.css">
            <link rel="stylesheet" href="speech.css" media="speech">
            <link rel="icon" href="favicon.png">
            """;
        Assert.Equal(["a.css", "/b.css?x=1&y=2", "c.css"], WebPageLoader.FindStyleSheetLinks(html));
    }

    [Fact]
    public void ResolveBase_UsesBaseHref()
    {
        var page = new Uri("https://example.com/docs/page.html");
        Assert.Equal(page, WebPageLoader.ResolveBase("<p>no base</p>", page));
        Assert.Equal(new Uri("https://cdn.example.com/assets/"),
            WebPageLoader.ResolveBase("<base href=\"https://cdn.example.com/assets/\">", page));
        Assert.Equal(new Uri("https://example.com/static/"), WebPageLoader.ResolveBase("<base href='/static/'>", page));
        Assert.Equal(page, WebPageLoader.ResolveBase("<base href=\"javascript:void(0)\">", page));
    }

    [Fact]
    public void DecodeText_HonorsBomHeaderAndMetaCharset()
    {
        Assert.Equal("é", WebPageLoader.DecodeText([0xEF, 0xBB, 0xBF, 0xC3, 0xA9], "iso-8859-1"));
        Assert.Equal("é", WebPageLoader.DecodeText([0xE9], "iso-8859-1"));
        Assert.Equal("é", WebPageLoader.DecodeText([0xC3, 0xA9], null));

        var meta = Encoding.ASCII.GetBytes("<meta charset=\"windows-1252\"><p>").Concat(new byte[] { 0x80 }).ToArray();
        Assert.EndsWith("€", WebPageLoader.DecodeText(meta, null));
    }

    [Fact]
    public async Task LoadAsync_FetchesPageAndLinkedStyleSheets()
    {
        var (loader, handler) = Create(new()
        {
            ["https://example.com/docs/page.html"] = Text("text/html; charset=utf-8",
                "<html><head><link rel=stylesheet href=\"style.css\"><link rel=stylesheet href=\"missing.css\"></head><body><h1>Hi</h1></body></html>"),
            ["https://example.com/docs/style.css"] = Text("text/css", "h1 { color: red }"),
        });

        var page = await loader.LoadAsync(new Uri("https://example.com/docs/page.html"));

        Assert.Contains("<h1>Hi</h1>", page.Html);
        Assert.Equal(new Uri("https://example.com/docs/page.html"), page.BaseUri);
        Assert.Contains("h1 { color: red }", page.StyleSheets);
        Assert.Contains("https://example.com/docs/missing.css", handler.Requests);
    }

    [Fact]
    public async Task LoadAsync_FailsForMissingPage()
    {
        var (loader, _) = Create(new());
        await Assert.ThrowsAsync<HttpRequestException>(() => loader.LoadAsync(new Uri("https://example.com/nope")));
    }

    [Fact]
    public async Task LoadAsync_RejectsNonHttp()
    {
        var (loader, _) = Create(new());
        await Assert.ThrowsAsync<ArgumentException>(() => loader.LoadAsync(new Uri("file:///tmp/x.html")));
    }

    [Fact]
    public void ResourceResolver_DownloadsRelativeImagesOnly()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var (loader, handler) = Create(new() { ["https://example.com/img/logo.png"] = ("image/png", png) });
        var resolve = loader.CreateResourceResolver(new Uri("https://example.com/docs/page.html"));

        Assert.Equal(png, resolve("../img/logo.png"));
        Assert.Null(resolve("missing.png"));
        Assert.Null(resolve("data:image/png;base64,AAAA"));
        Assert.Null(resolve("file:///etc/passwd"));
        Assert.DoesNotContain(handler.Requests, r => !r.StartsWith("https://example.com/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadedPageConvertsToPdfWithItsImages()
    {
        // A 1x1 PNG.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
        var (loader, handler) = Create(new()
        {
            ["https://example.com/"] = Text("text/html", "<h1>Title</h1><img src=\"pic.png\" width=\"50\" height=\"50\">"),
            ["https://example.com/pic.png"] = ("image/png", png),
        });

        var page = await loader.LoadAsync(new Uri("https://example.com/"));
        var pdf = HtmlToPdfConverter.Convert(page.Html, new HtmlToPdfOptions
        {
            UserStyleSheet = page.StyleSheets,
            ResourceResolver = loader.CreateResourceResolver(page.BaseUri),
        });

        Assert.Contains("https://example.com/pic.png", handler.Requests);
        Assert.Contains("/Subtype/Image", Encoding.Latin1.GetString(pdf));
    }
}
