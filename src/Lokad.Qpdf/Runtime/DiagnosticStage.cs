using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Runtime;

/// <summary>Exclusively scoped ordinary text until an internal reader closes.</summary>
internal sealed class DiagnosticStage : IDisposable
{
    private const long OwnerBytes = 96L;
    private const int ChunkLength = 1024;
    private const long ChunkBytes = 48L + 24L + ChunkLength;
    private readonly DiagnosticBag _bag;
    private readonly ResourceGovernor _governor;
    private ParserMemoryScope? _memory;
    private List<Entry>? _entries;
    private Chunk? _head;
    private Chunk? _tail;
    private long _length;
    private long _characters;
    private long _nonempty;
    private int _promoted;
    private bool _admitting;

    // ASCII uses one byte per character. Other messages retain raw UTF-16 code
    // units, including unmatched surrogates; UTF-8 replacement must not alter
    // observable text. A complemented length identifies this second encoding.
    internal readonly record struct Entry(QpdfDiagnosticCode Code, QpdfDiagnosticSeverity Severity,
        int EncodedLength, int Hash, long Offset, long Utf8Bytes)
    {
        internal int Length => EncodedLength < 0 ? ~EncodedLength : EncodedLength;
        internal bool Ascii => EncodedLength >= 0;
    }

    private sealed class Chunk
    {
        internal readonly byte[] Buffer = new byte[ChunkLength];
        internal Chunk? Next;
        internal int Used;
    }

    private DiagnosticStage(DiagnosticBag bag, ResourceGovernor governor, ParserMemoryScope memory, List<Entry> entries)
    {
        _bag = bag;
        _governor = governor;
        _memory = memory;
        _entries = entries;
    }

    internal static DiagnosticStage Create(DiagnosticBag bag, ResourceGovernor governor)
    {
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(OwnerBytes);
            List<Entry> entries = ParserContainers.CreateList<Entry>(memory);
            DiagnosticStage stage = new(bag, governor, memory, entries);
            governor.AttachDiagnosticStage(stage);
            return stage;
        }
        catch { memory.Dispose(); throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(DiagnosticStage));
    private List<Entry> Entries => _entries ?? throw new ObjectDisposedException(nameof(DiagnosticStage));

    internal bool Admitting => _admitting;

    internal void Append(QpdfDiagnosticCode code, QpdfDiagnosticSeverity severity, string message, int hash, bool ascii, long utf8Bytes)
        => AppendParts(code, severity, [message], new DiagnosticTextInfo(message.Length, utf8Bytes, hash, ascii));

    internal void AppendParts(QpdfDiagnosticCode code, QpdfDiagnosticSeverity severity, ReadOnlySpan<DiagnosticPart> parts, DiagnosticTextInfo info)
    {
        // Even an empty message has an admission operation. The estimator has
        // at most the fixed-width capacity-growth loops; no input scan hides here.
        _governor.ChargeWork(1L);
        _governor.CheckCancellation();
        Chunk? previousTail = _tail;
        int previousUsed = previousTail?.Used ?? 0;
        long previousLength = _length;
        long previousWork = PresentationWork(_governor.DiagnosticWorkState);
        _governor.SetDiagnosticWork(this, previousWork);
        long characters = checked(_characters + info.Length);
        long nonempty = _nonempty + (info.Length == 0 ? 0L : 1L);
        long length = checked(_length + info.Length * (info.Ascii ? 1L : 2L));
        _admitting = true;
        try
        {
            _governor.SetDiagnosticWork(this, PresentationWork(_governor.DiagnosticWorkState,
                Entries.Count - (long)_promoted + 1L, characters, nonempty, length));
            Span<char> digits = stackalloc char[20];
            Span<char> escaped = stackalloc char[3];
            foreach (DiagnosticPart part in parts)
            {
                _governor.ChargeWork(1L);
                _governor.CheckCancellation();
                int written = part.WriteNumber(_governor, digits);
                AppendText(part.Prefix, info.Ascii);
                AppendText(digits.Slice(digits.Length - written, written), info.Ascii);
                foreach (byte value in part.RawName)
                {
                    _governor.ChargeWork(1L);
                    int count = PdfName.WriteDisplayByte(value, escaped);
                    AppendText(escaped.Slice(0, count), info.Ascii);
                }
            }
            Entry entry = new(code, severity, info.Ascii ? info.Length : ~info.Length, info.Hash, previousLength, info.Utf8Bytes);
            if (Entries.Count == Entries.Capacity)
            {
                long capacity = Math.Min(Array.MaxLength, Math.Max(4L, Entries.Capacity * 2L));
                _governor.ChargeWork(checked(Entries.Count + capacity));
                _governor.CheckCancellation();
            }
            ParserContainers.AddItem(Memory, Entries, entry, 32L);
            _characters = characters;
            _nonempty = nonempty;
        }
        catch
        {
            Chunk? discarded = previousTail is null ? _head : previousTail.Next;
            if (previousTail is null) { _head = null; }
            else { previousTail.Next = null; previousTail.Used = previousUsed; }
            _tail = previousTail;
            _length = previousLength;
            _governor.SetDiagnosticWork(this, previousWork);
            while (discarded is not null)
            {
                Chunk? next = discarded.Next;
                discarded.Next = null;
                discarded = next;
                Memory.Release(ChunkBytes);
            }
            throw;
        }
        finally { _admitting = false; }
    }

    private void AppendText(ReadOnlySpan<char> text, bool ascii)
    {
        for (int offset = 0; offset < text.Length; offset += Math.Min(256, text.Length - offset))
        {
            int count = Math.Min(256, text.Length - offset);
            _governor.ChargeWork(count);
            _governor.CheckCancellation();
            for (int index = offset; index < offset + count; index++)
            {
                char value = text[index];
                WriteByte((byte)value);
                if (!ascii) { WriteByte((byte)(value >> 8)); }
            }
        }
    }

