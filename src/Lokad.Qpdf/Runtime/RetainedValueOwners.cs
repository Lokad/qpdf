using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Runtime;

/// <summary>Funds accepted values against their independently escaping storage.</summary>
// Only the job thread changes the index or budget. Collection publishes a
// payload retirement and an already allocated cleanup node; it never calls the
// governor or allocates a queue entry on the finalizer thread.
internal sealed class RetainedValueOwners
{
    // The ledger wrapper and its one shared weak link are admitted together.
    // Active jobs own the ledger through their governor; surviving carriers
    // must not keep a completed job's index and pending retirements alive.
    private const long LedgerBytes = 160L;
    // Independent warmed measurements allocate 376 bytes for the segment and
    // its initial table, including eight insertions. Keep a managed allowance
    // above that geometry and cap insertions before BCL table growth. Native
    // handle-pool/cold-state containment remains a separate governor audit.
    private const long SegmentBytes = 384L;
    private const int SegmentEntries = 8;
    // The receipt has one carrier/owner union and one queue link: measured
    // managed allocation is 48 bytes, with eight bytes of header allowance.
    private const long ReceiptBytes = 56L;
    private const long SlotBytes = 24L;
    private const int SlotsPerPage = 16;
    private readonly ResourceGovernor _governor;
    private readonly WeakReference<RetainedValueOwners> _weakSelf;
    private ParserMemoryScope? _storage;
    private int[]? _heads;
    private List<Slot[]>? _slots;
    private Segment? _segments;
    private Receipt? _pending;
    private long _retiredBytes;
    private int _free = -1;
    private int _count;
    private int _slotCount;
    private bool _busy;

    private readonly record struct Slot(Segment? Segment, int Hash, int Next, int Previous);

    private sealed class Segment
    {
        internal readonly ConditionalWeakTable<object, Receipt> Values = new();
        internal Segment? Next;
        internal Segment? Previous;
        internal int Added;
        internal int Count;
    }

    private sealed class Receipt
    {
        // Pending acceptance roots its carrier for rollback; activation reuses
        // the ledger's pre-admitted weak link without allocating. The governor
        // keeps that ledger alive while admission can still be spent.
        private object? _ownerOrCarrier;
        internal Receipt? Next;
        internal readonly int Index;
        internal readonly long Bytes;
        private int _active;

        internal Receipt(int index, long bytes, object carrier)
        {
            _ownerOrCarrier = carrier;
            Index = index;
            Bytes = bytes;
        }

        internal object? PendingCarrier => _ownerOrCarrier;

        internal void Activate(RetainedValueOwners owner)
        {
            _ownerOrCarrier = owner._weakSelf;
            Volatile.Write(ref _active, 1);
        }

        internal void Detach()
        {
            Next = null;
            _ownerOrCarrier = null;
        }

        internal void Cancel()
        {
            Volatile.Write(ref _active, 0);
            Detach();
            GC.SuppressFinalize(this);
        }

