using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Commands;

/// <summary>Scopes argv filtering scratch through plan construction.</summary>
internal sealed class ArgumentFiltering : IDisposable
{
    private ResourceGovernor? _governor;
    private ParserMemoryScope? _memory;
    private List<int>? _markers;
    private List<string>? _stripped;
    private List<string>? _kept;

    private ArgumentFiltering(ResourceGovernor governor, ParserMemoryScope memory)
    { _governor = governor; _memory = memory; }

    internal static ArgumentFiltering Create(ResourceGovernor governor)
    {
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try { memory.Reserve(56L); return new(governor, memory); }
        catch { memory.Dispose(); throw; }
    }

    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(ArgumentFiltering));
    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(ArgumentFiltering));
    internal void Visit() { Governor.CheckCancellation(); Governor.ChargeWork(1L); }
    internal List<int> CreateMarkers() => _markers = ParserContainers.CreateList<int>(Memory);
    internal List<string> CreateStripped() => _stripped = ParserContainers.CreateList<string>(Memory);
    internal List<string> CreateKept() => _kept = ParserContainers.CreateList<string>(Memory);
    internal void Add(List<int> values, int value)
        => GovernedCollections.AddItem(Memory, Governor, values, value, sizeof(int));
    internal void Add(List<string> values, string value)
        => GovernedCollections.AddItem(Memory, Governor, values, value, IntPtr.Size);

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _memory = null; _governor = null; _markers = null; _stripped = null; _kept = null;
        memory?.Dispose();
    }
}