    internal long PresentationWork(DiagnosticWorkState state)
        => _memory is null ? 0L : PresentationWork(state, Entries.Count - (long)_promoted, _characters, _nonempty, _length);

    private long PresentationWork(DiagnosticWorkState state, long entries, long characters, long nonempty, long length)
    {
        if (entries == 0L) { return 0L; }
        try
        {
            checked
            {
                // Copying locates a starting chunk and visits every character.
                // The UTF-8 quota count is already retained in the descriptor.
                long chunks = length / ChunkLength + (length % ChunkLength == 0L ? 0L : 1L);
                long text = characters + entries * chunks;

                // Only nonempty strings enter the weak memo. Equal-length hash
                // collisions may compare the whole message at every candidate.
                long messages = state.Messages + nonempty;
                long memo = messages * (nonempty + characters);

                // One record per entry, at most one string per nonempty entry,
                // plus the list. Each register can scan every indexed carrier,
                // charge its insertion, and charge two activation units.
                long carriers = entries + nonempty + 1L;
                long index = carriers * (state.Carriers + carriers + 1L) + 2L * carriers + entries;
                long slots = state.Slots + carriers;
                long buckets = state.Buckets;
                while (buckets < state.Carriers + carriers)
                {
                    // Rehash pre-admits rollback (slots + prior buckets) and
                    // charges each slot once again while constructing links.
                    index += 2L * slots + buckets;
                    buckets = Math.Max(4L, buckets * 2L);
                }

                long capacity = _bag.EntryCapacity;
                long growths = 0L;
                long list = 0L;
                while (capacity < _promoted + entries)
                {
                    long next = Math.Min(Array.MaxLength, Math.Max(1L, capacity * 2L));
                    if (next <= capacity) { throw new QpdfParseException("Diagnostic list exceeds supported capacity.", null, null); }
                    list += capacity + next;
                    capacity = next;
                    growths++;
                }

                // Each entry has outer string/record/memo reservations; list
                // growth and first list/ledger creation add these boundaries.
                // Each boundary drains at most 64 receipts and 64 memo nodes.
                // New records remain rooted by the bag during the whole export;
                // only existing carriers can retire or start memo maintenance.
                long retirement = Math.Min(64L, state.Carriers);
                long scan = state.Carriers > 0L || state.Messages > 0L || state.Maintenance ? Math.Min(64L, messages) : 0L;
                // A failed pending-stack CAS requires a new finalizer publication;
                // across the export there are at most the existing carrier count.
                long maintenance = (3L * entries + growths + 2L) * (retirement + scan) + state.Carriers;
                return text + memo + index + list + maintenance;
            }
        }
        catch (OverflowException)
        {
            throw new QpdfParseException("Diagnostic presentation exceeds the supported work range.", null, null);
        }
    }

    private void WriteByte(byte value)
    {
        if (_tail is null || _tail.Used == ChunkLength)
        {
            Memory.Reserve(ChunkBytes);
            Chunk next;
            try { next = new Chunk(); }
            catch { Memory.Release(ChunkBytes); throw; }
            if (_tail is null) { _head = next; }
            else { _tail.Next = next; }
            _tail = next;
        }
        _tail.Buffer[_tail.Used++] = value;
        _length = checked(_length + 1L);
    }

    private struct Cursor(Chunk? chunk, int offset)
    {
        private Chunk? _chunk = chunk;
        private int _offset = offset;

        internal byte ReadByte()
        {
            if (_chunk is not null && _offset == _chunk.Used) { _chunk = _chunk.Next; _offset = 0; }
            Chunk current = _chunk ?? throw new InvalidOperationException("Incomplete diagnostic storage.");
            return current.Buffer[_offset++];
        }

        internal char ReadChar(bool ascii)
        {
            int low = ReadByte();
            return (char)(ascii ? low : low | ReadByte() << 8);
        }
    }

    private Cursor Locate(long offset)
    {
        Chunk? chunk = _head;
        while (chunk is not null && offset >= chunk.Used)
        {
            _governor.ChargeWork(1L);
            _governor.CheckCancellation();
            offset -= chunk.Used;
            chunk = chunk.Next;
        }
        if (offset > int.MaxValue) { throw new InvalidOperationException("Diagnostic offset exceeds a chunk."); }
        return new Cursor(chunk, (int)offset);
    }

    internal void CopyTo(Entry entry, Span<char> target)
    {
        Cursor cursor = Locate(entry.Offset);
        for (int offset = 0; offset < target.Length; offset += Math.Min(256, target.Length - offset))
        {
            int count = Math.Min(256, target.Length - offset);
            _governor.ChargeWork(count);
            _governor.CheckCancellation();
            for (int index = offset; index < offset + count; index++) { target[index] = cursor.ReadChar(entry.Ascii); }
        }
    }

    internal void Flush()
    {
        if (_memory is null) { return; }
        _governor.BeginDiagnosticPresentation(this);
        try
        {
            while (_promoted < Entries.Count)
            {
                Entry entry = Entries[_promoted];
                using GovernedText text = GovernedText.Create(_governor, this, entry);
                _bag.AddStaged(entry, text);
                _promoted++;
                _characters -= entry.Length;
                if (entry.Length != 0) { _nonempty--; }
            }
        }
        finally { _governor.EndDiagnosticPresentation(this); }
        Dispose();
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _memory = null;
        _entries = null;
        _head = null;
        _tail = null;
        _governor.DetachDiagnosticStage(this);
        _bag.EndStaging(this);
        memory?.Dispose();
    }
}
