using System.Security.Cryptography;
using System.Text;
using HazukiPdf.Objects;

namespace HazukiPdf.Security;

/// <summary>
/// Decrypts files protected with the standard security handler (ISO 32000-2, section 7.6.4): RC4 40 to 128 bit
/// (revisions 2 to 4), AES-128 (revision 4) and AES-256 (revisions 5 and 6). Opens files with an empty user
/// password, which covers the common case of files that only restrict printing or editing, or with a given password.
/// </summary>
internal sealed class StandardSecurityHandler
{
    private static readonly byte[] Padding =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    private readonly byte[] _key;
    private readonly int _revision;
    private readonly CryptMethod _streamMethod;
    private readonly CryptMethod _stringMethod;
    private readonly bool _encryptMetadata;

    private enum CryptMethod
    {
        None,
        Rc4,
        Aes128,
        Aes256,
    }

    private StandardSecurityHandler(byte[] key, int revision, CryptMethod streamMethod, CryptMethod stringMethod, bool encryptMetadata)
    {
        _key = key;
        _revision = revision;
        _streamMethod = streamMethod;
        _stringMethod = stringMethod;
        _encryptMetadata = encryptMetadata;
    }

    /// <summary>
    /// Creates a handler, or throws <see cref="PdfEncryptedException"/> when the security handler is not supported
    /// or the password (empty by default) does not open the file.
    /// </summary>
    public static StandardSecurityHandler Create(PdfDictionary encrypt, byte[] firstId, string? password)
    {
        if (encrypt.GetName("Filter") != "Standard")
        {
            throw new PdfEncryptedException($"The file uses the unsupported {encrypt.GetName("Filter") ?? "unknown"} security handler.");
        }

        int v = GetInt(encrypt, "V", 0);
        int r = GetInt(encrypt, "R", 0);
        byte[] o = GetBytes(encrypt, "O");
        byte[] u = GetBytes(encrypt, "U");
        int p = GetInt(encrypt, "P", 0);
        bool encryptMetadata = encrypt.Get("EncryptMetadata") is not PdfBoolean { Value: false };

        CryptMethod streamMethod = CryptMethod.Rc4;
        CryptMethod stringMethod = CryptMethod.Rc4;
        if (v >= 4)
        {
            var filters = encrypt.Get("CF") as PdfDictionary;
            streamMethod = MethodFor(filters, encrypt.GetName("StmF") ?? "Identity");
            stringMethod = MethodFor(filters, encrypt.GetName("StrF") ?? "Identity");
        }

        if (r is 5 or 6)
        {
            var key = OpenAes256(encrypt, u, o, r, password ?? string.Empty)
                ?? throw new PdfEncryptedException("The file needs a password to open.");
            return new StandardSecurityHandler(key, r, streamMethod, stringMethod, encryptMetadata);
        }

        if (r is < 2 or > 4)
        {
            throw new PdfEncryptedException($"Security handler revision {r} is not supported.");
        }

        int length = r == 2 ? 5 : Math.Clamp(GetInt(encrypt, "Length", 40), 40, 128) / 8;
        byte[] passwordBytes = Encoding.Latin1.GetBytes(password ?? string.Empty);

        // Try the password as the user password, then as the owner password.
        var userKey = ComputeKey(passwordBytes, o, p, firstId, r, length, encryptMetadata);
        if (CheckUserKey(userKey, u, firstId, r))
        {
            return new StandardSecurityHandler(userKey, r, streamMethod, stringMethod, encryptMetadata);
        }

        var userPassword = RecoverUserPassword(passwordBytes, o, r, length);
        var ownerKey = ComputeKey(userPassword, o, p, firstId, r, length, encryptMetadata);
        if (CheckUserKey(ownerKey, u, firstId, r))
        {
            return new StandardSecurityHandler(ownerKey, r, streamMethod, stringMethod, encryptMetadata);
        }

        throw new PdfEncryptedException("The file needs a password to open.");
    }

    public byte[] DecryptString(byte[] data, int objectNumber, int generation) =>
        Decrypt(data, objectNumber, generation, _stringMethod);

    public byte[] DecryptStream(PdfStream stream, int objectNumber, int generation)
    {
        if (stream.GetName("Type") == "XRef" || (!_encryptMetadata && stream.GetName("Type") == "Metadata"))
        {
            return stream.Data;
        }

        // A stream whose first filter is /Crypt with no parameters uses the Identity filter (not encrypted).
        if (stream.Get("Filter") is PdfName { Value: "Crypt" } ||
            (stream.Get("Filter") is PdfArray filters && filters.Count > 0 && filters[0] is PdfName { Value: "Crypt" }))
        {
            return stream.Data;
        }

        return Decrypt(stream.Data, objectNumber, generation, _streamMethod);
    }

