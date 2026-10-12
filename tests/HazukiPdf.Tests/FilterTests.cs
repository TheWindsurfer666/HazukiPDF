using System.Text;
using HazukiPdf.Filters;
using HazukiPdf.Objects;

namespace HazukiPdf.Tests;

public class FilterTests
{
    [Fact]
    public void LzwDecode_DecodesTheSpecificationExample()
    {
        // ISO 32000-1, 7.4.4.2: "-----A---B" encodes to these bytes.
        byte[] encoded = { 0x80, 0x0B, 0x60, 0x50, 0x22, 0x0C, 0x0C, 0x85, 0x01 };
        Assert.Equal("-----A---B", Encoding.ASCII.GetString(StreamFilters.LzwDecode(encoded, 1)));
    }

    [Fact]
    public void Ascii85Decode_HandlesGroupsZerosAndPartialGroups()
    {
        Assert.Equal("Man ", Encoding.ASCII.GetString(StreamFilters.Ascii85Decode(Encoding.ASCII.GetBytes("9jqo^~>"))));
        Assert.Equal(new byte[4], StreamFilters.Ascii85Decode(Encoding.ASCII.GetBytes("z~>")));
        Assert.Equal("Man", Encoding.ASCII.GetString(StreamFilters.Ascii85Decode(Encoding.ASCII.GetBytes("<~9jqo~>"))));
    }

    [Fact]
    public void AsciiHexDecode_IgnoresWhitespaceAndPadsOddDigit()
    {
        Assert.Equal(new byte[] { 0x12, 0xAB, 0xC0 }, StreamFilters.AsciiHexDecode(Encoding.ASCII.GetBytes("12 ab\nC>")));
    }

    [Fact]
    public void RunLengthDecode_ExpandsLiteralAndRepeatedRuns()
    {
        byte[] encoded = { 2, (byte)'a', (byte)'b', (byte)'c', 254, (byte)'x', 128 };
        Assert.Equal("abcxxx", Encoding.ASCII.GetString(StreamFilters.RunLengthDecode(encoded)));
    }

    [Fact]
    public void DeflateAndInflate_RoundTrip()
    {
        var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("hello world ", 100)));
        var compressed = StreamFilters.Deflate(data);
        Assert.True(compressed.Length < data.Length);
        Assert.Equal(data, StreamFilters.Inflate(compressed, strict: true));
    }

    [Fact]
    public void Inflate_LenientModeRecoversTruncatedData()
    {
        var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => i.ToString())));
        var compressed = StreamFilters.Deflate(data);
        var corrupt = compressed.AsSpan(0, compressed.Length / 2).ToArray();
        var recovered = StreamFilters.Inflate(corrupt, strict: false);
        Assert.True(recovered.Length > 0);
        Assert.Equal(data.AsSpan(0, recovered.Length).ToArray(), recovered);
    }

    [Fact]
    public void PngPredictors_DecodeEveryRowType()
    {
        // Two columns of RGB (6 bytes per row); each row uses a different PNG filter.
        byte[][] rows =
        {
            new byte[] { 10, 20, 30, 40, 50, 60 },
            new byte[] { 11, 21, 31, 41, 51, 61 },
            new byte[] { 5, 5, 5, 200, 100, 0 },
            new byte[] { 7, 8, 9, 10, 11, 12 },
            new byte[] { 1, 2, 3, 4, 5, 6 },
        };
        var encoded = new List<byte>();
        byte[] prior = new byte[6];
        for (int r = 0; r < rows.Length; r++)
        {
            byte[] row = rows[r];
            encoded.Add((byte)r);
            for (int i = 0; i < 6; i++)
            {
                int left = i >= 3 ? row[i - 3] : 0;
                int up = prior[i];
                int upLeft = i >= 3 ? prior[i - 3] : 0;
                int predicted = r switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    _ => Paeth(left, up, upLeft),
                };
                encoded.Add((byte)(row[i] - predicted));
            }

            prior = row;
        }

        var parms = new PdfDictionary();
        parms.Set("Predictor", new PdfInteger(15));
        parms.Set("Colors", new PdfInteger(3));
        parms.Set("Columns", new PdfInteger(2));
        Assert.Equal(rows.SelectMany(r => r).ToArray(), Predictors.Decode(encoded.ToArray(), parms));
    }

    [Fact]
    public void PngUpEncoder_RoundTripsThroughDecoder()
    {
        var data = Enumerable.Range(0, 60).Select(i => (byte)(i * 7)).ToArray();
        var parms = new PdfDictionary();
        parms.Set("Predictor", new PdfInteger(12));
        parms.Set("Columns", new PdfInteger(5));
        Assert.Equal(data, Predictors.Decode(Predictors.EncodePngUp(data, 5), parms));
    }

    [Fact]
    public void TiffPredictor_AddsLeftNeighbour()
    {
        var parms = new PdfDictionary();
        parms.Set("Predictor", new PdfInteger(2));
        parms.Set("Colors", new PdfInteger(1));
        parms.Set("Columns", new PdfInteger(4));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 10, 10, 10, 10 }, Predictors.Decode(new byte[] { 1, 1, 1, 1, 10, 0, 0, 0 }, parms));
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
