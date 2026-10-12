using System.Globalization;
using System.Text;
using HazukiPdf.Html.Css;

namespace HazukiPdf.Html.Layout;

internal sealed partial class LayoutEngine
{
    private sealed class Line
    {
        public List<Piece> Pieces { get; } = [];
        public double Width { get; set; }
        public bool EndsWithBreak { get; set; }
        public bool IsLast { get; set; }
    }

    private void LayoutInline(InlineContent content, ComputedStyle blockStyle, double x, double width, Cursor c)
    {
        var pieces = GetPieces(content);
        foreach (var piece in pieces)
        {
            if (piece.Kind == PieceKind.Image)
                (piece.Width, piece.ImageHeight) = ImageSize(piece, width);
        }

        double indent = blockStyle.TextIndent.Resolve(width);
        var lines = BreakLines(pieces, width, indent);
        for (int i = 0; i < lines.Count; i++)
            PlaceLine(lines[i], blockStyle, x, width, i == 0 ? indent : 0, c);
    }

    private static List<Piece> GetPieces(InlineContent content)
    {
        if (content.Pieces is not null)
            return content.Pieces;

        var pieces = new List<Piece>();
        foreach (var item in content.Items)
        {
            switch (item)
            {
                case TextItem text:
                    AddTextPieces(pieces, text);
                    break;
                case ImageItem image:
                    pieces.Add(new Piece { Kind = PieceKind.Image, Style = image.Style, Link = image.Link, Image = image.Image });
                    break;
                case LineBreakItem br:
                    pieces.Add(new Piece { Kind = PieceKind.Break, Style = br.Style });
                    break;
                case AnchorItem anchor:
                    pieces.Add(new Piece { Kind = PieceKind.Anchor, Style = anchor.Style, AnchorId = anchor.Id });
                    break;
            }
        }
        content.Pieces = pieces;
        return pieces;
    }

