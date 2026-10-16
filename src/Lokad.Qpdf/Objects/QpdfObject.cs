using Lokad.Qpdf.Runtime;
using System.Runtime.InteropServices;

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

    /// <summary>Indicates whether this instance equals another PdfNull.</summary>
    public bool Equals(PdfNull? other)
    {
        return other is not null;
    }

    /// <summary>Determines whether the specified object equals this instance.</summary>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfNull);
    }

    /// <summary>Returns the hash code for this instance.</summary>
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

    /// <summary>Indicates whether this instance equals another PdfBoolean.</summary>
    public bool Equals(PdfBoolean? other)
    {
        return other is not null && Value == other.Value;
    }

    /// <summary>Determines whether the specified object equals this instance.</summary>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfBoolean);
    }

    /// <summary>Returns the hash code for this instance.</summary>
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
    private static readonly string[] SingleDigitText = ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9"];
    internal const int SharedValueCount = 256;
    private static readonly PdfInteger[] SharedValues = CreateSharedValues();

    internal static string GetSingleDigitText(int digit) => SingleDigitText[digit];
    internal static PdfInteger GetSharedValue(int value) => SharedValues[value];

    internal bool IsSharedValue => (ulong)Value < SharedValueCount
        && ReferenceEquals(this, SharedValues[(int)Value]);

    private static PdfInteger[] CreateSharedValues()
    {
        // Fixed immutable atoms, independent of documents and their owners.
        PdfInteger[] values = new PdfInteger[SharedValueCount];
        for (int value = 0; value < values.Length; value++)
        {
            string text = value < SingleDigitText.Length ? SingleDigitText[value]
                : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            values[value] = new PdfInteger(text, value);
        }
        return values;
    }

    // Process-lifetime literals contribute no job-owned text allocation.
    internal bool HasSharedText => Text.Length == 1 && Text[0] is >= '0' and <= '9'
        && ReferenceEquals(Text, SingleDigitText[Text[0] - '0']);

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

    /// <summary>Indicates whether this instance equals another PdfInteger.</summary>
    public bool Equals(PdfInteger? other)
    {
        return other is not null && Text == other.Text && Value == other.Value;
    }

    /// <summary>Determines whether the specified object equals this instance.</summary>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfInteger);
    }

    /// <summary>Returns the hash code for this instance.</summary>
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

    /// <summary>Indicates whether this instance equals another PdfReal.</summary>
    public bool Equals(PdfReal? other)
    {
        return other is not null && Text == other.Text && Value.Equals(other.Value);
    }

    /// <summary>Determines whether the specified object equals this instance.</summary>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfReal);
    }

    /// <summary>Returns the hash code for this instance.</summary>
    public override int GetHashCode()
    {
        return HashCode.Combine(Text, Value);
    }
}


/// <summary>
/// PDF name with raw bytes preserved losslessly, including `#` escapes.
/// Equality follows raw byte equality. Syntax repair represents a stray
/// hash with a zero byte, distinct from a correctly escaped hash.
/// </summary>
public sealed class PdfName : QpdfObject, IEquatable<PdfName>
{
    private readonly byte[] _raw;

    // A finite syntax vocabulary, independent of any input or execution owner.
    // Public byte access still returns copies. Arbitrary/escaped names are owned.
    private static class SharedSyntaxNames
    {
        internal static readonly PdfName[] Values =
        [
            CreateAscii("Type"), CreateAscii("Page"), CreateAscii("Pages"), CreateAscii("Parent"),
            CreateAscii("Length"), CreateAscii("Resources"), CreateAscii("Contents"), CreateAscii("Kids"),
            CreateAscii("Count"), CreateAscii("MediaBox"), CreateAscii("Catalog"), CreateAscii("Font"),
        ];
        internal static readonly int[] Hashes = Array.ConvertAll(Values, static name => CalculateHash(name._raw));
    }

    internal static PdfName? FindSharedAscii(ReadOnlySpan<byte> raw)
    {
        int index = SharedSyntaxIndex(raw);
        return index < 0 ? null : SharedSyntaxNames.Values[index];
    }

