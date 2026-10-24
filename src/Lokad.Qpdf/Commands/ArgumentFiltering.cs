using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Commands;

/// <summary>Scopes argv filtering scratch through plan construction.</summary>
internal sealed class ArgumentFiltering : IDisposable
{
    private ResourceGovernor? _governor;
    private ParserMemoryScope? _memory;

    private ArgumentFiltering(ResourceGovernor governor, ParserMemoryScope memory)
    { _governor = governor; _memory = memory; }

    internal static ArgumentFiltering Create(ResourceGovernor governor)
    {
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try { memory.Reserve(32L); return new(governor, memory); }
        catch { memory.Dispose(); throw; }
    }

    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(ArgumentFiltering));
    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(ArgumentFiltering));
    internal void Visit() { Governor.CheckCancellation(); }
    internal List<int> CreateMarkers() => ParserContainers.CreateList<int>(Memory);
    internal List<string> CreateStripped() => ParserContainers.CreateList<string>(Memory);
    internal List<string> CreateKept() => ParserContainers.CreateList<string>(Memory);
    internal void Add(List<int> values, int value)
        => GovernedCollections.AddItem(Memory, Governor, values, value, sizeof(int));
    internal void Add(List<string> values, string value)
        => GovernedCollections.AddItem(Memory, Governor, values, value, IntPtr.Size);

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _memory = null; _governor = null;
        memory?.Dispose();
    }
}
