using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

internal static partial class PageInheritance
{
    [InlineArray(4)]
    internal struct Depths
    {
        private int _first;
    }

    internal sealed class Observation(PdfDictionary dictionary)
    {
        internal PdfDictionary Dictionary { get; } = dictionary;
        private InheritedValues _values;
        private Depths _distance;
        private int _known;

        internal bool TryReplay(int mask, int depth, long limit, ref InheritedValues values, ref Depths foundAt)
        {
            if ((_known & mask) != mask) { return false; }
            for (int index = 0; index < 4; index++)
            {
                if ((mask & (1 << index)) != 0 && (long)depth + _distance[index] > limit) { return false; }
            }
            for (int index = 0; index < 4; index++)
            {
                if ((mask & (1 << index)) == 0) { continue; }
                values[index] = _values[index];
                foundAt[index] = depth + _distance[index];
            }
            return true;
        }

        internal void Complete(int mask, int depth, InheritedValues values, Depths foundAt)
        {
            for (int index = 0; index < 4; index++)
            {
                if ((mask & (1 << index)) == 0) { continue; }
                _values[index] = values[index];
                _distance[index] = foundAt[index] - depth;
            }
            _known |= mask;
        }
    }

    /// <summary>Shares successful ancestry observations across one source and set of inherited keys.</summary>
    internal sealed class Batch : IDisposable
    {
        private readonly record struct Visit(Observation Parent, int Depth, int Mask);

        private readonly IIndirectResolver _resolver;
        private readonly PdfName[] _names;
        private readonly QpdfLimits _limits;
        private readonly ResourceGovernor _governor;
        private readonly CancellationToken _token;
        private readonly ParserMemoryScope _owner;
        private ParserMemoryScope? _scratch;
        private Dictionary<(int Number, int Generation, bool IsFromSource), Observation>? _parents;
        private List<Visit>? _visits;
        private bool _disabled, _disposed;

        private Batch(IIndirectResolver resolver, PdfName[] names, QpdfLimits limits, ResourceGovernor governor,
            CancellationToken token, ParserMemoryScope owner)
        { _resolver = resolver; _names = names; _limits = limits; _governor = governor; _token = token; _owner = owner; }

        internal static Batch? TryCreate(bool share, IIndirectResolver resolver, PdfName[] names, QpdfLimits limits,
            ResourceGovernor governor, CancellationToken token)
        {
            if (!share) { return null; }
            token.ThrowIfCancellationRequested();
            ParserMemoryScope? owner = null;
            try
            {
                owner = ParserMemoryScope.Create(governor);
                owner.Reserve(96L);
                return new Batch(resolver, names, limits, governor, token, owner);
            }
            catch (QpdfLimitException error) when (error.LimitName == nameof(QpdfLimits.MaxMemoryBytes))
            { owner?.Dispose(); return null; }
            catch { owner?.Dispose(); throw; }
        }

        internal InheritedValues Find(PageLocation location)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return FindAncestors(_resolver, location, _names, _limits, _governor, _token, this);
        }

        internal bool Knows(PdfReference? reference)
            => reference is not null && Knows(reference.ObjectNumber, reference.Generation);

        internal bool Knows(int number, int generation)
            => _parents is not null && (_parents.ContainsKey((number, generation, false)) || _parents.ContainsKey((number, generation, true)));

        internal PdfDictionary? ResolveParent(PdfReference reference, out Observation? parent, out bool knownPair)
        {
            var key = (reference.ObjectNumber, reference.Generation, reference.IsFromSource);
            if (_parents?.TryGetValue(key, out parent) == true && parent is not null)
            { knownPair = true; return parent.Dictionary; }
            parent = null;
            knownPair = _parents?.ContainsKey((key.ObjectNumber, key.Generation, !key.IsFromSource)) == true;
            if (!_resolver.TryResolve(reference, out QpdfObject? value) || value is not PdfDictionary dictionary) { return null; }
            if (_disabled) { return dictionary; }
            try
            {
                ParserMemoryScope scope = _scratch ??= ParserMemoryScope.Create(_governor);
                _parents ??= ParserContainers.CreateDictionary<(int, int, bool), Observation>(scope);
                ParserContainers.EnsureEntryCapacity(scope, _parents, 2L * IntPtr.Size);
                scope.Reserve(88L);
                parent = new Observation(dictionary);
                _parents.Add(key, parent);
            }
            catch (QpdfLimitException error) when (error.LimitName == nameof(QpdfLimits.MaxMemoryBytes))
            { Disable(); parent = null; }
            return dictionary;
        }

        internal void Observe(Observation parent, int depth, int mask)
        {
            if (_scratch is not ParserMemoryScope scope) { return; }
            try
            {
                _visits ??= ParserContainers.CreateList<Visit>(scope);
                ParserContainers.AddItem(scope, _visits, new Visit(parent, depth, mask), 16L);
            }
            catch (QpdfLimitException error) when (error.LimitName == nameof(QpdfLimits.MaxMemoryBytes))
            { Disable(); }
        }

        internal void Complete(InheritedValues values, Depths foundAt, int missing, int depth)
        {
            if (_visits is null) { return; }
            // For a missing key, the proof extends to the end of the walk.
            for (int index = 0; index < _names.Length; index++)
            {
                if ((missing & (1 << index)) != 0) { foundAt[index] = depth; }
            }
            foreach (Visit visit in _visits)
            {
                _token.ThrowIfCancellationRequested(); _governor.CheckCancellation();
                visit.Parent.Complete(visit.Mask, visit.Depth, values, foundAt);
            }
        }

        internal void EndLookup() => _visits?.Clear();

        internal void Clear()
        {
            _parents = null; _visits = null;
            _scratch?.Dispose(); _scratch = null;
        }

        private void Disable() { Clear(); _disabled = true; }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            Clear();
            _owner.Dispose();
        }
    }
}
