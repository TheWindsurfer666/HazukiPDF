namespace HazukiPdf.WinForms;

/// <summary>The main window: one tab for compressing PDFs and one for converting HTML to PDF.</summary>
internal sealed class MainForm : Form
{
    private readonly CompressPanel _compressPanel = new() { Dock = DockStyle.Fill };
    private readonly HtmlPanel _htmlPanel = new() { Dock = DockStyle.Fill };

    public MainForm()
    {
        Text = "HazukiPDF";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(760, 520);
        ClientSize = new Size(1000, 660);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var compressTab = new TabPage("Compress PDF") { Padding = new Padding(8) };
        compressTab.Controls.Add(_compressPanel);
        var htmlTab = new TabPage("HTML to PDF") { Padding = new Padding(8) };
        htmlTab.Controls.Add(_htmlPanel);
        tabs.TabPages.Add(compressTab);
        tabs.TabPages.Add(htmlTab);
        Controls.Add(tabs);
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
        base.OnFormClosing(e);
    }
}
