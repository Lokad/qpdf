using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Foundation;

[CollectionDefinition(nameof(CompletedJobOwnershipCollection), DisableParallelization = true)]
public sealed class CompletedJobOwnershipCollection;

// These lifetime controls force process-wide collections. Run them separately
// so they do not interrupt another class's observed-work cancellation interval.
[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class CompletedJobOwnershipTests
{
    public static IEnumerable<object[]> Carriers()
    {
        foreach (string kind in new[] { "check-list", "check-record", "check-detail", "diagnostic-list",
            "diagnostic-record", "diagnostic-text", "integer", "integer-text", "dictionary-keys", "byte-buffer" })
        {
            yield return [kind];
        }
    }

    private sealed class Alias(object value, WeakReference[] metadata, long minimumBytes)
    {
        internal object? Value = value;
        internal readonly WeakReference[] Metadata = metadata;
        internal readonly long MinimumBytes = minimumBytes;
    }

    private static object Field(object value, string name)
        => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value)
            ?? throw new InvalidOperationException("Missing ownership field: " + name);

    private static WeakReference[] Metadata(ResourceGovernor governor)
    {
        object ledger = Field(governor, "_valueOwners");
        object segment = Field(ledger, "_segments");
        return [new(governor), new(ledger), new(Field(ledger, "_storage")), new(Field(ledger, "_heads")),
            new(Field(ledger, "_slots")), new(segment), new(Field(segment, "Values"))];
    }

    private static void Collect()
    {
        for (int iteration = 0; iteration < 3; iteration++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
    }

    private static void Collect(ResourceGovernor governor)
    {
        for (int iteration = 0; iteration < 3; iteration++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            governor.ReserveMemory(0L);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias Capture(ResourceGovernor governor, QpdfLimits limits, string kind)
    {
        object value;
        long minimumBytes;
        if (kind.StartsWith("check-", StringComparison.Ordinal))
        {
            InMemoryQpdfHost host = new();
            QpdfPath path = QpdfPath.From("check.pdf");
            host.AddInput(path, CheckTests.Assemble(CheckTests.SinglePage("<< /Length 0 >>", []), 1), QpdfInputOwnership.Transfer);
            DiagnosticBag diagnostics = new(governor);
            QpdfDocument document = QpdfDocument.OpenCore(host, path, limits, governor, diagnostics,
                QpdfRecoveryMode.Strict, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            try
            {
                List<QpdfCheckOutcome> checks = DocumentCheck.Run(document, new QpdfCheckOptions(), limits,
                    governor, diagnostics, false, CancellationToken.None);
                QpdfCheckOutcome selected = checks.Single(check => check.Name == "object-graph");
                value = kind == "check-list" ? checks : kind == "check-record" ? selected : selected.Detail;
                minimumBytes = 2L * selected.Detail.Length + (kind == "check-detail" ? 0L : 40L);
            }
            finally
            {
                document.DisposeAsync().AsTask().GetAwaiter().GetResult();
                Assert.Equal(0L, host.OpenLeaseCount);
            }
        }
        else if (kind.StartsWith("diagnostic-", StringComparison.Ordinal))
        {
            DiagnosticBag bag = new(governor);
            bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, new string('W', 1024));
            value = kind == "diagnostic-list" ? bag.Entries : kind == "diagnostic-record" ? bag.Entries[0] : bag.Entries[0].Message;
            minimumBytes = 2048L + (kind == "diagnostic-text" ? 0L : 40L);
        }
        else
        {
            PdfParser parser = new(limits, governor, new DiagnosticBag(governor), null);
            string source = kind == "dictionary-keys" ? "<< /A 7 >>" : kind == "byte-buffer" ? "(ABC)" : "00000123";
            QpdfObject parsed = parser.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(source)), 0);
            value = kind == "dictionary-keys" ? Assert.IsType<PdfDictionary>(parsed).Keys
                : kind == "byte-buffer" ? Assert.IsType<PdfByteString>(parsed).AllocationCarrier
                : kind == "integer-text" ? Assert.IsType<PdfInteger>(parsed).Text : parsed;
            minimumBytes = kind == "byte-buffer" ? 3L : kind == "integer-text" ? 16L : 32L;
        }
        Assert.True(governor.Snapshot().LiveMemoryBytes >= minimumBytes);
        Assert.Equal(0L, governor.Snapshot().OpenHandles);
        return new(value, Metadata(governor), minimumBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias Complete(string kind)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L };
        return Capture(new(limits, CancellationToken.None), limits, kind);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Verify(Alias alias, string kind)
    {
        object value = alias.Value ?? throw new InvalidOperationException("Missing retained carrier.");
        if (kind.StartsWith("check-", StringComparison.Ordinal))
        {
            string detail = value is string text ? text : value is QpdfCheckOutcome check ? check.Detail
                : ((IReadOnlyList<QpdfCheckOutcome>)value).Single(check => check.Name == "object-graph").Detail;
            Assert.Equal("6 objects resolved with 1 streams.", detail);
        }
        else if (kind.StartsWith("diagnostic-", StringComparison.Ordinal))
        {
            string message = value is string text ? text : value is QpdfDiagnostic diagnostic ? diagnostic.Message
                : ((IReadOnlyList<QpdfDiagnostic>)value)[0].Message;
            Assert.Equal(new string('W', 1024), message);
        }
        else if (kind == "integer") { Assert.Equal(123L, Assert.IsType<PdfInteger>(value).Value); }
        else if (kind == "integer-text") { Assert.Equal("00000123", Assert.IsType<string>(value)); }
        else if (kind == "dictionary-keys") { Assert.Equal(new byte[] { 65 }, Assert.Single((IEnumerable<PdfName>)value).RawBytes); }
        else { Assert.Equal(new byte[] { 65, 66, 67 }, Assert.IsType<byte[]>(value)); }
    }

    [Theory]
    [MemberData(nameof(Carriers))]
    public void CompletedJobAliasesDoNotKeepGovernorAndLedgerMetadataAlive(string kind)
    {
        Alias alias = Complete(kind);
        Collect();
        Assert.All(alias.Metadata, reference => Assert.False(reference.IsAlive));
        Verify(alias, kind);
        GC.KeepAlive(alias);
    }

    [Theory]
    [MemberData(nameof(Carriers))]
    public void LiveGovernorKeepsAliasesFundedUntilTheyRetire(string kind)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        Alias alias = Capture(governor, limits, kind);
        Collect(governor);
        Assert.True(alias.Metadata[0].IsAlive);
        Assert.True(alias.Metadata[1].IsAlive);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= alias.MinimumBytes);
        Verify(alias, kind);
        alias.Value = null;
        Collect(governor);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.All(alias.Metadata.Skip(1), reference => Assert.False(reference.IsAlive));
        GC.KeepAlive(governor);
    }

    [Fact]
    public void DisposingOneReaderPreservesAliasesAndAdmissionDuringTheNextReader()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        Alias first = Capture(governor, limits, "check-detail");
        Collect(governor);
        Alias second = Capture(governor, limits, "check-record");
        Collect(governor);
        Verify(first, "check-detail");
        Verify(second, "check-record");
        Assert.True(governor.Snapshot().LiveMemoryBytes >= first.MinimumBytes + second.MinimumBytes);
        first.Value = null;
        Collect(governor);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= second.MinimumBytes);
        Verify(second, "check-record");
        second.Value = null;
        Collect(governor);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().OpenHandles);
        GC.KeepAlive(governor);
    }
}
