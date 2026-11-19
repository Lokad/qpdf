using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Runtime;

// Protect the inputs consumed by a publication, including retired leases.
internal sealed class PublicationInputAliases : IDisposable
{
    private readonly ParserMemoryScope _memory;
    private readonly ResourceGovernor _governor;
    [Flags]
    private enum KeyKind : byte { Path = 1, Identity = 2 }
    private Dictionary<string, KeyKind>? _inputs;
    private (QpdfPath Requested, QpdfPath Opened, string Identity)? _replacement;

    internal static PublicationInputAliases? BeginIfNeeded(ResourceGovernor governor)
    {
        if (governor.PublicationInputs is not null) { return null; }
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(80L);
            PublicationInputAliases aliases = new(memory, governor);
            governor.PublicationInputs = aliases;
            return aliases;
        }
        catch { memory.Dispose(); throw; }
    }

    internal static PublicationInputAliases Active(ResourceGovernor governor)
        => governor.PublicationInputs ?? throw new InvalidOperationException("Missing publication input tracking.");

    private PublicationInputAliases(ParserMemoryScope memory, ResourceGovernor governor)
    {
        _memory = memory;
        _governor = governor;
    }

    internal void Remember(QpdfPath requested, QpdfPath opened, string identity, bool replacementSource)
    {
        if (replacementSource)
        {
            if (_replacement is not null) { throw new InvalidOperationException("Multiple replacement sources."); }
            _memory.Reserve(GovernedText.StringBytes(opened.Value.Length));
            _memory.Reserve(GovernedText.StringBytes(identity.Length));
            _replacement = (requested, opened, identity);
            return;
        }
        RememberPath(requested.Value);
        RememberPath(opened.Value);
        Remember(identity, KeyKind.Identity, true);
    }

    internal void RememberPath(string path) => Remember(path, KeyKind.Path, true);

    // The parsed job or typed caller retains this exact string through validation.
    internal void BorrowPath(string path) => Remember(path, KeyKind.Path, false);

    internal bool ContainsPath(string path) => Contains(path, KeyKind.Path);

    private bool Contains(string key, KeyKind kind)
        => _inputs is not null && _inputs.TryGetValue(key, out KeyKind known) && (known & kind) != 0;

    private void Remember(string key, KeyKind kind, bool admitText)
    {
        _inputs ??= ParserContainers.CreateDictionary<string, KeyKind>(_memory);
        if (_inputs.TryGetValue(key, out KeyKind known))
        {
            _inputs[key] = known | kind;
            return;
        }
        // A host may use its logical path text as storage identity.
        // Keep one admitted string and remember each way it can match.
        ParserContainers.EnsureEntryCapacity(_memory, _inputs, sizeof(byte));
        if (admitText) { _memory.Reserve(GovernedText.StringBytes(key.Length)); }
        _inputs.Add(key, kind);
    }

    internal ValueTask ValidateTargetAsync(IQpdfHost host, QpdfPath target, QpdfPath? capture,
        string outputKind, CancellationToken token)
        => ValidateTargetsCoreAsync(host, target, Array.Empty<QpdfPath>(), static path => path, capture, outputKind, token);

    internal ValueTask ValidateTargetsAsync<T>(IQpdfHost host, IReadOnlyList<T> targets, Func<T, QpdfPath> path,
        QpdfPath? capture, string outputKind, CancellationToken token)
        => ValidateTargetsCoreAsync(host, null, targets, path, capture, outputKind, token);

    private async ValueTask ValidateTargetsCoreAsync<T>(IQpdfHost host, QpdfPath? target, IReadOnlyList<T> targets,
        Func<T, QpdfPath> pathOf, QpdfPath? capture, string outputKind, CancellationToken token)
    {
        // Target probes must stay out of the set of graph inputs.
        _governor.PublicationInputs = null;
        try
        {
            if (target is null && targets.Count == 0) { return; }
            if (target is QpdfPath main) { CheckPath(main.Value, false); }
            foreach (T entry in targets) { CheckPath(pathOf(entry).Value, false); }
            if (capture is QpdfPath firstPass) { CheckPath(firstPass.Value, true); }

            using ParserMemoryScope targetMemory = ParserMemoryScope.Create(_governor);
            HashSet<string>? claimed = capture.HasValue || targets.Count > 1
                ? ParserContainers.CreateSet<string>(targetMemory) : null;
            if (target is QpdfPath pdf) { await Probe(pdf, false).ConfigureAwait(false); }
            foreach (T entry in targets) { await Probe(pathOf(entry), false).ConfigureAwait(false); }
            if (capture is QpdfPath pass) { await Probe(pass, true).ConfigureAwait(false); }

            async ValueTask Probe(QpdfPath path, bool isCapture)
            {
                token.ThrowIfCancellationRequested(); _governor.CheckCancellation();
                if (!isCapture && _replacement is { } primary && path == primary.Requested)
                {
                    if (ContainsPath(primary.Opened.Value) || Contains(primary.Identity, KeyKind.Identity))
                    { throw Alias(path.Value); }
                    return;
                }
                IQpdfInput input;
                try { input = await GovernedHost.OpenInputAsync(host, path, _governor, token).ConfigureAwait(false); }
                catch (QpdfHostException ex) when (ex.Reason is QpdfHostFailureReason.NotFound or QpdfHostFailureReason.Unsupported)
                { return; }
                await using (input.ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested(); _governor.CheckCancellation();
                    string identity = input.Identity;
                    if (ContainsPath(input.Path.Value) || Contains(identity, KeyKind.Identity)
                        || _replacement is { } source && (identity == source.Identity || input.Path == source.Opened))
                    { throw Alias(path.Value); }
                    if (claimed is null) { return; }
                    if (!ParserContainers.AddSetItem(targetMemory, claimed, identity, IntPtr.Size))
                    { throw new QpdfParseException($"{outputKind} outputs collide on '{path.Value}'.", null, null); }
                    targetMemory.Reserve(GovernedText.StringBytes(identity.Length));
                }
            }
        }
        finally { Dispose(); }

        void CheckPath(string name, bool isCapture)
        {
            token.ThrowIfCancellationRequested(); _governor.CheckCancellation();
            if (ContainsPath(name) || _replacement is { } source
                && (isCapture || name != source.Requested.Value) && (name == source.Requested.Value || name == source.Opened.Value))
            { throw Alias(name); }
            if (!isCapture && capture?.Value == name)
            {
                throw new QpdfParseException("The first-pass output must differ from every main PDF output.", null, null);
            }
        }

        QpdfParseException Alias(string name) => new($"{outputKind} output '{name}' aliases an input.", null, null);
    }

    public void Dispose()
    {
        if (ReferenceEquals(_governor.PublicationInputs, this)) { _governor.PublicationInputs = null; }
        _inputs = null;
        _replacement = null;
        _memory.Dispose();
    }
}
