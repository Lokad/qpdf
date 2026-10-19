using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class ParserDictionaryGrowthTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusedGrowthPreservesExistingEntriesWithoutTryingASmallerAllocation(bool set)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = set ? 900L : 1200L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            Dictionary<int, int>? dictionary = set ? null : ParserContainers.CreateDictionary<int, int>(memory);
            HashSet<int>? items = set ? ParserContainers.CreateSet<int>(memory) : null;
            for (int index = 0; index < 7; index++)
            {
                if (items is not null) { ParserContainers.AddSetItem(memory, items, index, sizeof(int)); }
                if (dictionary is not null)
                {
                    ParserContainers.EnsureEntryCapacity(memory, dictionary, sizeof(int));
                    dictionary.Add(index, index * 2);
                }
            }
            long before = governor.Snapshot().LiveMemoryBytes;
            QpdfLimitException error = Assert.Throws<QpdfLimitException>(() =>
            {
                if (items is not null) { ParserContainers.AddSetItem(memory, items, 7, sizeof(int)); }
                if (dictionary is not null) { ParserContainers.EnsureEntryCapacity(memory, dictionary, sizeof(int)); }
            });
            Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), error.LimitName);
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
            if (items is not null) { Assert.Equal(Enumerable.Range(0, 7), items.Order()); }
            if (dictionary is not null)
            {
                Assert.Equal(7, dictionary.Count);
                foreach ((int key, int value) in dictionary) { Assert.Equal(key * 2, value); }
            }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("normal", false)]
    [InlineData("memory", false)]
    [InlineData("normal", true)]
    [InlineData("memory", true)]
    public void KnownFirstSectionAndLaterGrowthPreserveSignedKeysUnderExistingCeilings(string budget, bool bounded)
    {
        QpdfLimits limits = budget switch
        {
            "memory" => QpdfLimits.Default with { MaxMemoryBytes = 4000L },
            _ => QpdfLimits.Default,
        };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            Dictionary<int, int> entries = ParserContainers.CreateDictionary<int, int>(memory);
            ParserContainers.HintEntryCapacity(memory, entries, 16, sizeof(int), governor);
            int[] keys = [int.MinValue, int.MaxValue, .. Enumerable.Range(-11, 22)];
            for (int index = 0; index < keys.Length; index++)
            {
                // A cardinality estimate remains a hint if later input adds
                // more identities. Both paths preserve accepted entries.
                if (bounded) { ParserContainers.EnsureEntryCapacity(memory, entries, sizeof(int), 16); }
                else { ParserContainers.EnsureEntryCapacity(memory, entries, sizeof(int)); }
                entries.Add(keys[index], index);
            }
            // A later overlapping section must not discard the populated map.
            ParserContainers.HintEntryCapacity(memory, entries, 128, sizeof(int), governor);
            entries[int.MinValue] = 99;
            Assert.Equal(keys.Length, entries.Count);
            Assert.Equal(99, entries[int.MinValue]);
            for (int index = 1; index < keys.Length; index++) { Assert.Equal(index, entries[keys[index]]); }
            Assert.InRange(governor.Snapshot().PeakMemoryBytes, 1L, limits.MaxMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
