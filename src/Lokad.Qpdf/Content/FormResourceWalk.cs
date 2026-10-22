using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Content;

/// <summary>Owns form-resource traversal metadata while borrowing PDF values.</summary>
internal sealed class FormResourceWalk : IDisposable
{
    internal readonly record struct Form(int? Id, PdfStream Stream);
    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private HashSet<PdfDictionary>? _seen;
    private GovernedIntSet? _visited;
    private List<PdfDictionary>? _scopes;
    private List<Form>? _forms;
    private CancellationToken _token;

    private FormResourceWalk(ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        _memory = memory; _governor = governor; _token = token;
        _visited = GovernedIntSet.Create(memory, governor, token);
        _scopes = ParserContainers.CreateList<PdfDictionary>(memory);
        _forms = ParserContainers.CreateList<Form>(memory);
        memory.Reserve(96L);
        // Scopes are graph nodes, not values to merge. Indirect forms have their
        // own object-number guard; identity also stops shared direct resources.
        _seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
    }

    internal static FormResourceWalk Create(ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(80L);
            return new(memory, governor, token);
        }
        catch { memory.Dispose(); throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(FormResourceWalk));
    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(FormResourceWalk));
    private List<PdfDictionary> Scopes => _scopes ?? throw new ObjectDisposedException(nameof(FormResourceWalk));
    private List<Form> Forms => _forms ?? throw new ObjectDisposedException(nameof(FormResourceWalk));
    private void Visit() { _token.ThrowIfCancellationRequested(); Governor.CheckCancellation(); }
    internal int ScopeCount => Scopes.Count;
    internal int Count => Forms.Count;
    internal PdfDictionary GetScope(int index) { Visit(); return Scopes[index]; }
    internal Form Get(int index) { Visit(); return Forms[index]; }
    internal PoolResolver CreateResolver(IReadOnlyDictionary<int, QpdfObject> pool)
    { Visit(); return PoolResolver.Create(Memory, pool); }
    internal bool VisitNumber(int number) { Visit(); return (_visited ?? throw new ObjectDisposedException(nameof(FormResourceWalk))).Add(Memory, number); }

    internal void Add(int? id, PdfStream stream)
    { Visit(); ParserContainers.AddItem(Memory, Forms, new Form(id, stream), 16L); }

    internal bool AddScope(PdfDictionary scope)
    {
        Visit(); HashSet<PdfDictionary> seen = _seen ?? throw new ObjectDisposedException(nameof(FormResourceWalk));
        if (seen.Contains(scope)) { return false; }
        ParserContainers.EnsureSetCapacity(Memory, seen, IntPtr.Size);
        List<PdfDictionary> scopes = Scopes;
        ParserContainers.AddItem(Memory, scopes, scope, IntPtr.Size);
        try
        {
            if (seen.Add(scope)) { return true; }
        }
        catch { scopes.RemoveAt(scopes.Count - 1); throw; }
        scopes.RemoveAt(scopes.Count - 1); return false;
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _scopes = null; _forms = null; _seen = null; _visited = null;
        _memory = null; _governor = null; _token = default;
        memory?.Dispose();
    }
}
