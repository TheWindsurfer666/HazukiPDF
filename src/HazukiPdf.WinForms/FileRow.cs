using System.ComponentModel;
using System.Runtime.CompilerServices;
using HazukiPdf.WinForms.Services;

namespace HazukiPdf.WinForms;

/// <summary>One file in the compression list, bound to the results grid.</summary>
internal sealed class FileRow(string path) : INotifyPropertyChanged
{
    private readonly long _length = SafeLength(path);
    private string _status = "Waiting";
    private FileCompressionResult? _result;

    public event PropertyChangedEventHandler? PropertyChanged;

    [Browsable(false)]
    public string Path { get; } = path;

    [Browsable(false)]
    public FileCompressionResult? Result
    {
        get => _result;
        set
        {
            _result = value;
            _status = value switch
            {
                null => "Waiting",
                { Succeeded: false } => "Failed: " + value.Error,
                { OriginalKept: true } => "Already optimal",
                _ => "Done",
            };
            OnPropertyChanged(string.Empty); // every column changes
        }
    }

    [DisplayName("File")]
    public string FileName => System.IO.Path.GetFileName(Path);

    [DisplayName("Status")]
    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
        }
    }

    [DisplayName("Original")]
    public string OriginalSize => SizeFormat.Bytes(_result?.OriginalSize ?? _length);

    [DisplayName("Compressed")]
    public string CompressedSize => _result is { Succeeded: true } r ? SizeFormat.Bytes(r.CompressedSize) : "";

    [DisplayName("Saved")]
    public string Saved => _result is { Succeeded: true } r ? SizeFormat.Percent(r.PercentSaved) : "";

    [DisplayName("Output")]
    public string Output => _result?.OutputPath ?? "";

    [DisplayName("Folder")]
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
