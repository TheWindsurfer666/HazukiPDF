using System.Globalization;

namespace HazukiPdf.Html.Css;

/// <summary>An opaque RGB color with components in 0-255.</summary>
internal readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);
}

/// <summary>Helpers for tokenizing and interpreting CSS values.</summary>
internal static class CssValues
{
    public const double PointsPerPixel = 0.75;

    /// <summary>Splits a value on top-level whitespace, keeping function arguments together.</summary>
    public static List<string> SplitWhitespace(string value)
    {
        var parts = new List<string>();
        int depth = 0, start = -1;
        char quote = '\0';
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                if (start < 0)
                    start = i;
                continue;
            }
            if (c == '(')
                depth++;
            else if (c == ')')
                depth--;
            if (char.IsWhiteSpace(c) && depth == 0)
            {
                if (start >= 0)
                    parts.Add(value[start..i]);
                start = -1;
            }
            else if (start < 0)
            {
                start = i;
            }
        }
        if (start >= 0)
            parts.Add(value[start..]);
        return parts;
    }

    public static bool IsLength(string token) =>
        token == "0" || TryParseLength(token, 16, 16, 0, out _) || token.Equals("auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Converts a CSS length to points. <paramref name="fontSize"/> resolves <c>em</c>,
    /// <paramref name="rootFontSize"/> resolves <c>rem</c> and <paramref name="percentBase"/>
    /// resolves percentages. Unitless numbers are accepted as pixels for leniency.
    /// </summary>
    public static bool TryParseLength(string token, double fontSize, double rootFontSize, double percentBase, out double points)
    {
        points = 0;
        token = token.Trim().ToLowerInvariant();
        if (token.Length == 0)
            return false;
        int unitStart = token.Length;
        while (unitStart > 0 && (char.IsAsciiLetter(token[unitStart - 1]) || token[unitStart - 1] == '%'))
            unitStart--;
        if (!double.TryParse(token[..unitStart], NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            return false;
        string unit = token[unitStart..];
        points = unit switch
        {
            "" or "px" => number * PointsPerPixel,
            "pt" => number,
            "pc" => number * 12,
            "in" => number * 72,
            "cm" => number * 72 / 2.54,
            "mm" => number * 72 / 25.4,
            "q" => number * 72 / 101.6,
            "em" => number * fontSize,
            "rem" => number * rootFontSize,
            "ex" => number * fontSize * 0.5,
            "ch" => number * fontSize * 0.5,
            "%" => number * percentBase / 100,
            "vw" or "vh" or "vmin" or "vmax" => double.NaN,
            _ => double.NaN,
        };
        return !double.IsNaN(points);
    }

    public static bool IsPercentage(string token) => token.TrimEnd().EndsWith('%');

    public static bool TryParseNumber(string token, out double number) =>
        double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);

    public static bool TryParseColor(string value, out Rgb color, out bool transparent)
    {
        color = Rgb.Black;
        transparent = false;
        value = value.Trim().ToLowerInvariant();
        if (value.Length == 0)
            return false;

        if (value == "transparent")
        {
            transparent = true;
            return true;
        }

        if (value[0] == '#')
        {
            string hex = value[1..];
            if (!hex.All(char.IsAsciiHexDigit))
                return false;
            if (hex.Length is 3 or 4)
            {
                color = new Rgb(Hex(hex[0], hex[0]), Hex(hex[1], hex[1]), Hex(hex[2], hex[2]));
                transparent = hex.Length == 4 && hex[3] == '0';
                return true;
            }
            if (hex.Length is 6 or 8)
            {
                color = new Rgb(Hex(hex[0], hex[1]), Hex(hex[2], hex[3]), Hex(hex[4], hex[5]));
                transparent = hex.Length == 8 && hex[6] == '0' && hex[7] == '0';
                return true;
            }
            return false;
        }

        if (value.StartsWith("rgb", StringComparison.Ordinal) || value.StartsWith("hsl", StringComparison.Ordinal))
        {
            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close < open)
                return false;
            var args = value[(open + 1)..close]
                .Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (args.Length < 3)
                return false;
            if (args.Length >= 4 && TryParseAlpha(args[3], out double alpha) && alpha <= 0)
                transparent = true;

            if (value.StartsWith("rgb", StringComparison.Ordinal))
            {
                if (!TryChannel(args[0], out byte r) || !TryChannel(args[1], out byte g) || !TryChannel(args[2], out byte b))
                    return false;
                color = new Rgb(r, g, b);
                return true;
            }

            if (!TryParseNumber(args[0].Replace("deg", ""), out double h) ||
                !TryParseNumber(args[1].TrimEnd('%'), out double s) ||
                !TryParseNumber(args[2].TrimEnd('%'), out double l))
                return false;
            color = FromHsl(h, s / 100, l / 100);
            return true;
        }

        if (NamedColors.TryGetValue(value, out var named))
        {
            color = named;
            return true;
        }
        return false;

        static byte Hex(char a, char b) => (byte)(Convert.ToInt32(a.ToString(), 16) * 16 + Convert.ToInt32(b.ToString(), 16));
    }

    private static bool TryParseAlpha(string text, out double alpha)
    {
        bool percent = text.EndsWith('%');
        bool ok = TryParseNumber(text.TrimEnd('%'), out alpha);
        if (percent)
            alpha /= 100;
        return ok;
    }

    private static bool TryChannel(string text, out byte value)
    {
        value = 0;
        bool percent = text.EndsWith('%');
        if (!TryParseNumber(text.TrimEnd('%'), out double v))
            return false;
        if (percent)
            v = v * 255 / 100;
        value = (byte)Math.Clamp(Math.Round(v), 0, 255);
        return true;
    }

    private static Rgb FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        return new Rgb(Channel(h + 1.0 / 3), Channel(h), Channel(h - 1.0 / 3));

        byte Channel(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            double v = t < 1.0 / 6 ? p + (q - p) * 6 * t
                : t < 0.5 ? q
                : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6
                : p;
            return (byte)Math.Round(v * 255);
        }
    }

    public static bool IsColor(string token) => TryParseColor(token, out _, out _);

    private static readonly Dictionary<string, Rgb> NamedColors = BuildNamedColors();

    private static Dictionary<string, Rgb> BuildNamedColors()
    {
        // The CSS named colors.
        const string table =
            "aliceblue f0f8ff antiquewhite faebd7 aqua 00ffff aquamarine 7fffd4 azure f0ffff beige f5f5dc bisque ffe4c4 " +
            "black 000000 blanchedalmond ffebcd blue 0000ff blueviolet 8a2be2 brown a52a2a burlywood deb887 cadetblue 5f9ea0 " +
            "chartreuse 7fff00 chocolate d2691e coral ff7f50 cornflowerblue 6495ed cornsilk fff8dc crimson dc143c cyan 00ffff " +
            "darkblue 00008b darkcyan 008b8b darkgoldenrod b8860b darkgray a9a9a9 darkgreen 006400 darkgrey a9a9a9 " +
            "darkkhaki bdb76b darkmagenta 8b008b darkolivegreen 556b2f darkorange ff8c00 darkorchid 9932cc darkred 8b0000 " +
            "darksalmon e9967a darkseagreen 8fbc8f darkslateblue 483d8b darkslategray 2f4f4f darkslategrey 2f4f4f " +
            "darkturquoise 00ced1 darkviolet 9400d3 deeppink ff1493 deepskyblue 00bfff dimgray 696969 dimgrey 696969 " +
            "dodgerblue 1e90ff firebrick b22222 floralwhite fffaf0 forestgreen 228b22 fuchsia ff00ff gainsboro dcdcdc " +
            "ghostwhite f8f8ff gold ffd700 goldenrod daa520 gray 808080 green 008000 greenyellow adff2f grey 808080 " +
            "honeydew f0fff0 hotpink ff69b4 indianred cd5c5c indigo 4b0082 ivory fffff0 khaki f0e68c lavender e6e6fa " +
            "lavenderblush fff0f5 lawngreen 7cfc00 lemonchiffon fffacd lightblue add8e6 lightcoral f08080 lightcyan e0ffff " +
            "lightgoldenrodyellow fafad2 lightgray d3d3d3 lightgreen 90ee90 lightgrey d3d3d3 lightpink ffb6c1 " +
            "lightsalmon ffa07a lightseagreen 20b2aa lightskyblue 87cefa lightslategray 778899 lightslategrey 778899 " +
            "lightsteelblue b0c4de lightyellow ffffe0 lime 00ff00 limegreen 32cd32 linen faf0e6 magenta ff00ff maroon 800000 " +
            "mediumaquamarine 66cdaa mediumblue 0000cd mediumorchid ba55d3 mediumpurple 9370db mediumseagreen 3cb371 " +
            "mediumslateblue 7b68ee mediumspringgreen 00fa9a mediumturquoise 48d1cc mediumvioletred c71585 " +
            "midnightblue 191970 mintcream f5fffa mistyrose ffe4e1 moccasin ffe4b5 navajowhite ffdead navy 000080 " +
            "oldlace fdf5e6 olive 808000 olivedrab 6b8e23 orange ffa500 orangered ff4500 orchid da70d6 palegoldenrod eee8aa " +
            "palegreen 98fb98 paleturquoise afeeee palevioletred db7093 papayawhip ffefd5 peachpuff ffdab9 peru cd853f " +
            "pink ffc0cb plum dda0dd powderblue b0e0e6 purple 800080 rebeccapurple 663399 red ff0000 rosybrown bc8f8f " +
            "royalblue 4169e1 saddlebrown 8b4513 salmon fa8072 sandybrown f4a460 seagreen 2e8b57 seashell fff5ee " +
            "sienna a0522d silver c0c0c0 skyblue 87ceeb slateblue 6a5acd slategray 708090 slategrey 708090 snow fffafa " +
            "springgreen 00ff7f steelblue 4682b4 tan d2b48c teal 008080 thistle d8bfd8 tomato ff6347 turquoise 40e0d0 " +
            "violet ee82ee wheat f5deb3 white ffffff whitesmoke f5f5f5 yellow ffff00 yellowgreen 9acd32";
        var parts = table.Split(' ');
        var colors = new Dictionary<string, Rgb>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            int v = Convert.ToInt32(parts[i + 1], 16);
            colors[parts[i]] = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
        return colors;
    }
}
