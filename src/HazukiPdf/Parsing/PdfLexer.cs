using System.Globalization;
using System.Text;
using HazukiPdf.Objects;

namespace HazukiPdf.Parsing;

internal enum TokenType
{
    EndOfFile,
    Integer,
    Real,
    String,
    HexString,
    Name,
    ArrayStart,
    ArrayEnd,
    DictionaryStart,
    DictionaryEnd,
    Keyword,
}

internal readonly record struct Token(TokenType Type, string Text, byte[]? Bytes, long Start)
{
    public long IntegerValue => long.Parse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
}

/// <summary>Splits PDF bytes into tokens (ISO 32000-1, section 7.2).</summary>
internal sealed class PdfLexer
{
    private readonly byte[] _data;
    private readonly int _end;

    public PdfLexer(byte[] data, int start = 0, int? end = null)
    {
        _data = data;
        Position = start;
        _end = end ?? data.Length;
    }

    public int Position { get; set; }

    public byte[] Data => _data;

    public int End => _end;

    public static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'['
        or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    public static bool IsRegular(byte b) => !IsWhitespace(b) && !IsDelimiter(b);

    public void SkipWhitespaceAndComments()
    {
        while (Position < _end)
        {
            byte b = _data[Position];
            if (IsWhitespace(b))
            {
                Position++;
            }
            else if (b == '%')
            {
                while (Position < _end && _data[Position] != '\r' && _data[Position] != '\n')
                {
                    Position++;
                }
            }
            else
            {
                break;
            }
        }
    }

    public Token Peek()
    {
        int saved = Position;
        var token = Next();
        Position = saved;
        return token;
    }

    public Token Next()
    {
        SkipWhitespaceAndComments();
        int start = Position;
        if (Position >= _end)
        {
            return new Token(TokenType.EndOfFile, string.Empty, null, start);
        }

        byte b = _data[Position];
        switch (b)
        {
            case (byte)'[':
                Position++;
                return new Token(TokenType.ArrayStart, "[", null, start);
            case (byte)']':
                Position++;
                return new Token(TokenType.ArrayEnd, "]", null, start);
            case (byte)'{':
                Position++;
                return new Token(TokenType.Keyword, "{", null, start);
            case (byte)'}':
                Position++;
                return new Token(TokenType.Keyword, "}", null, start);
            case (byte)'/':
                return new Token(TokenType.Name, ReadName(), null, start);
            case (byte)'(':
                return new Token(TokenType.String, string.Empty, ReadLiteralString(), start);
            case (byte)')':
                Position++;
                return Next();
            case (byte)'<':
                if (Position + 1 < _end && _data[Position + 1] == '<')
                {
                    Position += 2;
                    return new Token(TokenType.DictionaryStart, "<<", null, start);
                }

                return new Token(TokenType.HexString, string.Empty, ReadHexString(), start);
            case (byte)'>':
                if (Position + 1 < _end && _data[Position + 1] == '>')
                {
                    Position += 2;
                    return new Token(TokenType.DictionaryEnd, ">>", null, start);
                }

                Position++;
                return Next();
        }

        while (Position < _end && IsRegular(_data[Position]))
        {
            Position++;
        }

        string text = Encoding.Latin1.GetString(_data, start, Position - start);
        return new Token(ClassifyRegular(text), text, null, start);
    }

