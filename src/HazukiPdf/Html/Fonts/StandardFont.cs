namespace HazukiPdf.Html.Fonts;

/// <summary>Generic font families that map onto the standard 14 PDF fonts.</summary>
internal enum FontFamily
{
    Helvetica,
    Times,
    Courier,
}

/// <summary>
/// One of the twelve text fonts from the standard 14 PDF fonts. These fonts are built into every
/// conforming PDF reader, so nothing has to be embedded; text is encoded with WinAnsiEncoding.
/// </summary>
internal sealed class StandardFont
{
    private readonly short[]? _widths;

    private StandardFont(string baseFont, short[]? widths, int ascent, int descent)
    {
        BaseFont = baseFont;
        _widths = widths;
        Ascent = ascent;
        Descent = descent;
    }

    /// <summary>The PostScript name used as /BaseFont.</summary>
    public string BaseFont { get; }

    /// <summary>Ascender height in 1/1000 em.</summary>
    public int Ascent { get; }

    /// <summary>Descender depth in 1/1000 em (negative).</summary>
    public int Descent { get; }

    public static readonly StandardFont Helvetica = new("Helvetica", StandardFontWidths.Helvetica, 718, -207);
    public static readonly StandardFont HelveticaBold = new("Helvetica-Bold", StandardFontWidths.HelveticaBold, 718, -207);
    public static readonly StandardFont HelveticaOblique = new("Helvetica-Oblique", StandardFontWidths.Helvetica, 718, -207);
    public static readonly StandardFont HelveticaBoldOblique = new("Helvetica-BoldOblique", StandardFontWidths.HelveticaBold, 718, -207);
    public static readonly StandardFont TimesRoman = new("Times-Roman", StandardFontWidths.TimesRoman, 683, -217);
    public static readonly StandardFont TimesBold = new("Times-Bold", StandardFontWidths.TimesBold, 676, -205);
    public static readonly StandardFont TimesItalic = new("Times-Italic", StandardFontWidths.TimesItalic, 683, -205);
    public static readonly StandardFont TimesBoldItalic = new("Times-BoldItalic", StandardFontWidths.TimesBoldItalic, 699, -205);
    public static readonly StandardFont Courier = new("Courier", null, 629, -157);
    public static readonly StandardFont CourierBold = new("Courier-Bold", null, 626, -142);
    public static readonly StandardFont CourierOblique = new("Courier-Oblique", null, 629, -157);
    public static readonly StandardFont CourierBoldOblique = new("Courier-BoldOblique", null, 626, -142);

    public static StandardFont Get(FontFamily family, bool bold, bool italic) => family switch
    {
        FontFamily.Times => bold ? (italic ? TimesBoldItalic : TimesBold) : (italic ? TimesItalic : TimesRoman),
        FontFamily.Courier => bold ? (italic ? CourierBoldOblique : CourierBold) : (italic ? CourierOblique : Courier),
        _ => bold ? (italic ? HelveticaBoldOblique : HelveticaBold) : (italic ? HelveticaOblique : Helvetica),
    };

    /// <summary>Width of a WinAnsi character code in 1/1000 em.</summary>
    public int GetWidth(byte code)
    {
        if (_widths is null)
            return 600;
        return code < 32 ? 0 : _widths[code - 32];
    }

    /// <summary>Measures a string in points at the given font size.</summary>
    public double Measure(string text, double fontSize)
    {
        int total = 0;
        foreach (char c in text)
            total += GetWidth(WinAnsiEncoding.Encode(c));
        return total * fontSize / 1000.0;
    }
}
