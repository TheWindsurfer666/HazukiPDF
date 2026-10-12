using System.Globalization;
using System.Text;
using HazukiPdf.Filters;

namespace HazukiPdf.Tests;

/// <summary>Builds small PDF files with a classic cross-reference table for tests.</summary>
internal sealed class TestPdf
{
    private readonly List<byte[]?> _objects = new();

    public int Reserve()
    {
        _objects.Add(null);
        return _objects.Count;
    }

    public int Add(string body)
    {
        int n = Reserve();
        Set(n, body);
        return n;
    }

    public void Set(int number, string body) => _objects[number - 1] = Encoding.Latin1.GetBytes(body);

    public int AddStream(string dictionaryEntries, byte[] data)
    {
        int n = Reserve();
        SetStream(n, dictionaryEntries, data);
        return n;
    }

    public void SetStream(int number, string dictionaryEntries, byte[] data)
    {
        var head = Encoding.Latin1.GetBytes($"<< {dictionaryEntries} /Length {data.Length} >>\nstream\n");
        var tail = "\nendstream"u8.ToArray();
        _objects[number - 1] = head.Concat(data).Concat(tail).ToArray();
    }

    public byte[] Build(int root, int? info = null, string extraTrailer = "", string version = "1.4")
    {
        using var output = new MemoryStream();
        void Write(string s) => output.Write(Encoding.Latin1.GetBytes(s));
        Write($"%PDF-{version}\n%âãÏÓ\n");
        var offsets = new long[_objects.Count + 1];
        for (int i = 0; i < _objects.Count; i++)
        {
            if (_objects[i] == null)
            {
                continue;
            }

            offsets[i + 1] = output.Position;
            Write($"{i + 1} 0 obj\n");
            output.Write(_objects[i]);
            Write("\nendobj\n");
        }

        long xref = output.Position;
        Write($"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= _objects.Count; i++)
        {
            Write(_objects[i - 1] == null ? "0000000000 65535 f \n" : $"{offsets[i].ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        }

        string infoEntry = info.HasValue ? $" /Info {info} 0 R" : string.Empty;
        Write($"trailer\n<< /Size {_objects.Count + 1} /Root {root} 0 R{infoEntry} {extraTrailer} >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    /// <summary>A document with one page per content string, all sharing one Helvetica font.</summary>
    public static byte[] WithPages(params string[] contents)
    {
        var pdf = new TestPdf();
        int catalog = pdf.Reserve();
        int pages = pdf.Reserve();
        int font = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var kids = new List<int>();
        foreach (var content in contents)
        {
            int stream = pdf.AddStream(string.Empty, Encoding.Latin1.GetBytes(content));
            kids.Add(pdf.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R >> >> /Contents {stream} 0 R >>"));
        }

        pdf.Set(pages, $"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>");
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        return pdf.Build(catalog);
    }

    public static string TextPage(int lines = 40)
    {
        var sb = new StringBuilder("BT /F1 10 Tf 40 760 Td 12 TL\n");
        for (int i = 0; i < lines; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"(Line {i}: The quick brown fox jumps over the lazy dog.) '\n");
        }

        return sb.Append("ET\n").ToString();
    }

    /// <summary>A photo-like RGB image with smooth gradients and noise, so it has many distinct colors.</summary>
    public static byte[] PhotoPixels(int width, int height, int components = 3, int seed = 1)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height * components];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = ((y * width) + x) * components;
                int noise = random.Next(-6, 7);
                pixels[o] = Clamp(128 + (100 * Math.Sin(x / 37.0)) + noise);
                if (components == 3)
                {
                    pixels[o + 1] = Clamp(128 + (100 * Math.Cos(y / 23.0)) + noise);
                    pixels[o + 2] = Clamp((x + y) * 255.0 / (width + height) + noise);
                }
            }
        }

        return pixels;
    }

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    /// <summary>A one-page document showing an image (stored with Flate) at the given size in points.</summary>
    public static byte[] WithImage(byte[] pixels, int width, int height, string colorSpace, double shownWidth, double shownHeight, string extraImageEntries = "")
    {
        var pdf = new TestPdf();
        int catalog = pdf.Reserve();
        int pages = pdf.Reserve();
        int image = pdf.AddStream($"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {colorSpace} /BitsPerComponent 8 /Filter /FlateDecode {extraImageEntries}", StreamFilters.Deflate(pixels));
        string content = string.Create(CultureInfo.InvariantCulture, $"q {shownWidth} 0 0 {shownHeight} 50 50 cm /Im1 Do Q");
        int contents = pdf.AddStream(string.Empty, Encoding.ASCII.GetBytes(content));
        int page = pdf.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im1 {image} 0 R >> >> /Contents {contents} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        return pdf.Build(catalog);
    }
}
