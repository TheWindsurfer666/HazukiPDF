using System.Net;
using HazukiPdf.Html;
using HazukiPdf.WinForms.Services;

namespace HazukiPdf.WinForms;

/// <summary>Converts pasted HTML, an HTML file or a web page to PDF.</summary>
internal sealed class HtmlPanel : UserControl
{
    private static readonly (string Name, PageSize Size)[] PageSizes =
    [
        ("A4", PageSize.A4), ("Letter", PageSize.Letter), ("Legal", PageSize.Legal), ("A3", PageSize.A3), ("A5", PageSize.A5),
    ];

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly WebPageLoader _loader = new(Http);
    private readonly TextBox _url;
    private readonly Button _load;
    private readonly Button _openFile;
    private readonly Button _clear;
    private readonly TextBox _html;
    private readonly Label _source;
    private readonly ComboBox _pageSize;
    private readonly CheckBox _landscape;
    private readonly NumericUpDown _margin;
    private readonly Button _convert;
    private readonly Button _cancel;
    private readonly ProgressBar _progress;
    private readonly Label _status;
    private readonly LinkLabel _openPdf;
    private readonly TextBox _outputFolder;
    private readonly LogView _log;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;

    // Where relative image paths in the editor's HTML resolve: a web address or a folder, never both.
    private Uri? _baseUri;
    private string? _baseDirectory;
    private string? _styleSheets;
    private string? _lastPdf;

