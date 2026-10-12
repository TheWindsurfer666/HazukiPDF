namespace HazukiPdf.Html.Dom;

/// <summary>A node in the parsed HTML document tree.</summary>
internal abstract class HtmlNode
{
    public HtmlElement? Parent { get; internal set; }
}

/// <summary>A run of character data with entities already decoded.</summary>
internal sealed class HtmlText(string text) : HtmlNode
{
    public string Text { get; internal set; } = text;
}

/// <summary>An element with its attributes and children. Tag and attribute names are lower case.</summary>
internal sealed class HtmlElement(string tagName) : HtmlNode
{
    private string[]? _classes;

    public string TagName { get; } = tagName;

    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<HtmlNode> Children { get; } = [];

    public string? Id => GetAttribute("id");

    public string[] Classes => _classes ??= (GetAttribute("class") ?? "")
        .Split([' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries);

    public string? GetAttribute(string name) => Attributes.TryGetValue(name, out var value) ? value : null;

    public void AppendChild(HtmlNode node)
    {
        node.Parent = this;
        Children.Add(node);
    }

    public IEnumerable<HtmlElement> ChildElements => Children.OfType<HtmlElement>();

    public IEnumerable<HtmlElement> Descendants()
    {
        foreach (var child in ChildElements)
        {
            yield return child;
            foreach (var d in child.Descendants())
                yield return d;
        }
    }

    public HtmlElement? PreviousElementSibling
    {
        get
        {
            if (Parent is null)
                return null;
            HtmlElement? previous = null;
            foreach (var sibling in Parent.ChildElements)
            {
                if (ReferenceEquals(sibling, this))
                    return previous;
                previous = sibling;
            }
            return null;
        }
    }

    /// <summary>Concatenated text of all descendant text nodes.</summary>
    public string TextContent
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            AppendText(this, sb);
            return sb.ToString();

            static void AppendText(HtmlElement e, System.Text.StringBuilder sb)
            {
                foreach (var child in e.Children)
                {
                    if (child is HtmlText t)
                        sb.Append(t.Text);
                    else if (child is HtmlElement el)
                        AppendText(el, sb);
                }
            }
        }
    }

    public override string ToString() => $"<{TagName}>";
}
