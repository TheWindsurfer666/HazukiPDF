using HazukiPdf.Html.Dom;

namespace HazukiPdf.Html.Css;

internal enum Combinator
{
    Descendant,
    Child,
    Adjacent,
    Sibling,
}

/// <summary>
/// A complex selector such as <c>div.note &gt; p:first-child</c>. Supports type, universal, id,
/// class and attribute selectors, the four combinators and a few structural pseudo-classes.
/// Anything else makes the selector invalid so it never matches.
/// </summary>
internal sealed class Selector
{
    private readonly List<(Compound Compound, Combinator Combinator)> _parts; // right-most part is last

    private Selector(List<(Compound, Combinator)> parts, int specificity)
    {
        _parts = parts;
        Specificity = specificity;
    }

    public int Specificity { get; }

    /// <summary>The type selector of the right-most compound, or null when it matches any tag.</summary>
    public string? SubjectTag => _parts[^1].Compound.Tag;

    /// <summary>Parses a comma separated selector list. Invalid selectors are dropped.</summary>
    public static List<Selector> ParseList(string text)
    {
        var result = new List<Selector>();
        foreach (var part in SplitTopLevel(text, ','))
        {
            var selector = Parse(part.Trim());
            if (selector is not null)
                result.Add(selector);
        }
        return result;
    }

