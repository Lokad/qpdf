using System.Collections;

namespace Lokad.Qpdf.Runtime;

/// <summary>Set view over the shared governed integer index.</summary>
internal sealed class GovernedIntSet : IReadOnlyCollection<int>
{
    private readonly GovernedIntDictionary<byte> _index;
    private GovernedIntSet(GovernedIntDictionary<byte> index) { _index = index; }

    internal static GovernedIntSet Create(ParserMemoryScope memory, ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        memory.CheckOwner(governor);
        cancellationToken.ThrowIfCancellationRequested();
        governor.CheckCancellation();
        memory.Reserve(32L);
        GovernedIntDictionary<byte>? index = null;
        try
        {
            index = GovernedIntDictionary<byte>.Create(memory, governor, sizeof(byte), cancellationToken);
            return new GovernedIntSet(index);
        }
        catch
        {
            if (index is not null) { memory.Release(GovernedIntDictionary<byte>.WrapperBytes); }
            memory.Release(32L);
            throw;
        }
    }

    public int Count => _index.Count;
    internal void CheckOwner(ResourceGovernor governor) => _index.CheckOwner(governor);
    internal bool Add(ParserMemoryScope memory, int number) => _index.TryAdd(memory, number, 0);
    internal bool Contains(int number) => _index.ContainsKey(number);
    internal bool Remove(int number) => _index.Remove(number);
    public Enumerator GetEnumerator() => new(_index.GetEnumerator());
    IEnumerator<int> IEnumerable<int>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<int>
    {
        private GovernedIntDictionary<byte>.Enumerator _entries;
        internal Enumerator(GovernedIntDictionary<byte>.Enumerator entries) { _entries = entries; }
        public int Current => _entries.Current.Key;
        object IEnumerator.Current => Current;
        public bool MoveNext() => _entries.MoveNext();
        public void Dispose() => _entries.Dispose();
        public void Reset() => throw new NotSupportedException();
    }
}