    public HtmlPanel(LogView log, AppSettings settings)
    {
        _log = log;
        _settings = settings;

        _url = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "https://example.com", Margin = new Padding(3, 5, 3, 3) };
        _url.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await LoadUrlAsync();
            }
        };
        _load = CompressPanel.MakeButton("Load URL", async (_, _) => await LoadUrlAsync());
        _openFile = CompressPanel.MakeButton("Open HTML file…", (_, _) => OpenHtmlFile());
        _clear = CompressPanel.MakeButton("Clear", (_, _) => ClearSource());

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 5 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(CompressPanel.MakeLabel("Web address:"), 0, 0);
        top.Controls.Add(_url, 1, 0);
        top.Controls.Add(_load, 2, 0);
        top.Controls.Add(_openFile, 3, 0);
        top.Controls.Add(_clear, 4, 0);

        _source = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 4, 3, 4),
            Text = "Paste or type HTML below, load a web page, or open an HTML file.",
        };

        _html = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            MaxLength = 0,
            Font = new Font(FontFamily.GenericMonospace, 9.75f),
            Text = "<!DOCTYPE html>\r\n<html>\r\n<head>\r\n  <title>Hello</title>\r\n  <style>\r\n    body { font-family: sans-serif; }\r\n    h1 { color: #2a6; }\r\n  </style>\r\n</head>\r\n<body>\r\n  <h1>Hello from HazukiPDF</h1>\r\n  <p>Edit this HTML, then press <b>Convert to PDF</b>.</p>\r\n</body>\r\n</html>\r\n",
        };

        _pageSize = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        foreach (var (name, _) in PageSizes)
            _pageSize.Items.Add(name);
        _pageSize.SelectedIndex = 0;
        _landscape = new CheckBox { Text = "Landscape", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
        _margin = new NumericUpDown { Minimum = 0, Maximum = 50, Value = 12.7m, DecimalPlaces = 1, Increment = 1, Width = 70 };
        _outputFolder = new TextBox
        {
            Width = 340,
            Text = settings.HtmlOutputFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        _outputFolder.TextChanged += (_, _) => _settings.HtmlOutputFolder = _outputFolder.Text.Trim();
        var options = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 4, 0, 4) };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        options.Controls.Add(CompressPanel.MakeLabel("Page:"), 0, 0);
        options.Controls.Add(CompressPanel.Flow(_pageSize, _landscape, CompressPanel.MakeLabel("Margins (mm):"), _margin), 1, 0);
        options.Controls.Add(CompressPanel.MakeLabel("Save PDFs in:"), 0, 1);
        options.Controls.Add(CompressPanel.Flow(
            _outputFolder,
            CompressPanel.MakeButton("Browse…", (_, _) => BrowseOutputFolder()),
            CompressPanel.MakeButton("Open folder", (_, _) =>
            {
                if (Directory.Exists(_outputFolder.Text.Trim()))
                    Shell.Open(_outputFolder.Text.Trim());
            })), 1, 1);

        _status = new Label { AutoSize = true, Margin = new Padding(3, 8, 6, 3) };
        _openPdf = new LinkLabel { Text = "Open PDF", AutoSize = true, Visible = false, Margin = new Padding(3, 8, 12, 3) };
        _openPdf.LinkClicked += (_, _) =>
        {
            if (_lastPdf is not null && File.Exists(_lastPdf))
                Shell.Open(_lastPdf);
        };
        _progress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0, Height = 22, Margin = new Padding(3, 8, 3, 3) };
        _convert = CompressPanel.MakeButton("Convert to PDF…", async (_, _) => await ConvertAsync());
        _convert.Font = new Font(_convert.Font, FontStyle.Bold);
        _cancel = CompressPanel.MakeButton("Cancel", (_, _) => Cancel());
        _cancel.Enabled = false;
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 5 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_status, 0, 0);
        bottom.Controls.Add(_openPdf, 1, 0);
        bottom.Controls.Add(_progress, 2, 0);
        bottom.Controls.Add(_convert, 3, 0);
        bottom.Controls.Add(_cancel, 4, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(top, 0, 0);
        layout.Controls.Add(_source, 0, 1);
        layout.Controls.Add(_html, 0, 2);
        layout.Controls.Add(options, 0, 3);
        layout.Controls.Add(bottom, 0, 4);
        Controls.Add(layout);
    }

    public bool IsBusy => _cts is not null;

    public void Cancel() => _cts?.Cancel();

    private async Task LoadUrlAsync()
    {
        if (IsBusy)
            return;
        if (!WebPageLoader.TryNormalizeUrl(_url.Text, out var url))
        {
            MessageBox.Show(this, "Enter a web address starting with http:// or https://.", "HazukiPDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _url.Focus();
            return;
        }

        _log.Info($"Loading {url}…");
        var page = await RunAsync($"Loading {url}…", token => _loader.LoadAsync(url, token));
        if (page is null)
        {
            _log.Error($"Could not load {url}. {_status.Text}");
            return;
        }

        _html.Text = page.Html.ReplaceLineEndings("\r\n");
        _url.Text = url.ToString();
        _baseUri = page.BaseUri;
        _baseDirectory = null;
        _styleSheets = page.StyleSheets;
        _source.Text = $"Loaded {SizeFormat.Bytes(page.Html.Length)} from {page.BaseUri}. Images are downloaded from there when converting.";
        _status.Text = "Page loaded.";
        _log.Info($"Loaded {url} ({SizeFormat.Bytes(page.Html.Length)})" +
                  (page.StyleSheets.Length > 0 ? ", with linked style sheets" : ""));
    }

    private void OpenHtmlFile()
    {
        if (IsBusy)
            return;
        using var dialog = new OpenFileDialog { Title = "Open an HTML file", Filter = "HTML files (*.html;*.htm)|*.html;*.htm|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            _html.Text = WebPageLoader.DecodeText(File.ReadAllBytes(dialog.FileName), null).ReplaceLineEndings("\r\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, e.Message, "HazukiPDF", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _baseDirectory = Path.GetDirectoryName(dialog.FileName);
        _baseUri = null;
        _styleSheets = null;
        _url.Text = "";
        _source.Text = $"Opened {dialog.FileName}. Images next to it are included.";
        _log.Info("Opened HTML file", dialog.FileName);
    }

    private void ClearSource()
    {
        if (IsBusy)
            return;
        _html.Clear();
        _url.Clear();
        _baseUri = null;
        _baseDirectory = null;
        _styleSheets = null;
        _source.Text = "Paste or type HTML below, load a web page, or open an HTML file.";
        _status.Text = "";
        _openPdf.Visible = false;
    }

    private async Task ConvertAsync()
    {
        if (IsBusy)
            return;

        // A typed address with nothing in the editor yet: fetch it first.
        if (string.IsNullOrWhiteSpace(_html.Text) && _url.Text.Trim().Length > 0)
        {
            await LoadUrlAsync();
            if (string.IsNullOrWhiteSpace(_html.Text))
                return;
        }
        if (string.IsNullOrWhiteSpace(_html.Text))
        {
            MessageBox.Show(this, "There is no HTML to convert.", "HazukiPDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string html = _html.Text;
        using var dialog = new SaveFileDialog
        {
            Title = "Save PDF",
            Filter = "PDF files (*.pdf)|*.pdf",
            DefaultExt = "pdf",
            FileName = SuggestFileName(html),
            OverwritePrompt = true,
        };
        if (Directory.Exists(_outputFolder.Text.Trim()))
            dialog.InitialDirectory = _outputFolder.Text.Trim();
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        string target = dialog.FileName;
        // The folder picked in the dialog becomes the default for next time.
        _outputFolder.Text = Path.GetDirectoryName(target) ?? _outputFolder.Text;
        string sourceName = _baseUri?.ToString() ?? "pasted HTML";
        _log.Info($"Converting {sourceName}…", target);

        var pageSize = PageSizes[_pageSize.SelectedIndex].Size;
        bool landscape = _landscape.Checked;
        double marginPoints = (double)_margin.Value * 72 / 25.4;
        var baseUri = _baseUri;
        var baseDirectory = _baseDirectory;
        var styleSheets = _styleSheets;

        var outcome = await RunAsync("Converting…", token => Task.Run(() =>
        {
            var options = new HtmlToPdfOptions
            {
                PageSize = pageSize,
                Landscape = landscape,
                Margins = PageMargins.Uniform(marginPoints),
                BaseDirectory = baseDirectory,
                UserStyleSheet = styleSheets,
                ResourceResolver = baseUri is null ? null : _loader.CreateResourceResolver(baseUri, token),
            };
            using var buffer = new MemoryStream();
            int pages = HtmlToPdfConverter.Convert(html, buffer, options);
            token.ThrowIfCancellationRequested();
            File.WriteAllBytes(target, buffer.ToArray());
            return new ConversionOutcome(pages, buffer.Length);
        }, token));

        if (outcome is not { } done)
        {
            if (_status.Text.StartsWith("Cancelled", StringComparison.Ordinal))
                _log.Warning("Conversion cancelled", target);
            else
                _log.Error($"Conversion of {sourceName} failed. {_status.Text}", target);
            return;
        }
        _log.Success($"{done.Pages} page{(done.Pages == 1 ? "" : "s")}, {SizeFormat.Bytes(done.Bytes)}, saved to {target}", target);
        _lastPdf = target;
        _openPdf.Visible = true;
        _status.Text = $"Saved {done.Pages} page{(done.Pages == 1 ? "" : "s")}, {SizeFormat.Bytes(done.Bytes)}, to {Path.GetFileName(target)}.";
    }

    /// <summary>Runs background work with the busy state, cancel button and error reporting.</summary>
    /// <returns>The result, or default when it failed or was cancelled.</returns>
    private async Task<T?> RunAsync<T>(string message, Func<CancellationToken, Task<T>> work) where T : class
    {
        _cts = new CancellationTokenSource();
        SetBusy(true);
        _status.Text = message;
        _openPdf.Visible = false;
        try
        {
            return await work(_cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            _status.Text = "Cancelled.";
        }
        catch (Exception e)
        {
            // Anything else (a network error, an unwritable file, HTML the converter rejects) is shown, not thrown,
            // because an exception escaping an async event handler would close the app.
            string text = e is TaskCanceledException ? "The server took too long to answer." : e.Message;
            _status.Text = "Failed: " + text;
            MessageBox.Show(this, text, "HazukiPDF", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
        return default;
    }

    private void BrowseOutputFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Save converted PDFs in", ShowNewFolderButton = true };
        if (Directory.Exists(_outputFolder.Text.Trim()))
            dialog.InitialDirectory = _outputFolder.Text.Trim();
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _outputFolder.Text = dialog.SelectedPath;
    }

    private void SetBusy(bool busy)
    {
        _convert.Enabled = _load.Enabled = _openFile.Enabled = _clear.Enabled = !busy;
        _url.ReadOnly = _html.ReadOnly = busy;
        _pageSize.Enabled = _landscape.Enabled = _margin.Enabled = !busy;
        _cancel.Enabled = busy;
        _progress.MarqueeAnimationSpeed = busy ? 30 : 0;
        _progress.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
    }

    private string SuggestFileName(string html)
    {
        var title = System.Text.RegularExpressions.Regex.Match(html, @"<title[^>]*>(.*?)</title>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        string name = title.Success ? WebUtility.HtmlDecode(title.Groups[1].Value).Trim()
            : _baseUri?.Host ?? "document";
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Length > 80 ? name[..80] : name;
        return (name.Length == 0 ? "document" : name) + ".pdf";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        // Some sites refuse requests without a browser-like user agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) HazukiPDF/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,text/css,image/png,image/jpeg,*/*;q=0.8");
        return client;
    }

    private sealed record ConversionOutcome(int Pages, long Bytes);
}
