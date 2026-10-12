using System.Net;
using System.Text;

namespace HazukiPdf.Html.Dom;

/// <summary>
/// A forgiving HTML parser. It handles the markup people actually write (unclosed paragraphs and
/// list items, implicit table sections, void elements, comments, raw-text script/style) without
/// implementing the full HTML5 tree-construction algorithm.
/// </summary>
internal static class HtmlParser
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawTextElements = new(StringComparer.Ordinal)
    {
        "script", "style", "title", "textarea", "xmp",
    };

    /// <summary>Start tags that implicitly close an open &lt;p&gt;.</summary>
    private static readonly HashSet<string> ClosesParagraph = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "center", "details", "div", "dl", "fieldset", "figcaption",
        "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "main", "nav", "ol", "p",
        "pre", "section", "table", "ul",
    };

    /// <summary>Elements that stop the search for an open element to auto-close.</summary>
    private static readonly HashSet<string> ScopeBoundaries = new(StringComparer.Ordinal)
    {
        "html", "body", "table", "td", "th", "caption", "button", "blockquote", "div", "li", "dd", "dt", "section",
        "article", "aside", "header", "footer", "main", "nav", "figure",
    };

    /// <summary>Elements nested deeper than this are flattened, so hostile input cannot exhaust the stack.</summary>
    private const int MaxDepth = 256;

    public static HtmlElement Parse(string html)
    {
        var root = new HtmlElement("#document");
        var stack = new List<HtmlElement> { root };
        int pos = 0;
        var text = new StringBuilder();

        while (pos < html.Length)
        {
            char c = html[pos];
            if (c == '<' && pos + 1 < html.Length)
            {
                char next = html[pos + 1];
                if (next == '!')
                {
                    FlushText();
                    pos = SkipMarkupDeclaration(html, pos);
                    continue;
                }
                if (next == '?')
                {
                    FlushText();
                    pos = SkipTo(html, pos, ">");
                    continue;
                }
                if (next == '/' && pos + 2 < html.Length && char.IsAsciiLetter(html[pos + 2]))
                {
                    FlushText();
                    pos = ReadEndTag(html, pos, stack);
                    continue;
                }
                if (char.IsAsciiLetter(next))
                {
                    FlushText();
                    pos = ReadStartTag(html, pos, stack);
                    continue;
                }
            }
            text.Append(c);
            pos++;
        }
        FlushText();
        return root;

        void FlushText()
        {
            if (text.Length == 0)
                return;
            string decoded = WebUtility.HtmlDecode(text.ToString());
            text.Clear();
            var parent = stack[^1];
            if (parent.TagName is "table" or "tbody" or "thead" or "tfoot" or "tr" && string.IsNullOrWhiteSpace(decoded))
                return; // whitespace between table parts carries no meaning
            AppendText(parent, decoded);
        }
    }

    private static void AppendText(HtmlElement parent, string text)
    {
        if (parent.Children.Count > 0 && parent.Children[^1] is HtmlText last)
            last.Text += text;
        else
            parent.AppendChild(new HtmlText(text));
    }

    private static int SkipMarkupDeclaration(string html, int pos)
    {
        if (string.CompareOrdinal(html, pos, "<!--", 0, 4) == 0)
        {
            int end = html.IndexOf("-->", pos + 4, StringComparison.Ordinal);
            return end < 0 ? html.Length : end + 3;
        }
        if (string.CompareOrdinal(html, pos, "<![CDATA[", 0, 9) == 0)
        {
            int end = html.IndexOf("]]>", pos + 9, StringComparison.Ordinal);
            return end < 0 ? html.Length : end + 3;
        }
        return SkipTo(html, pos, ">");
    }

    private static int SkipTo(string html, int pos, string terminator)
    {
        int end = html.IndexOf(terminator, pos, StringComparison.Ordinal);
        return end < 0 ? html.Length : end + terminator.Length;
    }

    private static int ReadEndTag(string html, int pos, List<HtmlElement> stack)
    {
        int start = pos + 2;
        int i = start;
        while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>' && html[i] != '/')
            i++;
        string name = html[start..i].ToLowerInvariant();
        int end = html.IndexOf('>', i);
        pos = end < 0 ? html.Length : end + 1;

        if (name == "br")
        {
            // </br> is treated as <br> by browsers.
            stack[^1].AppendChild(new HtmlElement("br"));
            return pos;
        }

        for (int s = stack.Count - 1; s > 0; s--)
        {
            if (stack[s].TagName == name)
            {
                stack.RemoveRange(s, stack.Count - s);
                break;
            }
            // An end tag never closes past a table boundary it does not belong to.
            if (stack[s].TagName is "table" && name is not ("table"))
                break;
        }
        return pos;
    }

    private static int ReadStartTag(string html, int pos, List<HtmlElement> stack)
    {
        int i = pos + 1;
        int nameStart = i;
        while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>' && html[i] != '/')
            i++;
        var element = new HtmlElement(html[nameStart..i].ToLowerInvariant());
        bool selfClosing = false;

        // Attributes
        while (i < html.Length)
        {
            while (i < html.Length && char.IsWhiteSpace(html[i]))
                i++;
            if (i >= html.Length)
                break;
            if (html[i] == '>')
            {
                i++;
                break;
            }
            if (html[i] == '/')
            {
                selfClosing = true;
                i++;
                continue;
            }

            int attrStart = i;
            while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>' && html[i] != '=' &&
                   !(html[i] == '/' && i + 1 < html.Length && html[i + 1] == '>'))
                i++;
            string attrName = html[attrStart..i].ToLowerInvariant();
            if (attrName.Length == 0)
            {
                i++; // stray character such as a lone '='
                continue;
            }
            while (i < html.Length && char.IsWhiteSpace(html[i]))
                i++;

            string value = "";
            if (i < html.Length && html[i] == '=')
            {
                i++;
                while (i < html.Length && char.IsWhiteSpace(html[i]))
                    i++;
                if (i < html.Length && html[i] is '"' or '\'')
                {
                    char quote = html[i];
                    int close = html.IndexOf(quote, i + 1);
                    if (close < 0)
                        close = html.Length;
                    value = html[(i + 1)..close];
                    i = Math.Min(close + 1, html.Length);
                }
                else
                {
                    int valueStart = i;
                    while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>')
                        i++;
                    value = html[valueStart..i];
                }
            }
            element.Attributes.TryAdd(attrName, WebUtility.HtmlDecode(value));
        }

        string tag = element.TagName;
        ApplyImplicitEndTags(tag, stack);
        stack[^1].AppendChild(element);

        if (VoidElements.Contains(tag) || selfClosing && tag is not ("script" or "style"))
            return i;

        if (RawTextElements.Contains(tag))
        {
            int close = IndexOfEndTag(html, i, tag);
            string raw = close < 0 ? html[i..] : html[i..close];
            if (raw.Length > 0)
                element.AppendChild(new HtmlText(tag is "title" or "textarea" ? WebUtility.HtmlDecode(raw) : raw));
            if (close < 0)
                return html.Length;
            int gt = html.IndexOf('>', close);
            return gt < 0 ? html.Length : gt + 1;
        }

        if (stack.Count < MaxDepth)
            stack.Add(element);
        return i;
    }

    private static int IndexOfEndTag(string html, int from, string tag)
    {
        int i = from;
        while (true)
        {
            int lt = html.IndexOf("</", i, StringComparison.Ordinal);
            if (lt < 0)
                return -1;
            if (string.Compare(html, lt + 2, tag, 0, tag.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                int after = lt + 2 + tag.Length;
                if (after >= html.Length || html[after] is '>' or '/' || char.IsWhiteSpace(html[after]))
                    return lt;
            }
            i = lt + 2;
        }
    }

    /// <summary>Closes elements that the new start tag implicitly ends, as browsers do.</summary>
    private static void ApplyImplicitEndTags(string tag, List<HtmlElement> stack)
    {
        if (ClosesParagraph.Contains(tag))
            CloseIfOpen(stack, "p", ScopeBoundaries);

        switch (tag)
        {
            case "li":
                CloseIfOpen(stack, "li", ["ul", "ol", "menu", "table", "td", "th"]);
                break;
            case "dt" or "dd":
                CloseIfOpen(stack, "dt", ["dl", "table"]);
                CloseIfOpen(stack, "dd", ["dl", "table"]);
                break;
            case "td" or "th":
                CloseIfOpen(stack, "td", ["tr", "table"]);
                CloseIfOpen(stack, "th", ["tr", "table"]);
                EnsureRow(stack);
                break;
            case "tr":
                CloseIfOpen(stack, "tr", ["table", "thead", "tbody", "tfoot"]);
                break;
            case "thead" or "tbody" or "tfoot":
                CloseIfOpen(stack, "thead", ["table"]);
                CloseIfOpen(stack, "tbody", ["table"]);
                CloseIfOpen(stack, "tfoot", ["table"]);
                break;
            case "option":
                CloseIfOpen(stack, "option", ["select"]);
                break;
        }
    }

    /// <summary>A cell opened directly inside a table or table section gets an implicit &lt;tr&gt;.</summary>
    private static void EnsureRow(List<HtmlElement> stack)
    {
        var top = stack[^1];
        if (top.TagName is "table" or "thead" or "tbody" or "tfoot")
        {
            var row = new HtmlElement("tr");
            top.AppendChild(row);
            stack.Add(row);
        }
    }

    private static void CloseIfOpen(List<HtmlElement> stack, string tag, ICollection<string> boundaries)
    {
        for (int s = stack.Count - 1; s > 0; s--)
        {
            string name = stack[s].TagName;
            if (name == tag)
            {
                stack.RemoveRange(s, stack.Count - s);
                return;
            }
            if (boundaries.Contains(name))
                return;
        }
    }
}
