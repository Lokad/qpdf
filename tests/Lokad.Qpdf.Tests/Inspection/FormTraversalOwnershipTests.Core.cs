using Lokad.Qpdf.Tests.Foundation;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed partial class FormTraversalOwnershipTests
{
    private OwnershipBatch.Observation? _collection;

    private delegate QpdfCheckOutcome FormCheck(QpdfDocument document, ResourceGovernor governor,
        DiagnosticBag diagnostics, CancellationToken cancellationToken);

    private static readonly FormCheck RunForms = (typeof(DocumentCheck).GetMethod("CheckFormTree",
        BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing form checker."))
        .CreateDelegate<FormCheck>();

    private sealed class Prepared : IDisposable
    {
        internal readonly QpdfLimits Limits = QpdfLimits.Default with { MaxMemoryBytes = 16L * 1024L * 1024L };
        internal readonly ResourceGovernor Governor;
        internal readonly DiagnosticBag Diagnostics;
        internal readonly QpdfDocument Document;
        internal readonly InMemoryQpdfHost Host = new();
        internal readonly QpdfDiagnostic Prior;

        internal Prepared(int widgets)
        {
            Governor = new(Limits, CancellationToken.None);
            Diagnostics = new(Governor);
            Diagnostics.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "prior warning");
            Prior = Assert.Single(Diagnostics.Entries);
            QpdfPath path = QpdfPath.From("forms.pdf");
            Host.AddInput(path, Fixture(widgets), QpdfInputOwnership.Transfer);
            Document = QpdfDocument.OpenCore(Host, path, Limits, Governor, Diagnostics,
                QpdfRecoveryMode.Strict, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            IIndirectResolver resolver = (IIndirectResolver)Document.Engine;
            foreach (int number in Document.Engine.XRefEntries.Keys)
            {
                if (number != 0) { Assert.True(resolver.TryResolve(new PdfReference(number, 0), out _)); }
            }
        }

        internal long Fill(long available)
        {
            long filler = Limits.MaxMemoryBytes - Governor.Snapshot().LiveMemoryBytes - available;
            Governor.ReserveMemory(filler);
            return filler;
        }

        public void Dispose()
        {
            Document.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Assert.Equal(0L, Host.OpenLeaseCount);
            Assert.Equal(0L, Governor.Snapshot().OpenHandles);
        }
    }

    private static byte[] Fixture(int widgets)
    {
        string references = string.Join(' ', Enumerable.Range(4, widgets).Select(number => $"{number} 0 R"));
        List<(int Number, string Dict, byte[]? StreamData)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [" + references + "] >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [" + references + "] >>", null),
        ];
        foreach (int number in Enumerable.Range(4, widgets))
        {
            objects.Add((number, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (F) /Rect [0 0 10 10] >>", null));
        }
        return CheckTests.Assemble(objects, 1);
    }

    [Theory]
    [InlineData(63L)]
    [InlineData(127L)]
    [InlineData(223L)]
    [InlineData(319L)]
    public void ConstructionRejectsBeforeTraversalAndReturnsPartialAdmission(long available)
    {
        using Prepared prepared = new(512);
        long baseline = prepared.Governor.Snapshot().LiveMemoryBytes;
        long filler = prepared.Fill(available);
        QpdfExecutionCounters before = prepared.Governor.Snapshot();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
            RunForms(prepared.Document, prepared.Governor, prepared.Diagnostics, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        QpdfExecutionCounters after = prepared.Governor.Snapshot();
        Assert.Equal(before.LiveMemoryBytes, after.LiveMemoryBytes);
        Assert.InRange(after.TotalWork - before.TotalWork, 0L, 32L);
        Assert.Same(prepared.Prior, Assert.Single(prepared.Diagnostics.Entries));
        prepared.Governor.ReleaseMemory(filler);
        Assert.Equal(baseline, prepared.Governor.Snapshot().LiveMemoryBytes);
        QpdfCheckOutcome retry = RunForms(prepared.Document, prepared.Governor, prepared.Diagnostics, CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Passed, retry.Status);
        Assert.Equal("512 field nodes traversed, 512 widgets reached.", retry.Detail);
    }

    [Fact]
    public void ExplicitCancellationReturnsStateAdmissionAndPreservesAcceptedWarnings()
    {
        using Prepared prepared = new(512);
        long filler = prepared.Fill(65536L);
        QpdfExecutionCounters before = prepared.Governor.Snapshot();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            RunForms(prepared.Document, prepared.Governor, prepared.Diagnostics, cancellation.Token));
        QpdfExecutionCounters after = prepared.Governor.Snapshot();
        Assert.Equal(before.LiveMemoryBytes, after.LiveMemoryBytes);
        Assert.True(after.PeakMemoryBytes > before.LiveMemoryBytes);
        Assert.Equal(before.TotalWork, after.TotalWork);
        Assert.Same(prepared.Prior, Assert.Single(prepared.Diagnostics.Entries));
        prepared.Governor.ReleaseMemory(filler);
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
    private static WeakReference RunAndDrop(Prepared prepared)
    {
        QpdfCheckOutcome result = RunForms(prepared.Document, prepared.Governor, prepared.Diagnostics, CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Passed, result.Status);
        Assert.Equal("4 field nodes traversed, 4 widgets reached.", result.Detail);
        return new WeakReference(result.Detail);
    }

    internal async Task RepeatedSuccessfulTraversalRetiresOnlyItsTemporaryAndDiscardedStorage()
    {
        using Prepared prepared = new(4);
        await (_collection ?? throw new InvalidOperationException("Missing stress batch.")).CollectAsync(prepared.Governor, 3, 3, false, true);
        long baseline = prepared.Governor.Snapshot().LiveMemoryBytes;
        for (int iteration = 0; iteration < 32; iteration++)
        {
            WeakReference detail = RunAndDrop(prepared);
            await (_collection ?? throw new InvalidOperationException("Missing stress batch.")).CollectAsync(prepared.Governor, 3, 3, false, true);
            Assert.False(detail.IsAlive);
            Assert.Equal(baseline, prepared.Governor.Snapshot().LiveMemoryBytes);
            Assert.Same(prepared.Prior, Assert.Single(prepared.Diagnostics.Entries));
        }
    }

    internal static IEnumerable<(string Id, Func<OwnershipBatch.Observation, Task> Run)> StressCases()
        => (new object[][] { [] }).Select(row =>
            (OwnershipBatch.Id(nameof(FormTraversalOwnershipTests), nameof(RepeatedSuccessfulTraversalRetiresOnlyItsTemporaryAndDiscardedStorage), row),
            (Func<OwnershipBatch.Observation, Task>)(async observation =>
            {
                FormTraversalOwnershipTests fixture = new() { _collection = observation };
                await fixture.RepeatedSuccessfulTraversalRetiresOnlyItsTemporaryAndDiscardedStorage();
            })));
}
