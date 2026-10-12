namespace HazukiPdf.Html.Css;

/// <summary>Expands CSS shorthand properties into their longhand parts.</summary>
internal static class Shorthands
{
    private static readonly string[] Sides = ["top", "right", "bottom", "left"];

    private static readonly HashSet<string> BorderStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "hidden", "dotted", "dashed", "solid", "double", "groove", "ridge", "inset", "outset",
    };

    public static IEnumerable<(string Property, string Value)> Expand(string property, string value)
    {
        switch (property)
        {
            case "margin":
            case "padding":
                return Box(property + "-{0}", value);
            case "border-width":
                return Box("border-{0}-width", value);
            case "border-style":
                return Box("border-{0}-style", value);
            case "border-color":
                return Box("border-{0}-color", value);
            case "border":
                return Sides.SelectMany(side => Border("border-" + side, value));
            case "border-top" or "border-right" or "border-bottom" or "border-left":
                return Border(property, value);
            case "font":
                return Font(value);
            case "background":
                return Background(value);
            case "list-style":
                return ListStyle(value);
            case "text-decoration":
                return [("text-decoration-line", value)];
            case "border-spacing":
                return [(property, value)];
            case "page-break-before":
                return [("break-before", value)];
            case "page-break-after":
                return [("break-after", value)];
            case "page-break-inside":
                return [("break-inside", value)];
            case "inset" or "flex" or "grid" or "transition" or "animation":
                return [];
            default:
                return [(property, value)];
        }
    }

    private static IEnumerable<(string, string)> Box(string pattern, string value)
    {
        if (IsGlobalKeyword(value))
            return Sides.Select(s => (string.Format(pattern, s), value));
        var parts = CssValues.SplitWhitespace(value);
        string top, right, bottom, left;
        switch (parts.Count)
        {
            case 1:
                top = right = bottom = left = parts[0];
                break;
            case 2:
                top = bottom = parts[0];
                right = left = parts[1];
                break;
            case 3:
                top = parts[0];
                right = left = parts[1];
                bottom = parts[2];
                break;
            case 4:
                (top, right, bottom, left) = (parts[0], parts[1], parts[2], parts[3]);
                break;
            default:
                return [];
        }
        return
        [
            (string.Format(pattern, "top"), top),
            (string.Format(pattern, "right"), right),
            (string.Format(pattern, "bottom"), bottom),
            (string.Format(pattern, "left"), left),
        ];
    }

    private static IEnumerable<(string, string)> Border(string prefix, string value)
    {
        if (IsGlobalKeyword(value))
            return [(prefix + "-width", value), (prefix + "-style", value), (prefix + "-color", value)];
        string width = "medium", style = "none", color = "currentcolor";
        foreach (string token in CssValues.SplitWhitespace(value))
        {
            if (BorderStyles.Contains(token))
                style = token;
            else if (token is "thin" or "medium" or "thick" || CssValues.IsLength(token))
                width = token;
            else
                color = token;
        }
        return [(prefix + "-width", width), (prefix + "-style", style), (prefix + "-color", color)];
    }

    private static IEnumerable<(string, string)> Font(string value)
    {
        if (IsGlobalKeyword(value))
            return [("font-style", value), ("font-weight", value), ("font-size", value), ("line-height", value), ("font-family", value)];

        var tokens = CssValues.SplitWhitespace(value);
        string style = "normal", weight = "normal", lineHeight = "normal";
        int i = 0;
        for (; i < tokens.Count; i++)
        {
            string t = tokens[i].ToLowerInvariant();
            if (t is "italic" or "oblique")
                style = t;
            else if (t is "bold" or "bolder" or "lighter" || int.TryParse(t, out _))
                weight = t;
            else if (t is "normal" or "small-caps")
                continue;
            else
                break;
        }
        if (i >= tokens.Count)
            return [];
        string size = tokens[i++];
        int slash = size.IndexOf('/');
        if (slash >= 0)
        {
            lineHeight = size[(slash + 1)..];
            size = size[..slash];
            if (lineHeight.Length == 0 && i < tokens.Count)
                lineHeight = tokens[i++];
        }
        else if (i < tokens.Count && tokens[i].StartsWith('/'))
        {
            lineHeight = tokens[i].Length > 1 ? tokens[i][1..] : (i + 1 < tokens.Count ? tokens[++i] : "normal");
            i++;
        }
        string family = string.Join(" ", tokens.Skip(i));
        var result = new List<(string, string)>
        {
            ("font-style", style), ("font-weight", weight), ("font-size", size), ("line-height", lineHeight),
        };
        if (family.Length > 0)
            result.Add(("font-family", family));
        return result;
    }

    private static IEnumerable<(string, string)> Background(string value)
    {
        if (IsGlobalKeyword(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
            return [("background-color", value == "none" ? "transparent" : value)];
        foreach (string token in CssValues.SplitWhitespace(value))
        {
            if (CssValues.IsColor(token))
                return [("background-color", token)];
        }
        return [("background-color", "transparent")];
    }

    private static IEnumerable<(string, string)> ListStyle(string value)
    {
        foreach (string token in CssValues.SplitWhitespace(value))
        {
            if (token is not ("inside" or "outside") && !token.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
                return [("list-style-type", token)];
        }
        return [];
    }

    private static bool IsGlobalKeyword(string value) =>
        value.Trim().ToLowerInvariant() is "inherit" or "initial" or "unset";
}
