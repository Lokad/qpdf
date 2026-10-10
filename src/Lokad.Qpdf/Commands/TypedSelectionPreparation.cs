using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Commands;

/// <summary>Scopes typed selection snapshots and conversions through the shared public job.</summary>
internal sealed class TypedSelectionPreparation : IDisposable
{
    private ResourceGovernor? _governor;
    private ParserMemoryScope? _memory;
    private List<PdfPageSource>? _sources;
    private List<OutputRotation>? _rotations;
    private List<PageLabelSpec>? _labels;

    private TypedSelectionPreparation(ResourceGovernor governor, ParserMemoryScope memory)
    { _governor = governor; _memory = memory; }

    internal static TypedSelectionPreparation Create(ResourceGovernor governor)
    {
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try { memory.Reserve(56L); return new(governor, memory); }
        catch { memory.Dispose(); throw; }
    }

    internal IReadOnlyList<PdfPageSource> Sources => _sources ?? throw new InvalidOperationException("Typed sources have not been prepared.");
    internal IReadOnlyList<OutputRotation> Rotations => _rotations ?? (IReadOnlyList<OutputRotation>)Array.Empty<OutputRotation>();
    internal IReadOnlyList<PageLabelSpec> Labels => _labels ?? (IReadOnlyList<PageLabelSpec>)Array.Empty<PageLabelSpec>();
    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(TypedSelectionPreparation));
    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(TypedSelectionPreparation));

    internal void FreezeSources(IReadOnlyList<PdfPageSource> sources)
    {
        _sources = ParserContainers.CreateList<PdfPageSource>(Memory);
        foreach (PdfPageSource source in sources)
        {
            Step();
            if (source is null) { throw new ArgumentException("Page sources must not contain null entries.", nameof(sources)); }
            ParserContainers.AddItem(Memory, _sources, source, IntPtr.Size);
        }
        if (_sources.Count == 0) { throw new ArgumentException("At least one page source is required.", nameof(sources)); }
    }

    internal void ConvertRotations(IReadOnlyList<PdfPageRotation> rotations)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        if (rotations.Count == 0) { return; }
        _rotations = ParserContainers.CreateList<OutputRotation>(Memory);
        foreach (PdfPageRotation rotation in rotations)
        {
            Step();
            OutputRotation item = OutputRotation.FromPages(rotation, Governor, Memory);
            ParserContainers.AddItem(Memory, _rotations, item, IntPtr.Size);
        }
    }

    internal void ConvertLabels(IReadOnlyList<PdfPageLabel> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (labels.Count == 0) { return; }
        _labels = ParserContainers.CreateList<PageLabelSpec>(Memory);
        foreach (PdfPageLabel label in labels)
        {
            Step();
            if (label is null) { throw new ArgumentException("Page labels must not contain null entries.", nameof(labels)); }
            PageLabelSpec item = PageLabelSpec.FromLabel(label, Governor, Memory);
            ParserContainers.AddItem(Memory, _labels, item, IntPtr.Size);
        }
    }

    private void Step() { Governor.CheckCancellation(); Governor.ChargeWork(1L); }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _memory = null; _governor = null; _sources = null; _rotations = null; _labels = null;
        memory?.Dispose();
    }
}
