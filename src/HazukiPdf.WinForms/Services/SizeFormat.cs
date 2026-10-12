using System.Globalization;

namespace HazukiPdf.WinForms.Services;

/// <summary>Formats byte counts and savings for display.</summary>
public static class SizeFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Formats a byte count with a binary unit, for example "1.5 MB".</summary>
    public static string Bytes(long bytes)
    {
        double value = Math.Abs((double)bytes);
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        string sign = bytes < 0 ? "-" : "";
        string number = unit == 0 ? value.ToString("0", CultureInfo.CurrentCulture) : value.ToString("0.0", CultureInfo.CurrentCulture);
        return $"{sign}{number} {Units[unit]}";
    }

    /// <summary>The share of <paramref name="original"/> that was saved, from 0 to 100. Zero when the original is empty.</summary>
    public static double PercentSaved(long original, long compressed) =>
        original <= 0 ? 0 : (original - compressed) * 100.0 / original;

    /// <summary>Formats a percentage saved, for example "42.3 %".</summary>
    public static string Percent(double percent) => percent.ToString("0.0", CultureInfo.CurrentCulture) + " %";
}
