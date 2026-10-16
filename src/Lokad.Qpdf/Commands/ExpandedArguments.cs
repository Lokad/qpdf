using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Commands;

/// <summary>Owns an expansion vector and fresh text until execution cleanup.</summary>
internal sealed class ExpandedArguments : IDisposable
{
    private readonly ResourceGovernor _governor;
    private ParserMemoryScope? _memory;
    private List<string>? _values;

    private ExpandedArguments(ResourceGovernor governor, ParserMemoryScope memory, List<string> values)
    { _governor = governor; _memory = memory; _values = values; }

    internal static ExpandedArguments Create(ResourceGovernor governor)
    {
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(64L);
            List<string> values = ParserContainers.CreateList<string>(memory);
            return new(governor, memory, values);
        }
        catch { memory.Dispose(); throw; }
    }

    internal IReadOnlyList<string> Values => _values ?? throw new ObjectDisposedException(nameof(ExpandedArguments));
    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(ExpandedArguments));

    internal void AddBorrowed(string value)
    {
        List<string> values = _values ?? throw new ObjectDisposedException(nameof(ExpandedArguments));
        GovernedCollections.AddItem(Memory, _governor, values, value, IntPtr.Size);
    }

    internal string CopySubstring(string source, int start, int length)
        => GovernedText.CopySubstring(_governor, Memory, source, start, length);

    internal void AddResponseLines(GovernedText content)
    {
        content.CheckOwner(_governor);
        string text = content.Value;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if ((i & 255) == 0) { _governor.CheckCancellation(); _governor.ChargeWork(1L); }
            if (text[i] != '\n') { continue; }
            int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            AddBorrowed(CopySubstring(text, start, end - start));
            start = i + 1;
        }
        if (start == text.Length) { return; }
        if (start == 0)
        {
            // Substring(0, Length) borrows this same decoded string. Transfer
            // its existing charge rather than admitting the allocation twice.
            Memory.AdoptReserved(content.StringReservation);
            content.TransferToRetainedStorage(_governor);
            AddBorrowed(text);
        }
        else { AddBorrowed(CopySubstring(text, start, text.Length - start)); }
    }

    public void Dispose()
    {
        List<string>? values = _values;
        ParserMemoryScope? memory = _memory;
        _values = null; _memory = null;
        values?.Clear();
        memory?.Dispose();
    }
}
