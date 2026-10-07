using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Runtime;

/// <summary>Name storage with explicit construction/lookup work and no retained execution state.</summary>
internal sealed class PdfNameTable<T> : IReadOnlyDictionary<PdfName, T>, IEnumerable<PdfName>
{
    internal const long WrapperBytes = 96L;
    private readonly long _entryBytes;
    private int[] _buckets = Array.Empty<int>();
    private Entry[] _entries = Array.Empty<Entry>();
    // Mutable tables borrow their construction scope weakly; publication drops
    // even that reference. Immutable aliases retain storage without execution state.
    private WeakReference<ParserMemoryScope>? _origin;
    private int _count;
    private int _version;
    private bool _frozen;

    private struct Entry
    {
        internal PdfName? Key;
        internal int Hash;
        internal int Next;
        [AllowNull] internal T Value;
    }

    private PdfNameTable(ParserMemoryScope memory, long entryBytes)
    { _origin = new WeakReference<ParserMemoryScope>(memory); _entryBytes = entryBytes; }

    internal static PdfNameTable<T> Create(ParserMemoryScope memory, ResourceGovernor governor,
        long valueBytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueBytes);
        ArgumentNullException.ThrowIfNull(memory); ArgumentNullException.ThrowIfNull(governor);
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        long entryBytes = checked((16L + valueBytes + 7L) / 8L * 8L);
        governor.ChargeWork(1L); memory.Reserve(WrapperBytes);
        try { return new PdfNameTable<T>(memory, entryBytes); }
        catch { memory.Release(WrapperBytes); throw; }
    }

    public int Count => _count;
    internal bool IsFrozen => _frozen;
    internal int Capacity => _entries.Length;
    internal long StorageBytes => checked(WrapperBytes + ArrayBytes(Capacity));
    public IEnumerable<PdfName> Keys => this;
    public IEnumerable<T> Values => EnumerateValues();
    public T this[PdfName key] => TryGetValue(key, out T? value) ? value : throw new KeyNotFoundException();

    private IEnumerable<T> EnumerateValues()
    { foreach (KeyValuePair<PdfName, T> entry in this) { yield return entry.Value; } }

    private void CheckConstruction(ParserMemoryScope memory, ResourceGovernor governor)
    {
        if (_frozen) { throw new InvalidOperationException("Frozen name storage cannot change."); }
        memory.CheckOwner(governor);
        if (_origin is null || !_origin.TryGetTarget(out ParserMemoryScope? owner) || !ReferenceEquals(owner, memory))
        { throw new InvalidOperationException("Name storage belongs to its construction scope."); }
    }

    private void CheckRead(ResourceGovernor governor)
    {
        if (_frozen) { return; }
        if (_origin is null || !_origin.TryGetTarget(out ParserMemoryScope? owner))
        { throw new InvalidOperationException("Name construction scope is unavailable."); }
        owner.CheckOwner(governor);
    }

    public bool ContainsKey(PdfName key) => TryGetValue(key, out _);
    public bool TryGetValue(PdfName key, [MaybeNullWhen(false)] out T value)
        => Read(key, new Visits(null, CancellationToken.None, null), out value);

    internal bool TryGetValue(PdfName key, ResourceGovernor governor, CancellationToken token, [MaybeNullWhen(false)] out T value)
    {
        ArgumentNullException.ThrowIfNull(governor); CheckRead(governor);
        return Read(key, new Visits(governor, token, null), out value);
    }

    internal bool TryGetValue(ReadOnlySpan<byte> key, ResourceGovernor governor, CancellationToken token,
        [MaybeNullWhen(false)] out T value)
    {
        ArgumentNullException.ThrowIfNull(governor); CheckRead(governor);
        return ReadBytes(key, new Visits(governor, token, null), out value);
    }

    internal bool TryGetObserved(ReadOnlySpan<byte> key, ResourceGovernor governor, CancellationToken token,
        Action observe, [MaybeNullWhen(false)] out T value)
    {
        ArgumentNullException.ThrowIfNull(governor); ArgumentNullException.ThrowIfNull(observe); CheckRead(governor);
        return ReadBytes(key, new Visits(governor, token, observe), out value);
    }

    private bool ReadBytes(ReadOnlySpan<byte> key, Visits visits, [MaybeNullWhen(false)] out T value)
    {
        int found = Find(key, null, visits, out _);
        if (found < 0) { value = default; return false; }
        value = _entries[found].Value; return true;
    }

    // The observer follows completed byte blocks; ordinary callers retain no
    // observation state and both paths execute the same lookup engine.
    internal bool TryGetObserved(PdfName key, ResourceGovernor governor, CancellationToken token, Action observe,
        [MaybeNullWhen(false)] out T value)
    {
        ArgumentNullException.ThrowIfNull(governor); ArgumentNullException.ThrowIfNull(observe); CheckRead(governor);
        return Read(key, new Visits(governor, token, observe), out value);
    }

    private bool Read(PdfName key, Visits visits, [MaybeNullWhen(false)] out T value)
    {
        int found = Find(key, visits, out _);
        if (found < 0) { value = default; return false; }
        value = _entries[found].Value; return true;
    }

    internal bool TryAdd(ParserMemoryScope memory, ResourceGovernor governor, PdfName key, T value, CancellationToken token)
        => Store(memory, governor, key, value, token, false);
    internal void Assign(ParserMemoryScope memory, ResourceGovernor governor, PdfName key, T value, CancellationToken token)
        => Store(memory, governor, key, value, token, true);

    internal int EnsureCapacity(ParserMemoryScope memory, ResourceGovernor governor, int required, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(required); CheckConstruction(memory, governor);
        Visits visits = new(governor, token, null); visits.Step();
        // Keep the shared 3/7-slot geometry for small dictionaries. Larger
        // known copies can admit their exact extent without repeated growth.
        if (required > Capacity) { Grow(memory, visits, required <= 3 ? 3 : required <= 7 ? 7 : required); }
        return Capacity;
    }

    private bool Store(ParserMemoryScope memory, ResourceGovernor governor, PdfName key, T value, CancellationToken token, bool replace)
    {
        CheckConstruction(memory, governor); Visits visits = new(governor, token, null);
        int found = Find(key, visits, out int hash);
        if (found >= 0)
        {
            if (replace) { visits.Step(); _entries[found].Value = value; _version = unchecked(_version + 1); }
            return false;
        }
        visits.Step();
        if (_count == Capacity)
        { Grow(memory, visits, (int)Math.Min(Array.MaxLength, Math.Max(3L, Capacity * 2L + 1L))); }
        int bucket = (int)((uint)hash % (uint)_buckets.Length);
        _entries[_count] = new Entry { Key = key, Hash = hash, Next = _buckets[bucket] - 1, Value = value };
        _buckets[bucket] = ++_count; _version = unchecked(_version + 1); return true;
    }

    private int Find(PdfName key, Visits visits, out int hash)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Find(key.RawSpan, key, visits, out hash);
    }

    private int Find(ReadOnlySpan<byte> key, PdfName? identityKey, Visits visits, out int hash)
    {
        visits.Step(); hash = visits.Hash(key);
        if (_buckets.Length == 0) { return -1; }
        visits.Step(); int slot = _buckets[(uint)hash % (uint)_buckets.Length] - 1;
        while (slot >= 0)
        {
            visits.Step(); Entry entry = _entries[slot];
            if (entry.Hash == hash)
            {
                PdfName stored = entry.Key ?? throw new InvalidOperationException("Name slot has no key.");
                if (identityKey is null ? visits.Equal(stored, key) : visits.Equal(stored, identityKey)) { return slot; }
            }
            slot = entry.Next;
        }
        return -1;
    }

    private long ArrayBytes(int capacity) => capacity == 0 ? 0L : checked(64L + (4L + _entryBytes) * capacity);

    private void Grow(ParserMemoryScope memory, Visits visits, int capacity)
    {
        if (capacity <= _count || capacity > Array.MaxLength) { throw new QpdfParseException("Name table exceeds supported capacity.", null, null); }
        long bytes = ArrayBytes(capacity); memory.Reserve(bytes);
        try
        {
            visits.Units(checked(2L * capacity));
            int[] buckets = new int[capacity]; Entry[] entries = new Entry[capacity];
            for (int index = 0; index < _count; index++)
            {
                visits.Step(); Entry entry = _entries[index]; int bucket = (int)((uint)entry.Hash % (uint)capacity);
                visits.Step(); entry.Next = buckets[bucket] - 1; entries[index] = entry; buckets[bucket] = index + 1;
            }
            long previous = ArrayBytes(Capacity); _entries = entries; _buckets = buckets;
            memory.Release(previous);
        }
        catch { memory.Release(bytes); throw; }
    }

    internal void Freeze(ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        CheckConstruction(memory, governor); new Visits(governor, token, null).Step();
        _frozen = true; _origin = null;
    }

    internal void Discard(ParserMemoryScope memory, ResourceGovernor governor)
    {
        CheckConstruction(memory, governor); long bytes = StorageBytes;
        _entries = Array.Empty<Entry>(); _buckets = Array.Empty<int>(); _origin = null; _count = 0; _frozen = true;
        _version = unchecked(_version + 1); memory.Release(bytes);
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<KeyValuePair<PdfName, T>> IEnumerable<KeyValuePair<PdfName, T>>.GetEnumerator() => GetEnumerator();
    IEnumerator<PdfName> IEnumerable<PdfName>.GetEnumerator() => EnumerateKeys();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private IEnumerator<PdfName> EnumerateKeys()
    { foreach (KeyValuePair<PdfName, T> entry in this) { yield return entry.Key; } }

    internal struct Enumerator : IEnumerator<KeyValuePair<PdfName, T>>
    {
        private readonly PdfNameTable<T> _owner;
        private readonly int _version;
        private int _position;
        internal Enumerator(PdfNameTable<T> owner) { _owner = owner; _version = owner._version; _position = -1; }
        public KeyValuePair<PdfName, T> Current
        {
            get
            {
                CheckVersion(); if (_position < 0 || _position >= _owner._count) { throw new InvalidOperationException("Name enumerator is not on an entry."); }
                Entry entry = _owner._entries[_position]; return new(entry.Key ?? throw new InvalidOperationException("Name slot has no key."), entry.Value);
            }
        }
        object IEnumerator.Current => Current;
        private void CheckVersion() { if (_version != _owner._version) { throw new InvalidOperationException("Name storage changed during enumeration."); } }
        public bool MoveNext() { CheckVersion(); if (_position < _owner._count) { _position++; } return _position < _owner._count; }
        internal bool MoveNext(ResourceGovernor governor, CancellationToken token)
        { _owner.CheckRead(governor); new Visits(governor, token, null).Step(); return MoveNext(); }
        public void Reset() { CheckVersion(); _position = -1; }
        public void Dispose() { }
    }

    private readonly struct Visits(ResourceGovernor? governor, CancellationToken token, Action? observe)
    {
        internal void Step() => Units(1L);
        internal void Units(long count) { token.ThrowIfCancellationRequested(); governor?.CheckCancellation(); governor?.ChargeWork(count); }
        internal int Hash(ReadOnlySpan<byte> bytes)
        {
            HashCode hash = new();
            for (int index = 0; index < Math.Min(bytes.Length, 16); index++) { Step(); hash.Add(bytes[index]); }
            Step(); hash.Add(bytes.Length); Step(); return hash.ToHashCode();
        }
        internal bool Equal(PdfName left, PdfName right)
        {
            Step(); if (ReferenceEquals(left, right)) { return true; }
            return EqualBytes(left.RawSpan, right.RawSpan);
        }
        internal bool Equal(PdfName left, ReadOnlySpan<byte> right)
        { Step(); return EqualBytes(left.RawSpan, right); }
        private bool EqualBytes(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            if (a.Length != b.Length) { return false; }
            for (int index = 0; index < a.Length; index++)
            {
                Step(); if (a[index] != b[index]) { return false; }
                if ((index + 1) % 256 == 0) { observe?.Invoke(); }
            }
            return true;
        }
    }
}
