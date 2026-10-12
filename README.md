# HazukiPDF

A .NET 10 class library that makes PDF files smaller. It has no dependencies beyond the .NET base class library: the PDF parser, writer, stream filters and JPEG codec are all part of the library.

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

## How images are handled

The library walks every page's content (including forms, patterns and annotation appearances), tracking the transformation matrix, to work out how large each image is drawn. Images shown above `MaxImageDpi` are downsampled with area averaging. Images are then encoded with the built-in baseline JPEG encoder; RGB images that turn out to be gray are stored as grayscale.

To protect quality, these images are left alone: image masks and soft masks, color-key masked images, CMYK, indexed and other special color spaces, images with fewer than 256 colors (diagrams, screenshots, line art), tiny images, progressive JPEGs, and any image where the new encoding would not be meaningfully smaller.

## Limitations

- Encrypted PDFs are rejected with `PdfEncryptedException`.
- The output is never larger than the input: if nothing can be saved, the original bytes are returned and `PdfCompressionResult.OriginalKept` is true. The exception is when a removal option was asked for and removed something.
- Output is not linearized ("fast web view").

## Building

```
dotnet build
dotnet test
```