    private static int SharedSyntaxIndex(ReadOnlySpan<byte> raw) => raw.Length switch
    {
        4 => raw.SequenceEqual("Type"u8) ? 0 : raw.SequenceEqual("Page"u8) ? 1
            : raw.SequenceEqual("Kids"u8) ? 7 : raw.SequenceEqual("Font"u8) ? 11 : -1,
        5 => raw.SequenceEqual("Pages"u8) ? 2 : raw.SequenceEqual("Count"u8) ? 8 : -1,
        6 => raw.SequenceEqual("Parent"u8) ? 3 : raw.SequenceEqual("Length"u8) ? 4 : -1,
        7 => raw.SequenceEqual("Catalog"u8) ? 10 : -1,
        8 => raw.SequenceEqual("Contents"u8) ? 6 : raw.SequenceEqual("MediaBox"u8) ? 9 : -1,
        9 => raw.SequenceEqual("Resources"u8) ? 5 : -1,
        _ => -1,
    };

    internal bool IsSharedValue
    {
        get
        {
            foreach (PdfName name in SharedSyntaxNames.Values)
            {
                if (ReferenceEquals(this, name)) { return true; }
            }
            return false;
        }
    }

    /// <summary>Creates a name, copying the raw bytes.</summary>
    /// <exception cref="ArgumentNullException">The bytes are null.</exception>
    public PdfName(byte[] rawBytes) : this(rawBytes, true) { }

    private PdfName(byte[] rawBytes, bool copy)
    {
        if (rawBytes is null)
        {
            throw new ArgumentNullException(nameof(rawBytes));
        }

        _raw = copy ? (byte[])rawBytes.Clone() : rawBytes;
    }

    internal static PdfName FromOwnedBytes(byte[] rawBytes) => new(rawBytes, false);

    /// <summary>Gets an immutable ASCII name, reusing the finite syntax vocabulary.</summary>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="ArgumentException">The text is not ASCII.</exception>
    internal static PdfName FromAscii(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        int index = text switch
        {
            "Type" => 0, "Page" => 1, "Pages" => 2, "Parent" => 3,
            "Length" => 4, "Resources" => 5, "Contents" => 6, "Kids" => 7,
            "Count" => 8, "MediaBox" => 9, "Catalog" => 10, "Font" => 11,
            _ => -1,
        };
        return index < 0 ? CreateAscii(text) : SharedSyntaxNames.Values[index];
    }

