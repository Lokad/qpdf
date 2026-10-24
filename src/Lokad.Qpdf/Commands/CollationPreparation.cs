using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Commands;

/// <summary>Owns admission for parsed collation vectors and their policy through publication.</summary>
internal sealed class CollationPreparation : IDisposable
{
    private const long OwnerBytes = 32L;
    private ResourceGovernor? _governor;
    private ParserMemoryScope? _memory;

    private CollationPreparation(ResourceGovernor governor, ParserMemoryScope memory)
    { _governor = governor; _memory = memory; }

    internal static CollationPreparation Create(ResourceGovernor governor)
    {
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try { memory.Reserve(OwnerBytes); return new(governor, memory); }
        catch { memory.Dispose(); throw; }
    }

    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(CollationPreparation));
    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(CollationPreparation));
    internal bool HasStorage => Memory.ReservedBytes > OwnerBytes;
    internal List<int> CreateVector() => ParserContainers.CreateList<int>(Memory);
    internal void Add(List<int> target, int value)
        => GovernedCollections.AddItem(Memory, Governor, target, value, sizeof(int));
    internal void Append(List<int> target, IReadOnlyList<int> source)
    { foreach (int value in source) { Add(target, value); } }
    internal void Visit() { Governor.CheckCancellation(); }

    internal PdfPageCollation CreatePolicy(List<int> sizes)
    {
        // The public constructor creates the policy, read-only view and exact snapshot.
        Governor.CheckCancellation();
        Memory.Reserve(checked(48L + ((24L + sizeof(int) * (long)sizes.Count + 7L) & ~7L)));
        return new PdfPageCollation(sizes);
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _memory = null; _governor = null;
        memory?.Dispose();
    }
}
