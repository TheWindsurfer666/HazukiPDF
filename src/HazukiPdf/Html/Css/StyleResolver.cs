using HazukiPdf.Html.Dom;
using HazukiPdf.Html.Fonts;

namespace HazukiPdf.Html.Css;

/// <summary>Runs the cascade for every element and computes its style.</summary>
internal sealed class StyleResolver
{
    private const int UserAgentOrigin = 0;
    private const int HintOrigin = 1;
    private const int AuthorOrigin = 2;
    private const int InlineOrigin = 3;

    // Rules are bucketed by the tag their selector ends in, so each element only tests rules that can match it.
    private readonly Dictionary<string, List<(StyleRule Rule, int Origin)>> _rulesByTag = new(StringComparer.Ordinal);
    private readonly List<(StyleRule Rule, int Origin)> _anyTagRules = [];
    private readonly Dictionary<HtmlElement, ComputedStyle> _styles = [];
    private readonly double _rootFontSize;

    public StyleResolver(IEnumerable<StyleSheet> authorSheets, double defaultFontSize)
    {
        int order = 0;
        foreach (var rule in StyleSheet.Parse(UserAgentStyleSheet.Css, ref order).Rules)
            AddRule(rule, UserAgentOrigin);
        foreach (var sheet in authorSheets)
        {
            foreach (var rule in sheet.Rules)
                AddRule(rule, AuthorOrigin);
        }
        _rootFontSize = defaultFontSize;
    }

    private void AddRule(StyleRule rule, int origin)
    {
        if (rule.Selector.SubjectTag is not { } tag)
        {
            _anyTagRules.Add((rule, origin));
            return;
        }
        if (!_rulesByTag.TryGetValue(tag, out var list))
            _rulesByTag[tag] = list = [];
        list.Add((rule, origin));
    }

    public ComputedStyle GetStyle(HtmlElement element) => _styles[element];

    /// <summary>Computes styles for the element and all its descendants.</summary>
    public void Resolve(HtmlElement element, ComputedStyle parentStyle)
    {
        var style = Compute(element, parentStyle);
        _styles[element] = style;
        foreach (var child in element.ChildElements)
            Resolve(child, style);
    }

    private ComputedStyle Compute(HtmlElement element, ComputedStyle parent)
    {
        // Sequence keeps declarations of one rule in source order, since List.Sort is not stable.
        var matched = new List<(Declaration Decl, int Important, int Origin, int Specificity, int Order, int Sequence)>();
        int sequence = 0;
        var candidates = _rulesByTag.TryGetValue(element.TagName, out var tagged) ? tagged.Concat(_anyTagRules) : _anyTagRules;
        foreach (var (rule, origin) in candidates)
        {
            if (!rule.Selector.Matches(element))
                continue;
            foreach (var decl in rule.Declarations)
                matched.Add((decl, decl.Important ? 1 : 0, origin, rule.Selector.Specificity, rule.Order, sequence++));
        }

        int hintOrder = 0;
        foreach (var (hintProperty, hintValue) in PresentationalHints.For(element))
        {
            foreach (var (property, value) in Shorthands.Expand(hintProperty, hintValue))
                matched.Add((new Declaration(property, value, false), 0, HintOrigin, 0, hintOrder++, sequence++));
        }

        if (element.GetAttribute("style") is { } inline)
        {
            int inlineOrder = 0;
            foreach (var decl in StyleSheet.ParseDeclarations(inline))
                matched.Add((decl, decl.Important ? 1 : 0, InlineOrigin, 0, inlineOrder++, sequence++));
        }

        matched.Sort((a, b) =>
        {
            int c = a.Important.CompareTo(b.Important);
            if (c == 0) c = a.Origin.CompareTo(b.Origin);
            if (c == 0) c = a.Specificity.CompareTo(b.Specificity);
            if (c == 0) c = a.Order.CompareTo(b.Order);
            if (c == 0) c = a.Sequence.CompareTo(b.Sequence);
            return c;
        });

        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in matched)
            declared[m.Decl.Property] = m.Decl.Value.Trim();

        var style = parent.CreateChild();
        bool parentIsInline = parent.Display is Display.Inline;

        // Font size first: em units elsewhere depend on it.
        if (declared.TryGetValue("font-size", out var fontSize))
            style.FontSize = ResolveFontSize(fontSize, parent.FontSize);

