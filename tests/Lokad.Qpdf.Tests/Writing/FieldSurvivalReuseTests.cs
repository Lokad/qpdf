using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class FieldSurvivalReuseTests
{
    private sealed class ObservedPool(Dictionary<int, QpdfObject> objects) : IReadOnlyDictionary<int, QpdfObject>
    {
        internal int Reads { get; private set; }
        internal Action? OnResolve { get; set; }
        public int Count => objects.Count;
        public IEnumerable<int> Keys => objects.Keys;
        public IEnumerable<QpdfObject> Values => objects.Values;
        public QpdfObject this[int key] => objects[key];
        public bool ContainsKey(int key) => objects.ContainsKey(key);
        public bool TryGetValue(int key, [MaybeNullWhen(false)] out QpdfObject value)
        {
            Reads++;
            OnResolve?.Invoke();
            return objects.TryGetValue(key, out value);
        }
        public IEnumerator<KeyValuePair<int, QpdfObject>> GetEnumerator() => objects.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static PdfReference Link(int number) => new(number, 0);
    private static PdfDictionary Branch(params int[] numbers)
        => new(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("Kids")] = new PdfArray(numbers.Select(Link).ToArray()),
        });
    private static PdfDictionary Widget() => new(new Dictionary<PdfName, QpdfObject>
    {
        [PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Widget"),
    });

    [Fact]
    public void RepeatedAndDistinctRootsReuseMembershipWithoutChangingRegistrationOrder()
    {
        const int chain = 64, rootCount = 48;
        Dictionary<int, QpdfObject> objects = new();
        for (int i = 1; i < chain; i++) { objects.Add(i, Branch(i + 1)); }
        objects.Add(chain, Widget());
        for (int i = 1; i <= rootCount; i++) { objects.Add(chain + i, Branch(1)); }
        ObservedPool pool = new(objects);
        PdfReference[] roots = Enumerable.Range(chain + 1, rootCount).Select(Link).Reverse().ToArray();
        PdfReference[] fields = [.. roots, .. roots.Reverse(), roots[0]];

        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope result = ParserMemoryScope.Create(governor))
        {
            (List<PdfReference> kept, HashSet<int> closure) = AcroFormCopy.CollectKeptClosure(pool, fields, [chain],
                true, QpdfLimits.Default, governor, result, CancellationToken.None);
            Assert.Equal(fields, kept);
            Assert.Equal(objects.Count, closure.Count);
            Assert.InRange(pool.Reads, chain, 3 * objects.Count + fields.Length);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);

        using (ParserMemoryScope different = ParserMemoryScope.Create(governor))
        {
            (List<PdfReference> kept, HashSet<int> closure) = AcroFormCopy.CollectKeptClosure(pool, fields, [],
                false, QpdfLimits.Default, governor, different, CancellationToken.None);
            Assert.Empty(kept);
            Assert.Empty(closure);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void RootReplayKeepsCycleAndDepthBehavior()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = 3 };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        ObservedPool cycle = new(new Dictionary<int, QpdfObject>
        {
            [1] = Branch(2), [2] = Branch(1, 3), [3] = Widget(), [4] = Branch(),
        });
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            PdfReference[] roots = [Link(1), Link(2), Link(1), Link(4)];
            var whole = AcroFormCopy.CollectKeptClosure(cycle, roots, [3], false, limits, governor, memory, CancellationToken.None);
            Assert.Equal(roots, whole.Kept);
            Assert.Equal(4, whole.Closure.Count);
            var selected = AcroFormCopy.CollectKeptClosure(cycle, roots, [], true, limits, governor, memory, CancellationToken.None);
            Assert.Empty(selected.Kept);
        }

        ObservedPool deep = new(new Dictionary<int, QpdfObject>
        {
            [1] = Branch(6, 2), [2] = Branch(3), [3] = Branch(4), [4] = Branch(6), [6] = Widget(),
            [7] = Branch(2),
        });
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            // The original per-root walk sees the shared widget on the short
            // edge before it follows the longer edge through the same object.
            var valid = AcroFormCopy.CollectKeptClosure(deep, [Link(1)], [6], true, limits, governor, memory, CancellationToken.None);
            Assert.Equal(Link(1), Assert.Single(valid.Kept));
        }
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            long admitted = governor.Snapshot().LiveMemoryBytes;
            Assert.Throws<QpdfLimitException>(() => AcroFormCopy.CollectKeptClosure(deep, [Link(1), Link(7)], [6], true,
                limits, governor, memory, CancellationToken.None));
            Assert.Equal(admitted, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringMembershipDiscoveryRefundsThePhase(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        ObservedPool pool = new(new Dictionary<int, QpdfObject> { [1] = Branch(2), [2] = Widget() });
        pool.OnResolve = () => { if (pool.Reads == 2) { cancellation.Cancel(); } };
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            long admitted = governor.Snapshot().LiveMemoryBytes;
            Assert.ThrowsAny<OperationCanceledException>(() => AcroFormCopy.CollectKeptClosure(pool, [Link(1)], [2], true,
                QpdfLimits.Default, governor, memory, governorToken ? CancellationToken.None : cancellation.Token));
            Assert.Equal(admitted, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void RefusedMembershipLeavesPreviouslyAdmittedStorageOwned()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        Dictionary<int, QpdfObject> objects = new();
        for (int number = 1; number < 128; number++) { objects.Add(number, Branch(number + 1)); }
        objects.Add(128, Widget());
        ObservedPool pool = new(objects);
        using (ParserMemoryScope prior = ParserMemoryScope.Create(governor))
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            prior.Reserve(17L);
            long admitted = governor.Snapshot().LiveMemoryBytes;
            Assert.Throws<QpdfLimitException>(() => AcroFormCopy.CollectKeptClosure(pool, [Link(1)], [], false,
                limits, governor, memory, CancellationToken.None));
            Assert.Equal(admitted, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.InRange(pool.Reads, 2, objects.Count);
    }
}
