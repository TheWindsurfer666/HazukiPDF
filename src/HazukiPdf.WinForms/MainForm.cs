using HazukiPdf.WinForms.Services;

namespace HazukiPdf.WinForms;

/// <summary>The main window: tabs for compressing PDFs and converting HTML to PDF, with a shared log below.</summary>
internal sealed class MainForm : Form
{
    private readonly AppSettings _settings = AppSettings.Load(AppSettings.DefaultPath);
    private readonly LogView _log = new() { Dock = DockStyle.Fill };
    private readonly CompressPanel _compressPanel;
    private readonly HtmlPanel _htmlPanel;

    public MainForm()
    {
        Text = "HazukiPDF";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(800, 640);
        ClientSize = new Size(1040, 820);

        _compressPanel = new CompressPanel(_log, _settings) { Dock = DockStyle.Fill };
        _htmlPanel = new HtmlPanel(_log, _settings) { Dock = DockStyle.Fill };

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var compressTab = new TabPage("Compress PDF") { Padding = new Padding(8) };
        compressTab.Controls.Add(_compressPanel);
        var htmlTab = new TabPage("HTML to PDF") { Padding = new Padding(8) };
        htmlTab.Controls.Add(_htmlPanel);
        tabs.TabPages.Add(compressTab);
        tabs.TabPages.Add(htmlTab);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.Controls.Add(tabs);
        split.Panel2.Controls.Add(_log);
        split.Panel2.Padding = new Padding(8, 0, 8, 8);
        Controls.Add(split);
        Load += (_, _) =>
        {
            // Distance first: min sizes larger than the current split would otherwise be rejected.
            split.SplitterDistance = Math.Max(420, split.Height - 200);
            split.Panel1MinSize = 420;
            split.Panel2MinSize = 120;
        };

        _log.Info("Ready. Compressed files and converted PDFs are listed here.");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_compressPanel.IsBusy || _htmlPanel.IsBusy)
        {
            var answer = MessageBox.Show(this, "Work is still running. Close anyway?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _compressPanel.Cancel();
            _htmlPanel.Cancel();
        }
        _compressPanel.SaveSettings();
        _settings.Save(AppSettings.DefaultPath);
        base.OnFormClosing(e);
    }
}
