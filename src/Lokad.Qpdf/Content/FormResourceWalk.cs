using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Content;

/// <summary>Owns form-resource traversal metadata while borrowing PDF values.</summary>
internal sealed class FormResourceWalk : IDisposable
{
    internal readonly record struct Form(int? Id, PdfStream Stream);
    private sealed class ScopeComparer(PdfObjectComparison comparison) : IEqualityComparer<PdfDictionary>
    {
        public bool Equals(PdfDictionary? left, PdfDictionary? right)
            => left is null || right is null ? left is null && right is null : comparison.AreEqual(left, right);
        public int GetHashCode(PdfDictionary dictionary) => comparison.Hash(dictionary);
    }

    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private PdfObjectComparison? _comparison;
    private HashSet<PdfDictionary>? _seen;
    private GovernedIntSet? _visited;
    private List<PdfDictionary>? _scopes;
    private List<Form>? _forms;
    private CancellationToken _token;

    private FormResourceWalk(ParserMemoryScope memory, ResourceGovernor governor,
        PdfObjectComparison comparison, CancellationToken token)
    {
        _memory = memory; _governor = governor; _comparison = comparison; _token = token;
        _visited = GovernedIntSet.Create(memory, governor, token);
        _scopes = ParserContainers.CreateList<PdfDictionary>(memory);
        _forms = ParserContainers.CreateList<Form>(memory);
        memory.Reserve(160L); // Set and governed comparer wrappers.
        _seen = new HashSet<PdfDictionary>(new ScopeComparer(comparison));
    }

    internal static FormResourceWalk Create(ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfObjectComparison? comparison = null;
        try
        {
            memory.Reserve(80L); comparison = PdfObjectComparison.Create(governor, token);
            return new(memory, governor, comparison, token);
        }
        catch { comparison?.Dispose(); memory.Dispose(); throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(FormResourceWalk));
    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(FormResourceWalk));
    private List<PdfDictionary> Scopes => _scopes ?? throw new ObjectDisposedException(nameof(FormResourceWalk));
    private List<Form> Forms => _forms ?? throw new ObjectDisposedException(nameof(FormResourceWalk));
    private void Visit() { _token.ThrowIfCancellationRequested(); Governor.CheckCancellation(); Governor.ChargeWork(1L); }
    internal int ScopeCount => Scopes.Count;
    internal int Count => Forms.Count;
    internal PdfDictionary GetScope(int index) { Visit(); return Scopes[index]; }
    internal Form Get(int index) { Visit(); return Forms[index]; }
    internal PoolResolver CreateResolver(IReadOnlyDictionary<int, QpdfObject> pool)
    { Visit(); return PoolResolver.Create(Memory, pool); }
    internal bool VisitNumber(int number) { Visit(); return (_visited ?? throw new ObjectDisposedException(nameof(FormResourceWalk))).Add(Memory, number); }

    private void Grow<T>(List<T> items)
    {
        if (items.Count != items.Capacity) { return; }
        Governor.ChargeWork(checked(items.Count + Math.Min(Array.MaxLength, Math.Max(4L, items.Capacity * 2L))));
        _token.ThrowIfCancellationRequested(); Governor.CheckCancellation();
    }
    internal void Add(int? id, PdfStream stream)
    { Visit(); List<Form> forms = Forms; Grow(forms); ParserContainers.AddItem(Memory, forms, new Form(id, stream), 16L); }

    internal bool AddScope(PdfDictionary scope)
    {
        Visit(); HashSet<PdfDictionary> seen = _seen ?? throw new ObjectDisposedException(nameof(FormResourceWalk));
        if (seen.Contains(scope)) { return false; }
        if (seen.Count == seen.EnsureCapacity(0))
        {
            Governor.ChargeWork(checked(seen.Count + Math.Min(Array.MaxLength, (seen.Count + 1L) * 3L)));
            _token.ThrowIfCancellationRequested(); Governor.CheckCancellation();
        }
        List<PdfDictionary> scopes = Scopes; Grow(scopes);
        ParserContainers.AddItem(Memory, scopes, scope, IntPtr.Size);
        try
        {
            if (ParserContainers.AddSetItem(Memory, seen, scope, IntPtr.Size)) { return true; }
        }
        catch { scopes.RemoveAt(scopes.Count - 1); throw; }
        scopes.RemoveAt(scopes.Count - 1); return false;
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory; PdfObjectComparison? comparison = _comparison;
        _scopes = null; _forms = null; _seen = null; _visited = null; _comparison = null;
        _memory = null; _governor = null; _token = default;
        comparison?.Dispose(); memory?.Dispose();
    }
}
