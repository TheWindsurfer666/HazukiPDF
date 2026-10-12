using System.ComponentModel;
using HazukiPdf.WinForms.Services;

namespace HazukiPdf.WinForms;

/// <summary>Compresses one PDF or a batch and shows how much smaller each file got.</summary>
internal sealed class CompressPanel : UserControl
{
    private static readonly (PdfCompressionLevel Level, string Name, string Description)[] Levels =
    [
        (PdfCompressionLevel.Lossless, "Lossless", "Pages look exactly the same. Recompresses streams and removes unused and duplicate objects."),
        (PdfCompressionLevel.Balanced, "Balanced", "Recompresses photos as JPEG (quality 80) and downsamples images above 200 DPI. Good for most files."),
        (PdfCompressionLevel.Maximum, "Maximum", "JPEG quality 60, images above 120 DPI downsampled, metadata removed. Best for screen reading and email."),
    ];

    private readonly BindingList<FileRow> _rows = new();
    private readonly DataGridView _grid;
    private readonly ComboBox _level;
    private readonly Label _levelDescription;
    private readonly RadioButton _nextToOriginal;
    private readonly RadioButton _toFolder;
    private readonly TextBox _outputFolder;
    private readonly Button _browseOutput;
    private readonly TextBox _suffix;
    private readonly Button _compress;
    private readonly Button _cancel;
    private readonly Button[] _listButtons;
    private readonly ProgressBar _progress;
    private readonly Label _summary;
    private CancellationTokenSource? _cts;

