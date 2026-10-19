using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Reading;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Syntax;

public sealed class ParserDiscardTests
{
    private static IEnumerable<string> Values()
    {
        string literal = "(" + new string('A', 4096) + ")";
        yield return literal;
        yield return "<" + string.Concat(Enumerable.Repeat("41", 4096)) + ">";
        yield return "/" + new string('A', 4096);
        yield return new string('0', 4096) + "7";
        yield return "0." + new string('0', 4096) + "1";
        yield return "8 0 R";
        yield return "[ " + literal + " [ /Name 007 ] ]";
        yield return "<< /Keep [ " + literal + " /Name 007 ] /Drop " + literal + " /Drop null >>";
    }

    public static IEnumerable<object[]> Overwrites()
    {
        foreach (string value in Values())
        foreach (bool member in new[] { false, true })
        foreach (bool repair in new[] { false, true })
        { yield return [value, member, repair]; }
    }

    public static IEnumerable<object[]> Orphans()
    {
        foreach (string value in Values())
        foreach (bool member in new[] { false, true })
        { yield return ["[ " + value + " ]", member]; }
    }

    private static QpdfObject Parse(PdfParser parser, string body, bool member)
    {
        byte[] input = Encoding.ASCII.GetBytes(member ? body : "5 0 obj " + body + " endobj");
        return member ? parser.ParseObjectValue(new PdfTokenizer(input), 0)
            : parser.ParseIndirectObject(new PdfTokenizer(input), 0).Value;
    }