    private static void AddTextPieces(List<Piece> pieces, TextItem item)
    {
        var s = item.Style;
        string text = Transform(item.Text, s.TextTransform);
        bool canWrap = s.WhiteSpace is WhiteSpace.Normal or WhiteSpace.PreWrap or WhiteSpace.PreLine;
        var font = s.Font;
        double spaceWidth = font.Measure(" ", s.FontSize) + s.WordSpacing + s.LetterSpacing;

        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == ' ')
            {
                pieces.Add(new Piece
                {
                    Kind = PieceKind.Space, Style = s, Link = item.Link, Text = " ", Width = spaceWidth,
                    CanBreak = canWrap,
                });
                i++;
                continue;
            }
            int start = i;
            while (i < text.Length && text[i] != ' ')
            {
                // Allow a break after a hyphen that sits between letters.
                if (text[i] == '-' && canWrap && i > start && i + 1 < text.Length && char.IsLetter(text[i + 1]) &&
                    char.IsLetter(text[i - 1]))
                {
                    i++;
                    break;
                }
                i++;
            }
            string word = text[start..i];
            pieces.Add(MakeWord(word, s, item.Link));
            if (i < text.Length && text[i] != ' ' && word.EndsWith('-'))
            {
                // Zero-width break opportunity after the hyphen.
                pieces.Add(new Piece { Kind = PieceKind.Space, Style = s, Link = item.Link, Text = "", Width = 0, CanBreak = true });
            }
        }
    }

    private static Piece MakeWord(string word, ComputedStyle s, string? link) => new()
    {
        Kind = PieceKind.Word,
        Style = s,
        Link = link,
        Text = word,
        Width = s.Font.Measure(word, s.FontSize) + s.LetterSpacing * word.Length,
    };

    private static string Transform(string text, TextTransform transform) => transform switch
    {
        TextTransform.Uppercase => text.ToUpperInvariant(),
        TextTransform.Lowercase => text.ToLowerInvariant(),
        TextTransform.Capitalize => Capitalize(text),
        _ => text,
    };

    private static string Capitalize(string text)
    {
        var sb = new StringBuilder(text);
        bool startOfWord = true;
        for (int i = 0; i < sb.Length; i++)
        {
            if (char.IsWhiteSpace(sb[i]))
            {
                startOfWord = true;
            }
            else if (startOfWord)
            {
                sb[i] = char.ToUpper(sb[i], CultureInfo.InvariantCulture);
                startOfWord = false;
            }
        }
        return sb.ToString();
    }

    private static bool IsBreakOpportunity(Piece p) => p.Kind == PieceKind.Space && p.CanBreak;

    private static bool IsCollapsible(Piece p) => p.Style.WhiteSpace is not (WhiteSpace.Pre or WhiteSpace.PreWrap);

    private static List<Line> BreakLines(List<Piece> pieces, double width, double indent)
    {
        var lines = new List<Line>();
        var line = new Line();
        bool hasContent = false;
        double available = width - indent;
        var pendingSpaces = new List<Piece>();
        double pendingWidth = 0;

        int i = 0;
        while (i < pieces.Count)
        {
            var p = pieces[i];
            if (p.Kind == PieceKind.Break)
            {
                line.EndsWithBreak = true;
                FinishLine();
                i++;
                continue;
            }
            if (IsBreakOpportunity(p))
            {
                if (!hasContent && IsCollapsible(p))
                {
                    i++; // spaces at the start of a line disappear
                    continue;
                }
                pendingSpaces.Add(p);
                pendingWidth += p.Width;
                i++;
                continue;
            }

            // An unbreakable segment: everything up to the next break opportunity, with images on their own.
            int end = i;
            double segmentWidth = 0;
            while (end < pieces.Count && pieces[end].Kind != PieceKind.Break && !IsBreakOpportunity(pieces[end]))
            {
                if (pieces[end].Kind == PieceKind.Image && end > i)
                    break;
                segmentWidth += pieces[end].Width;
                end++;
                if (pieces[end - 1].Kind == PieceKind.Image)
                    break;
            }
            var segment = pieces.GetRange(i, end - i);
            bool segmentHasContent = segment.Any(s => s.Kind is PieceKind.Word or PieceKind.Image or PieceKind.Space);

            if (hasContent && segmentHasContent && line.Width + pendingWidth + segmentWidth > available + Epsilon)
            {
                FinishLine();
            }

            if (!hasContent && segmentWidth > available + Epsilon && CanSplit(segment))
            {
                // Break an over-long word (such as a URL) wherever it has to.
                foreach (var part in SplitSegment(segment, available, width))
                {
                    if (hasContent)
                        FinishLine();
                    AddToLine(part, part.Sum(s => s.Width));
                }
            }
            else
            {
                AddToLine(segment, segmentWidth);
            }
            i = end;
        }

        if (line.Pieces.Count > 0)
            FinishLine();
        if (lines.Count > 0)
            lines[^1].IsLast = true;
        return lines;

        void AddToLine(List<Piece> segment, double segmentWidth)
        {
            if (hasContent)
            {
                line.Pieces.AddRange(pendingSpaces);
                line.Width += pendingWidth;
            }
            pendingSpaces.Clear();
            pendingWidth = 0;
            line.Pieces.AddRange(segment);
            line.Width += segmentWidth;
            if (segment.Any(s => s.Kind is PieceKind.Word or PieceKind.Image or PieceKind.Space))
                hasContent = true;
        }

        void FinishLine()
        {
            lines.Add(line);
            line = new Line();
            hasContent = false;
            available = width;
            pendingSpaces.Clear();
            pendingWidth = 0;
        }
    }

    private static bool CanSplit(List<Piece> segment) =>
        segment.Any(p => p.Kind == PieceKind.Word && p.Text.Length > 1 && p.Style.WhiteSpace is not (WhiteSpace.Pre or WhiteSpace.NoWrap));

    /// <summary>Splits a segment into chunks no wider than the available width, breaking between characters.</summary>
    private static List<List<Piece>> SplitSegment(List<Piece> segment, double firstAvailable, double width)
    {
        var parts = new List<List<Piece>>();
        var current = new List<Piece>();
        double currentWidth = 0;
        double available = firstAvailable;

        foreach (var piece in segment)
        {
            if (piece.Kind != PieceKind.Word || piece.Style.WhiteSpace is WhiteSpace.Pre or WhiteSpace.NoWrap)
            {
                if (currentWidth + piece.Width > available + Epsilon && current.Count > 0)
                    NewPart();
                current.Add(piece);
                currentWidth += piece.Width;
                continue;
            }

            string text = piece.Text;
            int start = 0;
            while (start < text.Length)
            {
                int end = start;
                double w = 0;
                while (end < text.Length)
                {
                    double cw = piece.Style.Font.Measure(text[end].ToString(), piece.Style.FontSize) + piece.Style.LetterSpacing;
                    if (currentWidth + w + cw > available + Epsilon && (end > start || current.Count > 0))
                        break;
                    w += cw;
                    end++;
                }
                if (end > start)
                {
                    var chunk = MakeWord(text[start..end], piece.Style, piece.Link);
                    current.Add(chunk);
                    currentWidth += chunk.Width;
                    start = end;
                }
                if (start < text.Length)
                    NewPart();
            }
        }
        if (current.Count > 0)
            parts.Add(current);
        return parts;

        void NewPart()
        {
            parts.Add(current);
            current = [];
            currentWidth = 0;
            available = width;
        }
    }

    private void PlaceLine(Line line, ComputedStyle blockStyle, double x, double width, double indent, Cursor c)
    {
        // Trailing preserved spaces do not count for alignment.
        var pieces = line.Pieces;
        int last = pieces.Count - 1;
        while (last >= 0 && pieces[last].Kind == PieceKind.Space)
            last--;
        double contentWidth = 0;
        for (int i = 0; i <= last; i++)
            contentWidth += pieces[i].Width;

        // Vertical metrics: the block's strut plus every piece on the line.
        var (strutAscent, strutDescent) = TextMetrics(blockStyle);
        double ascent = strutAscent, descent = strutDescent;
        bool visible = false;
        foreach (var p in pieces)
        {
            switch (p.Kind)
            {
                case PieceKind.Word or PieceKind.Space:
                    var (a, d) = TextMetrics(p.Style);
                    ascent = Math.Max(ascent, a + p.Style.BaselineShift);
                    descent = Math.Max(descent, d - p.Style.BaselineShift);
                    visible = true;
                    break;
                case PieceKind.Image:
                    ascent = Math.Max(ascent, p.ImageHeight + p.Style.BaselineShift);
                    descent = Math.Max(descent, -p.Style.BaselineShift);
                    visible = true;
                    break;
            }
        }
        double lineHeight = ascent + descent;
        if (!visible && !line.EndsWithBreak)
            lineHeight = 0; // a line holding only anchors takes no room

        c.Flush();
        FitOnPage(c, lineHeight);
        double baseline = c.Y + ascent;

        double free = width - indent - contentWidth;
        double offset = indent;
        double extraSpacing = 0;
        switch (blockStyle.TextAlign)
        {
            case TextAlign.Right when free > 0:
                offset += free;
                break;
            case TextAlign.Center when free > 0:
                offset += free / 2;
                break;
            case TextAlign.Justify when free > 0 && !line.IsLast && !line.EndsWithBreak:
                int spaces = 0;
                for (int i = 0; i <= last; i++)
                {
                    if (pieces[i].Kind == PieceKind.Space && pieces[i].Text == " ")
                        spaces++;
                }
                if (spaces > 0)
                    extraSpacing = free / spaces;
                break;
        }

        if (_pendingMarker is not null && visible)
            DrawMarker(_pendingMarker, _markerX, baseline);

        double penX = x + offset;
        LinkOp? lastLink = null;
        int index = 0;
        while (index <= last)
        {
            var p = pieces[index];
            if (p.Kind == PieceKind.Anchor)
            {
                _list.Anchors.TryAdd(p.AnchorId!, (penX, c.Y));
                index++;
                continue;
            }
            if (p.Kind == PieceKind.Image)
            {
                double top = baseline - p.Style.BaselineShift - p.ImageHeight;
                if (p.Style.Visible && p.Width > 0 && p.ImageHeight > 0)
                    _list.Add(new ImageOp(penX, top, p.Width, p.ImageHeight, p.Image!));
                AddLink(p.Link, penX, top, p.Width, p.ImageHeight);
                penX += p.Width;
                index++;
                continue;
            }

            // Gather a run of text pieces sharing a style and link.
            var sb = new StringBuilder();
            double runWidth = 0;
            int runSpaces = 0;
            int j = index;
            while (j <= last && pieces[j].Kind is PieceKind.Word or PieceKind.Space &&
                   ReferenceEquals(pieces[j].Style, p.Style) && pieces[j].Link == p.Link)
            {
                sb.Append(pieces[j].Text);
                runWidth += pieces[j].Width;
                if (pieces[j].Kind == PieceKind.Space && pieces[j].Text == " ")
                {
                    runWidth += extraSpacing;
                    runSpaces++;
                }
                j++;
            }
            if (j == index)
            {
                index++;
                continue;
            }
            DrawTextRun(sb.ToString(), p.Style, penX, baseline, runWidth, extraSpacing);
            var (fontAscent, fontDescent) = FontBox(p.Style);
            AddLink(p.Link, penX, baseline - p.Style.BaselineShift - fontAscent, runWidth, fontAscent + fontDescent);
            penX += runWidth;
            index = j;
        }

        c.Y += lineHeight;

        void AddLink(string? target, double lx, double ly, double lw, double lh)
        {
            if (target is null || lw <= 0)
            {
                lastLink = null;
                return;
            }
            if (lastLink is not null && lastLink.Target == target && Math.Abs(lastLink.X + lastLink.Width - lx) < Epsilon)
            {
                int at = _list.Ops.LastIndexOf(lastLink);
                double y0 = Math.Min(lastLink.Y, ly), y1 = Math.Max(lastLink.Y + lastLink.Height, ly + lh);
                lastLink = new LinkOp(lastLink.X, y0, lastLink.Width + lw, y1 - y0, target);
                _list.Ops[at] = lastLink;
                return;
            }
            lastLink = new LinkOp(lx, ly, lw, lh, target);
            _list.Add(lastLink);
        }
    }

    private void DrawTextRun(string text, ComputedStyle s, double x, double baseline, double width, double extraSpacing)
    {
        if (!s.Visible)
            return;
        double shiftedBaseline = baseline - s.BaselineShift;
        var (ascent, descent) = FontBox(s);
        if (s.InlineBackground is { } bg)
            _list.Add(new RectOp(x, shiftedBaseline - ascent, width, ascent + descent, bg));

        if (text.Trim().Length > 0)
            _list.Add(new TextOp(x, shiftedBaseline, s.Font, s.FontSize, s.Color, text, s.WordSpacing + extraSpacing, s.LetterSpacing));

        double thickness = Math.Max(0.5, s.FontSize * 0.06);
        if (s.Underline)
            _list.Add(new RectOp(x, shiftedBaseline + s.FontSize * 0.1, width, thickness, s.Color));
        if (s.LineThrough)
            _list.Add(new RectOp(x, shiftedBaseline - s.FontSize * 0.3, width, thickness, s.Color));
        if (s.Overline)
            _list.Add(new RectOp(x, shiftedBaseline - ascent, width, thickness, s.Color));
    }

    /// <summary>Ascent and descent of a line box contribution, including half-leading.</summary>
    private static (double Ascent, double Descent) TextMetrics(ComputedStyle s)
    {
        var (ascent, descent) = FontBox(s);
        double halfLeading = (s.LineHeight - (ascent + descent)) / 2;
        return (ascent + halfLeading, descent + halfLeading);
    }

    /// <summary>Ascent and descent of the font itself, without leading.</summary>
    private static (double Ascent, double Descent) FontBox(ComputedStyle s)
    {
        var font = s.Font;
        return (font.Ascent * s.FontSize / 1000, -font.Descent * s.FontSize / 1000);
    }

    /// <summary>The used size of an image in points.</summary>
    private (double Width, double Height) ImageSize(Piece piece, double containerWidth)
    {
        var s = piece.Style;
        var image = piece.Image!;
        double intrinsicWidth = image.Width * CssValues.PointsPerPixel;
        double intrinsicHeight = image.Height * CssValues.PointsPerPixel;
        bool finite = !double.IsInfinity(containerWidth);

        double? width = s.Width.IsAuto || (s.Width.Percent != 0 && !finite) ? null : s.Width.Resolve(containerWidth);
        double? height = s.Height.IsAuto || s.Height.Percent != 0 ? null : s.Height.Points;
        double w, h;
        if (width is { } fw && height is { } fh)
            (w, h) = (fw, fh);
        else if (width is { } ow)
            (w, h) = (ow, ow * intrinsicHeight / intrinsicWidth);
        else if (height is { } oh)
            (w, h) = (oh * intrinsicWidth / intrinsicHeight, oh);
        else
            (w, h) = (intrinsicWidth, intrinsicHeight);

        if (!s.MaxWidth.IsAuto && (s.MaxWidth.Percent == 0 || finite))
            Scale(s.MaxWidth.Resolve(containerWidth) / w);
        if (!s.MaxHeight.IsAuto && s.MaxHeight.Percent == 0)
            Scale(s.MaxHeight.Points / h);
        if (finite)
            Scale(containerWidth / w);
        Scale(pageHeight / h);
        return (Math.Max(0, w), Math.Max(0, h));

        void Scale(double factor)
        {
            if (factor < 1 && factor > 0)
            {
                w *= factor;
                h *= factor;
            }
        }
    }
}
