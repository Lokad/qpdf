using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns one pass's offsets, lengths and compressed-member index.</summary>
internal sealed class LinearizedLayout : IDisposable
{
    private ParserMemoryScope? _memory;
    private GovernedIntDictionary<long>? _starts;
    private GovernedIntDictionary<long>? _lengths;
    private GovernedIntDictionary<(int Container, int Index)>? _members;

    internal long FirstXref { get; set; }
    internal long HintPosition { get; set; }
    internal long Part6End { get; set; }
    internal long MainXref { get; set; }
    internal long MainXrefTable { get; set; }
    internal long FileSize { get; set; }

    private LinearizedLayout(ParserMemoryScope memory, GovernedIntDictionary<long> starts,
        GovernedIntDictionary<long> lengths, GovernedIntDictionary<(int Container, int Index)>? members)
    {
        _memory = memory;
        _starts = starts;
        _lengths = lengths;
        _members = members;
    }

    internal static LinearizedLayout Create(ResourceGovernor governor, bool streamed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(governor);
        cancellationToken.ThrowIfCancellationRequested();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(128L);
            GovernedIntDictionary<long> starts = GovernedIntDictionary<long>.Create(memory, governor, sizeof(long), cancellationToken);
            GovernedIntDictionary<long> lengths = GovernedIntDictionary<long>.Create(memory, governor, sizeof(long), cancellationToken);
            GovernedIntDictionary<(int Container, int Index)>? members = streamed
                ? GovernedIntDictionary<(int Container, int Index)>.Create(memory, governor, 2L * sizeof(int), cancellationToken)
                : null;
            return new LinearizedLayout(memory, starts, lengths, members);
        }
        catch { memory.Dispose(); throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(LinearizedLayout));

    internal GovernedIntDictionary<long> Starts => _starts ?? throw new ObjectDisposedException(nameof(LinearizedLayout));

    internal GovernedIntDictionary<long> Lengths => _lengths ?? throw new ObjectDisposedException(nameof(LinearizedLayout));

    internal void RecordStart(int number, long position) => Starts.Assign(Memory, number, position);

    internal void RecordLength(int number, long length) => Lengths.Assign(Memory, number, length);

    internal void RecordMember(int number, (int Container, int Index) member)
    {
        ParserMemoryScope memory = Memory;
        GovernedIntDictionary<(int Container, int Index)> members = _members
            ?? throw new InvalidOperationException("Classic layouts have no compressed members.");
        members.Assign(memory, number, member);
    }

    internal bool TryGetMember(int number, out (int Container, int Index) member)
    {
        _ = Memory;
        if (_members is not null) { return _members.TryGetValue(number, out member); }
        member = default;
        return false;
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _starts = null;
        _lengths = null;
        _members = null;
        _memory = null;
        memory?.Dispose();
    }
}
