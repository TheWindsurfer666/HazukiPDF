using HazukiPdf.Html.Fonts;

namespace HazukiPdf.Html.Css;

internal enum Display
{
    Inline,
    Block,
    InlineBlock,
    ListItem,
    Table,
    TableRowGroup,
    TableHeaderGroup,
    TableFooterGroup,
    TableRow,
    TableCell,
    TableCaption,
    None,
}

internal enum TextAlign { Left, Right, Center, Justify }

internal enum WhiteSpace { Normal, NoWrap, Pre, PreWrap, PreLine }

internal enum VerticalAlign { Baseline, Sub, Super, Top, Middle, Bottom, Length }

internal enum BorderStyle { None, Solid, Dashed, Dotted, Double }

internal enum TextTransform { None, Uppercase, Lowercase, Capitalize }

/// <summary>A length that may be relative to the containing block width or be <c>auto</c>.</summary>
internal readonly record struct CssLength(double Points, double Percent, bool IsAuto)
{
    public static readonly CssLength Zero = new(0, 0, false);
    public static readonly CssLength Auto = new(0, 0, true);

    public static CssLength Pt(double points) => new(points, 0, false);

    public bool IsZero => !IsAuto && Points == 0 && Percent == 0;

    public double Resolve(double percentBase) => IsAuto ? 0 : Points + Percent * percentBase / 100;
}

internal readonly record struct BorderSide(double Width, BorderStyle Style, Rgb Color)
{
    public static readonly BorderSide None = new(0, BorderStyle.None, Rgb.Black);

    public bool IsVisible => Style != BorderStyle.None && Width > 0;

    /// <summary>The width the border takes up in layout.</summary>
    public double LayoutWidth => IsVisible ? Width : 0;
}

/// <summary>The resolved style of an element after cascading and inheritance. Lengths are in points.</summary>
internal sealed class ComputedStyle
{
    public Display Display { get; set; } = Display.Inline;

    // Inherited properties
    public FontFamily FontFamily { get; set; } = FontFamily.Helvetica;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public double FontSize { get; set; } = 12;
    public double? LineHeightFactor { get; set; }
    public double? LineHeightAbsolute { get; set; }
    public Rgb Color { get; set; } = Rgb.Black;
    public TextAlign TextAlign { get; set; } = TextAlign.Left;
    public CssLength TextIndent { get; set; } = CssLength.Zero;
    public TextTransform TextTransform { get; set; }
    public WhiteSpace WhiteSpace { get; set; }
    public string ListStyleType { get; set; } = "disc";
    public bool BorderCollapse { get; set; }
    public double BorderSpacing { get; set; }
    public bool Visible { get; set; } = true;
    public double LetterSpacing { get; set; }
    public double WordSpacing { get; set; }

    // Propagated to inline descendants
    public bool Underline { get; set; }
    public bool LineThrough { get; set; }
    public bool Overline { get; set; }
    public double BaselineShift { get; set; }
    public Rgb? InlineBackground { get; set; }

    // Not inherited
    public Rgb? BackgroundColor { get; set; }
    public VerticalAlign VerticalAlign { get; set; }
    public CssLength MarginTop { get; set; } = CssLength.Zero;
    public CssLength MarginRight { get; set; } = CssLength.Zero;
    public CssLength MarginBottom { get; set; } = CssLength.Zero;
    public CssLength MarginLeft { get; set; } = CssLength.Zero;
    public CssLength PaddingTop { get; set; } = CssLength.Zero;
    public CssLength PaddingRight { get; set; } = CssLength.Zero;
    public CssLength PaddingBottom { get; set; } = CssLength.Zero;
    public CssLength PaddingLeft { get; set; } = CssLength.Zero;
    public BorderSide BorderTop { get; set; } = BorderSide.None;
    public BorderSide BorderRight { get; set; } = BorderSide.None;
    public BorderSide BorderBottom { get; set; } = BorderSide.None;
    public BorderSide BorderLeft { get; set; } = BorderSide.None;
    public CssLength Width { get; set; } = CssLength.Auto;
    public CssLength Height { get; set; } = CssLength.Auto;
    public CssLength MinHeight { get; set; } = CssLength.Auto;
    public CssLength MaxWidth { get; set; } = CssLength.Auto;
    public CssLength MaxHeight { get; set; } = CssLength.Auto;
    public bool BreakBefore { get; set; }
    public bool BreakAfter { get; set; }
    public bool BreakInsideAvoid { get; set; }

    public StandardFont Font => StandardFont.Get(FontFamily, Bold, Italic);

    /// <summary>The used line height in points.</summary>
    public double LineHeight => LineHeightAbsolute ?? FontSize * (LineHeightFactor ?? 1.15);

    public bool IsBlockLevel => Display is Display.Block or Display.ListItem or Display.Table or Display.TableCaption;

    /// <summary>Creates the starting style for a child: inherited properties are copied, the rest reset.</summary>
    public ComputedStyle CreateChild()
    {
        return new ComputedStyle
        {
            FontFamily = FontFamily,
            Bold = Bold,
            Italic = Italic,
            FontSize = FontSize,
            LineHeightFactor = LineHeightFactor,
            LineHeightAbsolute = LineHeightAbsolute,
            Color = Color,
            TextAlign = TextAlign,
            TextIndent = TextIndent,
            TextTransform = TextTransform,
            WhiteSpace = WhiteSpace,
            ListStyleType = ListStyleType,
            BorderCollapse = BorderCollapse,
            BorderSpacing = BorderSpacing,
            Visible = Visible,
            LetterSpacing = LetterSpacing,
            WordSpacing = WordSpacing,
            Underline = Underline,
            LineThrough = LineThrough,
            Overline = Overline,
            BaselineShift = BaselineShift,
            InlineBackground = InlineBackground,
        };
    }
}
