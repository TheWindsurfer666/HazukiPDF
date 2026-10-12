using System.Text;

namespace HazukiPdf.WinForms;

/// <summary>A running log of what the app did: every file in a batch, every conversion, and every error.</summary>
internal sealed class LogView : UserControl
{
    private readonly ListView _list;
    private readonly List<string> _lines = new();

    public LogView()
    {
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            HideSelection = false,
            ShowItemToolTips = true,
        };
        _list.Columns.Add("Time", 70);
        _list.Columns.Add("", 60);
        _list.Columns.Add("File", 220);
        _list.Columns.Add("Details", 600);
        _list.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C)
                CopySelected();
        };

        var title = new Label { Text = "Log", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 8, 12, 3) };
        var copy = CompressPanel.MakeButton("Copy", (_, _) => CopySelected());
        var save = CompressPanel.MakeButton("Save log…", (_, _) => SaveLog());
        var clear = CompressPanel.MakeButton("Clear", (_, _) => { _list.Items.Clear(); _lines.Clear(); });
        var header = CompressPanel.Flow(title, copy, save, clear);
        header.WrapContents = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(_list, 0, 1);
        Controls.Add(layout);
    }

    public void Info(string message, string? file = null) => Add("Info", file, message, SystemColors.WindowText);

    public void Success(string message, string? file = null) => Add("OK", file, message, Color.FromArgb(0, 120, 40));

    public void Warning(string message, string? file = null) => Add("Warning", file, message, Color.FromArgb(170, 100, 0));

    public void Error(string message, string? file = null) => Add("Error", file, message, Color.FromArgb(190, 0, 0));

    private void Add(string kind, string? file, string message, Color color)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Add(kind, file, message, color));
            return;
        }
        string time = DateTime.Now.ToString("HH:mm:ss");
        string name = file is null ? "" : Path.GetFileName(file);
        var item = new ListViewItem([time, kind, name, message]) { ForeColor = color, ToolTipText = file ?? "" };
        _list.Items.Add(item);
        _lines.Add($"{time}\t{kind}\t{file ?? ""}\t{message}");
        item.EnsureVisible();
    }

    private void CopySelected()
    {
        var indices = _list.SelectedIndices.Count > 0
            ? _list.SelectedIndices.Cast<int>()
            : Enumerable.Range(0, _lines.Count);
        var text = string.Join(Environment.NewLine, indices.Select(i => _lines[i]));
        if (text.Length > 0)
            Clipboard.SetText(text);
    }

    private void SaveLog()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save log",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"HazukiPDF log {DateTime.Now:yyyy-MM-dd HHmm}.txt",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            File.WriteAllLines(dialog.FileName, _lines, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, e.Message, "HazukiPDF", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