    private static PdfName CreateAscii(string text)
    {
        byte[] raw = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] > 0x7F)
            {
                throw new ArgumentException("Name text must be ASCII.", nameof(text));
            }

            raw[i] = (byte)text[i];
        }

        return FromOwnedBytes(raw);
    }

    /// <summary>
    /// Gets a copy of the raw name bytes (without the leading slash).
    /// A zero byte in a repaired name represents an invalid hash escape;
    /// serialization preserves that hash and reopening requires syntax repair.
    /// Use <see cref="CopyRawBytes" /> for caller-budget admission.
    /// </summary>
    public byte[] RawBytes => (byte[])_raw.Clone();

    /// <summary>Copies the raw name bytes after admitting their capacity under a caller owner.</summary>
    /// <remarks>
    /// The borrowed budget must outlive the returned array. The independent copy
    /// can outlive the source graph; its charge ends when the caller owner closes.
    /// Failed or canceled copying refunds the unused admission.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The budget is null.</exception>
    /// <exception cref="QpdfMemoryAdmissionException">The shared allowance refused admission.</exception>
    /// <exception cref="OperationCanceledException">Copying was canceled.</exception>
    public byte[] CopyRawBytes(IMemoryBudget memoryBudget, CancellationToken cancellationToken)
        => CallerMemoryCopy.Copy<byte>(_raw, sizeof(byte), memoryBudget, cancellationToken);

    internal ReadOnlySpan<byte> RawSpan => _raw;

    internal ReadOnlyMemory<byte> RawMemory => _raw;

    internal object AllocationCarrier => _raw;

    /// <summary>
    /// Gets printable ASCII text with `#XX` escapes resolved where the result
    /// stays printable; other bytes surface as `#XX`. Never used for identity.
    /// </summary>
    public string DisplayText
    {
        get
        {
            int length = 0;
            foreach (byte b in _raw)
            {
                length = checked(length + (b >= 0x21 && b <= 0x7E && b != (byte)'#' ? 1 : 3));
            }

            return string.Create(length, _raw, static (target, raw) =>
            {
                int position = 0;
                foreach (byte b in raw)
                {
                    position += WriteDisplayByte(b, target[position..]);
                }
            });
        }
    }

    /// <summary>Indicates whether this instance equals another PdfName.</summary>
    public bool Equals(PdfName? other)
    {
        return other is not null && _raw.AsSpan().SequenceEqual(other._raw);
    }

    /// <summary>Determines whether the specified object equals this instance.</summary>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfName);
    }

    /// <summary>Returns the hash code for this instance.</summary>
    public override int GetHashCode()
    {
        int index = SharedSyntaxIndex(_raw);
        return index < 0 ? CalculateHash(_raw) : SharedSyntaxNames.Hashes[index];
    }

    private static int CalculateHash(ReadOnlySpan<byte> raw)
    {
        HashCode hash = new();
        int count = Math.Min(raw.Length, 16);
        for (int i = 0; i < count; i++)
        {
            hash.Add(raw[i]);
        }

        hash.Add(raw.Length);
        return hash.ToHashCode();
    }

    internal static int WriteDisplayByte(byte value, Span<char> target) => PdfNameDisplay.WriteByte(value, target);

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

    private PdfByteString(bool hex, byte[] owned)
    {
        _value = owned ?? throw new ArgumentNullException(nameof(owned));
        Hex = hex;
    }

    internal static PdfByteString FromOwnedBytes(byte[] value, bool hex) => new(hex, value);

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

    /// <summary>Gets a copy of the raw value bytes. Use <see cref="CopyValue" /> for caller-budget admission.</summary>
    public byte[] Value => (byte[])_value.Clone();

    /// <summary>Copies the raw value bytes after admitting their capacity under a caller owner.</summary>
    /// <remarks>
    /// The borrowed budget must outlive the returned array. The independent copy
    /// can outlive the source graph; its charge ends when the caller owner closes.
    /// Failed or canceled copying refunds the unused admission.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The budget is null.</exception>
    /// <exception cref="QpdfMemoryAdmissionException">The shared allowance refused admission.</exception>
    /// <exception cref="OperationCanceledException">Copying was canceled.</exception>
    public byte[] CopyValue(IMemoryBudget memoryBudget, CancellationToken cancellationToken)
        => CallerMemoryCopy.Copy<byte>(_value, sizeof(byte), memoryBudget, cancellationToken);

    internal ReadOnlySpan<byte> ValueSpan => _value;

    internal object AllocationCarrier => _value;

    /// <summary>Indicates whether this instance equals another PdfByteString.</summary>
    public bool Equals(PdfByteString? other)
    {
        return other is not null && Hex == other.Hex && _value.AsSpan().SequenceEqual(other._value);
    }

    /// <summary>Determines whether the specified object equals this instance.</summary>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfByteString);
    }

    /// <summary>Returns the hash code for this instance.</summary>
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

    private PdfArray(QpdfObject[] ownedItems) { _items = ownedItems; }

    internal static PdfArray FromOwnedItems(QpdfObject[] items) => new(items);

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

    /// <summary>Gets a copy of the items. Use <see cref="CopyItems" /> for caller-budget admission.</summary>
    public QpdfObject[] Items => [.. _items];

    /// <summary>Copies the item references after admitting the array capacity under a caller owner.</summary>
    /// <remarks>
    /// This is a shallow copy: keep the source graph's owner alive while using
    /// the referenced values. The borrowed budget owns only the new array and
    /// must outlive it. Failed or canceled copying refunds the unused admission.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The budget is null.</exception>
    /// <exception cref="QpdfMemoryAdmissionException">The shared allowance refused admission.</exception>
    /// <exception cref="OperationCanceledException">Copying was canceled.</exception>
    public QpdfObject[] CopyItems(IMemoryBudget memoryBudget, CancellationToken cancellationToken)
        => CallerMemoryCopy.Copy<QpdfObject>(_items, IntPtr.Size, memoryBudget, cancellationToken);

    internal object AllocationCarrier => _items;

    internal bool HasSharedEmptyStorage => ReferenceEquals(_items, Array.Empty<QpdfObject>());

    /// <summary>Indicates whether this instance equals another PdfArray.</summary>
    public bool Equals(PdfArray? other)
    {
        return other is not null && _items.AsSpan().SequenceEqual(other._items);
    }

    /// <summary>Determines whether the specified object equals this instance.</summary>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfArray);
    }

    /// <summary>Returns the hash code for this instance.</summary>
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
    private readonly IReadOnlyDictionary<PdfName, QpdfObject> _entries;

    private PdfDictionary(Dictionary<PdfName, QpdfObject> ownedEntries) { _entries = ownedEntries; }
    private PdfDictionary(PdfNameTable<QpdfObject> ownedEntries) { _entries = ownedEntries; }

    // The parser has admitted and populated this private dictionary. No caller
    // may retain mutable access after transfer; public construction still copies.
    internal static PdfDictionary FromOwnedEntries(Dictionary<PdfName, QpdfObject> entries) => new(entries);

    internal static PdfDictionary FromOwnedEntries(PdfNameTable<QpdfObject> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (!entries.IsFrozen) { throw new InvalidOperationException("Dictionary name storage must be frozen before transfer."); }
        return new(entries);
    }

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
    public IEnumerable<PdfName> Keys => _entries is PdfNameTable<QpdfObject> names ? names.DictionaryKeys : _entries.Keys;

    internal EntryEnumerator GetEntryEnumerator() => AllocationCarrier is PdfNameTable<QpdfObject> names
        ? new(names) : new((Dictionary<PdfName, QpdfObject>)AllocationCarrier);

    internal int StorageCapacity => _entries is PdfNameTable<QpdfObject> names
        ? names.Capacity : ((Dictionary<PdfName, QpdfObject>)_entries).EnsureCapacity(0);

    internal object AllocationCarrier => _entries;

    internal struct EntryEnumerator : IDisposable
    {
        private Dictionary<PdfName, QpdfObject>.Enumerator _legacy;
        private PdfNameTable<QpdfObject>.Enumerator _names;
        private readonly bool _owned;

        internal EntryEnumerator(Dictionary<PdfName, QpdfObject> entries)
        { _legacy = entries.GetEnumerator(); _names = default; _owned = false; }
        internal EntryEnumerator(PdfNameTable<QpdfObject> entries)
        { _legacy = default; _names = entries.GetEnumerator(); _owned = true; }
        internal KeyValuePair<PdfName, QpdfObject> Current => _owned ? _names.Current : _legacy.Current;
        internal bool MoveNext() => _owned ? _names.MoveNext() : _legacy.MoveNext();
        internal bool MoveNext(ResourceGovernor governor, CancellationToken token)
        {
            if (_owned) { return _names.MoveNext(governor, token); }
            token.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.ChargeWork(1L);
            return _legacy.MoveNext();
        }
        public void Dispose() { _legacy.Dispose(); _names.Dispose(); }
    }

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

    /// <summary>Indicates whether this instance equals another PdfDictionary.</summary>
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

    /// <summary>Determines whether the specified object equals this instance.</summary>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfDictionary);
    }

    /// <summary>Returns the hash code for this instance.</summary>
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

    /// <summary>Indicates whether this instance equals another PdfReference.</summary>
    public bool Equals(PdfReference? other)
    {
        return other is not null && ObjectNumber == other.ObjectNumber && Generation == other.Generation;
    }

    /// <summary>Determines whether the specified object equals this instance.</summary>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfReference);
    }

    /// <summary>Returns the hash code for this instance.</summary>
    public override int GetHashCode()
    {
        return HashCode.Combine(ObjectNumber, Generation);
    }
}