        // currentcolor in borders refers to the element's own color.
        if (declared.TryGetValue("color", out var color))
            style.Color = ResolveInheritedColor(color, parent.Color);

        if (declared.TryGetValue("display", out var display))
            style.Display = ParseDisplay(display, Display.Inline);

        foreach (var (property, value) in declared)
            Apply(style, parent, property, value.ToLowerInvariant() == "inherit" ? null : value);
        ApplyBorders(style, parent, declared);

        // Inline elements carry decoration, raised baselines and backgrounds down to their text.
        if (style.Display == Display.Inline)
        {
            if (!parentIsInline && !declared.ContainsKey("vertical-align"))
                style.BaselineShift = 0;
            style.InlineBackground = style.BackgroundColor ?? (parentIsInline ? parent.InlineBackground : null);
        }
        else
        {
            style.BaselineShift = 0;
            style.InlineBackground = null;
        }
        return style;
    }

    private void Apply(ComputedStyle s, ComputedStyle parent, string property, string? value)
    {
        // value == null means "inherit".
        string v = value?.ToLowerInvariant() ?? "";
        if (v is "initial" or "unset")
            v = value = InitialValue(property, parent);
        if (v == "inherit")
            value = null;

        switch (property)
        {
            case "font-family":
                s.FontFamily = value is null ? parent.FontFamily : ParseFontFamily(value, s.FontFamily);
                break;
            case "font-weight":
                s.Bold = value is null ? parent.Bold : v switch
                {
                    "bold" or "bolder" => true,
                    "normal" or "lighter" => false,
                    _ => int.TryParse(v, out int w) ? w >= 600 : s.Bold,
                };
                break;
            case "font-style":
                s.Italic = value is null ? parent.Italic : v is "italic" or "oblique";
                break;
            case "font-size":
                if (value is null)
                    s.FontSize = parent.FontSize;
                break;
            case "line-height":
                if (value is null)
                {
                    s.LineHeightFactor = parent.LineHeightFactor;
                    s.LineHeightAbsolute = parent.LineHeightAbsolute;
                }
                else if (v == "normal")
                {
                    s.LineHeightFactor = null;
                    s.LineHeightAbsolute = null;
                }
                else if (CssValues.TryParseNumber(v, out double factor))
                {
                    s.LineHeightFactor = factor;
                    s.LineHeightAbsolute = null;
                }
                else if (CssValues.TryParseLength(v, s.FontSize, _rootFontSize, s.FontSize, out double lh))
                {
                    s.LineHeightAbsolute = lh;
                    s.LineHeightFactor = null;
                }
                break;
            case "color":
                if (value is null)
                    s.Color = parent.Color;
                break;
            case "background-color":
                if (value is null)
                    s.BackgroundColor = parent.BackgroundColor;
                else if (CssValues.TryParseColor(ResolveCurrentColor(v, s), out var bg, out bool transparent))
                    s.BackgroundColor = transparent ? null : bg;
                break;
            case "text-align":
                s.TextAlign = value is null ? parent.TextAlign : v switch
                {
                    "right" or "end" or "-webkit-right" => TextAlign.Right,
                    "center" or "-webkit-center" => TextAlign.Center,
                    "justify" => TextAlign.Justify,
                    _ => TextAlign.Left,
                };
                break;
            case "text-indent":
                s.TextIndent = value is null ? parent.TextIndent : ParseLength(v, s, s.TextIndent);
                break;
            case "text-transform":
                s.TextTransform = value is null ? parent.TextTransform : v switch
                {
                    "uppercase" => TextTransform.Uppercase,
                    "lowercase" => TextTransform.Lowercase,
                    "capitalize" => TextTransform.Capitalize,
                    _ => TextTransform.None,
                };
                break;
            case "white-space":
                s.WhiteSpace = value is null ? parent.WhiteSpace : v switch
                {
                    "nowrap" => WhiteSpace.NoWrap,
                    "pre" => WhiteSpace.Pre,
                    "pre-wrap" or "break-spaces" => WhiteSpace.PreWrap,
                    "pre-line" => WhiteSpace.PreLine,
                    _ => WhiteSpace.Normal,
                };
                break;
            case "letter-spacing":
                s.LetterSpacing = value is null ? parent.LetterSpacing
                    : v == "normal" ? 0 : CssValues.TryParseLength(v, s.FontSize, _rootFontSize, 0, out double ls) ? ls : s.LetterSpacing;
                break;
            case "word-spacing":
                s.WordSpacing = value is null ? parent.WordSpacing
                    : v == "normal" ? 0 : CssValues.TryParseLength(v, s.FontSize, _rootFontSize, 0, out double ws) ? ws : s.WordSpacing;
                break;
            case "text-decoration-line":
                if (value is null)
                    break; // decorations already propagate from the parent
                foreach (string token in CssValues.SplitWhitespace(v))
                {
                    if (token == "underline") s.Underline = true;
                    else if (token == "line-through") s.LineThrough = true;
                    else if (token == "overline") s.Overline = true;
                }
                break;
            case "vertical-align":
                ApplyVerticalAlign(s, parent, value is null ? parent.VerticalAlign.ToString().ToLowerInvariant() : v);
                break;
            case "list-style-type":
                s.ListStyleType = value is null ? parent.ListStyleType : v;
                break;
            case "border-collapse":
                s.BorderCollapse = value is null ? parent.BorderCollapse : v == "collapse";
                break;
            case "border-spacing":
                if (value is null)
                    s.BorderSpacing = parent.BorderSpacing;
                else if (CssValues.TryParseLength(CssValues.SplitWhitespace(v)[0], s.FontSize, _rootFontSize, 0, out double spacing))
                    s.BorderSpacing = spacing;
                break;
            case "visibility":
                s.Visible = value is null ? parent.Visible : v is not ("hidden" or "collapse");
                break;
            case "margin-top": s.MarginTop = value is null ? parent.MarginTop : ParseLength(v, s, s.MarginTop); break;
            case "margin-right": s.MarginRight = value is null ? parent.MarginRight : ParseLength(v, s, s.MarginRight); break;
            case "margin-bottom": s.MarginBottom = value is null ? parent.MarginBottom : ParseLength(v, s, s.MarginBottom); break;
            case "margin-left": s.MarginLeft = value is null ? parent.MarginLeft : ParseLength(v, s, s.MarginLeft); break;
            case "padding-top": s.PaddingTop = value is null ? parent.PaddingTop : NonNegative(ParseLength(v, s, s.PaddingTop)); break;
            case "padding-right": s.PaddingRight = value is null ? parent.PaddingRight : NonNegative(ParseLength(v, s, s.PaddingRight)); break;
            case "padding-bottom": s.PaddingBottom = value is null ? parent.PaddingBottom : NonNegative(ParseLength(v, s, s.PaddingBottom)); break;
            case "padding-left": s.PaddingLeft = value is null ? parent.PaddingLeft : NonNegative(ParseLength(v, s, s.PaddingLeft)); break;
            case "width": s.Width = value is null ? parent.Width : ParseLength(v, s, s.Width); break;
            case "height": s.Height = value is null ? parent.Height : ParseLength(v, s, s.Height); break;
            case "min-height": s.MinHeight = value is null ? parent.MinHeight : ParseLength(v, s, s.MinHeight); break;
            case "max-width": s.MaxWidth = value is null ? parent.MaxWidth : v == "none" ? CssLength.Auto : ParseLength(v, s, s.MaxWidth); break;
            case "max-height": s.MaxHeight = value is null ? parent.MaxHeight : v == "none" ? CssLength.Auto : ParseLength(v, s, s.MaxHeight); break;
            case "break-before":
                s.BreakBefore = value is null ? parent.BreakBefore : v is "always" or "page" or "left" or "right" or "recto" or "verso";
                break;
            case "break-after":
                s.BreakAfter = value is null ? parent.BreakAfter : v is "always" or "page" or "left" or "right" or "recto" or "verso";
                break;
            case "break-inside":
                s.BreakInsideAvoid = value is null ? parent.BreakInsideAvoid : v is "avoid" or "avoid-page";
                break;
        }
    }

    private void ApplyBorders(ComputedStyle s, ComputedStyle parent, Dictionary<string, string> declared)
    {
        s.BorderTop = ResolveBorder("top", parent.BorderTop);
        s.BorderRight = ResolveBorder("right", parent.BorderRight);
        s.BorderBottom = ResolveBorder("bottom", parent.BorderBottom);
        s.BorderLeft = ResolveBorder("left", parent.BorderLeft);

        BorderSide ResolveBorder(string side, BorderSide inherited)
        {
            string? width = Get("width"), style = Get("style"), color = Get("color");
            var result = new BorderSide(3 * CssValues.PointsPerPixel, BorderStyle.None, s.Color);

            if (width == "inherit")
                result = result with { Width = inherited.Width };
            else if (width is not null)
            {
                result = result with
                {
                    Width = width switch
                    {
                        "thin" => 1 * CssValues.PointsPerPixel,
                        "medium" or "initial" or "unset" => 3 * CssValues.PointsPerPixel,
                        "thick" => 5 * CssValues.PointsPerPixel,
                        _ => CssValues.TryParseLength(width, s.FontSize, _rootFontSize, 0, out double w) ? Math.Max(0, w) : result.Width,
                    },
                };
            }

            if (style == "inherit")
                result = result with { Style = inherited.Style };
            else if (style is not null)
            {
                result = result with
                {
                    Style = style switch
                    {
                        "none" or "hidden" or "initial" or "unset" => BorderStyle.None,
                        "dashed" => BorderStyle.Dashed,
                        "dotted" => BorderStyle.Dotted,
                        "double" => BorderStyle.Double,
                        _ => BorderStyle.Solid,
                    },
                };
            }

            if (color == "inherit")
                result = result with { Color = inherited.Color };
            else if (color is not null && CssValues.TryParseColor(ResolveCurrentColor(color, s), out var c, out bool transparent))
                result = transparent ? result with { Style = BorderStyle.None } : result with { Color = c };

            return result;

            string? Get(string part) =>
                declared.TryGetValue($"border-{side}-{part}", out var v) ? v.ToLowerInvariant() : null;
        }
    }

    private static string ResolveCurrentColor(string value, ComputedStyle s) =>
        value == "currentcolor" ? $"rgb({s.Color.R},{s.Color.G},{s.Color.B})" : value;

    private void ApplyVerticalAlign(ComputedStyle s, ComputedStyle parent, string v)
    {
        double parentShift = parent.Display == Display.Inline ? parent.BaselineShift : 0;
        switch (v)
        {
            case "super":
                s.VerticalAlign = VerticalAlign.Super;
                s.BaselineShift = parentShift + parent.FontSize * 0.35;
                break;
            case "sub":
                s.VerticalAlign = VerticalAlign.Sub;
                s.BaselineShift = parentShift - parent.FontSize * 0.2;
                break;
            case "top" or "text-top":
                s.VerticalAlign = VerticalAlign.Top;
                break;
            case "middle":
                s.VerticalAlign = VerticalAlign.Middle;
                break;
            case "bottom" or "text-bottom":
                s.VerticalAlign = VerticalAlign.Bottom;
                break;
            case "baseline":
                s.VerticalAlign = VerticalAlign.Baseline;
                s.BaselineShift = parentShift;
                break;
            default:
                if (CssValues.TryParseLength(v, s.FontSize, _rootFontSize, s.LineHeight, out double shift))
                {
                    s.VerticalAlign = VerticalAlign.Length;
                    s.BaselineShift = parentShift + shift;
                }
                break;
        }
    }

    private CssLength ParseLength(string value, ComputedStyle s, CssLength fallback)
    {
        if (value == "auto")
            return CssLength.Auto;
        if (CssValues.IsPercentage(value) && CssValues.TryParseNumber(value.TrimEnd().TrimEnd('%'), out double pct))
            return new CssLength(0, pct, false);
        if (value.StartsWith("calc(", StringComparison.Ordinal))
            return ParseCalc(value, s, fallback);
        return CssValues.TryParseLength(value, s.FontSize, _rootFontSize, 0, out double pt) ? CssLength.Pt(pt) : fallback;
    }

    /// <summary>Supports simple <c>calc(a ± b)</c> expressions mixing a percentage and a length.</summary>
    private CssLength ParseCalc(string value, ComputedStyle s, CssLength fallback)
    {
        string inner = value[5..].TrimEnd(')').Trim();
        var tokens = CssValues.SplitWhitespace(inner);
        double points = 0, percent = 0;
        int sign = 1;
        foreach (string token in tokens)
        {
            if (token == "+") { sign = 1; continue; }
            if (token == "-") { sign = -1; continue; }
            if (CssValues.IsPercentage(token) && CssValues.TryParseNumber(token.TrimEnd('%'), out double p))
                percent += sign * p;
            else if (CssValues.TryParseLength(token, s.FontSize, _rootFontSize, 0, out double pt))
                points += sign * pt;
            else
                return fallback;
        }
        return new CssLength(points, percent, false);
    }

    private static CssLength NonNegative(CssLength length) =>
        length.Points < 0 || length.Percent < 0 ? CssLength.Zero : length;

    private Rgb ResolveInheritedColor(string value, Rgb parentColor)
    {
        string v = value.ToLowerInvariant();
        if (v is "inherit" or "currentcolor" or "unset")
            return parentColor;
        if (v == "initial")
            return Rgb.Black;
        return CssValues.TryParseColor(v, out var c, out _) ? c : parentColor;
    }

    private double ResolveFontSize(string value, double parentSize)
    {
        string v = value.ToLowerInvariant();
        double px = CssValues.PointsPerPixel;
        return v switch
        {
            "inherit" or "unset" => parentSize,
            "xx-small" => 9 * px,
            "x-small" => 10 * px,
            "small" => 13 * px,
            "medium" or "initial" => 16 * px,
            "large" => 18 * px,
            "x-large" => 24 * px,
            "xx-large" => 32 * px,
            "xxx-large" => 48 * px,
            "smaller" => parentSize / 1.2,
            "larger" => parentSize * 1.2,
            _ => CssValues.TryParseLength(v, parentSize, _rootFontSize, parentSize, out double size) && size > 0 ? size : parentSize,
        };
    }

    private static string InitialValue(string property, ComputedStyle parent) => property switch
    {
        "display" => "inline",
        "color" => "black",
        "background-color" => "transparent",
        "font-weight" or "font-style" or "line-height" or "letter-spacing" or "word-spacing" or "white-space" => "normal",
        "font-family" => "sans-serif",
        "text-align" => "left",
        "text-transform" or "text-decoration-line" => "none",
        "vertical-align" => "baseline",
        "list-style-type" => "disc",
        "width" or "height" or "min-height" or "break-before" or "break-after" or "break-inside" => "auto",
        "max-width" or "max-height" => "none",
        "visibility" => "visible",
        _ when property.EndsWith("-style", StringComparison.Ordinal) => "none",
        _ when property.EndsWith("-width", StringComparison.Ordinal) => "medium",
        _ when property.EndsWith("-color", StringComparison.Ordinal) => "currentcolor",
        _ => "0",
    };

    public static Display ParseDisplay(string value, Display fallback) => value.Trim().ToLowerInvariant() switch
    {
        "block" or "flow-root" or "flex" or "grid" or "flow" => Display.Block,
        "inline" or "contents" => Display.Inline,
        "inline-block" or "inline-flex" or "inline-grid" or "inline-table" => Display.InlineBlock,
        "list-item" => Display.ListItem,
        "table" => Display.Table,
        "table-row-group" => Display.TableRowGroup,
        "table-header-group" => Display.TableHeaderGroup,
        "table-footer-group" => Display.TableFooterGroup,
        "table-row" => Display.TableRow,
        "table-cell" => Display.TableCell,
        "table-caption" => Display.TableCaption,
        "none" => Display.None,
        _ => fallback,
    };

    public static FontFamily ParseFontFamily(string value, FontFamily fallback)
    {
        foreach (string raw in value.Split(','))
        {
            string name = raw.Trim().Trim('"', '\'').ToLowerInvariant();
            if (name.Length == 0)
                continue;
            if (name is "monospace" || name.Contains("courier") || name.Contains("mono") || name is "consolas" or "menlo" or "monaco")
                return FontFamily.Courier;
            if (name is "sans-serif" or "system-ui" or "-apple-system" or "blinkmacsystemfont" or "ui-sans-serif" ||
                name.Contains("helvetica") || name.Contains("arial") || name.Contains("verdana") || name.Contains("tahoma") ||
                name.Contains("segoe") || name.Contains("calibri") || name.Contains("roboto") || name.Contains("open sans") ||
                name.Contains("inter") || name.Contains("lato") || name.Contains("trebuchet") || name.Contains("geneva"))
                return FontFamily.Helvetica;
            if (name is "serif" or "ui-serif" || name.Contains("times") || name.Contains("georgia") ||
                name.Contains("garamond") || name.Contains("cambria") || name.Contains("palatino") ||
                name.Contains("book") || name.Contains("baskerville") || name.Contains("minion"))
                return FontFamily.Times;
        }
        return fallback;
    }
}
