using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class ParserDictionaryGrowthTests
{
    [Theory]
    [InlineData("normal", false)]
    [InlineData("memory", false)]
    [InlineData("work", false)]
    [InlineData("normal", true)]
    [InlineData("memory", true)]
    [InlineData("work", true)]
    public void KnownFirstSectionAndLaterGrowthPreserveSignedKeysUnderExistingCeilings(string budget, bool bounded)
    {
        QpdfLimits limits = budget switch
        {
            "memory" => QpdfLimits.Default with { MaxMemoryBytes = 4000L },
            "work" => QpdfLimits.Default with { MaxWork = 128L },
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
                governor.ChargeWork(1L);
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
