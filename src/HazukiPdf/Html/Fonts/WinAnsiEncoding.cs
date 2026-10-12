namespace HazukiPdf.Html.Fonts;

/// <summary>Maps Unicode characters onto WinAnsiEncoding (Windows code page 1252) byte codes.</summary>
internal static class WinAnsiEncoding
{
    /// <summary>Code used for characters the encoding cannot represent.</summary>
    public const byte Replacement = (byte)'?';

    private static readonly Dictionary<char, byte> Specials = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84,
        ['…'] = 0x85, ['†'] = 0x86, ['‡'] = 0x87, ['ˆ'] = 0x88,
        ['‰'] = 0x89, ['Š'] = 0x8A, ['‹'] = 0x8B, ['Œ'] = 0x8C,
        ['Ž'] = 0x8E, ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93,
        ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97,
        ['˜'] = 0x98, ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B,
        ['œ'] = 0x9C, ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
        // Common look-alikes outside the encoding.
        ['−'] = (byte)'-', ['‐'] = (byte)'-', ['‑'] = (byte)'-',
        [' '] = (byte)' ', [' '] = (byte)' ', [' '] = (byte)' ', [' '] = 0xA0,
        ['◦'] = (byte)'o', ['▪'] = 0x95, ['■'] = 0x95,
    };

    public static byte Encode(char c)
    {
        if (c is >= ' ' and <= '~' or >= ' ' and <= 'ÿ')
            return (byte)c;
        return Specials.TryGetValue(c, out byte b) ? b : Replacement;
    }

    public static byte[] Encode(string text)
    {
        var bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
            bytes[i] = Encode(text[i]);
        return bytes;
    }
}
