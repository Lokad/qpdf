using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Lokad.Qpdf.Runtime;

/// <summary>Integer index whose bucket visits, storage and rebuilds are governed.</summary>
internal sealed class GovernedIntDictionary<T> : IReadOnlyCollection<KeyValuePair<int, T>>
{
    // Covers this wrapper and its creation scope after admission transfers.
    internal const long WrapperBytes = 144L;
    private readonly ParserMemoryScope _memory;
    private readonly ResourceGovernor _governor;
    private readonly CancellationToken _cancellationToken;
    private readonly long _entryBytes;
    private int[] _buckets = Array.Empty<int>();
    private Entry[] _entries = Array.Empty<Entry>();
    private int _used;
    private int _count;
    private int _free = -1;
    private int _version;

    private struct Entry
    {
        internal int Key;
        // Live chains end at -1. Smaller values encode the next free slot.
        internal int Next;
        [AllowNull] internal T Value;
    }

    private GovernedIntDictionary(ParserMemoryScope memory, ResourceGovernor governor, long valueBytes, CancellationToken cancellationToken)
    {
        _memory = memory;
        _governor = governor;
        _cancellationToken = cancellationToken;
        _entryBytes = checked((8L + valueBytes + 7L) / 8L * 8L);
    }

    internal static GovernedIntDictionary<T> Create(ParserMemoryScope memory, ResourceGovernor governor,
        long valueBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueBytes);
        memory.CheckOwner(governor);
        cancellationToken.ThrowIfCancellationRequested();
        governor.CheckCancellation();
        memory.Reserve(WrapperBytes);
        try { return new GovernedIntDictionary<T>(memory, governor, valueBytes, cancellationToken); }
        catch { memory.Release(WrapperBytes); throw; }
    }

    public int Count => _count;

    internal T this[int key] => TryGetValue(key, out T? value) ? value : throw new KeyNotFoundException();

    internal void CheckOwner(ResourceGovernor governor)
    {
        if (!ReferenceEquals(_governor, governor))
        {
            throw new InvalidOperationException("Integer index belongs to a different governor.");
        }
    }

    private void Visit()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _governor.CheckCancellation();
    }

    private int Find(int key)
    {
        Visit();
        if (_buckets.Length == 0) { return -1; }
        int slot = _buckets[(uint)key % (uint)_buckets.Length] - 1;
        while (slot >= 0)
        {
            Visit();
            if (_entries[slot].Key == key) { return slot; }
            slot = _entries[slot].Next;
        }
        return -1;
    }

    internal bool ContainsKey(int key) => Find(key) >= 0;

    internal bool TryGetValue(int key, [MaybeNullWhen(false)] out T value)
    {
        int slot = Find(key);
        if (slot < 0) { value = default; return false; }
        value = _entries[slot].Value;
        return true;
    }

    internal bool TryAdd(ParserMemoryScope memory, int key, T value) => Store(memory, key, value, false);

    internal void Assign(ParserMemoryScope memory, int key, T value) => Store(memory, key, value, true);

    private bool Store(ParserMemoryScope memory, int key, T value, bool replace)
    {
        memory.CheckOwner(_governor);
        if (!ReferenceEquals(memory, _memory))
        {
            throw new InvalidOperationException("Integer index growth belongs to its creation scope.");
        }
        int found = Find(key);
        if (found >= 0)
        {
            if (replace) { _entries[found].Value = value; _version = unchecked(_version + 1); }
            return false;
        }
        Visit();
        if (_free < 0 && _used == _entries.Length) { Grow(memory); }
        int bucket = (int)((uint)key % (uint)_buckets.Length);
        int slot;
        if (_free >= 0)
        {
            slot = _free;
            _free = -_entries[slot].Next - 3;
        }
        else { slot = _used++; }
        _entries[slot] = new Entry { Key = key, Next = _buckets[bucket] - 1, Value = value };
        _buckets[bucket] = slot + 1;
        _count++;
        _version = unchecked(_version + 1);
        return true;
    }

    internal bool Remove(int key)
    {
        Visit();
        if (_buckets.Length == 0) { return false; }
        int bucket = (int)((uint)key % (uint)_buckets.Length);
        int previous = -1;
        int slot = _buckets[bucket] - 1;
        while (slot >= 0)
        {
            Visit();
            if (_entries[slot].Key == key)
            {
                if (previous < 0) { _buckets[bucket] = _entries[slot].Next + 1; }
                else { _entries[previous].Next = _entries[slot].Next; }
                _entries[slot].Value = default;
                _entries[slot].Next = -_free - 3;
                _free = slot;
                _count--;
                _version = unchecked(_version + 1);
                return true;
            }
            previous = slot;
            slot = _entries[slot].Next;
        }
        return false;
    }

    private long ArrayBytes(int capacity) => capacity == 0 ? 0L : checked(64L + (4L + _entryBytes) * capacity);

    internal void PrepareInitialCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _cancellationToken.ThrowIfCancellationRequested(); _governor.CheckCancellation();
        if (capacity <= 4 || capacity > Array.MaxLength || _entries.Length != 0) { return; }
        // This is an optional hint. Leave room for the other writer indexes and
        // publication storage; tight jobs retain their established growth path.
        if (ArrayBytes(capacity) > _governor.RemainingMemoryBytes / 4L) { return; }
        Grow(_memory, capacity);
    }

    private void Grow(ParserMemoryScope memory)
    {
        int capacity = (int)Math.Min(Array.MaxLength, Math.Max(4L, _entries.Length * 2L));
        if (capacity <= _used) { throw new QpdfParseException("Integer index exceeds the supported capacity.", null, null); }
        Grow(memory, capacity);
    }

    private void Grow(ParserMemoryScope memory, int capacity)
    {
        long reserved = ArrayBytes(capacity);
        memory.Reserve(reserved);
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _governor.CheckCancellation();
            // Admission bounds the two zero-initialized arrays before allocation.
            _governor.NoteOperation();
            int[] buckets = new int[capacity];
            Entry[] entries = new Entry[capacity];
            for (int index = 0; index < _used; index++)
            {
                Visit();
                Entry entry = _entries[index];
                if (entry.Next >= -1)
                {
                    int bucket = (int)((uint)entry.Key % (uint)capacity);
                    entry.Next = buckets[bucket] - 1;
                    buckets[bucket] = index + 1;
                }
                entries[index] = entry;
            }
            long previous = ArrayBytes(_entries.Length);
            _entries = entries;
            _buckets = buckets;
            memory.Release(previous);
        }
        catch { memory.Release(reserved); throw; }
    }

    internal int Version => _version;

    internal KeyValuePair<int, T> EntryAt(int index)
    {
        Visit();
        if ((uint)index >= (uint)_used || _entries[index].Next < -1)
        {
            throw new InvalidOperationException("Integer index has no current entry.");
        }
        Entry entry = _entries[index];
        return new KeyValuePair<int, T>(entry.Key, entry.Value);
    }

    internal bool MoveNext(ref int index, out KeyValuePair<int, T> current)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _governor.CheckCancellation();
        while (index < _used)
        {
            Visit();
            Entry entry = _entries[index++];
            if (entry.Next < -1) { continue; }
            current = new KeyValuePair<int, T>(entry.Key, entry.Value);
            return true;
        }
        current = default;
        return false;
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<KeyValuePair<int, T>> IEnumerable<KeyValuePair<int, T>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<KeyValuePair<int, T>>
    {
        private readonly GovernedIntDictionary<T> _owner;
        private readonly int _version;
        private int _index;
        internal Enumerator(GovernedIntDictionary<T> owner)
        {
            _owner = owner; _version = owner._version; _index = 0; Current = default;
        }
        public KeyValuePair<int, T> Current { get; private set; }
        object IEnumerator.Current => Current;
        public bool MoveNext()
        {
            _owner._cancellationToken.ThrowIfCancellationRequested();
            _owner._governor.CheckCancellation();
            if (_version != _owner._version) { throw new InvalidOperationException("Integer index changed during enumeration."); }
            bool found = _owner.MoveNext(ref _index, out KeyValuePair<int, T> current);
            Current = current;
            return found;
        }
        public void Dispose() { }
        public void Reset() => throw new NotSupportedException();
    }
}
