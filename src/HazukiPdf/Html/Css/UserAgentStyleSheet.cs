namespace HazukiPdf.Html.Css;

/// <summary>Default styles for HTML elements, modelled on the defaults browsers use.</summary>
internal static class UserAgentStyleSheet
{
    public const string Css = """
        html, address, blockquote, body, center, dd, details, dialog, dir, div, dl, dt, fieldset, figcaption, figure,
        footer, form, h1, h2, h3, h4, h5, h6, header, hgroup, hr, legend, main, menu, nav, ol, p, pre, section,
        article, aside, summary, ul { display: block; }
        head, script, style, title, meta, link, template, noscript, base, datalist, param, area, map, dialog, [hidden] { display: none; }
        input, select, textarea, button, iframe, object, embed, video, audio, canvas, svg, option { display: none; }

        body { margin: 8px; }
        p { margin-top: 1em; margin-bottom: 1em; }
        h1 { font-size: 2em; margin-top: 0.67em; margin-bottom: 0.67em; font-weight: bold; }
        h2 { font-size: 1.5em; margin-top: 0.83em; margin-bottom: 0.83em; font-weight: bold; }
        h3 { font-size: 1.17em; margin-top: 1em; margin-bottom: 1em; font-weight: bold; }
        h4 { margin-top: 1.33em; margin-bottom: 1.33em; font-weight: bold; }
        h5 { font-size: 0.83em; margin-top: 1.67em; margin-bottom: 1.67em; font-weight: bold; }
        h6 { font-size: 0.67em; margin-top: 2.33em; margin-bottom: 2.33em; font-weight: bold; }
        blockquote, figure { margin: 1em 40px; }
        dl { margin-top: 1em; margin-bottom: 1em; }
        dd { margin-left: 40px; }
        dt { font-weight: bold; }
        hr { border: 1px inset #9a9a9a; margin: 0.5em 0; }
        pre { font-family: monospace; white-space: pre; margin-top: 1em; margin-bottom: 1em; }
        center { text-align: center; }
        address { font-style: italic; }

        ul, ol, menu, dir { margin-top: 1em; margin-bottom: 1em; padding-left: 40px; }
        ul, menu, dir { list-style-type: disc; }
        ol { list-style-type: decimal; }
        li { display: list-item; }
        ul ul, ul ol, ol ul, ol ol { margin-top: 0; margin-bottom: 0; }
        ul ul, ol ul { list-style-type: circle; }
        ul ul ul, ul ol ul, ol ul ul, ol ol ul { list-style-type: square; }

        table { display: table; border-spacing: 2px; border-collapse: separate; }
        caption { display: table-caption; text-align: center; }
        thead { display: table-header-group; vertical-align: middle; }
        tbody { display: table-row-group; vertical-align: middle; }
        tfoot { display: table-footer-group; vertical-align: middle; }
        tr { display: table-row; vertical-align: inherit; }
        table > tr { vertical-align: middle; }
        td, th { display: table-cell; vertical-align: inherit; padding: 1px; }
        th { font-weight: bold; text-align: center; }
        col, colgroup { display: none; }

        b, strong { font-weight: bold; }
        i, em, cite, var, dfn { font-style: italic; }
        code, kbd, samp, tt { font-family: monospace; }
        u, ins { text-decoration: underline; }
        s, strike, del { text-decoration: line-through; }
        small { font-size: smaller; }
        big { font-size: larger; }
        sub { vertical-align: sub; font-size: smaller; }
        sup { vertical-align: super; font-size: smaller; }
        mark { background-color: yellow; color: black; }
        a:link { color: #0000ee; text-decoration: underline; }
        abbr[title] { text-decoration: underline; }
        q { font-style: normal; }
        """;
}