/// <summary>PDF stream: dictionary plus opaque bytes with structural equality.</summary>
public sealed class PdfStream : QpdfObject, IEquatable<PdfStream>
{
    private readonly ReadOnlyMemory<byte> _data;

    /// <summary>Creates a stream, copying the data.</summary>
    /// <exception cref="ArgumentNullException">Dictionary or data is null.</exception>
    public PdfStream(PdfDictionary dictionary, byte[] data) : this(dictionary, data, true) { }

    private PdfStream(PdfDictionary dictionary, byte[] data, bool copyData)
    {
        Dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        _data = copyData ? (byte[])data.Clone() : data;
    }

    private PdfStream(PdfDictionary dictionary, ReadOnlyMemory<byte> data)
    {
        Dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        _data = data;
    }

    /// <summary>Gets the stream dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>Gets a copy of the raw stream bytes. Use <see cref="CopyData" /> for caller-budget admission.</summary>
    public byte[] Data => _data.ToArray();

    /// <summary>Copies the raw stream bytes after admitting their capacity under a caller owner.</summary>
    /// <remarks>
    /// The borrowed budget must outlive the returned array. The independent copy
    /// can outlive the source graph; its charge ends when the caller owner closes.
    /// Failed or canceled copying refunds the unused admission.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The budget is null.</exception>
    /// <exception cref="QpdfMemoryAdmissionException">The shared allowance refused admission.</exception>
    /// <exception cref="OperationCanceledException">Copying was canceled.</exception>
    public byte[] CopyData(IMemoryBudget memoryBudget, CancellationToken cancellationToken)
        => CallerMemoryCopy.Copy<byte>(_data.Span, sizeof(byte), memoryBudget, cancellationToken);