    private static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '[')
                depth++;
            else if (c is ')' or ']')
                depth--;
            else if (c == separator && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    public static Selector? Parse(string text)
    {
        if (text.Length == 0)
            return null;
        var parts = new List<(Compound, Combinator)>();
        int i = 0;
        var combinator = Combinator.Descendant;
        int a = 0, b = 0, c = 0;

        while (i < text.Length)
        {
            var compound = new Compound();
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('>' or '+' or '~'))
            {
                char ch = text[i];
                if (ch == '*')
                {
                    i++;
                }
                else if (ch == '#')
                {
                    i++;
                    compound.Id = ReadIdent(text, ref i);
                    if (compound.Id.Length == 0)
                        return null;
                    a++;
                }
                else if (ch == '.')
                {
                    i++;
                    string cls = ReadIdent(text, ref i);
                    if (cls.Length == 0)
                        return null;
                    compound.Classes.Add(cls);
                    b++;
                }
                else if (ch == '[')
                {
                    int close = text.IndexOf(']', i);
                    if (close < 0)
                        return null;
                    var attr = AttributeTest.Parse(text[(i + 1)..close]);
                    if (attr is null)
                        return null;
                    compound.Attributes.Add(attr);
                    i = close + 1;
                    b++;
                }
                else if (ch == ':')
                {
                    i++;
                    if (i < text.Length && text[i] == ':')
                        return null; // pseudo-elements are not supported
                    string name = ReadIdent(text, ref i).ToLowerInvariant();
                    string? argument = null;
                    if (i < text.Length && text[i] == '(')
                    {
                        int close = text.IndexOf(')', i);
                        if (close < 0)
                            return null;
                        argument = text[(i + 1)..close].Trim();
                        i = close + 1;
                    }
                    var pseudo = PseudoClass.Create(name, argument);
                    if (pseudo is null)
                        return null;
                    compound.Pseudos.Add(pseudo);
                    b++;
                }
                else if (char.IsAsciiLetter(ch) || ch is '-' or '_')
                {
                    if (i != start)
                        return null; // a type selector must come first
                    compound.Tag = ReadIdent(text, ref i).ToLowerInvariant();
                    c++;
                }
                else
                {
                    return null;
                }
            }
            if (i == start)
                return null;
            parts.Add((compound, combinator));

            // Combinator
            bool sawSpace = false;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
                sawSpace = true;
            }
            if (i >= text.Length)
                break;
            combinator = Combinator.Descendant;
            if (text[i] is '>' or '+' or '~')
            {
                combinator = text[i] switch { '>' => Combinator.Child, '+' => Combinator.Adjacent, _ => Combinator.Sibling };
                i++;
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;
                if (i >= text.Length)
                    return null;
            }
            else if (!sawSpace)
            {
                return null;
            }
        }

        return parts.Count == 0 ? null : new Selector(parts, a * 10000 + b * 100 + c);
    }

    private static string ReadIdent(string text, ref int i)
    {
        int start = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '-' or '_' or '\\'))
            i++;
        return text[start..i].Replace("\\", "");
    }

    public bool Matches(HtmlElement element) => MatchesFrom(element, _parts.Count - 1);

    private bool MatchesFrom(HtmlElement element, int index)
    {
        var (compound, combinator) = _parts[index];
        if (!compound.Matches(element))
            return false;
        if (index == 0)
            return true;

        switch (combinator)
        {
            case Combinator.Child:
                return element.Parent is { } parent && IsElement(parent) && MatchesFrom(parent, index - 1);
            case Combinator.Descendant:
                for (var p = element.Parent; p is not null && IsElement(p); p = p.Parent)
                {
                    if (MatchesFrom(p, index - 1))
                        return true;
                }
                return false;
            case Combinator.Adjacent:
                return element.PreviousElementSibling is { } prev && MatchesFrom(prev, index - 1);
            default:
                for (var s = element.PreviousElementSibling; s is not null; s = s.PreviousElementSibling)
                {
                    if (MatchesFrom(s, index - 1))
                        return true;
                }
                return false;
        }
    }

    private static bool IsElement(HtmlElement e) => e.TagName != "#document";

    private sealed class Compound
    {
        public string? Tag;
        public string? Id;
        public readonly List<string> Classes = [];
        public readonly List<AttributeTest> Attributes = [];
        public readonly List<PseudoClass> Pseudos = [];

        public bool Matches(HtmlElement e)
        {
            if (Tag is not null && Tag != e.TagName)
                return false;
            if (Id is not null && !string.Equals(e.Id, Id, StringComparison.Ordinal))
                return false;
            foreach (var cls in Classes)
            {
                if (Array.IndexOf(e.Classes, cls) < 0)
                    return false;
            }
            foreach (var attr in Attributes)
            {
                if (!attr.Matches(e))
                    return false;
            }
            foreach (var pseudo in Pseudos)
            {
                if (!pseudo.Matches(e))
                    return false;
            }
            return true;
        }
    }

    private sealed class AttributeTest
    {
        private string _name = "";
        private string? _op;
        private string _value = "";

        public static AttributeTest? Parse(string text)
        {
            text = text.Trim();
            int opIndex = text.IndexOfAny(['=', '~', '|', '^', '$', '*']);
            if (opIndex < 0)
                return text.Length == 0 ? null : new AttributeTest { _name = text.ToLowerInvariant() };
            string op = text[opIndex] == '=' ? "=" : text.Substring(opIndex, 2);
            if (op.Length == 2 && op[1] != '=')
                return null;
            string value = text[(opIndex + op.Length)..].Trim();
            if (value.EndsWith(" i", StringComparison.OrdinalIgnoreCase))
                value = value[..^2].Trim();
            if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
                value = value[1..^1];
            return new AttributeTest { _name = text[..opIndex].Trim().ToLowerInvariant(), _op = op, _value = value };
        }

        public bool Matches(HtmlElement e)
        {
            string? actual = e.GetAttribute(_name);
            if (actual is null)
                return false;
            return _op switch
            {
                null => true,
                "=" => actual == _value,
                "~=" => actual.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(_value),
                "|=" => actual == _value || actual.StartsWith(_value + "-", StringComparison.Ordinal),
                "^=" => _value.Length > 0 && actual.StartsWith(_value, StringComparison.Ordinal),
                "$=" => _value.Length > 0 && actual.EndsWith(_value, StringComparison.Ordinal),
                "*=" => _value.Length > 0 && actual.Contains(_value, StringComparison.Ordinal),
                _ => false,
            };
        }
    }

    private sealed class PseudoClass
    {
        private Func<HtmlElement, bool> _test = _ => true;

        public static PseudoClass? Create(string name, string? argument)
        {
            Func<HtmlElement, bool>? test = name switch
            {
                "first-child" => e => IndexOf(e, fromEnd: false, ofType: false) == 1,
                "last-child" => e => IndexOf(e, fromEnd: true, ofType: false) == 1,
                "only-child" => e => IndexOf(e, false, false) == 1 && IndexOf(e, true, false) == 1,
                "first-of-type" => e => IndexOf(e, fromEnd: false, ofType: true) == 1,
                "last-of-type" => e => IndexOf(e, fromEnd: true, ofType: true) == 1,
                "nth-child" => Nth(argument, fromEnd: false, ofType: false),
                "nth-last-child" => Nth(argument, fromEnd: true, ofType: false),
                "nth-of-type" => Nth(argument, fromEnd: false, ofType: true),
                "root" => e => e.Parent?.TagName == "#document",
                "empty" => e => e.Children.Count == 0,
                "link" or "any-link" => e => e.TagName == "a" && e.GetAttribute("href") is not null,
                "not" when argument is not null && Selector.Parse(argument) is { } inner => e => !inner.Matches(e),
                // Dynamic states never apply to a printed page.
                "hover" or "active" or "focus" or "visited" or "focus-within" or "focus-visible" or "checked" => _ => false,
                _ => null,
            };
            return test is null ? null : new PseudoClass { _test = test };
        }

        public bool Matches(HtmlElement e) => _test(e);

        private static Func<HtmlElement, bool>? Nth(string? argument, bool fromEnd, bool ofType)
        {
            if (argument is null || !TryParseNth(argument, out int a, out int b))
                return null;
            return e =>
            {
                int index = IndexOf(e, fromEnd, ofType);
                if (a == 0)
                    return index == b;
                int n = index - b;
                return n % a == 0 && n / a >= 0;
            };
        }

        private static bool TryParseNth(string text, out int a, out int b)
        {
            text = text.Replace(" ", "").ToLowerInvariant();
            a = 0;
            b = 0;
            if (text == "odd")
            {
                a = 2;
                b = 1;
                return true;
            }
            if (text == "even")
            {
                a = 2;
                return true;
            }
            int nIndex = text.IndexOf('n');
            if (nIndex < 0)
                return int.TryParse(text, out b);
            string aText = text[..nIndex];
            a = aText switch { "" or "+" => 1, "-" => -1, _ => int.TryParse(aText, out int v) ? v : int.MinValue };
            if (a == int.MinValue)
                return false;
            string bText = text[(nIndex + 1)..];
            return bText.Length == 0 || int.TryParse(bText, out b);
        }

        private static int IndexOf(HtmlElement e, bool fromEnd, bool ofType)
        {
            if (e.Parent is null)
                return 1;
            var siblings = e.Parent.ChildElements.Where(s => !ofType || s.TagName == e.TagName).ToList();
            int index = siblings.IndexOf(e);
            return fromEnd ? siblings.Count - index : index + 1;
        }
    }
}
