using HazukiPdf.Html.Css;
using HazukiPdf.Html.Images;

namespace HazukiPdf.Html.Layout;

/// <summary>A block-level box: either a container of other blocks or a block of inline content.</summary>
internal class BlockBox(ComputedStyle style, string? id = null)
{
    public ComputedStyle Style { get; } = style;

    /// <summary>The element id, used as a target for internal links.</summary>
    public string? Id { get; } = id;

    /// <summary>Child boxes (<see cref="BlockBox"/> or <see cref="TableBox"/>), when this box contains blocks.</summary>
    public List<object> Children { get; } = [];

    /// <summary>Inline content, when this box establishes an inline formatting context.</summary>
    public InlineContent? Inline { get; set; }

    /// <summary>List marker for display: list-item boxes.</summary>
    public ListMarker? Marker { get; set; }
}

internal sealed class TableCellBox(ComputedStyle style, int colSpan, string? id) : BlockBox(style, id)
{
    public int ColSpan { get; } = colSpan;
}

internal sealed class TableRowBox(ComputedStyle style)
{
    public ComputedStyle Style { get; } = style;

    public List<TableCellBox> Cells { get; } = [];
}

internal sealed class TableBox(ComputedStyle style, string? id)
{
    public ComputedStyle Style { get; } = style;

    public string? Id { get; } = id;

    public BlockBox? Caption { get; set; }

    /// <summary>Header rows, repeated at the top of every page the table continues onto.</summary>
    public List<TableRowBox> HeaderRows { get; } = [];

    public List<TableRowBox> BodyRows { get; } = [];

    public List<TableRowBox> FooterRows { get; } = [];

    public IEnumerable<TableRowBox> AllRows => HeaderRows.Concat(BodyRows).Concat(FooterRows);
}

internal enum MarkerShape { None, Disc, Circle, Square }

internal sealed record ListMarker(string? Text, MarkerShape Shape, ComputedStyle Style);

/// <summary>A sequence of inline items laid out into lines.</summary>
internal sealed class InlineContent
{
    public List<InlineItem> Items { get; } = [];

    /// <summary>Cached result of splitting the items into measured pieces.</summary>
    internal List<Piece>? Pieces { get; set; }

    public bool IsEmpty => Items.All(i => i is TextItem t && t.Text.Length == 0);
}

internal abstract record InlineItem(ComputedStyle Style, string? Link);

/// <summary>Text with whitespace already processed according to the white-space property.</summary>
internal sealed record TextItem(string Text, ComputedStyle Style, string? Link) : InlineItem(Style, Link);

internal sealed record ImageItem(ImageData Image, ComputedStyle Style, string? Link) : InlineItem(Style, Link);

internal sealed record LineBreakItem(ComputedStyle Style) : InlineItem(Style, null);

/// <summary>Marks the position of an element id inside inline content.</summary>
internal sealed record AnchorItem(string Id, ComputedStyle Style) : InlineItem(Style, null);

internal enum PieceKind { Word, Space, Image, Break, Anchor }

/// <summary>A measured, unbreakable unit of inline content.</summary>
internal sealed class Piece
{
    public required PieceKind Kind { get; init; }
    public required ComputedStyle Style { get; init; }
    public string Text { get; init; } = "";
    public string? Link { get; init; }
    public ImageData? Image { get; init; }
    public string? AnchorId { get; init; }

    /// <summary>Natural width in points (images get their size at layout time).</summary>
    public double Width { get; set; }
    public double ImageHeight { get; set; }

    /// <summary>Whether a line may break at this space.</summary>
    public bool CanBreak { get; init; }
}
