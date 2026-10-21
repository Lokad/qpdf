using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns removal metadata while borrowing the copied pool and its values.</summary>
internal sealed class OrphanRemovalPlan : IDisposable
{
    private readonly record struct Drop(int Number, QpdfObject Value);
    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private Dictionary<int, QpdfObject>? _pool;
    private Dictionary<int, int>? _mapping;
    private GovernedIntSet? _wanted;
    private GovernedIntSet? _referenced;
    private List<QpdfObject?>? _stack;
    private List<Drop>? _drops;
    private List<KeyValuePair<int, int>>? _sources;
    private CancellationToken _token;

    private OrphanRemovalPlan(ParserMemoryScope memory, ResourceGovernor governor,
        Dictionary<int, QpdfObject> pool, Dictionary<int, int> mapping, CancellationToken token)
    {
        _memory = memory; _governor = governor; _pool = pool; _mapping = mapping; _token = token;
        _wanted = GovernedIntSet.Create(memory, governor, token);
    }

    internal static OrphanRemovalPlan Create(IReadOnlyList<int> candidates,
        Dictionary<int, QpdfObject> pool, Dictionary<int, int> mapping,
        ResourceGovernor governor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(mapping);
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        OrphanRemovalPlan? plan = null;
        try
        {
            memory.Reserve(96L);
            plan = new(memory, governor, pool, mapping, token);
            plan.Build(candidates); return plan;
        }
        catch { if (plan is null) { memory.Dispose(); } else { plan.Dispose(); } throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(OrphanRemovalPlan));
    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(OrphanRemovalPlan));
    private Dictionary<int, QpdfObject> Pool => _pool ?? throw new ObjectDisposedException(nameof(OrphanRemovalPlan));
    private Dictionary<int, int> Mapping => _mapping ?? throw new ObjectDisposedException(nameof(OrphanRemovalPlan));
    private void Check() { _token.ThrowIfCancellationRequested(); Governor.CheckCancellation(); }
    private void Visit() { Check(); Governor.NoteOperation(); }

    private void Add<T>(List<T> items, T value, long itemBytes)
    {
        Visit();
        if (items.Count == items.Capacity)
        {
            Governor.NoteOperation(); Check();
        }
        ParserContainers.AddItem(Memory, items, value, itemBytes);
    }

    private void Build(IReadOnlyList<int> candidates)
    {
        GovernedIntSet wanted = _wanted ?? throw new ObjectDisposedException(nameof(OrphanRemovalPlan));
        for (int index = 0; index < candidates.Count; index++)
        {
            Visit(); int candidate = candidates[index]; Visit();
            if (candidate > 0 && Pool.ContainsKey(candidate)) { wanted.Add(Memory, candidate); }
            Check();
        }
        if (wanted.Count == 0) { return; }

        GovernedIntSet referenced = GovernedIntSet.Create(Memory, Governor, _token); _referenced = referenced;
        List<QpdfObject?>? stack = ParserContainers.CreateList<QpdfObject?>(Memory); _stack = stack;
        foreach (KeyValuePair<int, QpdfObject> entry in Pool)
        {
            Add(stack, entry.Value, IntPtr.Size);
            while (stack.Count != 0)
            {
                Visit(); int last = stack.Count - 1;
                QpdfObject? current = stack[last]; stack.RemoveAt(last);
                if (current is PdfReference reference)
                {
                    if (wanted.Contains(reference.ObjectNumber)) { referenced.Add(Memory, reference.ObjectNumber); }
                }
                else if (current is PdfArray array)
                {
                    for (int index = 0; index < array.Count; index++) { Add(stack, array[index], IntPtr.Size); }
                }
                else if (current is PdfDictionary dictionary)
                {
                    using PdfDictionary.EntryEnumerator entries = dictionary.GetEntryEnumerator();
                    while (entries.MoveNext(Governor, _token))
                    { if (entries.Current.Value is QpdfObject value) { Add(stack, value, IntPtr.Size); } }
                }
                else if (current is PdfStream stream) { Add(stack, stream.Dictionary, IntPtr.Size); }
            }
        }

        long stackBytes = checked(32L + (stack.Capacity == 0 ? 0L : 32L + stack.Capacity * (long)IntPtr.Size));
        _stack = null; stack = null; Memory.Release(stackBytes);
        List<Drop> drops = ParserContainers.CreateList<Drop>(Memory); _drops = drops;
        foreach (int number in wanted)
        {
            if (referenced.Contains(number)) { continue; }
            Visit(); QpdfObject value = Pool[number]; Check(); Add(drops, new(number, value), 16L);
        }
        if (drops.Count == 0) { return; }
        List<KeyValuePair<int, int>> sources = ParserContainers.CreateList<KeyValuePair<int, int>>(Memory); _sources = sources;
        foreach (KeyValuePair<int, int> mapped in Mapping)
        {
            Visit();
            if (wanted.Contains(mapped.Value) && !referenced.Contains(mapped.Value)) { Add(sources, mapped, 8L); }
        }
    }

    internal void Apply()
    {
        Check();
        List<Drop>? drops = _drops; List<KeyValuePair<int, int>>? sources = _sources;
        if (drops is null || sources is null || drops.Count == 0) { return; }
        // Admit the forward mutations and their possible rollback before either
        // dictionary changes. Remove/Add reuse the existing dictionary slots.
        Governor.NoteOperation(); Check();
        int removedObjects = 0, removedSources = 0;
        try
        {
            foreach (Drop drop in drops)
            {
                Check();
                if (!Pool.Remove(drop.Number)) { throw new InvalidOperationException("Copied pool changed during orphan removal."); }
                removedObjects++;
            }
            foreach (KeyValuePair<int, int> source in sources)
            {
                Check();
                if (!Mapping.Remove(source.Key)) { throw new InvalidOperationException("Copied mapping changed during orphan removal."); }
                removedSources++;
            }
            Check();
        }
        catch
        {
            // Cleanup is bounded by the admitted plan. It
            // must finish even when the caller or governor token is cancelled.
            while (removedSources != 0)
            { KeyValuePair<int, int> source = sources[--removedSources]; Mapping.Add(source.Key, source.Value); }
            while (removedObjects != 0)
            { Drop drop = drops[--removedObjects]; Pool.Add(drop.Number, drop.Value); }
            throw;
        }
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _stack = null; _drops = null; _sources = null; _wanted = null; _referenced = null;
        _pool = null; _mapping = null; _memory = null; _governor = null; _token = default;
        memory?.Dispose();
    }
}
