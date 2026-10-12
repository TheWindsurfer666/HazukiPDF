# HazukiPDF

A .NET 10 class library that makes PDF files smaller and converts HTML to PDF. It has no dependencies beyond the .NET base class library: the PDF parser, writer, stream filters and JPEG codec are all part of the library.

## Usage

```csharp
using HazukiPdf;

// Pick a preset...
var result = PdfCompressor.CompressFile("in.pdf", "out.pdf", PdfCompressionLevel.Balanced);
Console.WriteLine($"{result.OriginalSize} -> {result.CompressedSize} bytes ({result.Ratio:P0})");

// ...or start from one and fine-tune it.
var options = PdfCompressionOptions.FromLevel(PdfCompressionLevel.Maximum);
options.JpegQuality = 70;
options.MaxImageDpi = 150;
options.RemoveDocumentInfo = true;
byte[] smaller = PdfCompressor.Compress(File.ReadAllBytes("in.pdf"), options);

// Streams work too.
using var input = File.OpenRead("in.pdf");
using var output = File.Create("out.pdf");
PdfCompressor.Compress(input, output, PdfCompressionLevel.Lossless);
```

## Presets

| Level | What it does |
|---|---|
| `Lossless` | Pages look exactly the same. Recompresses streams with maximum Deflate, packs objects into object streams, removes unused and duplicate objects. |
| `Balanced` | Lossless, plus JPEG recompression of photos at quality 80, downsampling of images shown above 200 DPI, and removal of page thumbnails and private editor data. |
| `Maximum` | Like Balanced, with JPEG quality 60, downsampling above 120 DPI, and XMP metadata removed. Good for screen reading and email. |

## Options

| Option | Default (`new PdfCompressionOptions()`) | |
|---|---|---|
| `RecompressStreams` | on | Decodes Flate, LZW, ASCII85, ASCIIHex and RunLength streams and re-encodes them with maximum Deflate. Uncompressed streams get compressed. |
| `OptimizeJpegs` | on | Re-encodes existing baseline JPEGs with Huffman tables optimized for each image and drops EXIF and other extra segments. The pixels stay identical. |
| `UseJbig2` | on | Re-encodes black-and-white images, such as scanned pages, with lossless JBIG2 (PDF 1.4). Scans typically shrink by half; the pixels stay identical. |
| `SubsetFonts` | on | Removes unused glyphs from embedded TrueType fonts that address glyphs directly. Microsoft Print to PDF, for example, embeds whole fonts of several hundred kilobytes. |
| `UseObjectStreams` | on | Packs small objects into compressed object streams with a cross-reference stream (PDF 1.5). |
| `RemoveUnusedObjects` | on | Drops objects nothing refers to, such as leftovers from incremental edits. |
| `DeduplicateObjects` | on | Merges identical objects, such as a font or image embedded on every page. |
| `CompressImages` | off | Re-encodes 8-bit grayscale and RGB images as JPEG. |
| `JpegQuality` | 75 | 1–100. |
| `MaxImageDpi` | 150 | Images drawn at a higher resolution are downsampled to it. `null` turns downsampling off. |
| `RemoveMetadata` | off | Removes XMP metadata (breaks PDF/A conformance). |
| `RemoveDocumentInfo` | off | Removes title, author, producer and so on. |
| `RemoveThumbnails` | off | Removes embedded page thumbnails. |
| `RemovePrivateApplicationData` | off | Removes `/PieceInfo` data stored by editors such as Illustrator. |
| `Password` | null | User or owner password for encrypted files. Files that only restrict printing or editing open without one. |

## How images are handled

The library walks every page's content (including forms, patterns and annotation appearances), tracking the transformation matrix, to work out how large each image is drawn. Images shown above `MaxImageDpi` are downsampled with area averaging. Images are then encoded with the built-in baseline JPEG encoder; RGB images that turn out to be gray are stored as grayscale.

To protect quality, these images are left alone: image masks and soft masks, color-key masked images, CMYK, indexed and other special color spaces, images with fewer than 256 colors (diagrams, screenshots, line art), tiny images, progressive JPEGs, and any image where the new encoding would not be meaningfully smaller.

## Limitations

- Encrypted PDFs (RC4, AES-128 and AES-256 standard security) are decrypted and written out unencrypted. A file that needs a password you did not give is rejected with `PdfEncryptedException`.
- The output is never larger than the input: if nothing can be saved, the original bytes are returned and `PdfCompressionResult.OriginalKept` is true. The exception is when a removal option was asked for and removed something.
- Output is not linearized ("fast web view").

## HTML to PDF

`HtmlToPdfConverter` turns an HTML document with CSS into a PDF. It has its own HTML parser, CSS cascade and layout engine, writes through the library's PDF writer, and sets text in the standard 14 PDF fonts, so nothing needs to be embedded.

```csharp
using HazukiPdf.Html;

// From a string...
byte[] pdf = HtmlToPdfConverter.Convert("<h1>Invoice</h1><p>Thank you for your order.</p>");

// ...from a file, with images resolved next to it...
HtmlToPdfConverter.ConvertFile("report.html", "report.pdf");

// ...or with options.
var options = new HtmlToPdfOptions
{
    PageSize = PageSize.Letter,
    Landscape = true,
    Margins = PageMargins.FromMillimeters(20, 15, 20, 15),
    BaseDirectory = "assets",              // relative <img> paths resolve here, and only here
    ResourceResolver = src => null,        // return bytes for e.g. https:// images; null falls back
    UserStyleSheet = "body { font-size: 10pt }",
    Title = "Report",
};
using var output = File.Create("out.pdf");
int pages = HtmlToPdfConverter.Convert(html, output, options);
```

### What is supported

