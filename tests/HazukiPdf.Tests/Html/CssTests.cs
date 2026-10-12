using HazukiPdf.Html.Css;
using HazukiPdf.Html.Dom;
using HazukiPdf.Html.Fonts;

namespace HazukiPdf.Tests.Html;

public class CssTests
{
    private static (HtmlElement Root, StyleResolver Styles) Resolve(string html, string css = "")
    {
        var root = HtmlParser.Parse(html);
        int order = 0;
        var resolver = new StyleResolver([StyleSheet.Parse(css, ref order)], 12);
        var rootStyle = new ComputedStyle { Display = Display.Block, FontSize = 12 };
        foreach (var child in root.ChildElements)
            resolver.Resolve(child, rootStyle);
        return (root, resolver);
    }

    private static ComputedStyle StyleOf(string html, string css, string id)
    {
        var (root, styles) = Resolve(html, css);
        return styles.GetStyle(root.Descendants().Concat(root.ChildElements).First(e => e.Id == id));
    }

    [Theory]
    [InlineData("p", true)]
    [InlineData("div p", true)]
    [InlineData("div > p", false)]
    [InlineData("section > p", true)]
    [InlineData("p.note", true)]
    [InlineData(".note.big", true)]
    [InlineData(".note.small", false)]
    [InlineData("#x", true)]
    [InlineData("[lang]", true)]
    [InlineData("[lang=sv]", true)]
    [InlineData("[lang^=s]", true)]
    [InlineData("[lang=en]", false)]
    [InlineData("h1 + p", true)]
    [InlineData("h1 ~ p", true)]
    [InlineData("p:first-child", false)]
    [InlineData("p:last-child", true)]
    [InlineData("p:nth-child(2)", true)]
    [InlineData("p:nth-child(odd)", false)]
    [InlineData("p:not(.other)", true)]
    [InlineData("p::before", false)]
    [InlineData("a:hover", false)]
    public void Selector_Matches(string selector, bool expected)
    {
        var root = HtmlParser.Parse("<div><section><h1>t</h1><p id=x class='note big' lang=sv>p</p></section></div>");
        var p = root.Descendants().First(e => e.TagName == "p");
        var parsed = Selector.Parse(selector);
        Assert.Equal(expected, parsed is not null && parsed.Matches(p));
    }

    [Fact]
    public void Selector_Specificity()
    {
        Assert.True(Selector.Parse("#a")!.Specificity > Selector.Parse(".a.b.c")!.Specificity);
        Assert.True(Selector.Parse(".a")!.Specificity > Selector.Parse("div p span")!.Specificity);
    }

    [Fact]
    public void Cascade_UsesSpecificityThenOrderThenImportance()
    {
        const string html = "<p id=x class=c style='color: green'>t</p>";
        Assert.Equal(new Rgb(0, 128, 0), StyleOf(html, "#x { color: red } p { color: blue }", "x").Color);
        Assert.Equal(new Rgb(255, 0, 0), StyleOf("<p id=x class=c>t</p>", "#x { color: red } .c { color: blue }", "x").Color);
        Assert.Equal(new Rgb(0, 0, 255), StyleOf("<p id=x class=c>t</p>", ".c { color: red } .c { color: blue }", "x").Color);
        Assert.Equal(new Rgb(0, 0, 255), StyleOf(html, "p { color: blue !important }", "x").Color);
    }

    [Fact]
    public void Inheritance_PassesTextPropertiesButNotBoxProperties()
    {
        var style = StyleOf("<div style='color:#123456; font-weight:bold; margin:10px; border:1px solid'><span id=s>t</span></div>", "", "s");
        Assert.Equal(new Rgb(0x12, 0x34, 0x56), style.Color);
        Assert.True(style.Bold);
        Assert.True(style.MarginTop.IsZero);
        Assert.False(style.BorderTop.IsVisible);
    }

    [Fact]
    public void FontSize_ResolvesRelativeUnits()
    {
        Assert.Equal(24, StyleOf("<div style='font-size:2em'><p id=p>t</p></div>", "", "p").FontSize, 3);
        Assert.Equal(13.5, StyleOf("<div style='font-size:24px'><p id=p style='font-size:75%'>t</p></div>", "", "p").FontSize, 3);
        Assert.Equal(15, StyleOf("<p id=p style='font-size:15pt'>t</p>", "", "p").FontSize, 3);
        Assert.Equal(24, StyleOf("<h1 id=h>t</h1>", "", "h").FontSize, 3);
    }

