using HazukiPdf.Html.Dom;

namespace HazukiPdf.Tests.Html;

public class HtmlParserTests
{
    private static HtmlElement Body(string html)
    {
        var document = HtmlParser.Parse(html);
        return document.Descendants().FirstOrDefault(e => e.TagName == "body") ?? document;
    }

    private static string Structure(HtmlElement element) =>
        string.Concat(element.Children.Select(n => n switch
        {
            HtmlElement e => $"<{e.TagName}>{Structure(e)}</{e.TagName}>",
            HtmlText t => t.Text,
            _ => "",
        }));

    [Fact]
    public void Parse_BuildsTreeWithAttributes()
    {
        var root = HtmlParser.Parse("<div id=main class='a  b' data-x=\"1\"><p>Hello <b>world</b></p></div>");
        var div = root.ChildElements.Single();
        Assert.Equal("div", div.TagName);
        Assert.Equal("main", div.Id);
        Assert.Equal(["a", "b"], div.Classes);
        Assert.Equal("1", div.GetAttribute("DATA-X"));
        Assert.Equal("<p>Hello <b>world</b></p>", Structure(div));
    }

    [Fact]
    public void Parse_DecodesEntities()
    {
        var root = HtmlParser.Parse("<p title=\"&quot;x&quot;\">a &amp; b &lt;c&gt; &euro;&#8364;&#x20AC; &nbsp;&copy;</p>");
        var p = root.ChildElements.Single();
        Assert.Equal("\"x\"", p.GetAttribute("title"));
        Assert.Equal("a & b <c> €€€  ©", p.TextContent);
    }

    [Fact]
    public void Parse_ClosesParagraphsAndListItemsImplicitly()
    {
        var root = HtmlParser.Parse("<p>one<p>two<div>three</div><ul><li>a<li>b</ul>");
        Assert.Equal("<p>one</p><p>two</p><div>three</div><ul><li>a</li><li>b</li></ul>", Structure(root));
    }

    [Fact]
    public void Parse_HandlesUnclosedTableParts()
    {
        var root = HtmlParser.Parse("<table><tr><td>1<td>2<tr><td>3</table>");
        Assert.Equal("<table><tr><td>1</td><td>2</td></tr><tr><td>3</td></tr></table>", Structure(root));
    }

    [Fact]
    public void Parse_AddsRowForCellsDirectlyInTable()
    {
        var root = HtmlParser.Parse("<table><td>x</td></table>");
        Assert.Equal("<table><tr><td>x</td></tr></table>", Structure(root));
    }

    [Fact]
    public void Parse_TreatsVoidAndSelfClosingElements()
    {
        var root = HtmlParser.Parse("<p>a<br>b<img src=x.png>c<br/>d<hr></p>");
        Assert.Equal("<p>a<br></br>b<img></img>c<br></br>d</p><hr></hr>", Structure(root));
    }

    [Fact]
    public void Parse_KeepsScriptAndStyleAsRawText()
    {
        var root = HtmlParser.Parse("<style>p > b { color: red }</style><script>if (a < b && c) {}</script><p>x</p>");
        Assert.Equal("p > b { color: red }", root.ChildElements.First().TextContent);
        Assert.Equal("if (a < b && c) {}", root.ChildElements.ElementAt(1).TextContent);
    }

    [Fact]
    public void Parse_SkipsCommentsDoctypeAndProcessingInstructions()
    {
        var root = HtmlParser.Parse("<!DOCTYPE html><?xml version=\"1.0\"?><!-- <p>hidden</p> --><p>shown</p>");
        Assert.Equal("<p>shown</p>", Structure(root));
    }

    [Fact]
    public void Parse_IgnoresStrayEndTagsAndToleratesTruncatedInput()
    {
        var root = HtmlParser.Parse("</span><div>text</b></div><p class=\"unterminated");
        Assert.Equal("<div>text</div><p></p>", Structure(root));
    }

    [Fact]
    public void Parse_TreatsLoneLessThanAsText()
    {
        Assert.Equal("1 < 2 and 3 <4", Body("1 < 2 and 3 <4").TextContent);
    }
}
