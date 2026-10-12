using System.Globalization;
using System.Text;

namespace HazukiPdf.Html.Pdf;

/// <summary>Formatting helpers for content stream operands.</summary>
internal static class PdfSyntax
{
    /// <summary>Formats a number the way PDF expects: invariant culture, no exponent, at most 3 decimals.</summary>
    public static string Num(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return "0";
        double rounded = Math.Round(value, 3);
        if (rounded == 0)
            return "0";
        return rounded.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>A literal string with special and non-printable bytes escaped.</summary>
    public static string LiteralString(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length + 2);
        sb.Append('(');
        foreach (byte b in bytes)
        {
            switch (b)
            {
                case (byte)'(' or (byte)')' or (byte)'\\':
                    sb.Append('\\').Append((char)b);
                    break;
                case < 32 or > 126:
                    sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    break;
                default:
                    sb.Append((char)b);
                    break;
            }
        }
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>Bytes of a text string for metadata: ASCII when possible, otherwise UTF-16BE with a byte order mark.</summary>
    public static byte[] TextStringBytes(string text)
    {
        if (text.All(c => c is >= ' ' and <= '~'))
            return Encoding.ASCII.GetBytes(text);
        return [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)];
    }
}
