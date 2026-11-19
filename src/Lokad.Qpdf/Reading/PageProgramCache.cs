using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

// Stores successful tokenization keys. The reader owns the streams, and each
// page still runs its decoding and resource checks.
internal sealed class PageProgramCache(
    ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token) : IDisposable
{
    private const int MaximumEntries = 8;
    private const int MaximumStreams = 1024;
    private readonly record struct Entry(PdfStream[] Streams, QpdfObject? Resources, bool UsesResources)
    {
        internal long Charge => 32L + IntPtr.Size * (long)Streams.Length;
    }

    private List<Entry>? _recent;

    internal static PageProgramCache? TryCreate(ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (governor.RemainingMemoryBytes < 4096L) { return null; }
        ParserMemoryScope? memory = null;
        try
        {
            memory = ParserMemoryScope.Create(governor);
            memory.Reserve(64L);
            return new(memory, governor, token);
        }
        catch (Exception ex) when (ex is QpdfLimitException or QpdfMemoryAdmissionException)
        { memory?.Dispose(); return null; }
        catch { memory?.Dispose(); throw; }
    }

    internal bool Contains(ContentEntries streams, QpdfObject? resources)
    {
        if (_recent is null || streams.Count > MaximumStreams) { return false; }
        for (int index = _recent.Count - 1; index >= 0; index--)
        {
            token.ThrowIfCancellationRequested(); governor.NoteOperation();
            Entry entry = _recent[index];
            if (entry.Streams.Length != streams.Count
                || entry.UsesResources && !SameResources(entry.Resources, resources)) { continue; }
            int slot = 0;
            while (slot < entry.Streams.Length && ReferenceEquals(entry.Streams[slot], streams.Get(slot).Stream)) { slot++; }
            if (slot == entry.Streams.Length) { return true; }
        }
        return false;
    }

    internal void Remember(ContentEntries streams, QpdfObject? resources, bool usesResources)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        int count = streams.Count;
        long charge = 32L + IntPtr.Size * (long)count;
        if (count > MaximumStreams || charge > governor.RemainingMemoryBytes / 8L) { return; }
        try
        {
            _recent ??= ParserContainers.CreateList<Entry>(memory, MaximumEntries, 32L);
            memory.Reserve(charge);
        }
        catch (Exception ex) when (ex is QpdfLimitException or QpdfMemoryAdmissionException) { return; }

        try
        {
            PdfStream[] key = new PdfStream[count];
            for (int index = 0; index < count; index++) { key[index] = streams.Get(index).Stream; }
            if (_recent.Count == MaximumEntries)
            {
                long freed = _recent[0].Charge;
                _recent.RemoveAt(0);
                memory.Release(freed);
            }
            _recent.Add(new(key, usesResources ? resources : null, usesResources));
        }
        catch { memory.Release(charge); throw; }
    }

    // Preserve unresolved context identity without opening resources for a
    // page whose program never consults them.
    private static bool SameResources(QpdfObject? first, QpdfObject? second)
        => ReferenceEquals(first, second)
            || first is PdfReference left && second is PdfReference right
                && left.Equals(right) && left.IsFromSource == right.IsFromSource;

    public void Dispose()
    {
        _recent = null;
        memory.Dispose();
    }
}
