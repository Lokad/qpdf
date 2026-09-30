using System.Text;

namespace Lokad.Qpdf.Objects;

/// <summary>Base type for every PDF value the strict reader produces.</summary>
public abstract class QpdfObject;

/// <summary>PDF null. Also stands for deleted or undefined references.</summary>
public sealed class PdfNull : QpdfObject, IEquatable<PdfNull>
{
    /// <summary>Gets the singleton null instance.</summary>
    public static PdfNull Instance { get; } = new();

    private PdfNull()
    {
    }

    /// <inheritdoc />
    public bool Equals(PdfNull? other)
    {
        return other is not null;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfNull);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return 0;
    }
}

/// <summary>PDF boolean.</summary>
public sealed class PdfBoolean : QpdfObject, IEquatable<PdfBoolean>
{
    /// <summary>Gets true.</summary>
    public static PdfBoolean True { get; } = new(true);

    /// <summary>Gets false.</summary>
    public static PdfBoolean False { get; } = new(false);

    /// <summary>Creates a boolean.</summary>
    public PdfBoolean(bool value)
    {
        Value = value;
    }

    /// <summary>Gets the value.</summary>
    public bool Value { get; }

    /// <inheritdoc />
    public bool Equals(PdfBoolean? other)
    {
        return other is not null && Value == other.Value;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfBoolean);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return Value.GetHashCode();
    }
}
/// <summary>
/// PDF integer. The original token text is preserved so the writer never
/// rounds arbitrary PDF numbers through a wider or narrower representation.
/// </summary>
public sealed class PdfInteger : QpdfObject, IEquatable<PdfInteger>
{
    /// <summary>Creates an integer.</summary>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    public PdfInteger(string text, long value)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Value = value;
    }

    /// <summary>Gets the original token text.</summary>
    public string Text { get; }

    /// <summary>Gets the parsed value.</summary>
    public long Value { get; }

    /// <inheritdoc />
    public bool Equals(PdfInteger? other)
    {
        return other is not null && Text == other.Text && Value == other.Value;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfInteger);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(Text, Value);
    }
}

/// <summary>
/// PDF real number. The original token text is preserved alongside the parsed
/// value for the same reason as <see cref="PdfInteger" />.
/// </summary>
public sealed class PdfReal : QpdfObject, IEquatable<PdfReal>
{
    /// <summary>Creates a real.</summary>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    public PdfReal(string text, double value)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Value = value;
    }

    /// <summary>Gets the original token text.</summary>
    public string Text { get; }

    /// <summary>Gets the parsed value.</summary>
    public double Value { get; }

    /// <inheritdoc />
    public bool Equals(PdfReal? other)
    {
        return other is not null && Text == other.Text && Value.Equals(other.Value);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfReal);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(Text, Value);
    }
}


/// <summary>
/// PDF name with raw bytes preserved losslessly, including `#` escapes.
/// Equality follows raw byte equality.
/// </summary>
public sealed class PdfName : QpdfObject, IEquatable<PdfName>
{
    private readonly byte[] _raw;

    /// <summary>Creates a name, copying the raw bytes.</summary>
    /// <exception cref="ArgumentNullException">The bytes are null.</exception>
    public PdfName(byte[] rawBytes)
    {
        if (rawBytes is null)
        {
            throw new ArgumentNullException(nameof(rawBytes));
        }

        _raw = (byte[])rawBytes.Clone();
    }

