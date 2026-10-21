using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class JsonSchemaMemoryTests
{
    // Exercise the checker called by JSON inspection, including drift and bad
    // JSON that the ordinary serializer deliberately cannot produce.
    private static readonly Action<string, long, int, ResourceGovernor, DiagnosticBag, CancellationToken> Checker =
        (typeof(Qpdf).GetMethod("CheckJsonSchema", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing JSON inspection checker."))
        .CreateDelegate<Action<string, long, int, ResourceGovernor, DiagnosticBag, CancellationToken>>();

    private static void Check(string json, int version, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
        => Checker(json, System.Text.Encoding.UTF8.GetByteCount(json), version, governor, diagnostics, token);

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

    private static string DenseJson() => "{\"pages\":[" + string.Join(',', Enumerable.Repeat("0", 262144)) + "]}";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DenseNestedValuesNeedByteStorageWithoutAFullOutputDom(int version)
    {
        string json = DenseJson();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 2 * 1024 * 1024 }, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        Check(json, version, governor, diagnostics, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.JsonSchemaCheck, Assert.Single(diagnostics.Entries).Code);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, json.Length, json.Length + 8192L);
        Assert.InRange(governor.Snapshot().LiveMemoryBytes, 1L, 1024L);
        governor.CompletePrivateStorage();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusalBeforeFullEncodingRefundsScratch(bool shared)
    {
        string json = DenseJson(); RecordingMemoryBudget root = new(4096);
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
        string json = DenseJson(); RecordingMemoryBudget root = new(1048576);
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
    public void MalformedOutputCannotProduceASuccessDiagnostic(string json)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        Assert.ThrowsAny<JsonException>(() => Check(json, 2, governor, diagnostics, CancellationToken.None));
        Assert.Empty(diagnostics.Entries); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(1, "objects", true)]
    [InlineData(2, "objects", false)]
    [InlineData(1, "qpdf", false)]
    [InlineData(2, "qpdf", true)]
    public void EscapedTopLevelKeysUseTheVersionInventoryWhileNestedKeysAreIgnored(int version, string key, bool valid)
    {
        string json = "{\"\\u00" + ((int)key[0]).ToString("x2") + key[1..] + "\":{\"unlisted-nested-key\":1}}";
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        Check(json, version, governor, diagnostics, CancellationToken.None);
        QpdfDiagnostic diagnostic = Assert.Single(diagnostics.Entries);
        Assert.Equal(valid ? QpdfDiagnosticCode.JsonSchemaCheck : QpdfDiagnosticCode.CheckFailed, diagnostic.Code);
        if (!valid) { Assert.EndsWith(": " + key + ".", diagnostic.Message, StringComparison.Ordinal); }
    }

    [Fact]
    public void DriftDiagnosticPreservesOrderDuplicatesAndUnicode()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        Check("{\"drift\":0,\"drift\":1,\"\\ud83d\\ude00\":{}}", 2, governor, diagnostics, CancellationToken.None);
        QpdfDiagnostic diagnostic = Assert.Single(diagnostics.Entries);
        Assert.Equal(QpdfDiagnosticCode.CheckFailed, diagnostic.Code);
        Assert.Equal("JSON output has keys missing from --json-help version 2: drift, drift, 😀.", diagnostic.Message);
    }

    [Fact]
    public void Utf8EncodingPreservesASurrogatePairAcrossAChunkBoundary()
    {
        const string prefix = "{\"pages\":[\"";
        string json = prefix + new string('a', 255 - prefix.Length) + "😀\"]}";
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
        Check("{\"pages\":[]}", 2, governor, diagnostics, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.JsonSchemaCheck, Assert.Single(diagnostics.Entries).Code);
    }
}
