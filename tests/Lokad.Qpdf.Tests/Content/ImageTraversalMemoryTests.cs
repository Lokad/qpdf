using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Image;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ImageTraversalMemoryTests
{
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
                if (bytes >= 16384) { cancellation.Cancel(); }
                return admitted;
            }
            public void Dispose() => inner.Dispose();
        }
    }

    private static PdfDictionary Dict(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));

    private static DocumentEngine Engine()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary trailer = Dict();
        using ParserMemoryScope fixtureStorage = ParserMemoryScope.Create(governor);
        Dictionary<int, XrefEntry> fixtureEntries = ParserContainers.CreateDictionary<int, XrefEntry>(fixtureStorage);
        using ParserMemoryScope fixtureValues = ParserMemoryScope.Create(governor);
        List<PdfDictionary> fixtureTrailers = ParserContainers.CreateList<PdfDictionary>(fixtureStorage, 1, IntPtr.Size);
        fixtureTrailers.Add(trailer);
        XRefTable fixtureTable = XRefTable.CreateOwned(0, fixtureEntries, trailer, fixtureTrailers,
            governor, fixtureStorage, fixtureValues, false);
        DocumentEngine fixtureEngine;
        try
        {
            fixtureStorage.Reserve(32L);
            fixtureEngine = DocumentEngine.CreateOwned(ReadOnlyMemory<byte>.Empty, fixtureTable, QpdfLimits.Default,
                governor, new(governor), QpdfRecoveryMode.Strict, 32L, valuesEscape: true, hostInput: false, snapshotBorrow: null);
            fixtureStorage.Retain();
        }
        catch { fixtureTable.Dispose(); throw; }
        return fixtureEngine;
    }

    // The pool belongs to the construction governor. Only traversal scratch is
    // under test, so a large preowned graph cannot hide a missing visit-set charge.
    private static GraphCopier Forms(DocumentEngine engine, int count, ResourceGovernor construction, ParserMemoryScope memory)
    {
        GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 1, memory);
        Dictionary<PdfName, QpdfObject> links = new(count);
        PdfStream form = new(Dict(("Subtype", PdfName.FromAscii("Form"))), []);
        for (int index = 0; index < count; index++)
        {
            int id = index + 2;
            copier.AddIndirectObjectWithId(form, id); links.Add(PdfName.FromAscii("F" + index), new PdfReference(id, 0));
        }
        copier.AddIndirectObjectWithId(Dict(("Resources", Dict(("XObject", new PdfDictionary(links))))), 1);
        return copier;
    }

    private static void Apply(GraphCopier copier, ResourceGovernor governor, CancellationToken token)
        => ImageOptimizer.Apply([(copier, 1)], new(true, 0, 0, 0, false, null), QpdfLimits.Default, governor, new(governor), token);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeVisitSetRefusesAndRefundsWithoutReleasingEarlierStorage(bool shared)
    {
        using DocumentEngine engine = Engine();
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copyMemory = ParserMemoryScope.Create(construction);
        GraphCopier copier = Forms(engine, 4096, construction, copyMemory);
        RecordingMemoryBudget root = new(4096);
        using IMemoryScope caller = root.CreateScope();
        QpdfLimits limits = shared ? QpdfLimits.Default : QpdfLimits.Default with { MaxMemoryBytes = 4096 };
        ResourceGovernor governor = shared ? new(limits, CancellationToken.None, new MemoryHost(caller)) : new(limits, CancellationToken.None);
        using (ParserMemoryScope prior = ParserMemoryScope.Create(governor))
        {
            prior.Reserve(17); long baseline = governor.Snapshot().LiveMemoryBytes;
            if (shared) { Assert.Throws<QpdfMemoryAdmissionException>(() => Apply(copier, governor, CancellationToken.None)); }
            else { Assert.Throws<QpdfLimitException>(() => Apply(copier, governor, CancellationToken.None)); }
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(4097, copier.CopiedObjects.Count);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public void CancellationDuringVisitSetGrowthRefundsScratch()
    {
        using DocumentEngine engine = Engine();
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copyMemory = ParserMemoryScope.Create(construction);
        GraphCopier copier = Forms(engine, 4096, construction, copyMemory);
        RecordingMemoryBudget root = new(1048576);
        using IMemoryScope caller = root.CreateScope(); using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token,
            new MemoryHost(new CancelingGrowth(caller, cancellation)));
        Assert.ThrowsAny<OperationCanceledException>(() => Apply(copier, governor, CancellationToken.None));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CyclicSharedFormsKeepResourceIdentitiesAndOptimizeNestedImages(bool indirectResources)
    {
        string resources = indirectResources ? "7 0 R" : "<< /XObject 8 0 R >>";
        byte[] samples = new byte[128 * 128]; new Random(42).NextBytes(samples);
        byte[] pdf = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources " + resources + " >>", null),
            (4, "<< /Subtype /Form /BBox [0 0 10 10] /Resources << /XObject << /Self 4 0 R /Other 5 0 R /Im 6 0 R >> >> >>", []),
            (5, "<< /Subtype /Form /BBox [0 0 10 10] /Resources 7 0 R >>", []),
            (6, "<< /Subtype /Image /Width 128 /Height 128 /BitsPerComponent 8 /ColorSpace /DeviceGray >>", samples),
            (7, "<< /XObject 8 0 R >>", null),
            (8, "<< /First 4 0 R /Shared 4 0 R /Second 5 0 R /Invalid null >>", null),
        ], 1);
        InMemoryQpdfHost host = new(QpdfLimits.Default, LokadImageJpegCodec.Instance);
        host.AddInput(QpdfPath.From("input.pdf"), pdf, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["input.pdf", "--optimize-images", "--oi-min-width=0", "--oi-min-height=0", "--oi-min-area=0",
                "--remove-unreferenced-resources=no", "--compress-streams=n", "output.pdf"]), CancellationToken.None);
        Assert.True(result.ExitCode != QpdfExitCode.Error, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.True(host.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] output));
        Assert.Contains("/DCTDecode", System.Text.Encoding.Latin1.GetString(output), StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
        host.AddInput(QpdfPath.From("reread.pdf"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread.pdf"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
    }
}
