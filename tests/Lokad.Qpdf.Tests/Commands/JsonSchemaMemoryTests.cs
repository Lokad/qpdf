using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Operations;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Hosting;
using System.Text.Json;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class JsonSchemaMemoryTests
{
    // Exercise the checker called by JSON inspection, including drift and bad
    // JSON that the ordinary serializer deliberately cannot produce.
    private static void Check(string json, int version, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
        => JsonExecution.CheckJsonSchema(json, System.Text.Encoding.UTF8.GetByteCount(json), version,
            [version == 1 ? "objects" : "qpdf"], true, governor, diagnostics, token);

    private sealed class MemoryHost(IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class CancelingGrowth(IMemoryBudget inner, CancellationTokenSource cancellation) : IMemoryBudget
    {
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            bool admitted = inner.TryReserve(bytes, out IMemoryReservation? storage);
            reservation = storage is null ? null : new Reservation(storage, cancellation); return admitted;
        }
        private sealed class Reservation(IMemoryReservation inner, CancellationTokenSource cancellation) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public bool TryResize(long bytes)
            {
                bool admitted = inner.TryResize(bytes);
                if (bytes >= 32768) { cancellation.Cancel(); }
                return admitted;
            }
            public void Dispose() => inner.Dispose();
        }
    }

    private static string Prefix(int version) => version == 1 ? "{\"objects\":{\"1 0 R\":"
        : "{\"qpdf\":[{\"jsonversion\":2,\"pdfversion\":\"1.7\",\"maxobjectid\":1,\"pushedinheritedpageresources\":false,\"calledgetallpages\":true},{\"obj:1 0 R\":{\"value\":";
    private static string Suffix(int version) => version == 1 ? "}}" : "}}]}";
    private static string DenseJson(int version) => Prefix(version) + "[" + string.Join(',', Enumerable.Repeat("0", 262144)) + "]" + Suffix(version);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DenseNestedValuesNeedByteStorageWithoutAFullOutputDom(int version)
    {
        string json = DenseJson(version);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 2 * 1024 * 1024 }, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Check(json, version, governor, diagnostics, CancellationToken.None);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 1L, json.Length + 384L * 1024L);
        Assert.Equal(QpdfDiagnosticCode.JsonSchemaCheck, Assert.Single(diagnostics.Entries).Code);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, json.Length, json.Length + 256L * 1024L);
        Assert.InRange(governor.Snapshot().LiveMemoryBytes, 1L, 1024L);
        governor.CompletePrivateStorage();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusalBeforeFullEncodingRefundsScratch(bool shared)
    {
        string json = DenseJson(2); RecordingMemoryBudget root = new(4096);
        using IMemoryScope caller = root.CreateScope();
        ResourceGovernor governor = shared ? new(QpdfLimits.Default, CancellationToken.None, new MemoryHost(caller))
            : new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        if (shared) { Assert.Throws<QpdfMemoryAdmissionException>(() => Check(json, 2, governor, diagnostics, CancellationToken.None)); }
        else { Assert.Throws<QpdfLimitException>(() => Check(json, 2, governor, diagnostics, CancellationToken.None)); }
        Assert.Empty(diagnostics.Entries);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringEncodingObservesBothTokensAndRefunds(bool governorToken)
    {
        string json = DenseJson(2); RecordingMemoryBudget root = new(1048576);
        using IMemoryScope caller = root.CreateScope(); using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None,
            new MemoryHost(new CancelingGrowth(caller, cancellation)));
        DiagnosticBag diagnostics = new(governor);
        Assert.ThrowsAny<OperationCanceledException>(() => Check(json, 2, governor, diagnostics,
            governorToken ? CancellationToken.None : cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested); Assert.Empty(diagnostics.Entries);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData("{\"pages\":[0,]}")]
    [InlineData("{\"pages\":{")]
    [InlineData("[]")]
    [InlineData("{} {}")]
    [InlineData("{\"pages\":null")]
    [InlineData("{\"pages\":0,\"drift\":[}")]
    public void MalformedOutputCannotProduceASuccessDiagnostic(string json)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        Assert.ThrowsAny<JsonException>(() => Check(json, 2, governor, diagnostics, CancellationToken.None));
        Assert.Empty(diagnostics.Entries); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void GeneratedSyntaxStillHasAFiniteCeilingAndRefundsFailureStorage()
    {
        string json = "{\"pages\":" + new string('[', Lokad.Qpdf.Json.DocumentJson.SyntaxMaxDepth)
            + "0" + new string(']', Lokad.Qpdf.Json.DocumentJson.SyntaxMaxDepth) + "}";
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        Assert.ThrowsAny<JsonException>(() => Check(json, 2, governor, diagnostics, CancellationToken.None));
        Assert.Empty(diagnostics.Entries);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(1, "objects", true)]
    [InlineData(2, "objects", false)]
    [InlineData(1, "qpdf", false)]
    [InlineData(2, "qpdf", true)]
    public void EscapedTopLevelKeysUseTheVersionedContainerDescription(int version, string key, bool valid)
    {
        string value = key == "objects" ? "{\"unlisted-nested-key\":1}" : Prefix(2)[8..] + "null" + Suffix(2)[..^1];
        string json = "{\"\\u00" + ((int)key[0]).ToString("x2") + key[1..] + "\":" + value + "}";
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        Check(json, version, governor, diagnostics, CancellationToken.None);
        QpdfDiagnostic diagnostic = Assert.Single(diagnostics.Entries);
        Assert.Equal(valid ? QpdfDiagnosticCode.JsonSchemaCheck : QpdfDiagnosticCode.CheckFailed, diagnostic.Code);
        if (!valid) { Assert.Contains("unknown key '" + key + "'", diagnostic.Message, StringComparison.Ordinal); }
    }

    [Fact]
    public void DriftDiagnosticPreservesOrderDuplicatesAndUnicode()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        Check("{\"drift\":0,\"drift\":1,\"\\ud83d\\ude00\":{}}", 2, governor, diagnostics, CancellationToken.None);
        QpdfDiagnostic diagnostic = Assert.Single(diagnostics.Entries);
        Assert.Equal(QpdfDiagnosticCode.CheckFailed, diagnostic.Code);
        Assert.Contains("unknown key 'drift'; $: unknown key 'drift'; $: unknown key '😀';", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Utf8EncodingPreservesASurrogatePairAcrossAChunkBoundary()
    {
        string prefix = Prefix(2) + "\"";
        string json = prefix + new string('a', 255 - prefix.Length) + "😀\"" + Suffix(2);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        Check(json, 2, governor, diagnostics, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.JsonSchemaCheck, Assert.Single(diagnostics.Entries).Code);
    }

    [Fact]
    public void DiagnosticOverflowFailsExplicitlyAndRefundsUnacceptedText()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxDiagnosticBytes = 48 }, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        Assert.Throws<QpdfLimitException>(() => Check("{\"drift\":0}", 2, governor, diagnostics, CancellationToken.None));
        Assert.Empty(diagnostics.Entries); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Check(Prefix(2) + "null" + Suffix(2), 2, governor, diagnostics, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.JsonSchemaCheck, Assert.Single(diagnostics.Entries).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SchemaModelRefusalLeavesEarlierStorageFunded(bool shared)
    {
        RecordingMemoryBudget budget = new(4096);
        using IMemoryScope caller = budget.CreateScope();
        ResourceGovernor governor = shared ? new(QpdfLimits.Default, CancellationToken.None, new MemoryHost(caller))
            : new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        using (ParserMemoryScope earlier = ParserMemoryScope.Create(governor))
        {
            earlier.Reserve(97); DiagnosticBag diagnostics = new(governor);
            long before = governor.Snapshot().LiveMemoryBytes, charged = budget.ReservedBytes;
            if (shared)
            { Assert.Throws<QpdfMemoryAdmissionException>(() => JsonFormatValidator.Check("{}"u8, HelpText.JsonHelpV2, 2, [], false, governor, diagnostics, CancellationToken.None)); }
            else
            { Assert.Throws<QpdfLimitException>(() => JsonFormatValidator.Check("{}"u8, HelpText.JsonHelpV2, 2, [], false, governor, diagnostics, CancellationToken.None)); }
            Assert.Empty(diagnostics.Entries); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(charged, budget.ReservedBytes); Assert.Equal(97L, earlier.ReservedBytes);
        }
        governor.CompletePrivateStorage(); Assert.Equal(0L, budget.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CancellationDuringMatchingAndPdfSkippingChecksBothTokens(bool governorToken, bool pdfLeaf)
    {
        byte[] json = System.Text.Encoding.UTF8.GetBytes(pdfLeaf
            ? "{\"rows\":[{\"payload\":[" + string.Join(',', Enumerable.Repeat("0", 1024)) + "]}]}"
            : "{\"rows\":[" + string.Join(',', Enumerable.Repeat("{}", 1024)) + "]}");
        string description = pdfLeaf ? """{"rows":[{"payload":"PDF value"}]}""" : """{"rows":[{}]}""";
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope earlier = ParserMemoryScope.Create(governor); earlier.Reserve(97);
        long before = governor.Snapshot().LiveMemoryBytes; DiagnosticBag diagnostics = new(governor);
        int reads = 0; long last = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => JsonFormatValidator.CheckObserved(json, description, 2, [], true,
            governor, diagnostics, governorToken ? CancellationToken.None : cancellation.Token, consumed =>
            { reads++; last = consumed; if (consumed >= 128) { cancellation.Cancel(); } }));
        Assert.True(reads > 8); Assert.InRange(last, 128L, 132L);
        Assert.Empty(diagnostics.Entries); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(97L, earlier.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedUnknownKeysFailBeforeDiagnosticUnescaping(bool escaped)
    {
        string key = string.Concat(Enumerable.Repeat(escaped ? "\\u0061" : "a", 32768));
        byte[] json = System.Text.Encoding.UTF8.GetBytes("{\"rows\":[],\"" + key + "\":null}");
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxDiagnosticBytes = 128 }, CancellationToken.None);
        using ParserMemoryScope earlier = ParserMemoryScope.Create(governor); earlier.Reserve(97);
        long live = governor.Snapshot().LiveMemoryBytes; DiagnosticBag diagnostics = new(governor);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => JsonFormatValidator.Check(json,
            """{"rows":[]}""", 2, [], true, governor, diagnostics, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxDiagnosticBytes), failure.LimitName);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 1L, 32768L);
        Assert.Empty(diagnostics.Entries); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class SchemaBudget(IMemoryBudget inner, CancellationTokenSource cancellation, bool cancel) : IMemoryBudget
    {
        internal bool Hit { get; private set; }
        private bool Accept(long bytes)
        {
            if (bytes < 131072) { return true; }
            Hit = true;
            if (cancel) { cancellation.Cancel(); }
            return cancel;
        }
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            reservation = null;
            if (!Accept(bytes) || !inner.TryReserve(bytes, out IMemoryReservation? backing)) { return false; }
            reservation = new Reservation(backing, this); return true;
        }
        private sealed class Reservation(IMemoryReservation inner, SchemaBudget owner) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public bool TryResize(long bytes) => owner.Accept(bytes) && inner.TryResize(bytes);
            public void Dispose() => inner.Dispose();
        }
    }

    private sealed class PublicationHost(InMemoryQpdfHost inner, SchemaBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        internal int OutputCreates { get; set; }
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => inner.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { OutputCreates++; return inner.CreateOutputAsync(path, token); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SchemaAdmissionFailurePrecedesPublicationAndPreservesTheTarget(bool job, bool cancel)
    {
        RecordingMemoryBudget root = new(4 * 1024 * 1024);
        using (IMemoryScope caller = root.CreateScope())
        using (InMemoryQpdfHost inner = new(QpdfLimits.Default, null, caller))
        using (CancellationTokenSource cancellation = new())
        {
            inner.AddInput(QpdfPath.From("input"), await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Transfer);
            QpdfPath report = QpdfPath.From("report");
            await using (IQpdfOutputTransaction seed = await inner.CreateOutputAsync(report, CancellationToken.None))
            { await seed.WriteAsync("prior-target"u8.ToArray(), CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
            SchemaBudget budget = new(caller, cancellation, cancel); PublicationHost host = new(inner, budget);
            QpdfExecutionResult control = await Qpdf.ExecuteAsync(host, new(["input", "--json=2", "control"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, control.Status); Assert.False(budget.Hit); host.OutputCreates = 0;
            QpdfExecutionResult failed = job
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"report","json":"2","testJsonSchema":true}""", cancellation.Token)
                : await Qpdf.ExecuteAsync(host, new(["input", "--json=2", "--test-json-schema", "report"]), cancellation.Token);
            Assert.True(budget.Hit); Assert.Equal(cancel ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, failed.Status);
            Assert.Equal(0, host.OutputCreates); Assert.Equal(0L, inner.OpenLeaseCount);
            if (!cancel) { Assert.Contains(failed.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded); }
            Assert.True(inner.TryGetOutput(report, out byte[] bytes)); Assert.Equal("prior-target"u8.ToArray(), bytes);
        }
        Assert.Equal(0L, root.ReservedBytes); Assert.Equal(0, root.LiveReservations);
    }
}
