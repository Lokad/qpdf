using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns exact ordered-sequence storage while borrowing immutable values.</summary>
internal sealed class WriterOrder<T> : IDisposable
{
    private ResourceGovernor? _governor;
    private CancellationToken _token;
    private ParserMemoryScope? _memory;
    private T[]? _items;
    private int _count;
    private int _version;

    private WriterOrder(ParserMemoryScope memory, T[] items, ResourceGovernor governor, CancellationToken token)
    {
        _memory = memory; _items = items; _governor = governor; _token = token;
    }

    internal static WriterOrder<T> Create(int capacity, long itemBytes, ResourceGovernor governor, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemBytes);
        ArgumentNullException.ThrowIfNull(governor);
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (capacity > Array.MaxLength) { throw new QpdfException("Writer sequence exceeds the supported capacity."); }
        long bytes = checked(capacity * itemBytes);
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(64L + (capacity == 0 ? 0L : 32L + bytes));
            governor.ChargeWork(checked(1L + bytes));
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            return new WriterOrder<T>(memory, capacity == 0 ? Array.Empty<T>() : new T[capacity], governor, token);
        }
        catch { memory.Dispose(); throw; }
    }

    private T[] Items => _items ?? throw new ObjectDisposedException(nameof(WriterOrder<T>));
    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(WriterOrder<T>));
    internal int Count { get { _ = Items; return _count; } }

    private void Visit()
    {
        _ = Items; _token.ThrowIfCancellationRequested(); Governor.CheckCancellation(); Governor.ChargeWork(1L);
    }

    internal void Add(T value)
    {
        Visit();
        T[] items = Items;
        if (_count == items.Length) { throw new QpdfException("Writer sequence exceeded its admitted capacity."); }
        items[_count++] = value; _version = unchecked(_version + 1);
    }

    internal T this[int index]
    {
        get
        {
            Visit();
            if ((uint)index >= (uint)_count) { throw new ArgumentOutOfRangeException(nameof(index)); }
            return Items[index];
        }
    }

    internal void Sort<TComparer>(TComparer comparer) where TComparer : struct, IComparer<T>
    {
        Visit(); _version = unchecked(_version + 1);
        GovernedSort.Sort(Items.AsSpan(0, _count), comparer, Governor, _token);
    }

    public Enumerator GetEnumerator() => new(this);

    internal struct Enumerator
    {
        private readonly WriterOrder<T> _owner;
        private readonly int _version;
        private int _index;
        internal Enumerator(WriterOrder<T> owner) { _owner = owner; _version = owner._version; _index = -1; }
        public T Current => _owner[_index];
        public bool MoveNext()
        {
            _owner.Visit();
            if (_version != _owner._version) { throw new InvalidOperationException("Writer sequence changed during enumeration."); }
            return ++_index < _owner._count;
        }
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _items = null; _memory = null; _governor = null; _token = CancellationToken.None;
        _count = 0; _version = unchecked(_version + 1);
        memory?.Dispose();
    }
}

internal readonly struct WriterNumberOrder : IComparer<int>
{
    public int Compare(int left, int right) => left.CompareTo(right);
}

internal readonly struct WriterObjectOrder : IComparer<NumberedObject>
{
    public int Compare(NumberedObject left, NumberedObject right) => left.Number.CompareTo(right.Number);
}

internal readonly struct WriterNameOrder(ResourceGovernor governor, CancellationToken token) : IComparer<PdfName>
{
    public int Compare(PdfName? left, PdfName? right)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        return PdfNameDisplay.Compare(left, right, governor, token);
    }
}