    /// <summary>Decrypts every string inside a direct object, in place.</summary>
    public PdfObject DecryptStrings(PdfObject obj, int objectNumber, int generation, int depth = 0)
    {
        if (depth > 256)
        {
            return obj;
        }

        switch (obj)
        {
            case PdfString s:
                return new PdfString(DecryptString(s.Value, objectNumber, generation), s.IsHex);
            case PdfArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    a[i] = DecryptStrings(a[i], objectNumber, generation, depth + 1);
                }

                break;
            case PdfDictionary d:
                for (int i = 0; i < d.Count; i++)
                {
                    var value = d.EntryAt(i).Value;
                    var decrypted = DecryptStrings(value, objectNumber, generation, depth + 1);
                    if (!ReferenceEquals(value, decrypted))
                    {
                        d.ReplaceValue(i, decrypted);
                    }
                }

                break;
        }

        return obj;
    }

    private byte[] Decrypt(byte[] data, int objectNumber, int generation, CryptMethod method)
    {
        switch (method)
        {
            case CryptMethod.None:
                return data;
            case CryptMethod.Aes256:
                return AesDecrypt(_key, data);
        }

        // Algorithm 1: per-object key.
        var input = new byte[_key.Length + 5 + (method == CryptMethod.Aes128 ? 4 : 0)];
        _key.CopyTo(input, 0);
        input[_key.Length] = (byte)objectNumber;
        input[_key.Length + 1] = (byte)(objectNumber >> 8);
        input[_key.Length + 2] = (byte)(objectNumber >> 16);
        input[_key.Length + 3] = (byte)generation;
        input[_key.Length + 4] = (byte)(generation >> 8);
        if (method == CryptMethod.Aes128)
        {
            "sAlT"u8.CopyTo(input.AsSpan(_key.Length + 5));
        }

        var hash = MD5.HashData(input);
        var objectKey = hash.AsSpan(0, Math.Min(_key.Length + 5, 16)).ToArray();
        return method == CryptMethod.Aes128 ? AesDecrypt(objectKey, data) : Rc4(objectKey, data);
    }

    private static CryptMethod MethodFor(PdfDictionary? filters, string name)
    {
        if (name == "Identity")
        {
            return CryptMethod.None;
        }

        var filter = filters?.Get(name) as PdfDictionary;
        return filter?.GetName("CFM") switch
        {
            "V2" => CryptMethod.Rc4,
            "AESV2" => CryptMethod.Aes128,
            "AESV3" => CryptMethod.Aes256,
            "None" => CryptMethod.None,
            null => CryptMethod.None,
            var other => throw new PdfEncryptedException($"The {other} crypt filter is not supported."),
        };
    }

    // ------------------------------------------------------------------ RC4 / AES-128 (revisions 2-4)

    /// <summary>Algorithm 2: the file key from a user password.</summary>
    private static byte[] ComputeKey(byte[] password, byte[] o, int p, byte[] firstId, int r, int length, bool encryptMetadata)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(Pad(password));
        md5.AppendData(o.AsSpan(0, Math.Min(32, o.Length)));
        md5.AppendData(new[] { (byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24) });
        md5.AppendData(firstId);
        if (r >= 4 && !encryptMetadata)
        {
            md5.AppendData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        }

        var hash = md5.GetHashAndReset();
        if (r >= 3)
        {
            for (int i = 0; i < 50; i++)
            {
                hash = MD5.HashData(hash.AsSpan(0, length));
            }
        }

        return hash.AsSpan(0, length).ToArray();
    }

    /// <summary>Algorithms 4 and 5: checks a file key against /U.</summary>
    private static bool CheckUserKey(byte[] key, byte[] u, byte[] firstId, int r)
    {
        if (r == 2)
        {
            return Rc4(key, Padding).AsSpan().SequenceEqual(u.AsSpan(0, Math.Min(32, u.Length)));
        }

        var data = MD5.HashData(Padding.Concat(firstId).ToArray());
        data = Rc4(key, data);
        for (byte i = 1; i <= 19; i++)
        {
            data = Rc4(XorKey(key, i), data);
        }

        return u.Length >= 16 && data.AsSpan().SequenceEqual(u.AsSpan(0, 16));
    }

    /// <summary>Algorithm 7 in reverse: decrypts /O with the owner password to get the user password.</summary>
    private static byte[] RecoverUserPassword(byte[] ownerPassword, byte[] o, int r, int length)
    {
        var hash = MD5.HashData(Pad(ownerPassword));
        if (r >= 3)
        {
            for (int i = 0; i < 50; i++)
            {
                hash = MD5.HashData(hash);
            }
        }

        var key = hash.AsSpan(0, length).ToArray();
        var data = o.AsSpan(0, Math.Min(32, o.Length)).ToArray();
        if (r == 2)
        {
            return Rc4(key, data);
        }

        for (int i = 19; i >= 0; i--)
        {
            data = Rc4(XorKey(key, (byte)i), data);
        }

        return data;
    }

    private static byte[] XorKey(byte[] key, byte value)
    {
        var result = new byte[key.Length];
        for (int i = 0; i < key.Length; i++)
        {
            result[i] = (byte)(key[i] ^ value);
        }

        return result;
    }

    private static byte[] Pad(byte[] password)
    {
        var result = new byte[32];
        int n = Math.Min(32, password.Length);
        password.AsSpan(0, n).CopyTo(result);
        Padding.AsSpan(0, 32 - n).CopyTo(result.AsSpan(n));
        return result;
    }

    public static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            s[i] = (byte)i;
        }

        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 0xFF]);
        }

        return output;
    }

    /// <summary>AES-CBC with the IV in the first 16 bytes and PKCS#7 padding, tolerating bad padding.</summary>
    private static byte[] AesDecrypt(byte[] key, byte[] data)
    {
        if (data.Length < 32 || data.Length % 16 != 0)
        {
            return data.Length < 16 ? Array.Empty<byte>() : AesDecryptLenient(key, data);
        }

        using var aes = Aes.Create();
        aes.Key = key;
        try
        {
            return aes.DecryptCbc(data.AsSpan(16), data.AsSpan(0, 16), PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            return aes.DecryptCbc(data.AsSpan(16), data.AsSpan(0, 16), PaddingMode.None);
        }
    }

    private static byte[] AesDecryptLenient(byte[] key, byte[] data)
    {
        int length = (data.Length - 16) / 16 * 16;
        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(data.AsSpan(16, length), data.AsSpan(0, 16), PaddingMode.None);
    }

    // ------------------------------------------------------------------ AES-256 (revisions 5 and 6)

    private static byte[]? OpenAes256(PdfDictionary encrypt, byte[] u, byte[] o, int r, string password)
    {
        var pw = Encoding.UTF8.GetBytes(password);
        if (pw.Length > 127)
        {
            pw = pw[..127];
        }

        if (u.Length < 48 || o.Length < 48)
        {
            return null;
        }

        // User password.
        if (Hash(pw, u.AsSpan(32, 8), ReadOnlySpan<byte>.Empty, r).AsSpan().SequenceEqual(u.AsSpan(0, 32)))
        {
            var intermediate = Hash(pw, u.AsSpan(40, 8), ReadOnlySpan<byte>.Empty, r);
            return UnwrapKey(intermediate, GetBytes(encrypt, "UE"));
        }

        // Owner password.
        var udata = u.AsSpan(0, 48);
        if (Hash(pw, o.AsSpan(32, 8), udata, r).AsSpan().SequenceEqual(o.AsSpan(0, 32)))
        {
            var intermediate = Hash(pw, o.AsSpan(40, 8), udata, r);
            return UnwrapKey(intermediate, GetBytes(encrypt, "OE"));
        }

        return null;
    }

    private static byte[]? UnwrapKey(byte[] key, byte[] wrapped)
    {
        if (wrapped.Length < 32)
        {
            return null;
        }

        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(wrapped.AsSpan(0, 32), new byte[16], PaddingMode.None);
    }

    /// <summary>Algorithm 2.A (revision 5) and 2.B (revision 6) password hashing.</summary>
    private static byte[] Hash(byte[] password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> udata, int r)
    {
        var input = new byte[password.Length + salt.Length + udata.Length];
        password.CopyTo(input, 0);
        salt.CopyTo(input.AsSpan(password.Length));
        udata.CopyTo(input.AsSpan(password.Length + salt.Length));
        var k = SHA256.HashData(input);
        if (r == 5)
        {
            return k;
        }

        using var aes = Aes.Create();
        for (int round = 0; ; round++)
        {
            int blockLength = password.Length + k.Length + udata.Length;
            var k1 = new byte[blockLength * 64];
            for (int i = 0; i < 64; i++)
            {
                int o = i * blockLength;
                password.CopyTo(k1, o);
                k.CopyTo(k1, o + password.Length);
                udata.CopyTo(k1.AsSpan(o + password.Length + k.Length));
            }

            aes.Key = k.AsSpan(0, 16).ToArray();
            var e = aes.EncryptCbc(k1, k.AsSpan(16, 16), PaddingMode.None);
            int sum = 0;
            for (int i = 0; i < 16; i++)
            {
                sum += e[i];
            }

            k = (sum % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };

            if (round >= 63 && e[^1] <= round - 31)
            {
                break;
            }
        }

        return k.AsSpan(0, 32).ToArray();
    }

    // ------------------------------------------------------------------ helpers

    private static int GetInt(PdfDictionary dict, string key, int fallback) =>
        dict.Get(key).AsNumber() is double d ? (int)(long)d : fallback;

    private static byte[] GetBytes(PdfDictionary dict, string key) => (dict.Get(key) as PdfString)?.Value ?? Array.Empty<byte>();
}