    private static TokenType ClassifyRegular(string text)
    {
        bool digits = false;
        bool dot = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is >= '0' and <= '9')
            {
                digits = true;
            }
            else if (c == '.')
            {
                if (dot)
                {
                    return TokenType.Keyword;
                }

                dot = true;
            }
            else if ((c == '+' || c == '-') && i == 0)
            {
                continue;
            }
            else
            {
                return TokenType.Keyword;
            }
        }

        if (!digits && !dot)
        {
            return TokenType.Keyword;
        }

        return dot ? TokenType.Real : TokenType.Integer;
    }

    private string ReadName()
    {
        Position++; // '/'
        var sb = new StringBuilder();
        while (Position < _end && IsRegular(_data[Position]))
        {
            byte b = _data[Position];
            if (b == '#' && Position + 2 < _end && IsHexDigit(_data[Position + 1]) && IsHexDigit(_data[Position + 2]))
            {
                sb.Append((char)((HexValue(_data[Position + 1]) << 4) | HexValue(_data[Position + 2])));
                Position += 3;
            }
            else
            {
                sb.Append((char)b);
                Position++;
            }
        }

        return sb.ToString();
    }

    private byte[] ReadLiteralString()
    {
        Position++; // '('
        var result = new List<byte>();
        int depth = 1;
        while (Position < _end)
        {
            byte b = _data[Position++];
            if (b == '\\')
            {
                if (Position >= _end)
                {
                    break;
                }

                byte e = _data[Position++];
                switch (e)
                {
                    case (byte)'n': result.Add(10); break;
                    case (byte)'r': result.Add(13); break;
                    case (byte)'t': result.Add(9); break;
                    case (byte)'b': result.Add(8); break;
                    case (byte)'f': result.Add(12); break;
                    case (byte)'\r':
                        if (Position < _end && _data[Position] == '\n')
                        {
                            Position++;
                        }

                        break;
                    case (byte)'\n':
                        break;
                    default:
                        if (e is >= (byte)'0' and <= (byte)'7')
                        {
                            int value = e - '0';
                            for (int i = 0; i < 2 && Position < _end && _data[Position] is >= (byte)'0' and <= (byte)'7'; i++)
                            {
                                value = (value * 8) + (_data[Position++] - '0');
                            }

                            result.Add((byte)value);
                        }
                        else
                        {
                            result.Add(e);
                        }

                        break;
                }
            }
            else if (b == '(')
            {
                depth++;
                result.Add(b);
            }
            else if (b == ')')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }

                result.Add(b);
            }
            else if (b == '\r')
            {
                // An unescaped end-of-line inside a string is read as a single line feed.
                if (Position < _end && _data[Position] == '\n')
                {
                    Position++;
                }

                result.Add(10);
            }
            else
            {
                result.Add(b);
            }
        }

        return result.ToArray();
    }

    private byte[] ReadHexString()
    {
        Position++; // '<'
        var result = new List<byte>();
        int high = -1;
        while (Position < _end)
        {
            byte b = _data[Position++];
            if (b == '>')
            {
                break;
            }

            if (!IsHexDigit(b))
            {
                continue;
            }

            if (high < 0)
            {
                high = HexValue(b);
            }
            else
            {
                result.Add((byte)((high << 4) | HexValue(b)));
                high = -1;
            }
        }

        if (high >= 0)
        {
            result.Add((byte)(high << 4));
        }

        return result.ToArray();
    }

    public static bool IsHexDigit(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    public static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        _ => b - 'A' + 10,
    };

    /// <summary>Parses one direct object starting at the current position, resolving "n g R" references.</summary>
    public PdfObject? ReadObject() => ReadObject(Next());

    public PdfObject? ReadObject(Token token, int depth = 0)
    {
        if (depth > 512)
        {
            throw new PdfFormatException("Objects are nested too deeply.");
        }

        switch (token.Type)
        {
            case TokenType.Integer:
            {
                // Look ahead for "gen R".
                int saved = Position;
                var second = Next();
                if (second.Type == TokenType.Integer)
                {
                    var third = Next();
                    if (third.Type == TokenType.Keyword && third.Text == "R")
                    {
                        if (long.TryParse(token.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long num) &&
                            long.TryParse(second.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long gen) &&
                            num is > 0 and < int.MaxValue && gen is >= 0 and < int.MaxValue)
                        {
                            return new PdfReference((int)num, (int)gen);
                        }

                        return PdfNull.Instance;
                    }
                }

                Position = saved;
                return ParseInteger(token.Text);
            }

            case TokenType.Real:
                return ParseReal(token.Text);
            case TokenType.String:
                return new PdfString(token.Bytes!);
            case TokenType.HexString:
                return new PdfString(token.Bytes!, isHex: true);
            case TokenType.Name:
                return PdfName.Get(token.Text);
            case TokenType.ArrayStart:
            {
                var array = new PdfArray();
                while (true)
                {
                    var next = Next();
                    if (next.Type is TokenType.ArrayEnd or TokenType.EndOfFile)
                    {
                        break;
                    }

                    if (next.Type == TokenType.Keyword && next.Text is "endobj" or "obj" or "stream" or "endstream")
                    {
                        Position = (int)next.Start;
                        break;
                    }

                    var item = ReadObject(next, depth + 1);
                    if (item != null)
                    {
                        array.Add(item);
                    }
                }

                return array;
            }

            case TokenType.DictionaryStart:
            {
                var dict = new PdfDictionary();
                while (true)
                {
                    var key = Next();
                    if (key.Type is TokenType.DictionaryEnd or TokenType.EndOfFile)
                    {
                        break;
                    }

                    if (key.Type == TokenType.Keyword && key.Text is "endobj" or "obj" or "stream" or "endstream")
                    {
                        Position = (int)key.Start;
                        break;
                    }

                    if (key.Type != TokenType.Name)
                    {
                        // Malformed entry; skip it and keep going.
                        continue;
                    }

                    var valueToken = Next();
                    if (valueToken.Type == TokenType.DictionaryEnd)
                    {
                        break;
                    }

                    var value = ReadObject(valueToken, depth + 1);
                    if (value != null && value is not PdfNull)
                    {
                        dict.AddParsed(PdfName.Get(key.Text), value);
                    }
                }

                return dict;
            }

            case TokenType.Keyword:
                return token.Text switch
                {
                    "true" => PdfBoolean.True,
                    "false" => PdfBoolean.False,
                    "null" => PdfNull.Instance,
                    _ => null,
                };
            default:
                return null;
        }
    }

    public static PdfObject ParseInteger(string text)
    {
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
        {
            return new PdfInteger(value);
        }

        return ParseReal(text);
    }

    public static PdfReal ParseReal(string text)
    {
        string normalized = text;
        if (normalized.StartsWith('+'))
        {
            normalized = normalized[1..];
        }

        if (normalized is "." or "-." or "-" or "")
        {
            return new PdfReal(0);
        }

        double.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value);

        // Keep the original text only when it is already in canonical PDF form.
        bool canonical = !text.StartsWith('+') && !text.EndsWith('.') && !text.StartsWith("-.", StringComparison.Ordinal);
        return new PdfReal(value, canonical ? text : null);
    }
}
