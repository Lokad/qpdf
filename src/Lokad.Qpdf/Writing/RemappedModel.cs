using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns rebuilt metadata while borrowing immutable source payloads.</summary>
internal sealed class RemappedModel : IDisposable
{
    private ParserMemoryScope? _memory;
    private GovernedIntDictionary<QpdfObject>? _objects;

    private RemappedModel(ParserMemoryScope memory, GovernedIntDictionary<QpdfObject> objects)
    {
        _memory = memory;
        _objects = objects;
    }

    internal static RemappedModel Create(GovernedIntDictionary<QpdfObject> source,
        GovernedIntDictionary<int> renumber, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(renumber);
        ArgumentNullException.ThrowIfNull(governor);
        source.CheckOwner(governor);
        renumber.CheckOwner(governor);
        cancellationToken.ThrowIfCancellationRequested();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(64L);
            GovernedIntDictionary<QpdfObject> objects = GovernedIntDictionary<QpdfObject>.Create(
                memory, governor, IntPtr.Size, cancellationToken);
            foreach (KeyValuePair<int, QpdfObject> entry in source)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!renumber.TryGetValue(entry.Key, out int number))
                {
                    throw new QpdfException($"Linearized publication lost object {entry.Key}.");
                }
                QpdfObject mapped = ObjectRemap.RemapIds(entry.Value, renumber, memory, governor, cancellationToken);
                if (!objects.TryAdd(memory, number, mapped))
                {
                    throw new QpdfException("Linearized publication mapped multiple objects to one number.");
                }
            }
            return new RemappedModel(memory, objects);
        }
        catch { memory.Dispose(); throw; }
    }

    internal int Count => _objects?.Count ?? 0;

    internal bool TryGetValue(int number, [MaybeNullWhen(false)] out QpdfObject value)
    {
        ObjectDisposedException.ThrowIf(_objects is null, this);
        return _objects.TryGetValue(number, out value);
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _objects = null;
        _memory = null;
        memory?.Dispose();
    }
}
