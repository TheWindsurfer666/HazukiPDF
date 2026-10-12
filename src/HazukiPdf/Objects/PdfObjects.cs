using System.Globalization;
using System.Text;

namespace HazukiPdf.Objects;

/// <summary>Base type for every PDF object (ISO 32000-1, section 7.3).</summary>
internal abstract class PdfObject
{
}

internal sealed class PdfNull : PdfObject
{
    public static readonly PdfNull Instance = new();

    private PdfNull()
    {
    }
}

internal sealed class PdfBoolean : PdfObject
{
    public static readonly PdfBoolean True = new(true);
    public static readonly PdfBoolean False = new(false);

    private PdfBoolean(bool value) => Value = value;

    public bool Value { get; }

    public static PdfBoolean Get(bool value) => value ? True : False;
}

internal sealed class PdfInteger : PdfObject
{
    public PdfInteger(long value) => Value = value;

    public long Value { get; }
}

internal sealed class PdfReal : PdfObject
{
    public PdfReal(double value, string? text = null)
    {
        Value = value;
        Text = text;
    }

    public double Value { get; }

    /// <summary>The token as it appeared in the source file, kept so it can be written back unchanged.</summary>
    public string? Text { get; }

    public string Format()
    {
        if (Text != null)
        {
            return Text;
        }

        if (double.IsNaN(Value) || double.IsInfinity(Value))
        {
            return "0";
        }

        double clamped = Math.Clamp(Value, -1e15, 1e15);
        string s = Math.Round((decimal)clamped, 6).ToString(CultureInfo.InvariantCulture);
        if (s.Contains('.'))
        {
            s = s.TrimEnd('0').TrimEnd('.');
        }

        return s.Length == 0 || s == "-" ? "0" : s;
    }
}

internal sealed class PdfString : PdfObject
{
    public PdfString(byte[] value, bool isHex = false)
    {
        Value = value;
        IsHex = isHex;
    }

    public byte[] Value { get; }

    public bool IsHex { get; }
}

internal sealed class PdfName : PdfObject, IEquatable<PdfName>
{
    private static readonly Dictionary<string, PdfName> Cache = new();

    private PdfName(string value) => Value = value;

    /// <summary>The decoded name without the leading slash; each char holds one byte (Latin-1).</summary>
    public string Value { get; }

    public static PdfName Get(string value)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(value, out var name))
            {
                name = new PdfName(value);
                if (Cache.Count < 100_000)
                {
                    Cache[value] = name;
                }
            }

            return name;
        }
    }

    public bool Equals(PdfName? other) => other is not null && other.Value == Value;

    public override bool Equals(object? obj) => Equals(obj as PdfName);

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => "/" + Value;
}

internal sealed class PdfArray : PdfObject
{
    public PdfArray()
    {
        Items = new List<PdfObject>();
    }

    public PdfArray(IEnumerable<PdfObject> items)
    {
        Items = new List<PdfObject>(items);
    }

    public List<PdfObject> Items { get; }

    public int Count => Items.Count;

    public PdfObject this[int index]
    {
        get => Items[index];
        set => Items[index] = value;
    }

    public void Add(PdfObject item) => Items.Add(item);
}

internal class PdfDictionary : PdfObject
{
    private readonly List<KeyValuePair<PdfName, PdfObject>> _entries = new();

    public int Count => _entries.Count;

    public IEnumerable<KeyValuePair<PdfName, PdfObject>> Entries => _entries;

    public IEnumerable<PdfName> Keys => _entries.Select(e => e.Key);

    public PdfObject? this[string key]
    {
        get => Get(key);
        set => Set(key, value);
    }

    public PdfObject? Get(string key)
    {
        foreach (var entry in _entries)
        {
            if (entry.Key.Value == key)
            {
                return entry.Value;
            }
        }

        return null;
    }

    public bool ContainsKey(string key) => Get(key) != null;

    /// <summary>Sets a value; a null value removes the key (a null entry is equivalent to an absent one).</summary>
    public void Set(string key, PdfObject? value)
    {
        int index = _entries.FindIndex(e => e.Key.Value == key);
        if (value == null || value is PdfNull)
        {
            if (index >= 0)
            {
                _entries.RemoveAt(index);
            }

            return;
        }

        var pair = new KeyValuePair<PdfName, PdfObject>(PdfName.Get(key), value);
        if (index >= 0)
        {
            _entries[index] = pair;
        }
        else
        {
            _entries.Add(pair);
        }
    }

    public bool Remove(string key)
    {
        int index = _entries.FindIndex(e => e.Key.Value == key);
        if (index < 0)
        {
            return false;
        }

        _entries.RemoveAt(index);
        return true;
    }

    /// <summary>Adds an entry during parsing; a later duplicate key replaces the earlier one.</summary>
    internal void AddParsed(PdfName key, PdfObject value) => Set(key.Value, value);

    public void ReplaceValue(int index, PdfObject value) =>
        _entries[index] = new KeyValuePair<PdfName, PdfObject>(_entries[index].Key, value);

    public KeyValuePair<PdfName, PdfObject> EntryAt(int index) => _entries[index];

    public string? GetName(string key) => (Get(key) as PdfName)?.Value;
}

internal sealed class PdfStream : PdfDictionary
{
    public PdfStream(PdfDictionary dictionary, byte[] data)
    {
        foreach (var entry in dictionary.Entries)
        {
            AddParsed(entry.Key, entry.Value);
        }

        Data = data;
    }

    /// <summary>The encoded stream bytes, exactly as stored in the file (filters still applied).</summary>
    public byte[] Data { get; set; }
}

internal sealed class PdfReference : PdfObject, IEquatable<PdfReference>
{
    public PdfReference(int objectNumber, int generation)
    {
        ObjectNumber = objectNumber;
        Generation = generation;
    }

    public int ObjectNumber { get; }

    public int Generation { get; }

    public bool Equals(PdfReference? other) =>
        other is not null && other.ObjectNumber == ObjectNumber && other.Generation == Generation;

    public override bool Equals(object? obj) => Equals(obj as PdfReference);

    public override int GetHashCode() => HashCode.Combine(ObjectNumber, Generation);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{ObjectNumber} {Generation} R");
}

internal static class PdfObjectExtensions
{
    public static double? AsNumber(this PdfObject? obj) => obj switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        _ => null,
    };

    public static string AsText(this PdfString s) => Encoding.Latin1.GetString(s.Value);
}