        ~Receipt()
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) { return; }
            if (_ownerOrCarrier is not WeakReference<RetainedValueOwners> reference || !reference.TryGetTarget(out RetainedValueOwners? owner)) { return; }
            Interlocked.Add(ref owner._retiredBytes, Bytes);
            Receipt? previous;
            do
            {
                previous = Volatile.Read(ref owner._pending);
                Next = previous;
            }
            while (!ReferenceEquals(Interlocked.CompareExchange(ref owner._pending, this, previous), previous));
        }
    }

    private sealed class Acceptance
    {
        internal Receipt? First;
        internal long Bytes;
    }

    private RetainedValueOwners(ResourceGovernor governor)
    {
        _governor = governor;
        _weakSelf = new(this);
    }

    internal static RetainedValueOwners Create(ResourceGovernor governor)
    {
        governor.ReserveMemory(LedgerBytes);
        try { return new RetainedValueOwners(governor); }
        catch { governor.ReleaseMemory(LedgerBytes); throw; }
    }

    internal long RetiredBytes => Interlocked.Read(ref _retiredBytes);

    internal bool Busy => _busy;

    internal bool Empty => _count == 0 && Volatile.Read(ref _pending) is null;

    private ParserMemoryScope Storage => _storage ?? throw new InvalidOperationException("Value ownership storage is unavailable.");
    private int[] Heads => _heads ?? throw new InvalidOperationException("Value ownership index is unavailable.");
    private List<Slot[]> SlotPages => _slots ?? throw new InvalidOperationException("Value ownership slots are unavailable.");

    private Slot GetSlot(int index) => SlotPages[index / SlotsPerPage][index % SlotsPerPage];

    private void SetSlot(int index, Slot value) { SlotPages[index / SlotsPerPage][index % SlotsPerPage] = value; }

    private int AddSlot()
    {
        if (_slotCount >= Array.MaxLength) { throw new QpdfParseException("Value ownership exceeds supported slot count.", null, null); }
        if (_slotCount % SlotsPerPage == 0)
        {
            long bytes = checked(32L + SlotBytes * SlotsPerPage);
            Storage.Reserve(bytes);
            try
            {
                Slot[] page = new Slot[SlotsPerPage];
                ParserContainers.AddItem(Storage, SlotPages, page, IntPtr.Size);
            }
            catch { Storage.Release(bytes); throw; }
        }
        return _slotCount++;
    }

    private void Initialize()
    {
        if (_storage is not null) { return; }
        ParserMemoryScope storage = ParserMemoryScope.Create(_governor);
        try
        {
            List<Slot[]> slots = ParserContainers.CreateList<Slot[]>(storage);
            _heads = Array.Empty<int>();
            _slots = slots;
            _storage = storage;
        }
        catch { storage.Dispose(); throw; }
    }

    internal long Retain(QpdfObject value, long reservedBytes)
    {
        return RetainCore(value, 0L, null, reservedBytes);
    }

    internal long Retain(object wrapper, long wrapperBytes, IReadOnlyList<QpdfObject> roots, long reservedBytes)
    {
        return RetainCore(wrapper, wrapperBytes, roots, reservedBytes);
    }

    private long RetainCore(object value, long wrapperBytes, IReadOnlyList<QpdfObject>? roots, long reservedBytes)
    {
        if (_busy) { throw new InvalidOperationException("Value ownership acceptance cannot be nested."); }
        _busy = true;
        Acceptance? acceptance = null;
        bool fundedAcceptance = false;
        try
        {
            _governor.ReserveMemory(48L);
            fundedAcceptance = true;
            acceptance = new Acceptance();
            Initialize();
            if (roots is null)
            {
                if (value is not QpdfObject parsed) { throw new InvalidOperationException("Expected a parsed value."); }
                Visit(parsed, acceptance);
            }
            else
            {
                Register(value, wrapperBytes, acceptance);
                foreach (QpdfObject root in roots)
                {
                    _governor.CheckCancellation();
                    Visit(root, acceptance);
                }
            }
            if (acceptance.Bytes > reservedBytes)
            {
                throw new InvalidOperationException("Value carriers exceed their accepted subtree's receipt.");
            }
            // Every carrier remains rooted by the argument until this atomic
            // handoff finishes. Preflight charges/checks every pending receipt;
            // no operation that may fail follows the first activation.
            for (Receipt? receipt = acceptance.First; receipt is not null; receipt = receipt.Next)
            {
                _governor.CheckCancellation();
                _governor.ChargeWork(2L);
            }
            for (Receipt? receipt = acceptance.First; receipt is not null;)
            {
                Receipt? next = receipt.Next;
                receipt.Next = null;
                receipt.Activate(this);
                receipt = next;
            }
            acceptance.First = null;
            GC.KeepAlive(value);
            GC.KeepAlive(roots);
            return acceptance.Bytes;
        }
        catch
        {
            if (acceptance is not null) { Rollback(acceptance); }
            throw;
        }
        finally
        {
            _busy = false;
            if (fundedAcceptance) { _governor.ReleaseMemory(48L); }
        }
    }

    private void Visit(QpdfObject value, Acceptance acceptance)
    {
        _governor.CheckCancellation();
        _governor.ChargeWork(1L);
        // Acceptance receives graphs already checked by the effective parser
        // policy. The governor's construction limits can differ from that
        // policy after explicit parser settings; do not impose a second depth
        // interpretation here. Identity registration bounds repeated/cyclic visits.
        switch (value)
        {
            case PdfNull or PdfBoolean:
                return;
            case PdfInteger integer:
                if (!Register(integer, 32L, acceptance)) { return; }
                Register(integer.Text, checked(32L + integer.Text.Length * 2L), acceptance);
                return;
            case PdfReal real:
                if (!Register(real, 32L, acceptance)) { return; }
                Register(real.Text, checked(32L + real.Text.Length * 2L), acceptance);
                return;
            case PdfReference reference:
                Register(reference, 32L, acceptance);
                return;
            case PdfName name:
                if (!Register(name, 24L, acceptance)) { return; }
                Register(name.AllocationCarrier, checked(40L + name.RawSpan.Length), acceptance);
                return;
            case PdfByteString bytes:
                if (!Register(bytes, 32L, acceptance)) { return; }
                Register(bytes.AllocationCarrier, checked(32L + bytes.ValueSpan.Length), acceptance);
                return;
            case PdfArray array:
                if (!Register(array, 24L, acceptance)) { return; }
                if (!array.HasSharedEmptyStorage)
                {
                    Register(array.AllocationCarrier, checked(40L + IntPtr.Size * (long)array.Count), acceptance);
                }
                for (int index = 0; index < array.Count; index++) { Visit(array[index], acceptance); }
                return;
            case PdfDictionary dictionary:
                if (!Register(dictionary, 24L, acceptance)) { return; }
                int capacity = dictionary.StorageCapacity;
                // The backing owner includes the lazy cached key collection,
                // which can keep an empty dictionary alive after its wrapper dies.
                Register(dictionary.AllocationCarrier, checked(104L + (capacity == 0 ? 0L : 64L + 28L * capacity)), acceptance);
                Dictionary<PdfName, QpdfObject>.Enumerator entries = dictionary.GetEntryEnumerator();
                while (entries.MoveNext())
                {
                    Visit(entries.Current.Key, acceptance);
                    Visit(entries.Current.Value, acceptance);
                }
                return;
            case PdfStream stream:
                if (!Register(stream, 32L, acceptance)) { return; }
                Register(stream.AllocationCarrier, checked(32L + stream.RawMemory.Length), acceptance);
                Visit(stream.Dictionary, acceptance);
                return;
            default:
                throw new InvalidOperationException("Unknown parsed value ownership.");
        }
    }

    private bool Register(object carrier, long bytes, Acceptance acceptance)
    {
        int hash = RuntimeHelpers.GetHashCode(carrier);
        if (Heads.Length > 0)
        {
            int current = Heads[hash & (Heads.Length - 1)] - 1;
            while (current >= 0)
            {
                _governor.ChargeWork(1L);
                _governor.CheckCancellation();
                Slot slot = GetSlot(current);
                // A segment is already a weak identity index, capped at eight
                // insertions. Avoid another weak handle for the same carrier.
                if (slot.Hash == hash && slot.Segment is not null && slot.Segment.Values.TryGetValue(carrier, out _)) { return false; }
                current = slot.Next;
            }
        }
        _governor.ChargeWork(1L);
        _governor.CheckCancellation();
        long nextBytes = checked(acceptance.Bytes + bytes);
        Segment segment = GetSegment();
        EnsureBucketCapacity();
        Storage.Reserve(ReceiptBytes);
        int index = -1;
        Receipt? receipt = null;
        bool stored = false;
        bool added = false;
        try
        {
            if (_free >= 0)
            {
                index = _free;
                _free = GetSlot(index).Next;
            }
            else
            {
                index = AddSlot();
            }
            receipt = new Receipt(index, bytes, carrier);
            segment.Values.Add(carrier, receipt);
            added = true;
            segment.Added++;
            segment.Count++;
            int bucket = hash & (Heads.Length - 1);
            int previous = Heads[bucket] - 1;
            SetSlot(index, new Slot(segment, hash, previous, -1));
            if (previous >= 0) { SetSlot(previous, GetSlot(previous) with { Previous = index }); }
            Heads[bucket] = index + 1;
            _count++;
            stored = true;
            receipt.Next = acceptance.First;
            acceptance.First = receipt;
            acceptance.Bytes = nextBytes;
            return true;
        }
        catch
        {
            receipt?.Cancel();
            if (!stored)
            {
                if (added)
                {
                    segment.Values.Remove(carrier);
                    segment.Count--;
                }
                if (index >= 0 && index < _slotCount)
                {
                    SetSlot(index, new Slot(null, 0, _free, -1));
                    _free = index;
                }
                Storage.Release(ReceiptBytes);
            }
            throw;
        }
    }

    private static long BucketBytes(int capacity) => capacity == 0 ? 0L : checked(32L + sizeof(int) * (long)capacity);

    private void EnsureBucketCapacity()
    {
        if (_count < Heads.Length) { return; }
        int previousCapacity = Heads.Length;
        long proposed = Math.Max(4L, previousCapacity * 2L);
        if (proposed > Array.MaxLength) { throw new QpdfParseException("Value ownership index exceeds supported capacity.", null, null); }
        int capacity = (int)proposed;
        long bucketBytes = BucketBytes(capacity);
        Storage.Reserve(bucketBytes);
        int[]? buckets = null;
        bool changedLinks = false;
        try
        {
            buckets = new int[capacity];
            // Rollback reconstructs the old index from stable hashes instead
            // of allocating a copy of every pair of links. Admit the complete
            // allocation-free rollback before the first link changes.
            _governor.CheckCancellation();
            _governor.ChargeWork(checked((long)_slotCount + previousCapacity));
            for (int index = 0; index < _slotCount; index++)
            {
                _governor.CheckCancellation();
                _governor.ChargeWork(1L);
                Slot slot = GetSlot(index);
                if (slot.Segment is not null)
                {
                    changedLinks = true;
                    int bucket = slot.Hash & (capacity - 1);
                    int head = buckets[bucket] - 1;
                    SetSlot(index, slot with { Next = head, Previous = -1 });
                    if (head >= 0) { SetSlot(head, GetSlot(head) with { Previous = index }); }
                    buckets[bucket] = index + 1;
                }
            }
            _heads = buckets;
            buckets = null;
            Storage.Release(BucketBytes(previousCapacity));
        }
        catch
        {
            if (changedLinks)
            {
                Array.Clear(Heads);
                for (int index = 0; index < _slotCount; index++)
                {
                    Slot slot = GetSlot(index);
                    if (slot.Segment is null) { continue; }
                    int bucket = slot.Hash & (Heads.Length - 1);
                    int head = Heads[bucket] - 1;
                    SetSlot(index, slot with { Next = head, Previous = -1 });
                    if (head >= 0) { SetSlot(head, GetSlot(head) with { Previous = index }); }
                    Heads[bucket] = index + 1;
                }
            }
            buckets = null;
            Storage.Release(bucketBytes);
            throw;
        }
    }

    private Segment GetSegment()
    {
        Segment? segment = _segments;
        if (segment is not null && segment.Added < SegmentEntries) { return segment; }
        Storage.Reserve(SegmentBytes);
        try
        {
            segment = new Segment { Next = _segments };
            if (_segments is not null) { _segments.Previous = segment; }
            _segments = segment;
            return segment;
        }
        catch { Storage.Release(SegmentBytes); throw; }
    }

    private void Rollback(Acceptance acceptance)
    {
        for (Receipt? receipt = acceptance.First; receipt is not null;)
        {
            Receipt? next = receipt.Next;
            Slot slot = GetSlot(receipt.Index);
            // Pending carriers are strong only during acceptance, allowing
            // allocation-free rollback even when the budget is exhausted.
            if (receipt.PendingCarrier is object carrier) { slot.Segment?.Values.Remove(carrier); }
            receipt.Cancel();
            RemoveSlot(receipt.Index);
            receipt = next;
        }
        acceptance.First = null;
        // A failed registration may have admitted an empty new head before
        // obtaining its first slot. All other empty segments unlink on removal.
        if (_segments is { Count: 0 } empty) { RemoveSegment(empty); }
    }

    private void RemoveSlot(int index)
    {
        Slot removed = GetSlot(index);
        if (removed.Previous < 0)
        {
            Heads[removed.Hash & (Heads.Length - 1)] = removed.Next + 1;
        }
        else
        {
            SetSlot(removed.Previous, GetSlot(removed.Previous) with { Next = removed.Next });
        }
        if (removed.Next >= 0) { SetSlot(removed.Next, GetSlot(removed.Next) with { Previous = removed.Previous }); }
        if (removed.Segment is not null) { removed.Segment.Count--; }
        SetSlot(index, new Slot(null, 0, _free, -1));
        _free = index;
        _count--;
        Storage.Release(ReceiptBytes);
        if (removed.Segment is { Count: 0 } empty) { RemoveSegment(empty); }
    }

    internal void Drain()
    {
        if (_busy) { return; }
        _busy = true;
        try
        {
            for (int count = 0; count < 64; count++)
            {
                Receipt? receipt = Volatile.Read(ref _pending);
                if (receipt is null) { break; }
                _governor.CheckCancellation();
                _governor.ChargeWork(1L);
                if (!ReferenceEquals(Interlocked.CompareExchange(ref _pending, receipt.Next, receipt), receipt)) { count--; continue; }
                receipt.Detach();
                RemoveSlot(receipt.Index);
            }
        }
        finally { _busy = false; }
    }

    private void RemoveSegment(Segment segment)
    {
        if (segment.Previous is null) { _segments = segment.Next; }
        else { segment.Previous.Next = segment.Next; }
        if (segment.Next is not null) { segment.Next.Previous = segment.Previous; }
        segment.Next = null;
        segment.Previous = null;
        Storage.Release(SegmentBytes);
    }

    internal void DisposeEmpty()
    {
        if (!Empty || _busy) { throw new InvalidOperationException("Live value ownership cannot be discarded."); }
        _heads = null;
        _slots = null;
        _segments = null;
        ParserMemoryScope? storage = _storage;
        _storage = null;
        storage?.Dispose();
        _governor.ReleaseMemory(LedgerBytes);
    }
}
