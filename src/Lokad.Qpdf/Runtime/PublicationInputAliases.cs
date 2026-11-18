using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Runtime;

// Protect the sources that supplied the graph, including leases already retired.
internal sealed class PublicationInputAliases : IDisposable
{
    private readonly ParserMemoryScope _memory;
    private readonly ResourceGovernor _governor;
    private readonly string _outputKind;
    private readonly HashSet<string> _paths;
    private readonly HashSet<string> _identities;

    internal static PublicationInputAliases Begin(ParserMemoryScope memory, ResourceGovernor governor, string outputKind)
    {
        memory.CheckOwner(governor);
        if (governor.PublicationInputs is not null)
        { throw new InvalidOperationException("Publication input tracking is already active."); }
        memory.Reserve(56L);
        PublicationInputAliases aliases = new(memory, governor, outputKind);
        governor.PublicationInputs = aliases;
        return aliases;
    }

    private PublicationInputAliases(ParserMemoryScope memory, ResourceGovernor governor, string outputKind)
    {
        _memory = memory;
        _governor = governor;
        _outputKind = outputKind;
        _paths = ParserContainers.CreateSet<string>(memory);
        _identities = ParserContainers.CreateSet<string>(memory);
    }

    internal void Remember(QpdfPath requested, QpdfPath opened, string identity)
    {
        RememberPath(requested.Value);
        RememberPath(opened.Value);
        Remember(_identities, identity);
    }

    internal void RememberPath(string path) => Remember(_paths, path);

    internal bool ContainsPath(string path) => _paths.Contains(path);

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
            if (ContainsPath(name)) { throw Alias(name); }
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
                if (_identities.Contains(identity) || ContainsPath(input.Path.Value)) { throw Alias(name); }
                if (!ParserContainers.AddSetItem(targetMemory, claimed, identity, IntPtr.Size))
                { throw new QpdfParseException($"{_outputKind} outputs collide on '{name}'.", null, null); }
                targetMemory.Reserve(GovernedText.StringBytes(identity.Length));
            }
        }
    }

    private QpdfParseException Alias(string name)
        => new($"{_outputKind} output '{name}' aliases an input.", null, null);

    public void Dispose()
    {
        if (ReferenceEquals(_governor.PublicationInputs, this)) { _governor.PublicationInputs = null; }
    }
}
