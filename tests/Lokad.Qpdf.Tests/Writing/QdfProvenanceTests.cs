using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class QdfProvenanceTests
{
    private delegate Dictionary<int, int> InvertCall(IEnumerable<IReadOnlyDictionary<int, int>> maps,
        ParserMemoryScope owner, ResourceGovernor governor, CancellationToken token);
    private static readonly InvertCall Invert = (typeof(Qpdf).GetMethod("InvertOriginals", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException()).CreateDelegate<InvertCall>();

    [Fact]
    public void LargeInversionRefusesAndRefundsTheCandidate()
    {
        Dictionary<int, int> source = Enumerable.Range(1, 262144).ToDictionary(i => i, i => i + 100);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor); owner.Reserve(31L);
        long before = governor.Snapshot().LiveMemoryBytes, allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => Invert([source], owner, governor, CancellationToken.None));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0L, 65536L);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(31L, owner.ReservedBytes);
    }

    [Fact]
    public void EmptyAndOverlappingSourceMapsKeepLastWinsAndTransferOnlyCompletedStorage()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            Assert.Empty(Invert([], owner, governor, CancellationToken.None));
            Dictionary<int, int> result = Invert([new Dictionary<int, int> { [7] = 3, [9] = 4 },
                new Dictionary<int, int> { [12] = 3, [13] = 5 }], owner, governor, CancellationToken.None);
            Assert.Equal(3, result.Count); Assert.Equal(12, result[3]); Assert.Equal(9, result[4]); Assert.Equal(13, result[5]);
            Assert.True(owner.ReservedBytes > 0L);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class CancelingMap(Action cancel) : IReadOnlyDictionary<int, int>
    {
        internal int Visited;
        public int Count => 128;
        public IEnumerable<int> Keys => throw new NotSupportedException();
        public IEnumerable<int> Values => throw new NotSupportedException();
        public int this[int key] => throw new NotSupportedException();
        public bool ContainsKey(int key) => throw new NotSupportedException();
        public bool TryGetValue(int key, out int value) => throw new NotSupportedException();
        public IEnumerator<KeyValuePair<int, int>> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
            { if (index == 32) { cancel(); } Visited++; yield return new(index, index + 100); }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherTokenInterruptsInversionAndRefundsItsCandidate(bool caller)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor); owner.Reserve(31L);
        long before = governor.Snapshot().LiveMemoryBytes; CancelingMap source = new(cancellation.Cancel);
        Assert.Throws<OperationCanceledException>(() => Invert([source], owner, governor, caller ? cancellation.Token : CancellationToken.None));
        Assert.InRange(source.Visited, 1, source.Count - 1);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(31L, owner.ReservedBytes);
    }

    [Theory]
    [InlineData("single", false, false)]
    [InlineData("single", false, true)]
    [InlineData("single", true, false)]
    [InlineData("single", true, true)]
    [InlineData("merge", false, false)]
    [InlineData("merge", false, true)]
    [InlineData("merge", true, false)]
    [InlineData("merge", true, true)]
    [InlineData("split", false, false)]
    [InlineData("split", false, true)]
    [InlineData("split", true, false)]
    [InlineData("split", true, true)]
    public async Task OriginalIdCommentsAndSuppressionAgreeAcrossRoutes(string operation, bool streamed, bool suppress)
    {
        List<byte[]> argvOutput = [];
        foreach (bool job in new[] { false, true })
        {
            using InMemoryQpdfHost host = new();
            host.AddInput(QpdfPath.From("input"), ObjectStreamPlanningTests.Fixture(8), QpdfInputOwnership.Transfer);
            host.AddInput(QpdfPath.From("other"), ObjectStreamPlanningTests.Fixture(8), QpdfInputOwnership.Transfer);
            string mode = streamed ? "generate" : "disable";
            List<string> arguments = ["input", "--qdf", "--static-id", "--object-streams=" + mode];
            Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["qdf"] = "", ["staticId"] = "", ["objectStreams"] = mode };
            if (suppress) { arguments.Add("--no-original-object-ids"); settings["noOriginalObjectIds"] = ""; }
            if (operation == "merge")
            {
                arguments.AddRange(["--pages", ".", "1", "other", "1", "--"]);
                settings["pages"] = new[] { new { file = ".", range = "1" }, new { file = "other", range = "1" } };
            }
            if (operation == "split")
            {
                arguments.AddRange(["--split-pages=1", "--pages", ".", "1,1", "--"]); settings["splitPages"] = "1";
                settings["pages"] = new[] { new { file = ".", range = "1,1" } };
            }
            arguments.Add("output");
            QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            int outputs = operation == "split" ? 2 : 1;
            for (int index = 0; index < outputs; index++)
            {
                QpdfPath path = QpdfPath.From(operation == "split" ? "output-" + (index + 1) : "output");
                Assert.True(host.TryGetOutput(path, out byte[] bytes));
                string text = Encoding.ASCII.GetString(bytes);
                Assert.Contains("%QDF-1.0", text, StringComparison.Ordinal);
                Assert.Equal(!suppress, text.Contains("original object ID:", StringComparison.OrdinalIgnoreCase));
                using (UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(bytes))
                { Assert.Equal(operation == "merge" ? 2 : 1, independent.NumberOfPages); }
                if (job) { Assert.Equal(argvOutput[index], bytes); } else { argvOutput.Add(bytes); }
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
    }
}
