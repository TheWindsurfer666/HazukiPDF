using System.Diagnostics;

namespace HazukiPdf.WinForms;

/// <summary>Opens files and folders with Windows Explorer.</summary>
internal static class Shell
{
    public static void Open(string path) => Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public static void ShowInFolder(string path) =>
        Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = false });

    private static void Start(ProcessStartInfo info)
    {
        try
        {
            using var _ = Process.Start(info);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            MessageBox.Show(e.Message, "HazukiPDF", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