    /// <summary>Creates a name from ASCII text, rejecting non-ASCII input.</summary>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="ArgumentException">The text is not ASCII.</exception>
    internal static PdfName FromAscii(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        byte[] raw = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] > 0x7F)
            {
                throw new ArgumentException("Name text must be ASCII.", nameof(text));
            }

            raw[i] = (byte)text[i];
        }

        return new PdfName(raw);
    }

    /// <summary>Gets a copy of the raw name bytes (without the leading slash).</summary>
    public byte[] RawBytes => (byte[])_raw.Clone();

    /// <summary>
    /// Gets printable ASCII text with `#XX` escapes resolved where the result
    /// stays printable; other bytes surface as `#XX`. Never used for identity.
    /// </summary>
    public string DisplayText
    {
        get
        {
            StringBuilder builder = new(_raw.Length);
            for (int i = 0; i < _raw.Length; i++)
            {
                byte b = _raw[i];
                if (b == (byte)'#' && i + 2 < _raw.Length)
                {
                    int hi = HexValue(_raw[i + 1]);
                    int lo = HexValue(_raw[i + 2]);
                    if (hi >= 0 && lo >= 0)
                    {
                        byte decoded = (byte)((hi << 4) | lo);
                        builder.Append(ToDisplay(decoded));
                        i += 2;
                        continue;
                    }
                }

                builder.Append(ToDisplay(b));
            }

            return builder.ToString();
        }
    }

    /// <inheritdoc />
    public bool Equals(PdfName? other)
    {
        return other is not null && _raw.AsSpan().SequenceEqual(other._raw);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfName);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        int count = Math.Min(_raw.Length, 16);
        for (int i = 0; i < count; i++)
        {
            hash.Add(_raw[i]);
        }

        hash.Add(_raw.Length);
        return hash.ToHashCode();
    }

    private static string ToDisplay(byte b)
    {
        return b >= 0x21 && b <= 0x7E && b != (byte)'#' ? ((char)b).ToString() : $"#{b:X2}";
    }

    private static int HexValue(byte b)
    {
        if (b >= (byte)'0' && b <= (byte)'9')
        {
            return b - (byte)'0';
        }

        if (b >= (byte)'A' && b <= (byte)'F')
        {
            return b - (byte)'A' + 10;
        }

        if (b >= (byte)'a' && b <= (byte)'f')
        {
            return b - (byte)'a' + 10;
        }

        return -1;
    }
}

/// <summary>
/// PDF byte string from literal or hexadecimal syntax. Raw bytes are preserved;
/// <see cref="Hex" /> records which syntax produced them for writer fidelity.
/// Text decoding (PDFDocEncoding, UTF-16) is a separate later operation.
/// </summary>
public sealed class PdfByteString : QpdfObject, IEquatable<PdfByteString>
{
    private readonly byte[] _value;

    /// <summary>Creates a byte string, copying the value.</summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public PdfByteString(byte[] value, bool hex)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        _value = (byte[])value.Clone();
        Hex = hex;
    }

    /// <summary>Gets whether hexadecimal syntax produced this string.</summary>
    public bool Hex { get; }

    /// <summary>Gets a copy of the raw value bytes.</summary>
    public byte[] Value => (byte[])_value.Clone();

    /// <inheritdoc />
    public bool Equals(PdfByteString? other)
    {
        return other is not null && Hex == other.Hex && _value.AsSpan().SequenceEqual(other._value);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfByteString);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        int count = Math.Min(_value.Length, 16);
        for (int i = 0; i < count; i++)
        {
            hash.Add(_value[i]);
        }

        hash.Add(_value.Length);
        hash.Add(Hex);
        return hash.ToHashCode();
    }
}

/// <summary>PDF array with structural equality.</summary>
public sealed class PdfArray : QpdfObject, IEquatable<PdfArray>
{
    private readonly QpdfObject[] _items;

    /// <summary>Creates an array, copying the items.</summary>
    /// <exception cref="ArgumentNullException">The items are null.</exception>
    public PdfArray(IReadOnlyList<QpdfObject> items)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        _items = [.. items];
    }

    /// <summary>Gets the item count.</summary>
    public int Count => _items.Length;

    /// <summary>Gets the item at an index.</summary>
    public QpdfObject this[int index] => _items[index];

    /// <summary>Gets a copy of the items.</summary>
    public QpdfObject[] Items => [.. _items];

    /// <inheritdoc />
    public bool Equals(PdfArray? other)
    {
        return other is not null && _items.AsSpan().SequenceEqual(other._items);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfArray);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        int count = Math.Min(_items.Length, 8);
        for (int i = 0; i < count; i++)
        {
            hash.Add(_items[i]);
        }

        hash.Add(_items.Length);
        return hash.ToHashCode();
    }
}