    private static long ControlAdmission(string body, bool member, bool repair, IReadOnlyList<QpdfDiagnostic> recorded)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxParserErrors = 0L, MaxMemoryBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        QpdfObject value = Parse(new PdfParser(limits, governor, diagnostics, null) { TolerateObjectErrors = repair }, body, member);
        DiagnosticControl.Replay(diagnostics, recorded);
        long admitted = governor.Snapshot().LiveMemoryBytes;
        GC.KeepAlive(value);
        GC.KeepAlive(diagnostics);
        return admitted;
    }

    [Theory]
    [MemberData(nameof(Overwrites))]
    public void SuccessfulOverwritesRetainOnlyTheSurvivingGraph(string discarded, bool member, bool repair)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxParserErrors = 0L, MaxMemoryBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        PdfParser parser = new(limits, governor, diagnostics, null) { TolerateObjectErrors = repair };
        PdfDictionary value = Assert.IsType<PdfDictionary>(Parse(parser, "<< /A " + discarded + " /A null >>", member));
        Assert.Equal(1, value.Count);
        Assert.True(value.TryGet(PdfName.FromAscii("A"), out QpdfObject surviving));
        Assert.Same(PdfNull.Instance, surviving);
        Assert.False(parser.ObjectAborted);
        Assert.Equal(ControlAdmission("<< /A null >>", member, repair, diagnostics.Entries), governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().PeakMemoryBytes > governor.Snapshot().LiveMemoryBytes);
        Assert.Contains(diagnostics.Entries, d => d.Code == QpdfDiagnosticCode.DuplicateKey);
    }

    [Theory]
    [MemberData(nameof(Orphans))]
    public void SuccessfulRecoveryDiscardsOnlyOrphanValues(string discarded, bool member)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxParserErrors = 0L, MaxMemoryBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        PdfParser parser = new(limits, governor, diagnostics, null) { TolerateObjectErrors = true, SanityChecks = true };
        PdfDictionary value = Assert.IsType<PdfDictionary>(Parse(parser, "<< " + discarded + " /A null >>", member));
        Assert.Equal(1, value.Count);
        Assert.True(value.TryGet(PdfName.FromAscii("A"), out QpdfObject surviving));
        Assert.Same(PdfNull.Instance, surviving);
        Assert.Equal(ControlAdmission("<< /A null >>", member, true, diagnostics.Entries), governor.Snapshot().LiveMemoryBytes);
        Assert.Contains(diagnostics.Entries, d => d.Message.Contains("Ignored orphan", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Orphans))]
    public void OrdinaryRepairTransfersOrphansToTheirGeneratedKeys(string kept, bool member)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxParserErrors = 0L, MaxMemoryBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        PdfParser parser = new(limits, governor, diagnostics, null) { TolerateObjectErrors = true };
        PdfDictionary value = Assert.IsType<PdfDictionary>(Parse(parser, "<< " + kept + " /A null >>", member));
        Assert.True(value.TryGet(PdfName.FromAscii("QPDFFake1"), out QpdfObject orphan));
        Assert.NotSame(PdfNull.Instance, orphan);
        Assert.Equal(ControlAdmission("<< /QPDFFake1 " + kept + " /A null >>", member, true, diagnostics.Entries), governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void EquivalentDuplicateNamesDoNotRetainTheSecondKey()
    {
        string key = new('A', 4096);
        string escaped = string.Concat(Enumerable.Repeat("#41", 4096));
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        PdfParser parser = new(limits, governor, diagnostics, null);
        PdfDictionary value = Assert.IsType<PdfDictionary>(Parse(parser, "<< /" + key + " true /" + escaped + " null >>", true));
        Assert.Equal(Encoding.ASCII.GetBytes(key), Assert.Single(value.Keys).RawBytes);
        Assert.Equal(ControlAdmission("<< /" + key + " null >>", true, false, diagnostics.Entries), governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingDuplicateValueReleasesItsPredecessor(bool member)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L, MaxParserErrors = 0L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        PdfParser parser = new(limits, governor, diagnostics, null) { TolerateObjectErrors = true };
        PdfDictionary value = Assert.IsType<PdfDictionary>(Parse(parser, "<< /A (" + new string('A', 4096) + ") /A >>", member));
        Assert.True(value.TryGet(PdfName.FromAscii("A"), out QpdfObject surviving));
        Assert.Same(PdfNull.Instance, surviving);
        Assert.Equal(ControlAdmission("<< /A null >>", member, true, diagnostics.Entries), governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(2, diagnostics.Entries.Count);
    }

    [Fact]
    public void PublicDictionaryConstructionStillCopiesMutableEntries()
    {
        PdfName key = PdfName.FromAscii("A");
        Dictionary<PdfName, QpdfObject> entries = new() { [key] = PdfBoolean.True };
        PdfDictionary value = new(entries);
        entries[key] = PdfBoolean.False;
        entries.Add(PdfName.FromAscii("B"), PdfNull.Instance);
        Assert.Equal(1, value.Count);
        Assert.True(value.TryGet(key, out QpdfObject kept));
        Assert.Same(PdfBoolean.True, kept);
    }

    [Fact]
    public void RepeatedSuccessfulDiscardsPreservePreviousValueAliases()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L, MaxParserErrors = 0L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        PdfParser parser = new(limits, governor, new DiagnosticBag(governor), null) { TolerateObjectErrors = true };
        string literal = "(" + new string('A', 4096) + ")";
        PdfArray kept = Assert.IsType<PdfArray>(Parse(parser, "[ " + literal + " ]", true));
        QpdfObject alias = kept[0];
        List<PdfDictionary> accepted = [];
        for (int i = 1; i <= 50; i++)
        {
            accepted.Add(Assert.IsType<PdfDictionary>(Parse(parser, "<< /A [ " + literal + " ] /A null >>", true)));
            Assert.InRange(governor.Snapshot().LiveMemoryBytes, 4096L, limits.MaxMemoryBytes);
            Assert.Same(alias, kept[0]);
        }
        Assert.Equal(Encoding.ASCII.GetBytes(new string('A', 4096)), Assert.IsType<PdfByteString>(alias).Value);
        Assert.All(accepted, dictionary =>
        {
            PdfName key = Assert.Single(dictionary.Keys);
            Assert.Equal("A"u8.ToArray(), key.RawBytes);
            Assert.True(dictionary.TryGet(key, out QpdfObject value));
            Assert.Same(PdfNull.Instance, value);
        });
        GC.KeepAlive(accepted); GC.KeepAlive(kept);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PublicBulkSuccessfulDiscardsFitFiniteMemory(bool job, bool brokenXref)
    {
        string body = "<< /A (" + new string('A', 4096) + ") /A null >>";
        string[] bodies = Enumerable.Repeat(body, 50).ToArray();
        byte[] input = TolerantObjectLoadingTests.Pdf(bodies, brokenXref);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("inputs/control.pdf"), TolerantObjectLoadingTests.Pdf(bodies, false), QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = input.Length + 80000L, MaxParserErrors = 0L };
        await using (QpdfDocument control = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/control.pdf"), limits, CancellationToken.None))
        {
            Assert.Equal(1, control.PageCount);
            Assert.InRange(CounterObservation.Snapshot(control).PeakMemoryBytes, input.Length, limits.MaxMemoryBytes);
        }
        QpdfPath priorPath = QpdfPath.From("outputs/prior.pdf");
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(priorPath, CancellationToken.None))
        {
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        QpdfExecutionOptions options = new(limits);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","outputFile":"outputs/report.json","json":"2","jsonKey":["qpdf"],"global":{"parserMaxErrors":0}}""", options, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--json=2", "--json-key=qpdf", "--parser-max-errors=0", "--", "outputs/report.json"]), options, CancellationToken.None);
        Assert.True(result.Status == QpdfExecutionStatus.Completed, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Equal(50, result.Diagnostics.Count(d => d.Code == QpdfDiagnosticCode.DuplicateKey));
        Assert.InRange(result.Counters.PeakMemoryBytes, input.Length, limits.MaxMemoryBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/report.json"), out byte[] reportBytes));
        using JsonDocument report = JsonDocument.Parse(reportBytes);
        JsonElement objects = report.RootElement.GetProperty("qpdf")[1];
        for (int number = 5; number < 55; number++)
        {
            JsonElement dictionary = objects.GetProperty($"obj:{number} 0 R").GetProperty("value");
            Assert.Equal(JsonValueKind.Object, dictionary.ValueKind);
            Assert.Empty(dictionary.EnumerateObject());
        }
        Assert.True(host.TryGetOutput(priorPath, out byte[] priorBytes));
        Assert.Equal("prior"u8.ToArray(), priorBytes);
    }

    [Fact]
    public async Task SuccessfulStreamKeepsFreshNestedLengthWhileDiscardingDictionaryValues()
    {
        string length = new string('0', 4096) + "3";
        byte[] damaged = TolerantObjectLoadingTests.Pdf(["<< /Length 6 0 R /A (" + new string('A', 4096) + ") /A null >>\nstream\nabc\nendstream", length], false);
        byte[] controlInput = TolerantObjectLoadingTests.Pdf(["<< /Length 6 0 R /A null >>\nstream\nabc\nendstream", length], false);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), damaged, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("inputs/control.pdf"), controlInput, QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        await using (QpdfDocument control = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/control.pdf"), limits, CancellationToken.None))
        await using (QpdfDocument discarded = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), limits, CancellationToken.None))
        {
            long controlBase = CounterObservation.Snapshot(control).LiveMemoryBytes;
            long discardedBase = CounterObservation.Snapshot(discarded).LiveMemoryBytes;
            Assert.Equal("abc"u8.ToArray(), Assert.IsType<PdfStream>(control.GetObject(5)).Data);
            Assert.Equal("abc"u8.ToArray(), Assert.IsType<PdfStream>(discarded.GetObject(5)).Data);
            DiagnosticControl.Replay(DiagnosticControl.Read(control), DiagnosticControl.Read(discarded).Entries);
            Assert.Equal(CounterObservation.Snapshot(control).LiveMemoryBytes - controlBase, CounterObservation.Snapshot(discarded).LiveMemoryBytes - discardedBase);
            PdfInteger cached = Assert.IsType<PdfInteger>(discarded.GetObject(6));
            Assert.Equal(3L, cached.Value);
            Assert.Same(cached, discarded.GetObject(6));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureAfterDiscardPreservesPreviouslyRetainedValues(bool abort)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L, MaxParserErrors = abort ? 2L : 0L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        PdfParser parser = new(limits, governor, diagnostics, null) { TolerateObjectErrors = abort };
        PdfByteString kept = Assert.IsType<PdfByteString>(Parse(parser, "(kept)", true));
        string body = "<< /A (" + new string('A', 4096) + ") /A null /B @ >>";
        if (abort) { Assert.Same(PdfNull.Instance, Parse(parser, body, true)); }
        else { Assert.Throws<QpdfParseException>(() => Parse(parser, body, true)); }
        Assert.Equal(ControlAdmission("(kept)", true, abort, diagnostics.Entries), governor.Snapshot().LiveMemoryBytes);
        Assert.Equal("kept"u8.ToArray(), kept.Value);
    }

    [Fact]
    public async Task DiscardedNestedStreamPreservesFreshResolverCache()
    {
        // Adversarial nested-stream syntax accepted by the existing parser.
        // This tests containment and cache ownership, not an oracle grammar claim.
        string length = new string('0', 4096) + "3";
        byte[] input = TolerantObjectLoadingTests.Pdf(["<< /A << /Length 6 0 R >>\nstream\nabc\nendstream /A null >>", length], false);
        byte[] controlInput = TolerantObjectLoadingTests.Pdf(["<< /A null >>", length], false);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("inputs/control.pdf"), controlInput, QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        await using (QpdfDocument control = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/control.pdf"), limits, CancellationToken.None))
        await using (QpdfDocument discarded = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), limits, CancellationToken.None))
        {
            long controlBase = CounterObservation.Snapshot(control).LiveMemoryBytes;
            long discardedBase = CounterObservation.Snapshot(discarded).LiveMemoryBytes;
            Assert.IsType<PdfDictionary>(control.GetObject(5));
            Assert.Equal(3L, Assert.IsType<PdfInteger>(control.GetObject(6)).Value);
            PdfDictionary surviving = Assert.IsType<PdfDictionary>(discarded.GetObject(5));
            Assert.True(surviving.TryGet(PdfName.FromAscii("A"), out QpdfObject kept));
            Assert.Same(PdfNull.Instance, kept);
            DiagnosticControl.Replay(DiagnosticControl.Read(control), DiagnosticControl.Read(discarded).Entries);
            Assert.Equal(CounterObservation.Snapshot(control).LiveMemoryBytes - controlBase, CounterObservation.Snapshot(discarded).LiveMemoryBytes - discardedBase);
            PdfInteger cached = Assert.IsType<PdfInteger>(discarded.GetObject(6));
            Assert.Equal(3L, cached.Value);
            Assert.Same(cached, discarded.GetObject(6));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CancellationAfterOverwriteReleasesItsAttempt()
    {
        byte[] input = Encoding.ASCII.GetBytes("<< /A (" + new string('A', 4096) + ") /A null /B (" + new string('B', 16 * 1024 * 1024) + ") >>");
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = 1L };
        ResourceGovernor governor = new(limits, cancellation.Token);
        DiagnosticBag diagnostics = new(governor);
        PdfParser parser = new(limits, governor, diagnostics, null);
        Task<QpdfObject> parsing = Task.Factory.StartNew(() => parser.ParseObjectValue(new PdfTokenizer(input), 0), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        bool observed = SpinWait.SpinUntil(() => governor.Snapshot().LiveMemoryBytes >= 16L * 1024L * 1024L || parsing.IsCompleted, TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        Assert.True(observed);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await parsing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(governor.Snapshot().PeakMemoryBytes >= 16L * 1024L * 1024L);
        Assert.Equal(ControlAdmission("null", true, false, diagnostics.Entries), governor.Snapshot().LiveMemoryBytes);
    }
}
