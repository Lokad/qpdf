using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Commands;

/// <summary>Owns parsed collation vectors and their policy through publication.</summary>
internal sealed class CollationPreparation : IDisposable
{
    private ResourceGovernor? _governor;
    private ParserMemoryScope? _memory;
    private List<List<int>>? _vectors;

    private CollationPreparation(ResourceGovernor governor, ParserMemoryScope memory)
    { _governor = governor; _memory = memory; }

    internal static CollationPreparation Create(ResourceGovernor governor)
    {
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try { memory.Reserve(40L); return new(governor, memory); }
        catch { memory.Dispose(); throw; }
    }

    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(CollationPreparation));
    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(CollationPreparation));
    internal bool HasStorage => _vectors is not null;

    internal List<int> CreateVector()
    {
        _vectors ??= ParserContainers.CreateList<List<int>>(Memory);
        List<int> vector = ParserContainers.CreateList<int>(Memory);
        GovernedCollections.AddItem(Memory, Governor, _vectors, vector, IntPtr.Size);
        return vector;
    }
    internal void Add(List<int> target, int value)
        => GovernedCollections.AddItem(Memory, Governor, target, value, sizeof(int));
    internal void Append(List<int> target, IReadOnlyList<int> source)
    { foreach (int value in source) { Add(target, value); } }
    internal void Visit() { Governor.CheckCancellation(); Governor.ChargeWork(1L); }

    internal PdfPageCollation CreatePolicy(List<int> sizes)
    {
        // The public constructor creates its own wrapper and exact integer snapshot.
        Governor.CheckCancellation(); Governor.ChargeWork(checked(2L * sizes.Count + 1L));
        Memory.Reserve(checked(24L + ((24L + sizeof(int) * (long)sizes.Count + 7L) & ~7L)));
        return new PdfPageCollation(sizes);
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _memory = null; _governor = null; _vectors = null;
        memory?.Dispose();
    }
}
