using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns an integer writer index while borrowing immutable values.</summary>
internal sealed class WriterIndex<T> : IDisposable
{
    private ParserMemoryScope? _memory;
    private GovernedIntDictionary<T>? _index;

    private WriterIndex(ParserMemoryScope memory, GovernedIntDictionary<T> index)
    { _memory = memory; _index = index; }

    internal static WriterIndex<T> Create(long valueBytes, ResourceGovernor governor, CancellationToken token)
        => Create(0, valueBytes, governor, token);

    internal static WriterIndex<T> Create(int count, long valueBytes, ResourceGovernor governor, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueBytes);
        ArgumentNullException.ThrowIfNull(governor);
        token.ThrowIfCancellationRequested();
        governor.CheckCancellation();
        governor.ChargeWork(1L);
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(64L);
            GovernedIntDictionary<T> index = GovernedIntDictionary<T>.Create(memory, governor, valueBytes, token);
            index.PrepareInitialCapacity(count);
            return new WriterIndex<T>(memory, index);
        }
        catch { memory.Dispose(); throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(WriterIndex<T>));
    private GovernedIntDictionary<T> Index => _index ?? throw new ObjectDisposedException(nameof(WriterIndex<T>));
    internal int Count => Index.Count;
    internal void CheckOwner(ResourceGovernor governor) => Index.CheckOwner(governor);
    internal bool ContainsKey(int number) => Index.ContainsKey(number);
    internal bool TryGetValue(int number, [MaybeNullWhen(false)] out T value) => Index.TryGetValue(number, out value);
    internal bool TryAdd(int number, T value) => Index.TryAdd(Memory, number, value);
    internal void Add(int number, T value)
    {
        if (!TryAdd(number, value)) { throw new ArgumentException("Writer index already contains the object number.", nameof(number)); }
    }
    internal T this[int number]
    {
        get => Index[number];
        set => Index.Assign(Memory, number, value);
    }

    public Enumerator GetEnumerator() => new(this, Index.Version);

    internal struct Enumerator
    {
        private readonly WriterIndex<T> _owner;
        private readonly int _version;
        private int _position;
        private bool _hasCurrent;
        internal Enumerator(WriterIndex<T> owner, int version)
        { _owner = owner; _version = version; _position = 0; _hasCurrent = false; }
        private GovernedIntDictionary<T> Index
        {
            get
            {
                GovernedIntDictionary<T> index = _owner.Index;
                if (_version != index.Version) { throw new InvalidOperationException("Writer index changed during enumeration."); }
                return index;
            }
        }
        public KeyValuePair<int, T> Current
        {
            get
            {
                GovernedIntDictionary<T> index = Index;
                if (!_hasCurrent) { throw new InvalidOperationException("Writer index has no current entry."); }
                return index.EntryAt(_position - 1);
            }
        }
        public bool MoveNext() => _hasCurrent = Index.MoveNext(ref _position, out _);
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _index = null; _memory = null;
        memory?.Dispose();
    }
}
