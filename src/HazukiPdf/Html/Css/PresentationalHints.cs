using HazukiPdf.Html.Dom;

namespace HazukiPdf.Html.Css;

/// <summary>
/// Maps legacy presentational attributes (align, bgcolor, width, border, cellpadding, ...) to CSS,
/// as browsers do. Author style sheets override these.
/// </summary>
internal static class PresentationalHints
{
    public static IEnumerable<(string Property, string Value)> For(HtmlElement e)
    {
        string tag = e.TagName;

        if (e.GetAttribute("align") is { } align)
        {
            string a = align.Trim().ToLowerInvariant();
            if (tag == "table" && a == "center")
            {
                yield return ("margin-left", "auto");
                yield return ("margin-right", "auto");
            }
            else if (tag is not ("img" or "table") && a is "left" or "right" or "center" or "justify")
            {
                yield return ("text-align", a);
            }
        }

        if (e.GetAttribute("valign") is { } valign && tag is "td" or "th" or "tr" or "tbody" or "thead" or "tfoot")
            yield return ("vertical-align", valign.Trim().ToLowerInvariant());

        if (e.GetAttribute("bgcolor") is { } bgcolor)
            yield return ("background-color", bgcolor);

        if (tag is "img" or "table" or "td" or "th" or "hr" or "col")
        {
            if (Dimension(e.GetAttribute("width")) is { } width)
                yield return ("width", width);
            if (tag is not "hr" && Dimension(e.GetAttribute("height")) is { } height)
                yield return ("height", height);
        }

        if (tag == "font")
        {
            if (e.GetAttribute("color") is { } color)
                yield return ("color", color);
            if (e.GetAttribute("face") is { } face)
                yield return ("font-family", face);
            if (e.GetAttribute("size") is { } size && FontSizeFromAttribute(size) is { } fontSize)
                yield return ("font-size", fontSize);
        }

        if (tag == "body" && e.GetAttribute("text") is { } text)
            yield return ("color", text);

        if (tag == "table")
        {
            if (e.GetAttribute("border") is { } border)
            {
                int width = int.TryParse(border, out int w) ? w : 1;
                if (width > 0)
                    yield return ("border", $"{width}px outset #808080");
            }
            if (e.GetAttribute("cellspacing") is { } spacing && int.TryParse(spacing, out int s))
                yield return ("border-spacing", $"{s}px");
        }

        if (tag is "td" or "th" && FindTable(e) is { } table)
        {
            if (table.GetAttribute("border") is { } border && (!int.TryParse(border, out int w) || w > 0))
                yield return ("border", "1px inset #808080");
            if (table.GetAttribute("cellpadding") is { } padding && int.TryParse(padding, out int p))
                yield return ("padding", $"{p}px");
        }

        if (tag == "td" && e.GetAttribute("nowrap") is not null)
            yield return ("white-space", "nowrap");

        if (tag is "ul" or "ol" && e.GetAttribute("type") is { } type)
        {
            string? listStyle = type switch
            {
                "1" => "decimal",
                "a" => "lower-alpha",
                "A" => "upper-alpha",
                "i" => "lower-roman",
                "I" => "upper-roman",
                _ => type.ToLowerInvariant() is "disc" or "circle" or "square" ? type.ToLowerInvariant() : null,
            };
            if (listStyle is not null)
                yield return ("list-style-type", listStyle);
        }
    }

    private static string? Dimension(string? value)
    {
        if (value is null)
            return null;
        value = value.Trim();
        if (value.EndsWith('%'))
            return value;
        return double.TryParse(value.TrimEnd('p', 'x'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double n) ? $"{n.ToString(System.Globalization.CultureInfo.InvariantCulture)}px" : null;
    }

    private static string? FontSizeFromAttribute(string value)
    {
        value = value.Trim();
        int size;
        if (value.StartsWith('+') || value.StartsWith('-'))
            size = int.TryParse(value, out int delta) ? 3 + delta : 3;
        else if (!int.TryParse(value, out size))
            return null;
        return Math.Clamp(size, 1, 7) switch
        {
            1 => "x-small",
            2 => "small",
            3 => "medium",
            4 => "large",
            5 => "x-large",
            6 => "xx-large",
            _ => "xxx-large",
        };
    }

    private static HtmlElement? FindTable(HtmlElement cell)
    {
        for (var p = cell.Parent; p is not null; p = p.Parent)
        {
            if (p.TagName == "table")
                return p;
        }
        return null;
    }
}
