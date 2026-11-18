using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Runtime;

// Protect the sources that supplied the graph, including leases already retired.
internal sealed class SplitInputAliases : IDisposable
{
    private readonly ParserMemoryScope _memory;
    private readonly ResourceGovernor _governor;
    private readonly HashSet<string> _paths;
    private readonly HashSet<string> _identities;

    internal static SplitInputAliases Begin(ParserMemoryScope memory, ResourceGovernor governor)
    {
        memory.CheckOwner(governor);
        if (governor.SplitInputs is not null)
        { throw new InvalidOperationException("Split input tracking is already active."); }
        memory.Reserve(48L);
        SplitInputAliases aliases = new(memory, governor);
        governor.SplitInputs = aliases;
        return aliases;
    }

    private SplitInputAliases(ParserMemoryScope memory, ResourceGovernor governor)
    {
        _memory = memory;
        _governor = governor;
        _paths = ParserContainers.CreateSet<string>(memory);
        _identities = ParserContainers.CreateSet<string>(memory);
    }

    internal void Remember(QpdfPath requested, QpdfPath opened, string identity)
    {
        Remember(_paths, requested.Value);
        Remember(_paths, opened.Value);
        Remember(_identities, identity);
    }

    private void Remember(HashSet<string> entries, string value)
    {
        if (ParserContainers.AddSetItem(_memory, entries, value, IntPtr.Size))
        { _memory.Reserve(GovernedText.StringBytes(value.Length)); }
    }

    internal async ValueTask ValidateTargetsAsync(IQpdfHost host, IReadOnlyList<string> names,
        ResourceGovernor governor, CancellationToken token)
    {
        _memory.CheckOwner(governor);
        // Target probes must stay out of the set of graph inputs.
        Dispose();
        if (names.Count == 0) { return; }
        foreach (string name in names)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (_paths.Contains(name)) { throw Alias(name); }
        }
        using ParserMemoryScope targetMemory = ParserMemoryScope.Create(governor);
        HashSet<string> claimed = ParserContainers.CreateSet<string>(targetMemory);
        foreach (string name in names)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            IQpdfInput input;
            try { input = await GovernedHost.OpenInputAsync(host, QpdfPath.From(name), governor, token).ConfigureAwait(false); }
            catch (QpdfHostException ex) when (ex.Reason is QpdfHostFailureReason.NotFound or QpdfHostFailureReason.Unsupported)
            { continue; }
            await using (input.ConfigureAwait(false))
            {
                string identity = input.Identity;
                if (_identities.Contains(identity) || _paths.Contains(input.Path.Value)) { throw Alias(name); }
                if (!ParserContainers.AddSetItem(targetMemory, claimed, identity, IntPtr.Size))
                { throw new QpdfParseException($"Split outputs collide on '{name}'.", null, null); }
                targetMemory.Reserve(GovernedText.StringBytes(identity.Length));
            }
        }
    }

    private static QpdfParseException Alias(string name)
        => new($"Split output '{name}' aliases an input.", null, null);

    public void Dispose()
    {
        if (ReferenceEquals(_governor.SplitInputs, this)) { _governor.SplitInputs = null; }
    }
}
