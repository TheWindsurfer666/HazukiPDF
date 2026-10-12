using System.Text;
using HazukiPdf.Html.Css;
using HazukiPdf.Html.Dom;
using HazukiPdf.Html.Images;

namespace HazukiPdf.Html.Layout;

/// <summary>Turns the styled DOM into a tree of block, table and inline boxes.</summary>
internal sealed class BoxBuilder(StyleResolver styles, ImageLoader images)
{
    /// <summary>Builds the box for the document root, using <paramref name="rootStyle"/> for it.</summary>
    public BlockBox BuildRoot(HtmlElement root, ComputedStyle rootStyle)
    {
        var box = new BlockBox(rootStyle, root.Id);
        FillBlock(box, root, null);
        return box;
    }

    private BlockBox BuildBlock(HtmlElement e, ComputedStyle style, string? link, int ordinal = 1)
    {
        var box = new BlockBox(style, e.Id);
        if (style.Display == Display.ListItem)
            box.Marker = CreateMarker(style, ordinal);
        FillBlock(box, e, link);
        return box;
    }

    private void FillBlock(BlockBox box, HtmlElement e, string? link)
    {
        var state = new BlockState(box);
        AddChildren(e, box.Style, state, link);
        state.Finish();
    }

    private void AddChildren(HtmlElement parent, ComputedStyle parentStyle, BlockState state, string? link)
    {
        // Ordered list numbering.
        bool isList = parent.TagName is "ol" or "ul" or "menu" or "dir";
        int counter = 1, step = 1;
        if (parent.TagName == "ol")
        {
            bool reversed = parent.GetAttribute("reversed") is not null;
            int count = parent.ChildElements.Count(c => styles.GetStyle(c).Display == Display.ListItem);
            counter = int.TryParse(parent.GetAttribute("start"), out int start) ? start : reversed ? count : 1;
            step = reversed ? -1 : 1;
        }

        bool first = true;
        foreach (var node in parent.Children)
        {
            if (node is HtmlText text)
            {
                string value = text.Text;
                if (first && parent.TagName is "pre" or "textarea" or "listing" && value.StartsWith('\n'))
                    value = value[1..];
                AddText(value, parentStyle, state, link);
            }
            else if (node is HtmlElement child)
            {
                var style = styles.GetStyle(child);
                if (style.Display == Display.ListItem && isList)
                {
                    if (int.TryParse(child.GetAttribute("value"), out int value))
                        counter = value;
                    AddElement(child, style, state, link, counter);
                    counter += step;
                }
                else
                {
                    AddElement(child, style, state, link, 1);
                }
            }
            first = false;
        }
    }

    private void AddElement(HtmlElement e, ComputedStyle style, BlockState state, string? link, int ordinal)
    {
        if (style.Display == Display.None)
            return;

        if (e.TagName == "a" && e.GetAttribute("href") is { Length: > 0 } href)
            link = href.Trim();

        switch (style.Display)
        {
            case Display.Inline or Display.InlineBlock:
                if (e.Id is { } id)
                    state.AddInline(new AnchorItem(id, style));
                if (e.TagName == "a" && e.GetAttribute("name") is { } name)
                    state.AddInline(new AnchorItem(name, style));
                switch (e.TagName)
                {
                    case "br":
                        state.AddInline(new LineBreakItem(style));
                        state.LastWasSpace = true;
                        return;
                    case "img":
                        AddImage(e, style, state, link);
                        return;
                    case "wbr":
                        return;
                    case "q":
                        AddText("“", style, state, link);
                        AddChildren(e, style, state, link);
                        AddText("”", style, state, link);
                        return;
                }
                AddChildren(e, style, state, link);
                return;

            case Display.Table:
                state.AddBlock(BuildTable(e, style, link));
                return;

            default:
                // Block-level, including table parts found outside a table.
                state.AddBlock(BuildBlock(e, style, link, ordinal));
                return;
        }
    }

    private void AddImage(HtmlElement e, ComputedStyle style, BlockState state, string? link)
    {
        var image = e.GetAttribute("src") is { } src ? images.Load(src) : null;
        if (image is not null)
        {
            state.AddInline(new ImageItem(image, style, link));
            state.LastWasSpace = false;
        }
        else if (e.GetAttribute("alt") is { Length: > 0 } alt)
        {
            AddText(alt, style, state, link);
        }
    }

