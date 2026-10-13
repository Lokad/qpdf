using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Runtime;

internal readonly record struct DiagnosticWorkState(long Carriers, long Slots, long Buckets, long Messages, bool Maintenance);

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
    private const long SlotBytes = 32L;
    private const int SlotsPerPage = 16;
    private readonly ResourceGovernor _governor;
    private readonly WeakReference<RetainedValueOwners> _weakSelf;
    private ParserMemoryScope? _storage;
    private int[]? _heads;
    private List<Slot[]>? _slots;
    private Segment? _segments;
    private Segment? _valueSegment;
    private Segment? _diagnosticSegment;
    private Receipt? _pending;
    private MessageNode? _messages;
    private long _messageCount;
    private long _retiredBytes;
    private int _free = -1;
    private int _count;
    private int _slotCount;
    private bool _busy;

    // The cached message link enlarges each physical slot from 24 to 32 bytes.
    private readonly record struct Slot(Segment? Segment, int Hash, int Next, int Previous, MessageNode? Message);

    private sealed class Segment
    {
        internal readonly ConditionalWeakTable<object, Receipt> Values = new();
        internal Segment? Next;
        internal Segment? Previous;
        internal int Added;
        internal int Count;
    }

    internal sealed class Receipt
    {
        // Pending acceptance roots its carrier for rollback; activation reuses
        // the ledger's pre-admitted weak link without allocating. The governor
        // keeps that ledger alive while admission can still be spent.
        private object? _ownerOrCarrier;
        internal Receipt? Next;
        // A negative complement denotes prepaid retirement without enlarging
        // every receipt. Pending/ordinary receipts retain their positive index.
        internal int Index;
        internal int SlotIndex => Index < 0 ? ~Index : Index;
        internal bool PrepaidRetirement => Index < 0;
        internal long Bytes;
        private int _active;

        internal Receipt(int index, long bytes, object carrier)
        {
            _ownerOrCarrier = carrier;
            Index = index;
            Bytes = bytes;
        }

        internal object? PendingCarrier => _ownerOrCarrier;

        internal void Activate(RetainedValueOwners owner, bool prepaidRetirement)
        {
            if (prepaidRetirement) { Index = ~Index; }
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
        internal bool Diagnostics;
        internal bool PrepaidRetirement;
        internal CancellationToken Token;
    }

    // The index holds weak text references, never job-lifetime string roots.
    // Node and weak-reference managed storage are admitted before construction.
    private const long MessageNodeBytes = 80L;

    private sealed class MessageNode
    {
        internal readonly WeakReference<string> Message;
        internal readonly int Hash;
        internal MessageNode? Next;
        internal MessageNode? Previous;

        internal MessageNode(string message, int hash)
        {
            Message = new(message);
            Hash = hash;
        }
    }

    internal string? FindDiagnosticMessage(string message, int hash)
    {
        for (MessageNode? node = _messages; node is not null; node = node.Next)
        {
            _governor.ChargeWork(1L);
            _governor.CheckCancellation();
            if (node.Hash != hash || !node.Message.TryGetTarget(out string? candidate) || candidate.Length != message.Length) { continue; }
            bool equal = true;
            for (int offset = 0; offset < message.Length; offset += Math.Min(256, message.Length - offset))
            {
                int count = Math.Min(256, message.Length - offset);
                _governor.ChargeWork(count);
                _governor.CheckCancellation();
                if (!candidate.AsSpan(offset, count).SequenceEqual(message.AsSpan(offset, count))) { equal = false; break; }
            }
            if (equal) { return candidate; }
        }
        return null;
    }

    internal string? FindDiagnosticMessage(ReadOnlySpan<DiagnosticPart> parts, DiagnosticTextInfo info)
    {
        for (MessageNode? node = _messages; node is not null; node = node.Next)
        {
            _governor.ChargeWork(1L);
            _governor.CheckCancellation();
            if (node.Hash != info.Hash || !node.Message.TryGetTarget(out string? candidate) || candidate.Length != info.Length) { continue; }
            if (DiagnosticMessage.Matches(_governor, parts, candidate)) { return candidate; }
        }
        return null;
    }

    internal long RetainCachedDiagnostic(QpdfDiagnostic diagnostic, long reservedBytes, int messageHash)
    {
        // The bag's list is already retained, so this scope cannot disappear
        // while a new diagnostic and its weak message index are accepted.
        // A fresh record/string and memo node can enlarge the future export
        // bound of a different bag. Preflight the combined effect before any
        // receipt activates; normal carrier preflight then remains conservative.
        _governor.PreflightDiagnosticWork(WorkState with
        {
            Carriers = checked(_count + 2L), Slots = checked(_slotCount + 2L), Messages = checked(_messageCount + 1L),
        });
        Storage.Reserve(MessageNodeBytes);
        try
        {
            MessageNode node = new(diagnostic.Message, messageHash);
            long retained = RetainCore(diagnostic, 0L, null, reservedBytes, false, null, node, out _);
            node.Next = _messages;
            if (_messages is not null) { _messages.Previous = node; }
            _messages = node;
            _messageCount++;
            return retained;
        }
        catch { Storage.Release(MessageNodeBytes); throw; }
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

    internal DiagnosticWorkState WorkState => new(_count, _slotCount, _heads?.Length ?? 0, _messageCount, false);

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
        return RetainCore(value, 0L, null, reservedBytes, false, null, null, out _);
    }

    internal long Retain(object wrapper, long wrapperBytes, IReadOnlyList<QpdfObject> roots, long reservedBytes)
    {
        return RetainCore(wrapper, wrapperBytes, roots, reservedBytes, false, null, null, out _);
    }

    internal long Retain(OwnedAllocationBatch allocations, long reservedBytes)
        => RetainCore(allocations, 0L, null, reservedBytes, false, null, null, out _);

    internal long Retain(OwnedAllocationBatch allocations, QpdfObject parsed, long reservedBytes)
        => RetainCore(allocations, 0L, null, reservedBytes, false, parsed, null, out _);

    internal Receipt RetainAllocation(object carrier, long reservedBytes)
    {
        RetainCore(carrier, reservedBytes, null, reservedBytes, true, null, null, out Receipt? receipt);
        return receipt ?? throw new InvalidOperationException("A new allocation needs its own receipt.");
    }

    internal long RetainDiagnostic(QpdfDiagnostic diagnostic, long reservedBytes)
        => RetainCore(diagnostic, 0L, null, reservedBytes, false, null, null, out _);

    internal long RetainCheckOutcome(QpdfCheckOutcome outcome, long detailBytes, long reservedBytes)
        => RetainCore(outcome, detailBytes, null, reservedBytes, false, null, null, out _);

    // Failure cleanup for an exclusively owned allocation. Its owner still
    // roots the carrier, so collection cannot race this allocation-free removal.
    internal void DropAllocation(object carrier, Receipt receipt)
    {
        if (_busy) { throw new InvalidOperationException("Allocation cleanup cannot be nested."); }
        _busy = true;
        try
        {
            Slot slot = GetSlot(receipt.SlotIndex);
            slot.Segment?.Values.Remove(carrier);
            receipt.Cancel();
            RemoveSlot(receipt.SlotIndex);
            if (receipt.PrepaidRetirement) { _governor.ReleaseAllocationRetirementWork(); }
        }
        finally { _busy = false; }
        GC.KeepAlive(carrier);
    }

    private long RetainCore(object value, long wrapperBytes, IReadOnlyList<QpdfObject>? roots,
        long reservedBytes, bool allocationOnly, QpdfObject? sourceParsed, MessageNode? cachedMessage, out Receipt? allocationReceipt)
    {
        allocationReceipt = null;
        if (_busy) { throw new InvalidOperationException("Value ownership acceptance cannot be nested."); }
        _busy = true;
        Acceptance? acceptance = null;
        bool fundedAcceptance = false;
        try
        {
            _governor.ReserveMemory(48L);
            fundedAcceptance = true;
            acceptance = new Acceptance { Token = value is OwnedAllocationBatch batch ? batch.Token : CancellationToken.None,
                PrepaidRetirement = value is not OwnedAllocationBatch owned || owned.PrepaidRetirement,
                Diagnostics = value is QpdfDiagnostic or IReadOnlyList<QpdfDiagnostic>
                or QpdfCheckOutcome or IReadOnlyList<QpdfCheckOutcome> };
            Initialize();
            if (roots is null)
            {
                if (allocationOnly)
                {
                    if (!Register(value, wrapperBytes, acceptance))
                    {
                        throw new InvalidOperationException("Allocation carrier is already retained.");
                    }
                    allocationReceipt = acceptance.First;
                }
                else if (value is OwnedAllocationBatch allocations)
                {
                    if (sourceParsed is not null) { Visit(sourceParsed, acceptance); }
                    for (int index = 0; index < allocations.Count; index++)
                    {
                        OwnedAllocationBatch.Allocation allocation = allocations.Get(index);
                        if (!Register(allocation.Carrier ?? throw new InvalidOperationException("Allocation batch has no carrier."), allocation.Bytes, acceptance))
                        { throw new InvalidOperationException("Allocation carrier is already retained."); }
                        allocations.ObserveAcceptedVisit();
                    }
                }
                else if (value is QpdfObject parsed) { Visit(parsed, acceptance); }
                else if (value is QpdfDiagnostic diagnostic) { VisitDiagnostic(diagnostic, acceptance, cachedMessage); }
                else if (value is QpdfCheckOutcome outcome)
                {
                    Register(outcome, 40L, acceptance);
                    if (wrapperBytes != 0L) { Register(outcome.Detail, wrapperBytes, acceptance); }
                }
                else if (value is IReadOnlyList<QpdfDiagnostic> diagnostics)
                {
                    foreach (QpdfDiagnostic entry in diagnostics) { VisitDiagnostic(entry, acceptance, null); }
                }
                else { throw new InvalidOperationException("Expected retained storage."); }
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
            long retirements = 0L;
            for (Receipt? receipt = acceptance.First; receipt is not null; receipt = receipt.Next)
            {
                acceptance.Token.ThrowIfCancellationRequested();
                _governor.CheckCancellation();
                _governor.ChargeWork(2L);
                retirements++;
            }
            if (acceptance.PrepaidRetirement)
            {
                _governor.ReserveAllocationRetirementWork(retirements);
            }
            for (Receipt? receipt = acceptance.First; receipt is not null;)
            {
                Receipt? next = receipt.Next;
                receipt.Next = null;
                receipt.Activate(this, acceptance.PrepaidRetirement);
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

    private void VisitDiagnostic(QpdfDiagnostic diagnostic, Acceptance acceptance, MessageNode? cachedMessage)
    {
        _governor.CheckCancellation();
        _governor.ChargeWork(1L);
        if (!Register(diagnostic, 32L, acceptance)) { return; }
        // String.Create returns the global empty string without an allocation.
        // It must not anchor a job's weak ownership ledger.
        if (diagnostic.Message.Length != 0)
        {
            bool added = Register(diagnostic.Message, GovernedText.StringBytes(diagnostic.Message.Length), acceptance);
            if (cachedMessage is not null)
            {
                if (!added || acceptance.First is not Receipt receipt)
                { throw new InvalidOperationException("A cached message needs its own carrier receipt."); }
                Slot slot = GetSlot(receipt.SlotIndex);
                SetSlot(receipt.SlotIndex, slot with { Message = cachedMessage });
            }
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
                if (integer.IsSharedValue) { return; }
                if (!Register(integer, 32L, acceptance)) { return; }
                if (!integer.HasSharedText)
                { Register(integer.Text, checked(32L + integer.Text.Length * 2L), acceptance); }
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
                PdfDictionary.EntryEnumerator entries = dictionary.GetEntryEnumerator();
                while (entries.MoveNext())
                {
                    Visit(entries.Current.Key, acceptance);
                    Visit(entries.Current.Value, acceptance);
                }
                return;
            case PdfStream stream:
                if (!Register(stream, 32L, acceptance)) { return; }
                Register(stream.AllocationCarrier, stream.AllocationBytes, acceptance);
                Visit(stream.Dictionary, acceptance);
                return;
            default:
                throw new InvalidOperationException("Unknown parsed value ownership.");
        }
    }

    private bool Register(object carrier, long bytes, Acceptance acceptance)
    {
        acceptance.Token.ThrowIfCancellationRequested();
        int hash = RuntimeHelpers.GetHashCode(carrier);
        if (Heads.Length > 0)
        {
            int current = Heads[hash & (Heads.Length - 1)] - 1;
            while (current >= 0)
            {
                acceptance.Token.ThrowIfCancellationRequested();
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
        _governor.PreflightDiagnosticWork(WorkState with
        {
            Carriers = checked(_count + 1L), Slots = checked(_slotCount + (_free < 0 ? 1L : 0L)),
        });
        long nextBytes = checked(acceptance.Bytes + bytes);
        Segment segment = GetSegment(acceptance.Diagnostics);
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
            SetSlot(index, new Slot(segment, hash, previous, -1, null));
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
                    SetSlot(index, new Slot(null, 0, _free, -1, null));
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

    private Segment GetSegment(bool diagnostics)
    {
        // Long-lived warnings must not pin nearly empty tables containing
        // short-lived reader values from every disposed document. Both groups
        // share the same index/retirement path and eight-insertion table cap.
        Segment? segment = diagnostics ? _diagnosticSegment : _valueSegment;
        if (segment is not null && segment.Added < SegmentEntries) { return segment; }
        Storage.Reserve(SegmentBytes);
        try
        {
            segment = new Segment { Next = _segments };
            if (_segments is not null) { _segments.Previous = segment; }
            _segments = segment;
            if (diagnostics) { _diagnosticSegment = segment; }
            else { _valueSegment = segment; }
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
        // Pending acceptance may roll back before the cache node is linked.
        if (removed.Message is MessageNode node && (ReferenceEquals(_messages, node) || node.Previous is not null))
        {
            if (node.Previous is null) { _messages = node.Next; }
            else { node.Previous.Next = node.Next; }
            if (node.Next is not null) { node.Next.Previous = node.Previous; }
            node.Next = null;
            node.Previous = null;
            _messageCount--;
            Storage.Release(MessageNodeBytes);
        }
        SetSlot(index, new Slot(null, 0, _free, -1, null));
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
                if (!receipt.PrepaidRetirement)
                { _governor.CheckCancellation(); _governor.ChargeWork(1L); }
                if (!ReferenceEquals(Interlocked.CompareExchange(ref _pending, receipt.Next, receipt), receipt)) { count--; continue; }
                if (receipt.PrepaidRetirement) { _governor.ChargeAllocationRetirementWork(); }
                receipt.Detach();
                RemoveSlot(receipt.SlotIndex);
            }
        }
        finally { _busy = false; }
    }

    private void RemoveSegment(Segment segment)
    {
        if (ReferenceEquals(_valueSegment, segment)) { _valueSegment = null; }
        if (ReferenceEquals(_diagnosticSegment, segment)) { _diagnosticSegment = null; }
        if (segment.Previous is null) { _segments = segment.Next; }
        else { segment.Previous.Next = segment.Next; }
        if (segment.Next is not null) { segment.Next.Previous = segment.Previous; }
        segment.Next = null;
        segment.Previous = null;
        Storage.Release(SegmentBytes);
    }

    internal void DisposeUnusedIndex()
    {
        if (_count != 0 || _busy) { return; }
        // Drop index and weak-message storage after their last carrier dies.
        _heads = null;
        _slots = null;
        _segments = null;
        _valueSegment = null;
        _diagnosticSegment = null;
        _messages = null;
        _messageCount = 0L;
        _free = -1;
        _slotCount = 0;
        ParserMemoryScope? storage = _storage;
        _storage = null;
        storage?.Dispose();
    }

    internal void DisposeEmpty()
    {
        if (!Empty || _busy) { throw new InvalidOperationException("Live value ownership cannot be discarded."); }
        DisposeUnusedIndex();
        _governor.ReleaseMemory(LedgerBytes);
    }
}