| Area | Supported |
|---|---|
| HTML | Forgiving parser for real-world markup: implicit end tags, entities, comments, `<style>` blocks, `style` attributes, legacy attributes (`align`, `valign`, `bgcolor`, `width`, `height`, `border`, `cellpadding`, `cellspacing`, `<font>`, `<center>`). |
| Text | Headings, paragraphs, `b`/`strong`, `i`/`em`, `u`, `s`/`del`, `sub`/`sup`, `small`/`big`, `mark`, `code`/`pre`, `blockquote`, `br`, `hr`, `q`, `dl`. Word wrapping (also after hyphens and inside over-long words), `text-align` including `justify`, `text-indent`, `text-transform`, `letter-spacing`, `word-spacing`, `white-space` (`normal`, `nowrap`, `pre`, `pre-wrap`, `pre-line`). |
| Fonts | `font-family` maps onto Helvetica (sans-serif, the default), Times (serif) or Courier (monospace), with bold and italic. Text is WinAnsi (Windows-1252) encoded: Western European languages work; other characters become `?`. |
| Lists | `ul`/`ol`, nesting, `start`, `reversed`, `value`, `list-style-type` (`disc`, `circle`, `square`, `decimal`, `decimal-leading-zero`, `lower`/`upper-alpha`, `lower`/`upper-roman`, `none`). |
| Tables | Automatic and fixed column widths, `colspan`, `thead` rows repeated on every page, `caption`, `border-collapse`, `border-spacing`, cell `vertical-align`, row and cell backgrounds. Rows are kept on one page when they fit. |
| Box model | `display` (`block`, `inline`, `list-item`, `table` parts, `none`), `margin` (with collapsing and `auto` centering), `padding`, `border` (`solid`, `dashed`, `dotted`, `double`), `width`, `max-width`, `height`, `min-height`, `background-color`, `color`, `visibility`. A background on `html` or `body` fills the page. |
| Selectors | Type, class, id, universal, attribute (`[a]`, `=`, `~=`, `\|=`, `^=`, `$=`, `*=`), descendant, `>`, `+`, `~`, `:first-child`, `:last-child`, `:only-child`, `:nth-child()`, `:nth-last-child()`, `:first-of-type`, `:last-of-type`, `:nth-of-type()`, `:not()`, `:root`, `:empty`, `:link`. Specificity, source order and `!important` are honoured. |
| Values | Units `px`, `pt`, `pc`, `in`, `cm`, `mm`, `em`, `rem`, `ex`, `ch`, `%`, simple `calc()`. Colors as names, `#rgb`, `#rrggbb`, `rgb()`, `rgba()`, `hsl()`. |
| Pages | `page-break-before`/`after` and `break-before`/`after`, `@page { size; margin }` (`A3`, `A4`, `A5`, `letter`, `legal`, `landscape`, explicit sizes), `@media print` rules. |
| Images | PNG (all color types and bit depths, interlaced, transparency as a soft mask) and JPEG (passed through untouched) from `data:` URIs, files under `BaseDirectory`, or a `ResourceResolver`. Missing images show their `alt` text. Images are scaled to fit the page. |
| Links | `<a href>` to `http`, `https`, `mailto`, `ftp` and `tel` URLs become link annotations; `href="#id"` jumps to the element with that id. |
| Metadata | Title from `<title>` (or `Title`), `Author`, creation date. |

### What is not supported

- Floats, `position` (absolute, relative, fixed), flexbox and grid. Such elements are laid out in normal flow (`display: flex` and `grid` act as `block`, `inline-block` acts as `inline`).
- `rowspan` (cells are laid out as if it were 1), `<col>` widths and `table-layout: fixed`.
- Fonts other than the standard 14 (`@font-face` is ignored), and text outside Windows-1252, such as Cyrillic, Greek, CJK, emoji and right-to-left scripts.
- `::before`/`::after` and other pseudo-elements, counters, CSS variables, `@import`.
- Background images, gradients, shadows, rounded corners, opacity, transforms, `overflow` clipping, `box-sizing`, `outline`.
- Inline `padding`, `border` and `margin` on inline elements (inline backgrounds work), and `vertical-align` other than `sub`, `super` and lengths on inline content.
- GIF, BMP, WebP and SVG images; `<svg>`, `<canvas>`, form controls, `<iframe>`, video and audio are not drawn.
- JavaScript is never run, and remote resources are only fetched through your `ResourceResolver`.
- Widows, orphans and `break-inside: avoid`; a block's top and bottom borders are not repeated where it is split across pages.

## Desktop app

`src/HazukiPdf.WinForms` is a Windows Forms app (Windows only) built on the library.

- **Compress PDF**: add one PDF or many (buttons, or drop files and folders on the list), pick Lossless, Balanced or Maximum, and press Compress. Each file gets a row with its original size, new size and the percentage saved, plus a total for the batch. Results are saved next to the originals with a suffix (`report_compressed.pdf`) or in a folder you choose; originals are never overwritten. Double-click a row to open the result.
- **HTML to PDF**: paste HTML, open an HTML file, or enter a web address and press Load URL. Press Convert to PDF and choose where to save it. For web pages the app downloads linked style sheets and images with `HttpClient`, since the converter itself never touches the network.

A progress bar shows how many files are done, and a log under both tabs lists every file with its result (copy it or save it to a text file). The output folders and level are remembered between runs. Work runs off the UI thread and can be cancelled. Run it with `dotnet run --project src/HazukiPdf.WinForms`, or download the `HazukiPdf.WinForms` artifact from a CI run (needs the .NET 10 Desktop Runtime). The project sets `EnableWindowsTargeting`, so the whole solution still builds on Linux and macOS; the app's UI-free services (`src/HazukiPdf.WinForms/Services`) are compiled into the test project and tested on every OS.

## Building

```
dotnet build
dotnet test
```
