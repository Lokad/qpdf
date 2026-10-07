using System.Collections;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Filters;

/// <summary>Owns admitted stage storage while borrowing immutable parameters and resolvers.</summary>
internal sealed class FilterPlan : IReadOnlyList<FilterStage>, IDisposable
{
    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private CancellationToken _token;
    private FilterStage[]? _stages;

    private FilterPlan(ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    { _memory = memory; _governor = governor; _token = token; _stages = Array.Empty<FilterStage>(); }

    internal static FilterPlan Create(ResourceGovernor governor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(governor);
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(64L); governor.ChargeWork(1L);
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            return new FilterPlan(memory, governor, token);
        }
        catch { memory.Dispose(); throw; }
    }

    private FilterStage[] Stages => _stages ?? throw new ObjectDisposedException(nameof(FilterPlan));
    internal ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(FilterPlan));
    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(FilterPlan));
    public int Count => Stages.Length;

    internal void Visit()
    { _ = Stages; _token.ThrowIfCancellationRequested(); Governor.CheckCancellation(); Governor.ChargeWork(1L); }

    internal void Allocate(int count)
    {
        Visit(); ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (Stages.Length != 0) { throw new InvalidOperationException("Stage storage is already allocated."); }
        if (count > Array.MaxLength) { throw new QpdfException("Filter sequence exceeds supported capacity."); }
        if (count == 0) { return; }
        // Each readonly stage is three references, stored inline in the array.
        long bytes = checked(32L + count * (3L * IntPtr.Size));
        Memory.Reserve(bytes);
        try
        {
            Governor.ChargeWork(checked(1L + count * (3L * IntPtr.Size)));
            _token.ThrowIfCancellationRequested(); Governor.CheckCancellation();
            _stages = new FilterStage[count];
        }
        catch { Memory.Release(bytes); throw; }
    }

    internal void Set(int index, FilterStage stage) { Visit(); Stages[index] = stage; }
    public FilterStage this[int index] { get { Visit(); return Stages[index]; } }
    internal void ReserveName(long bytes) => Memory.Reserve(bytes);
    internal void ReleaseName(long bytes) => Memory.Release(bytes);
    internal void RetainName(string value, long bytes) => Memory.RetainAllocation(value, bytes);

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<FilterStage> IEnumerable<FilterStage>.GetEnumerator() => BoxedEnumerator.Create(this);
    IEnumerator IEnumerable.GetEnumerator() => BoxedEnumerator.Create(this);

    internal struct Enumerator
    {
        private readonly FilterPlan _owner;
        private int _index;
        internal Enumerator(FilterPlan owner) { _owner = owner; _index = -1; }
        public FilterStage Current => _owner[_index];
        public bool MoveNext() { _owner.Visit(); return ++_index < _owner.Count; }
    }

    private sealed class BoxedEnumerator : IEnumerator<FilterStage>
    {
        private FilterPlan? _owner;
        private ParserMemoryScope? _memory;
        private int _index;
        private BoxedEnumerator(FilterPlan owner, ParserMemoryScope memory)
        { _owner = owner; _memory = memory; _index = -1; }

        internal static BoxedEnumerator Create(FilterPlan owner)
        {
            owner.Visit(); ParserMemoryScope memory = ParserMemoryScope.Create(owner.Governor);
            try { memory.Reserve(48L); owner.Visit(); return new BoxedEnumerator(owner, memory); }
            catch { memory.Dispose(); throw; }
        }

        private FilterPlan Owner => _owner ?? throw new ObjectDisposedException(nameof(BoxedEnumerator));
        public FilterStage Current => Owner[_index];
        object IEnumerator.Current
        {
            get
            {
                FilterStage stage = Current;
                ParserMemoryScope memory = _memory ?? throw new ObjectDisposedException(nameof(BoxedEnumerator));
                const long boxBytes = 40L;
                memory.Reserve(boxBytes);
                try
                {
                    object boxed = stage;
                    memory.RetainAllocation(boxed, boxBytes);
                    return boxed;
                }
                catch { memory.Release(boxBytes); throw; }
            }
        }
        public bool MoveNext() { Owner.Visit(); return ++_index < Owner.Count; }
        public void Reset() { Owner.Visit(); _index = -1; }
        public void Dispose()
        { ParserMemoryScope? memory = _memory; _owner = null; _memory = null; memory?.Dispose(); }
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _stages = null; _memory = null; _governor = null; _token = CancellationToken.None;
        memory?.Dispose();
    }
}
