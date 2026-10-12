using System.Text;

namespace HazukiPdf.Html.Css;

internal readonly record struct Declaration(string Property, string Value, bool Important);

internal sealed record StyleRule(Selector Selector, IReadOnlyList<Declaration> Declarations, int Order);

/// <summary>A parsed style sheet: style rules plus the declarations of any <c>@page</c> rules.</summary>
internal sealed class StyleSheet
{
    public List<StyleRule> Rules { get; } = [];

    public List<Declaration> PageDeclarations { get; } = [];

    /// <summary>
    /// Parses CSS text. Rules inside <c>@media</c> blocks are kept when the media query mentions
    /// <c>print</c> or <c>all</c>; other at-rules except <c>@page</c> are skipped.
    /// </summary>
    public static StyleSheet Parse(string css, ref int order)
    {
        var sheet = new StyleSheet();
        sheet.ParseBlock(StripComments(css), ref order);
        return sheet;
    }

    private void ParseBlock(string css, ref int order)
    {
        int i = 0;
        while (i < css.Length)
        {
            while (i < css.Length && (char.IsWhiteSpace(css[i]) || css[i] == ';'))
                i++;
            if (i >= css.Length)
                break;

            if (css[i] == '@')
            {
                int braceOrSemi = css.IndexOfAny(['{', ';'], i);
                if (braceOrSemi < 0)
                    break;
                string prelude = css[(i + 1)..braceOrSemi].Trim();
                if (css[braceOrSemi] == ';')
                {
                    i = braceOrSemi + 1; // @import, @charset, ...
                    continue;
                }
                int end = FindBlockEnd(css, braceOrSemi);
                string body = css[(braceOrSemi + 1)..end];
                string keyword = prelude.Split([' ', '\t', '\n', '\r', '('], 2)[0].ToLowerInvariant();
                if (keyword == "media")
                {
                    string query = prelude[5..].ToLowerInvariant();
                    if (query.Contains("print") || query.Contains("all") || query.Trim().Length == 0)
                        ParseBlock(body, ref order);
                }
                else if (keyword == "page")
                {
                    PageDeclarations.AddRange(ParseDeclarations(body));
                }
                i = Math.Min(end + 1, css.Length);
                continue;
            }

            int open = css.IndexOf('{', i);
            if (open < 0)
                break;
            int close = FindBlockEnd(css, open);
            string selectorText = css[i..open].Trim();
            var declarations = ParseDeclarations(css[(open + 1)..close]);
            if (declarations.Count > 0)
            {
                foreach (var selector in Selector.ParseList(selectorText))
                    Rules.Add(new StyleRule(selector, declarations, order++));
            }
            i = Math.Min(close + 1, css.Length);
        }
    }

    private static int FindBlockEnd(string css, int open)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = open; i < css.Length; i++)
        {
            char c = css[i];
            if (quote != '\0')
            {
                if (c == '\\')
                    i++;
                else if (c == quote)
                    quote = '\0';
                continue;
            }
            if (c is '"' or '\'')
                quote = c;
            else if (c == '{')
                depth++;
            else if (c == '}' && --depth == 0)
                return i;
        }
        return css.Length;
    }

    private static string StripComments(string css)
    {
        if (!css.Contains("/*", StringComparison.Ordinal))
            return css;
        var sb = new StringBuilder(css.Length);
        int i = 0;
        while (i < css.Length)
        {
            int start = css.IndexOf("/*", i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(css, i, css.Length - i);
                break;
            }
            sb.Append(css, i, start - i);
            int end = css.IndexOf("*/", start + 2, StringComparison.Ordinal);
            i = end < 0 ? css.Length : end + 2;
        }
        return sb.ToString();
    }

    /// <summary>Parses the body of a rule or a style attribute, expanding shorthand properties.</summary>
    public static List<Declaration> ParseDeclarations(string text)
    {
        var result = new List<Declaration>();
        foreach (string part in SplitDeclarations(StripComments(text)))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0)
                continue;
            string property = part[..colon].Trim().ToLowerInvariant();
            string value = part[(colon + 1)..].Trim();
            bool important = false;
            int bang = value.LastIndexOf('!');
            if (bang >= 0 && value[(bang + 1)..].Trim().Equals("important", StringComparison.OrdinalIgnoreCase))
            {
                important = true;
                value = value[..bang].Trim();
            }
            if (property.Length == 0 || value.Length == 0)
                continue;
            foreach (var (p, v) in Shorthands.Expand(property, value))
                result.Add(new Declaration(p, v, important));
        }
        return result;
    }

    private static IEnumerable<string> SplitDeclarations(string text)
    {
        int start = 0, depth = 0;
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                continue;
            }
            if (c is '"' or '\'')
                quote = c;
            else if (c == '(')
                depth++;
            else if (c == ')')
                depth--;
            else if (c == ';' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        if (start < text.Length)
            yield return text[start..];
    }
}