    private void AddText(string text, ComputedStyle style, BlockState state, string? link)
    {
        text = CleanCharacters(text);
        switch (style.WhiteSpace)
        {
            case WhiteSpace.Pre or WhiteSpace.PreWrap:
            {
                var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0)
                    {
                        state.AddInline(new LineBreakItem(style));
                        state.Column = 0;
                    }
                    string line = ExpandTabs(lines[i], ref state.Column);
                    if (line.Length > 0)
                        state.AddInline(new TextItem(line, style, link));
                }
                state.LastWasSpace = false;
                break;
            }
            case WhiteSpace.PreLine:
            {
                var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0)
                    {
                        state.AddInline(new LineBreakItem(style));
                        state.LastWasSpace = true;
                    }
                    AddCollapsed(lines[i], style, state, link);
                }
                break;
            }
            default:
                AddCollapsed(text, style, state, link);
                break;
        }
    }

    private static void AddCollapsed(string text, ComputedStyle style, BlockState state, string? link)
    {
        var sb = new StringBuilder(text.Length);
        bool space = state.LastWasSpace;
        foreach (char c in text)
        {
            if (c is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                if (!space)
                    sb.Append(' ');
                space = true;
            }
            else
            {
                sb.Append(c);
                space = false;
            }
        }
        state.LastWasSpace = space;
        if (sb.Length > 0)
            state.AddInline(new TextItem(sb.ToString(), style, link));
    }

    private static string ExpandTabs(string line, ref int column)
    {
        if (!line.Contains('\t'))
        {
            column += line.Length;
            return line;
        }
        var sb = new StringBuilder();
        foreach (char c in line)
        {
            if (c == '\t')
            {
                int spaces = 8 - column % 8;
                sb.Append(' ', spaces);
                column += spaces;
            }
            else
            {
                sb.Append(c);
                column++;
            }
        }
        return sb.ToString();
    }

    /// <summary>Removes invisible characters and replaces characters outside the BMP.</summary>
    private static string CleanCharacters(string text)
    {
        bool clean = true;
        foreach (char c in text)
        {
            if (c is '­' or '​' or '‌' or '‍' or '﻿' || char.IsSurrogate(c))
            {
                clean = false;
                break;
            }
        }
        if (clean)
            return text;

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '­' or '​' or '‌' or '‍' or '﻿')
                continue;
            if (char.IsHighSurrogate(c))
            {
                sb.Append('?');
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    i++;
                continue;
            }
            sb.Append(char.IsLowSurrogate(c) ? '?' : c);
        }
        return sb.ToString();
    }

    private TableBox BuildTable(HtmlElement e, ComputedStyle style, string? link)
    {
        var table = new TableBox(style, e.Id);
        TableRowBox? anonymousRow = null;
        foreach (var child in e.ChildElements)
        {
            var childStyle = styles.GetStyle(child);
            switch (childStyle.Display)
            {
                case Display.TableCaption:
                    table.Caption ??= BuildBlock(child, childStyle, link);
                    break;
                case Display.TableHeaderGroup when table.HeaderRows.Count == 0:
                    AddRows(child, table.HeaderRows, link);
                    break;
                case Display.TableFooterGroup when table.FooterRows.Count == 0:
                    AddRows(child, table.FooterRows, link);
                    break;
                case Display.TableRowGroup or Display.TableHeaderGroup or Display.TableFooterGroup:
                    AddRows(child, table.BodyRows, link);
                    break;
                case Display.TableRow:
                    anonymousRow = null;
                    table.BodyRows.Add(BuildRow(child, childStyle, link));
                    break;
                case Display.TableCell:
                    if (anonymousRow is null)
                    {
                        anonymousRow = new TableRowBox(style.CreateChild());
                        table.BodyRows.Add(anonymousRow);
                    }
                    anonymousRow.Cells.Add(BuildCell(child, childStyle, link));
                    break;
            }
        }
        return table;
    }

    private void AddRows(HtmlElement group, List<TableRowBox> rows, string? link)
    {
        foreach (var child in group.ChildElements)
        {
            var style = styles.GetStyle(child);
            if (style.Display == Display.TableRow)
                rows.Add(BuildRow(child, style, link));
        }
    }

    private TableRowBox BuildRow(HtmlElement tr, ComputedStyle style, string? link)
    {
        var row = new TableRowBox(style);
        foreach (var child in tr.ChildElements)
        {
            var cellStyle = styles.GetStyle(child);
            if (cellStyle.Display == Display.TableCell)
                row.Cells.Add(BuildCell(child, cellStyle, link));
        }
        return row;
    }

    private TableCellBox BuildCell(HtmlElement e, ComputedStyle style, string? link)
    {
        int colSpan = int.TryParse(e.GetAttribute("colspan"), out int span) ? Math.Clamp(span, 1, 1000) : 1;
        var cell = new TableCellBox(style, colSpan, e.Id);
        FillBlock(cell, e, link);
        return cell;
    }

    private static ListMarker? CreateMarker(ComputedStyle style, int ordinal)
    {
        string type = style.ListStyleType;
        return type switch
        {
            "none" => null,
            "disc" => new ListMarker(null, MarkerShape.Disc, style),
            "circle" => new ListMarker(null, MarkerShape.Circle, style),
            "square" => new ListMarker(null, MarkerShape.Square, style),
            "decimal-leading-zero" => new ListMarker(ordinal.ToString("00") + ".", MarkerShape.None, style),
            "lower-alpha" or "lower-latin" => new ListMarker(Alpha(ordinal, 'a') + ".", MarkerShape.None, style),
            "upper-alpha" or "upper-latin" => new ListMarker(Alpha(ordinal, 'A') + ".", MarkerShape.None, style),
            "lower-roman" => new ListMarker(Roman(ordinal).ToLowerInvariant() + ".", MarkerShape.None, style),
            "upper-roman" => new ListMarker(Roman(ordinal) + ".", MarkerShape.None, style),
            _ => type.Length > 2 && type[0] is '"' or '\'' && type[^1] == type[0]
                ? new ListMarker(type[1..^1], MarkerShape.None, style)
                : new ListMarker(ordinal + ".", MarkerShape.None, style),
        };
    }

    internal static string Alpha(int n, char first)
    {
        if (n <= 0)
            return n.ToString();
        var sb = new StringBuilder();
        while (n > 0)
        {
            n--;
            sb.Insert(0, (char)(first + n % 26));
            n /= 26;
        }
        return sb.ToString();
    }

    internal static string Roman(int n)
    {
        if (n is <= 0 or >= 4000)
            return n.ToString();
        (int Value, string Symbol)[] table =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
            (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
        ];
        var sb = new StringBuilder();
        foreach (var (value, symbol) in table)
        {
            while (n >= value)
            {
                sb.Append(symbol);
                n -= value;
            }
        }
        return sb.ToString();
    }

    /// <summary>Collects a block's children, grouping runs of inline content.</summary>
    private sealed class BlockState(BlockBox box)
    {
        private readonly List<object> _children = [];
        private InlineContent? _run;

        public bool LastWasSpace = true;
        public int Column;

        public void AddInline(InlineItem item)
        {
            _run ??= new InlineContent();
            _run.Items.Add(item);
        }

        public void AddBlock(object block)
        {
            FlushRun();
            _children.Add(block);
            LastWasSpace = true;
        }

        private void FlushRun()
        {
            if (_run is not null && HasContent(_run))
                _children.Add(_run);
            _run = null;
        }

        private static bool HasContent(InlineContent run) =>
            run.Items.Any(i => i is not TextItem t || t.Text.Any(c => c != ' ')) || run.Items.Any(i => i is TextItem t && t.Style.WhiteSpace is WhiteSpace.Pre or WhiteSpace.PreWrap);

        public void Finish()
        {
            FlushRun();
            if (_children.Count == 1 && _children[0] is InlineContent only)
            {
                box.Inline = only;
                return;
            }
            foreach (var child in _children)
            {
                if (child is InlineContent run)
                {
                    var anonymousStyle = box.Style.CreateChild();
                    anonymousStyle.Display = Display.Block;
                    box.Children.Add(new BlockBox(anonymousStyle) { Inline = run });
                }
                else
                {
                    box.Children.Add(child);
                }
            }
        }
    }
}
