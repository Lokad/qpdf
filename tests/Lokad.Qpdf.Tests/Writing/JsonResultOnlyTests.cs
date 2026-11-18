using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonResultOnlyTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input");
    private static readonly byte[] Payload = [0, 255, 17, 10, 23];

    private sealed class ObservedHost(InMemoryQpdfHost storage, bool writable) : IQpdfHost
    {
        internal readonly List<QpdfPath> Outputs = [];
        internal int Opens;
        internal QpdfPath? FailingCommit { get; init; }
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        { Opens++; return storage.OpenInputAsync(path, token); }
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            Outputs.Add(path);
            if (!writable) { throw new InvalidOperationException("Unexpected publication."); }
            IQpdfOutputTransaction transaction = await storage.CreateOutputAsync(path, token);
            return FailingCommit is QpdfPath target && target.Equals(path) ? new CommitFault(transaction) : transaction;
        }

        private sealed class CommitFault(IQpdfOutputTransaction inner) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token) => inner.WriteAsync(data, token);
            public ValueTask CommitAsync(CancellationToken token) => throw new QpdfHostException(Path, QpdfHostFailureReason.Unsupported);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private static InMemoryQpdfHost Storage()
    {
        InMemoryQpdfHost storage = new();
        storage.AddInput(Input, PdfTestDocument.Assemble(PdfTestDocument.SinglePage("<< /Length 5 >>", Payload), 1), QpdfInputOwnership.Transfer);
        return storage;
    }

    [Theory]
    [InlineData(1, "none")]
    [InlineData(1, "inline")]
    [InlineData(2, "none")]
    [InlineData(2, "inline")]
    public async Task ReportsMatchPublishingWithoutTouchingOutputCapability(int version, string mode)
    {
        using InMemoryQpdfHost storage = Storage();
        ObservedHost host = new(storage, false);
        QpdfJsonOptions options = new(version, [], mode, null, [], false);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, Input, options, CancellationToken.None);
        Assert.Empty(host.Outputs); Assert.Equal(1, host.Opens); Assert.Equal(0L, storage.OpenLeaseCount);
        using JsonDocument json = JsonDocument.Parse(result.Json);
        Assert.Equal(1, json.RootElement.GetProperty("pages").GetArrayLength());
        // V1 keeps its existing descriptor-only stream representation.
        if (mode == "inline" && version == 2) { Assert.Contains(Convert.ToBase64String(Payload), result.Json, StringComparison.Ordinal); }
        else { Assert.DoesNotContain(Convert.ToBase64String(Payload), result.Json, StringComparison.Ordinal); }
        QpdfJsonResult published = await Qpdf.GetDocumentJsonAsync(storage, Input, QpdfPath.From("report"), options, CancellationToken.None);
        Assert.Equal(published.Json, result.Json); Assert.Equal(published.Warnings, result.Warnings);
        Assert.True(storage.TryGetOutput(QpdfPath.From("report"), out byte[] bytes));
        Assert.Equal(result.Json, System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("input-prefix")]
    public async Task FileModeNeedsAPrefixAndPublishesOnlySelectedSidecars(string? prefix)
    {
        using InMemoryQpdfHost storage = Storage();
        ObservedHost host = new(storage, true);
        QpdfJsonOptions options = new(2, ["qpdf"], "file", prefix, ["4"], false);
        if (prefix is null)
        {
            await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.GetDocumentJsonAsync(host, Input, options, CancellationToken.None).AsTask());
            Assert.Empty(host.Outputs); Assert.Equal(0, host.Opens);
        }
        else
        {
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, Input, options, QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(QpdfPath.From("input-prefix-4"), Assert.Single(host.Outputs));
            Assert.True(storage.TryGetOutput(host.Outputs[0], out byte[] bytes)); Assert.Equal(Payload, bytes);
            using JsonDocument json = JsonDocument.Parse(result.Json);
            Assert.Equal("input-prefix-4", json.RootElement.GetProperty("qpdf")[1].GetProperty("obj:4 0 R").GetProperty("stream").GetProperty("datafile").GetString());
        }
        Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Fact]
    public async Task ExpandedSidecarCannotReplaceTheSource()
    {
        using InMemoryQpdfHost storage = Storage();
        QpdfPath source = QpdfPath.From("side-4");
        storage.AddInput(source, PdfTestDocument.Assemble(PdfTestDocument.SinglePage("<< /Length 5 >>", Payload), 1), QpdfInputOwnership.Transfer);
        ObservedHost host = new(storage, true);
        await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.GetDocumentJsonAsync(host, source,
            new QpdfJsonOptions(2, ["qpdf"], "file", "side", ["4"], false), CancellationToken.None).AsTask());
        Assert.Empty(host.Outputs); Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Fact]
    public async Task FailedSecondSidecarKeepsFirstCommitAndPriorSecondTarget()
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(Input, PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R /Private 5 0 R >>", null),
            (4, "<< /Length 5 >>", Payload), (5, "<< /Length 2 >>", new byte[] { 7, 11 })], 1), QpdfInputOwnership.Transfer);
        QpdfPath second = QpdfPath.From("side-5");
        byte[] prior = [41, 43, 47];
        await using (IQpdfOutputTransaction seed = await storage.CreateOutputAsync(second, CancellationToken.None))
        { await seed.WriteAsync(prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        ObservedHost host = new(storage, true) { FailingCommit = second };
        await Assert.ThrowsAsync<QpdfHostException>(() => Qpdf.GetDocumentJsonAsync(host, Input,
            new QpdfJsonOptions(2, ["qpdf"], "file", "side", ["4", "5"], false), CancellationToken.None).AsTask());
        Assert.Equal(new[] { QpdfPath.From("side-4"), second }, host.Outputs);
        Assert.True(storage.TryGetOutput(host.Outputs[0], out byte[] first)); Assert.Equal(Payload, first);
        Assert.True(storage.TryGetOutput(second, out byte[] retained)); Assert.Equal(prior, retained);
        Assert.Equal(0L, storage.OpenLeaseCount);
    }
}