    public CompressPanel()
    {
        // Toolbar
        var addFiles = MakeButton("Add files…", (_, _) => AddFilesFromDialog());
        var addFolder = MakeButton("Add folder…", (_, _) => AddFolderFromDialog());
        var remove = MakeButton("Remove", (_, _) => RemoveSelected());
        var clear = MakeButton("Clear", (_, _) => { _rows.Clear(); UpdateSummary(); });
        _listButtons = [addFiles, addFolder, remove, clear];
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        toolbar.Controls.AddRange(_listButtons);
        toolbar.Controls.Add(new Label
        {
            Text = "or drop PDF files and folders on the list",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(8, 8, 3, 3),
        });

        // Results grid
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = true,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window,
            AllowDrop = true,
            DataSource = _rows,
        };
        _grid.DataBindingComplete += (_, _) => ConfigureColumns();
        _grid.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true && !IsBusy ? DragDropEffects.Copy : DragDropEffects.None;
        _grid.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
                AddPaths(paths);
        };
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _rows[e.RowIndex].Result?.OutputPath is { } output && File.Exists(output))
                Shell.Open(output);
        };
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete && !IsBusy)
                RemoveSelected();
        };
        _grid.ContextMenuStrip = BuildContextMenu();

        // Options
        _level = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        foreach (var level in Levels)
            _level.Items.Add(level.Name);
        _level.SelectedIndex = 1;
        _levelDescription = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(8, 6, 3, 3) };
        _level.SelectedIndexChanged += (_, _) => _levelDescription.Text = Levels[_level.SelectedIndex].Description;
        _levelDescription.Text = Levels[_level.SelectedIndex].Description;

        _nextToOriginal = new RadioButton { Text = "Next to the originals", AutoSize = true, Checked = true };
        _toFolder = new RadioButton { Text = "In this folder:", AutoSize = true };
        _outputFolder = new TextBox { Width = 320, Enabled = false };
        _browseOutput = MakeButton("Browse…", (_, _) => BrowseOutputFolder());
        _browseOutput.Enabled = false;
        _toFolder.CheckedChanged += (_, _) => _outputFolder.Enabled = _browseOutput.Enabled = _toFolder.Checked;
        _suffix = new TextBox { Text = OutputPathPlanner.DefaultSuffix, Width = 120 };

        var options = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 6, 0, 6) };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        options.Controls.Add(MakeLabel("Level:"), 0, 0);
        options.Controls.Add(Flow(_level, _levelDescription), 1, 0);
        options.Controls.Add(MakeLabel("Save:"), 0, 1);
        options.Controls.Add(Flow(_nextToOriginal, _toFolder, _outputFolder, _browseOutput), 1, 1);
        options.Controls.Add(MakeLabel("Name suffix:"), 0, 2);
        options.Controls.Add(Flow(_suffix, new Label
        {
            Text = "report.pdf becomes report" + OutputPathPlanner.DefaultSuffix + ".pdf. Originals are never overwritten.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(8, 6, 3, 3),
        }), 1, 2);

        // Bottom bar
        _progress = new ProgressBar { Dock = DockStyle.Fill, Height = 22, Margin = new Padding(3, 8, 3, 3) };
        _summary = new Label { AutoSize = true, Margin = new Padding(3, 8, 12, 3) };
        _compress = MakeButton("Compress", async (_, _) => await CompressAsync());
        _compress.Font = new Font(_compress.Font, FontStyle.Bold);
        _cancel = MakeButton("Cancel", (_, _) => Cancel());
        _cancel.Enabled = false;
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_summary, 0, 0);
        bottom.Controls.Add(_progress, 1, 0);
        bottom.Controls.Add(_compress, 2, 0);
        bottom.Controls.Add(_cancel, 3, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(_grid, 0, 1);
        layout.Controls.Add(options, 0, 2);
        layout.Controls.Add(bottom, 0, 3);
        Controls.Add(layout);

        UpdateSummary();
    }

    public bool IsBusy => _cts is not null;

    public void Cancel() => _cts?.Cancel();

    /// <summary>Adds PDF files, and every PDF inside the given folders, skipping files already listed.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        if (IsBusy)
            return;
        var known = new HashSet<string>(_rows.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.Exists(path)
                    ? Directory.EnumerateFiles(path, "*.pdf", SearchOption.AllDirectories).ToList()
                    : File.Exists(path) && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? [path] : [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"Could not read {path}:\n{e.Message}", "HazukiPDF", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                continue;
            }
            foreach (var file in files)
            {
                string full = Path.GetFullPath(file);
                if (known.Add(full))
                    _rows.Add(new FileRow(full));
            }
        }
        UpdateSummary();
    }

    private async Task CompressAsync()
    {
        if (IsBusy)
            return;
        if (_rows.Count == 0)
        {
            AddFilesFromDialog();
            if (_rows.Count == 0)
                return;
        }

        string? outputFolder = null;
        if (_toFolder.Checked)
        {
            outputFolder = _outputFolder.Text.Trim();
            if (outputFolder.Length == 0)
            {
                BrowseOutputFolder();
                outputFolder = _outputFolder.Text.Trim();
                if (outputFolder.Length == 0)
                    return;
            }
            try
            {
                Directory.CreateDirectory(outputFolder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                MessageBox.Show(this, $"Could not use the output folder:\n{e.Message}", "HazukiPDF", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        var rows = _rows.ToList();
        OutputPathPlanner planner;
        try
        {
            planner = new OutputPathPlanner(rows.Select(r => r.Path), outputFolder, _suffix.Text);
        }
        catch (ArgumentException e)
        {
            MessageBox.Show(this, e.Message, "HazukiPDF", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var options = PdfCompressionOptions.FromLevel(Levels[_level.SelectedIndex].Level);
        var rowByPath = rows.ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            row.Result = null;
            row.Status = "Queued";
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);
        _progress.Maximum = rows.Count;
        _progress.Value = 0;
        _summary.Text = $"Compressing {rows.Count} file{(rows.Count == 1 ? "" : "s")}…";

        // Progress<T> is created on the UI thread, so its callback runs there.
        var progress = new Progress<FileCompressionResult>(result =>
        {
            rowByPath[result.InputPath].Result = result;
            _progress.Value = Math.Min(_progress.Maximum, _progress.Value + 1);
        });

        try
        {
            var results = await BatchCompressor.CompressAsync(rows.Select(r => r.Path).ToList(), options, planner, progress,
                cancellationToken: _cts.Token);
            // Progress callbacks are posted, so some may not have run yet; the returned list is complete.
            foreach (var result in results)
                rowByPath[result.InputPath].Result = result;
            foreach (var row in rows.Where(r => r.Result is null))
                row.Status = "Cancelled";
            _progress.Value = results.Count;
            ShowSummary(BatchSummary.From(results), _cts.IsCancellationRequested);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void ShowSummary(BatchSummary summary, bool cancelled)
    {
        int succeeded = summary.Files - summary.Failed;
        string text = succeeded == 0
            ? "No files were compressed."
            : $"{succeeded} file{(succeeded == 1 ? "" : "s")}: {SizeFormat.Bytes(summary.OriginalSize)} → {SizeFormat.Bytes(summary.CompressedSize)} " +
              $"({SizeFormat.Percent(summary.PercentSaved)} smaller, {SizeFormat.Bytes(summary.OriginalSize - summary.CompressedSize)} saved)";
        if (summary.Failed > 0)
            text += $", {summary.Failed} failed";
        if (cancelled)
            text += ", cancelled";
        _summary.Text = text;
    }

    private void UpdateSummary()
    {
        if (IsBusy)
            return;
        _summary.Text = _rows.Count switch
        {
            0 => "Add one PDF or many.",
            1 => "1 file",
            var n => $"{n} files",
        };
        _progress.Value = 0;
    }

    private void SetBusy(bool busy)
    {
        _compress.Enabled = !busy;
        _cancel.Enabled = busy;
        foreach (var button in _listButtons)
            button.Enabled = !busy;
        _level.Enabled = _nextToOriginal.Enabled = _toFolder.Enabled = _suffix.Enabled = !busy;
        _outputFolder.Enabled = _browseOutput.Enabled = !busy && _toFolder.Checked;
    }

    private void AddFilesFromDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose PDF files to compress",
            Filter = "PDF files (*.pdf)|*.pdf",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            AddPaths(dialog.FileNames);
    }

    private void AddFolderFromDialog()
    {
        using var dialog = new FolderBrowserDialog { Description = "Add every PDF in this folder and its subfolders" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            AddPaths([dialog.SelectedPath]);
    }

    private void BrowseOutputFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Save the compressed files in", ShowNewFolderButton = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _outputFolder.Text = dialog.SelectedPath;
            _toFolder.Checked = true;
        }
    }

    private void RemoveSelected()
    {
        foreach (var row in _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => (FileRow)r.DataBoundItem!).ToList())
            _rows.Remove(row);
        UpdateSummary();
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        var openOutput = new ToolStripMenuItem("Open compressed file", null, (_, _) => WithSelected(r => r.Result?.OutputPath, Shell.Open));
        var showOutput = new ToolStripMenuItem("Show compressed file in folder", null, (_, _) => WithSelected(r => r.Result?.OutputPath, Shell.ShowInFolder));
        var openOriginal = new ToolStripMenuItem("Open original", null, (_, _) => WithSelected(r => r.Path, Shell.Open));
        var removeItem = new ToolStripMenuItem("Remove from list", null, (_, _) => RemoveSelected());
        menu.Items.AddRange([openOutput, showOutput, openOriginal, new ToolStripSeparator(), removeItem]);
        menu.Opening += (_, e) =>
        {
            var row = SelectedRow();
            if (row is null)
            {
                e.Cancel = true;
                return;
            }
            bool hasOutput = row.Result?.OutputPath is { } p && File.Exists(p);
            openOutput.Enabled = showOutput.Enabled = hasOutput;
            removeItem.Enabled = !IsBusy;
        };
        _grid.CellMouseDown += (_, e) =>
        {
            // Right-clicking a row selects it first, like Explorer.
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && !_grid.Rows[e.RowIndex].Selected)
            {
                _grid.ClearSelection();
                _grid.Rows[e.RowIndex].Selected = true;
            }
        };
        return menu;
    }

    private FileRow? SelectedRow() => _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].DataBoundItem as FileRow : null;

    private void WithSelected(Func<FileRow, string?> path, Action<string> action)
    {
        if (SelectedRow() is { } row && path(row) is { } p && File.Exists(p))
            action(p);
    }

    private void ConfigureColumns()
    {
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
            switch (column.DataPropertyName)
            {
                case nameof(FileRow.FileName):
                    column.FillWeight = 160;
                    break;
                case nameof(FileRow.Status):
                    column.FillWeight = 110;
                    break;
                case nameof(FileRow.OriginalSize) or nameof(FileRow.CompressedSize) or nameof(FileRow.Saved):
                    column.FillWeight = 55;
                    column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                    break;
                case nameof(FileRow.Output):
                    column.FillWeight = 180;
                    break;
                case nameof(FileRow.Folder):
                    column.FillWeight = 140;
                    break;
            }
        }
    }

    internal static Button MakeButton(string text, EventHandler onClick)
    {
        var button = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 2, 6, 2) };
        button.Click += onClick;
        return button;
    }

    internal static Label MakeLabel(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 8, 6, 3) };

    internal static FlowLayoutPanel Flow(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
        panel.Controls.AddRange(controls);
        return panel;
    }
}