    [Fact]
    public void Shorthands_ExpandToLonghands()
    {
        var style = StyleOf("<div id=d>t</div>",
            "#d { margin: 1px 2px 3px; padding: 4px 8px; border: 2px dashed red; border-left: none; font: italic bold 20px/1.5 Georgia, serif; background: #fff url(x.png) no-repeat }",
            "d");
        Assert.Equal(0.75, style.MarginTop.Points, 3);
        Assert.Equal(1.5, style.MarginRight.Points, 3);
        Assert.Equal(2.25, style.MarginBottom.Points, 3);
        Assert.Equal(1.5, style.MarginLeft.Points, 3);
        Assert.Equal(3, style.PaddingTop.Points, 3);
        Assert.Equal(6, style.PaddingLeft.Points, 3);
        Assert.Equal(BorderStyle.Dashed, style.BorderTop.Style);
        Assert.Equal(1.5, style.BorderTop.Width, 3);
        Assert.Equal(new Rgb(255, 0, 0), style.BorderTop.Color);
        Assert.False(style.BorderLeft.IsVisible);
        Assert.True(style.Italic);
        Assert.True(style.Bold);
        Assert.Equal(15, style.FontSize, 3);
        Assert.Equal(22.5, style.LineHeight, 3);
        Assert.Equal(FontFamily.Times, style.FontFamily);
        Assert.Equal(Rgb.White, style.BackgroundColor);
    }

    [Theory]
    [InlineData("#f00", 255, 0, 0)]
    [InlineData("#00ff7f", 0, 255, 127)]
    [InlineData("rgb(10, 20, 30)", 10, 20, 30)]
    [InlineData("rgba(10 20 30 / 50%)", 10, 20, 30)]
    [InlineData("rgb(100%, 0%, 50%)", 255, 0, 128)]
    [InlineData("hsl(120, 100%, 25%)", 0, 128, 0)]
    [InlineData("RebeccaPurple", 102, 51, 153)]
    public void Colors_Parse(string value, int r, int g, int b)
    {
        Assert.True(CssValues.TryParseColor(value, out var color, out _));
        Assert.Equal(new Rgb((byte)r, (byte)g, (byte)b), color);
    }

    [Theory]
    [InlineData("72pt", 72)]
    [InlineData("96px", 72)]
    [InlineData("1in", 72)]
    [InlineData("25.4mm", 72)]
    [InlineData("2.54cm", 72)]
    [InlineData("2em", 20)]
    [InlineData("50%", 100)]
    public void Lengths_ConvertToPoints(string value, double expected)
    {
        Assert.True(CssValues.TryParseLength(value, 10, 12, 200, out double points));
        Assert.Equal(expected, points, 3);
    }

    [Fact]
    public void StyleSheet_KeepsPrintMediaAndPageRulesAndSkipsScreen()
    {
        int order = 0;
        var sheet = StyleSheet.Parse(
            "/* comment */ @import url(x.css); @media screen { p { color: red } } @media print { p { color: blue } } " +
            "@page { size: A4 landscape; margin: 1cm } @font-face { font-family: X } h1, h2 { color: green }",
            ref order);
        Assert.Equal(3, sheet.Rules.Count);
        Assert.Contains(sheet.PageDeclarations, d => d.Property == "size" && d.Value == "A4 landscape");
        Assert.Contains(sheet.PageDeclarations, d => d.Property == "margin-left" && d.Value == "1cm");
    }

    [Fact]
    public void PresentationalAttributes_MapToCss()
    {
        var (root, styles) = Resolve("<table border=1 cellpadding=5 align=center><tr><td id=c bgcolor=yellow align=right width=50>x</td></tr></table>");
        var cell = styles.GetStyle(root.Descendants().First(e => e.Id == "c"));
        Assert.Equal(new Rgb(255, 255, 0), cell.BackgroundColor);
        Assert.Equal(TextAlign.Right, cell.TextAlign);
        Assert.Equal(37.5, cell.Width.Points, 3);
        Assert.Equal(3.75, cell.PaddingLeft.Points, 3);
        Assert.True(cell.BorderTop.IsVisible);
        var table = styles.GetStyle(root.ChildElements.First());
        Assert.True(table.MarginLeft.IsAuto && table.MarginRight.IsAuto);
    }

    [Fact]
    public void Display_FromUserAgentAndAuthorStyles()
    {
        var (root, styles) = Resolve("<div><span>a</span><li>b</li><script>x</script><p hidden>h</p></div>", "span { display: block }");
        var children = root.ChildElements.First().ChildElements.ToList();
        Assert.Equal(Display.Block, styles.GetStyle(children[0]).Display);
        Assert.Equal(Display.ListItem, styles.GetStyle(children[1]).Display);
        Assert.Equal(Display.None, styles.GetStyle(children[2]).Display);
        Assert.Equal(Display.None, styles.GetStyle(children[3]).Display);
    }

    [Theory]
    [InlineData("Arial, sans-serif", "Helvetica")]
    [InlineData("'Times New Roman', serif", "Times")]
    [InlineData("Consolas, monospace", "Courier")]
    [InlineData("UnknownFont, Georgia", "Times")]
    [InlineData("UnknownFont", "Helvetica")]
    public void FontFamily_MapsToStandardFonts(string value, string expected) =>
        Assert.Equal(expected, StyleResolver.ParseFontFamily(value, FontFamily.Helvetica).ToString());
}
