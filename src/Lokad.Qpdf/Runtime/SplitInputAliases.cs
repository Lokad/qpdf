using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Runtime;

// Expanded split names must not replace any of the inputs that supplied the
// bound graph, including sidecars whose leases have already been released.
internal sealed class SplitInputAliases
{
    private readonly ParserMemoryScope _memory;
    private readonly HashSet<string> _paths;
    private readonly HashSet<string> _identities;

    internal SplitInputAliases(ParserMemoryScope memory)
    {
        _memory = memory;
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
        using ParserMemoryScope targetMemory = ParserMemoryScope.Create(governor);
        HashSet<string> claimed = ParserContainers.CreateSet<string>(targetMemory);
        foreach (string name in names)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (_paths.Contains(name)) { throw Alias(name); }
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
}