/// <summary>
/// PDF dictionary with structural equality. Duplicate keys resolve last-wins
/// with a recorded warning, matching the upstream reference behavior.
/// </summary>
public sealed class PdfDictionary : QpdfObject, IEquatable<PdfDictionary>
{
    private readonly Dictionary<PdfName, QpdfObject> _entries;

    /// <summary>Creates a dictionary, copying the entries.</summary>
    /// <exception cref="ArgumentNullException">The entries are null.</exception>
    public PdfDictionary(IReadOnlyDictionary<PdfName, QpdfObject> entries)
    {
        if (entries is null)
        {
            throw new ArgumentNullException(nameof(entries));
        }

        _entries = new Dictionary<PdfName, QpdfObject>(entries);
    }

    /// <summary>Gets the entry count.</summary>
    public int Count => _entries.Count;

    /// <summary>Gets the entry keys.</summary>
    public IEnumerable<PdfName> Keys => _entries.Keys;

    /// <summary>Looks up a key by raw name bytes.</summary>
    public bool TryGet(PdfName key, out QpdfObject value)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        if (_entries.TryGetValue(key, out QpdfObject? found) && found is not null)
        {
            value = found;
            return true;
        }

        value = PdfNull.Instance;
        return false;
    }

    /// <inheritdoc />
    public bool Equals(PdfDictionary? other)
    {
        if (other is null || _entries.Count != other._entries.Count)
        {
            return false;
        }

        foreach (KeyValuePair<PdfName, QpdfObject> entry in _entries)
        {
            if (!other._entries.TryGetValue(entry.Key, out QpdfObject? candidate) || candidate is null)
            {
                return false;
            }

            if (!Equals(entry.Value, candidate))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfDictionary);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return _entries.Count;
    }
}

/// <summary>Indirect reference to an object number and generation.</summary>
public sealed class PdfReference : QpdfObject, IEquatable<PdfReference>
{
    /// <summary>Creates a reference.</summary>
    public PdfReference(int objectNumber, int generation)
    {
        ObjectNumber = objectNumber;
        Generation = generation;
    }

    /// <summary>Gets the object number.</summary>
    public int ObjectNumber { get; }

    /// <summary>Gets the generation.</summary>
    public int Generation { get; }

    /// <inheritdoc />
    public bool Equals(PdfReference? other)
    {
        return other is not null && ObjectNumber == other.ObjectNumber && Generation == other.Generation;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfReference);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(ObjectNumber, Generation);
    }
}

/// <summary>PDF stream: dictionary plus opaque bytes with structural equality.</summary>
public sealed class PdfStream : QpdfObject, IEquatable<PdfStream>
{
    private readonly byte[] _data;

    /// <summary>Creates a stream, copying the data.</summary>
    /// <exception cref="ArgumentNullException">Dictionary or data is null.</exception>
    public PdfStream(PdfDictionary dictionary, byte[] data)
    {
        Dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        _data = (byte[])data.Clone();
    }

    /// <summary>Gets the stream dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>Gets a copy of the raw stream bytes.</summary>
    public byte[] Data => (byte[])_data.Clone();

    /// <inheritdoc />
    public bool Equals(PdfStream? other)
    {
        return other is not null
            && Dictionary.Equals(other.Dictionary)
            && _data.AsSpan().SequenceEqual(other._data);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfStream);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(_data.Length);
        hash.Add(Dictionary.Count);
        return hash.ToHashCode();
    }
}