    internal ReadOnlyMemory<byte> RawMemory => _data;

    // The caller admits this fresh buffer and relinquishes mutable access.
    internal static PdfStream FromOwnedData(PdfDictionary dictionary, byte[] data) => new(dictionary, data, false);

    // Exclusive complete jobs keep the entire immutable input funded until
    // publication. Public readers continue to create independent payloads.
    internal static PdfStream FromImmutableSlice(PdfDictionary dictionary, ReadOnlyMemory<byte> data)
    {
        if (!MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment) || segment.Array is null)
        { throw new InvalidOperationException("An immutable stream slice requires owned array storage."); }
        return new(dictionary, data);
    }

    // Header-only rewrites keep the immutable payload's existing owner.
    internal PdfStream WithDictionary(PdfDictionary dictionary) => new(dictionary, _data);

    internal object AllocationCarrier => MemoryMarshal.TryGetArray(_data, out ArraySegment<byte> segment)
        && segment.Array is byte[] bytes ? bytes : throw new InvalidOperationException("Missing stream array storage.");

    internal long AllocationBytes => 32L + ((byte[])AllocationCarrier).Length;

    // Array-based filters and content consumers need the payload, not its
    // potentially larger backing. Materialize a slice under their scratch owner.
    internal byte[] ArrayData(ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        byte[] backing = (byte[])AllocationCarrier;
        if (_data.Length == backing.Length) { return backing; }
        memory.Reserve(checked(32L + _data.Length));
        byte[] bytes = GC.AllocateUninitializedArray<byte>(_data.Length);
        GovernedBufferStream.CopyBytes(_data.Span, bytes, governor, token, governor.CancellationToken);
        return bytes;
    }

    /// <summary>Indicates whether this instance equals another PdfStream.</summary>
    public bool Equals(PdfStream? other)
    {
        return other is not null
            && Dictionary.Equals(other.Dictionary)
            && _data.Span.SequenceEqual(other._data.Span);
    }

    /// <summary>Determines whether the specified object equals this instance.</summary>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfStream);
    }

    /// <summary>Returns the hash code for this instance.</summary>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(_data.Length);
        hash.Add(Dictionary.Count);
        return hash.ToHashCode();
    }
}
