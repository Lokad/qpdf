using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Security;

public sealed partial class EncryptedSerializerTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    public async Task EncryptedLinearizationReplaysExactOffsetsAndAuthenticates(bool metadata, bool compressHints, bool capture, bool streamed)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] output;
        using (ParserMemoryScope source = ParserMemoryScope.Create(governor))
        using (OutputSecurity security = OutputSecurity.CreateRevision6("reader"u8, "owner"u8, -3388, metadata, governor))
        {
            List<NumberedObject> model = LinearizedModel(governor, source, security);
            LinearizedWriter writer = new(QpdfLimits.Default, governor, security, 7, 6);
            long before = governor.Snapshot().LiveMemoryBytes;
            CountingSink counter = new();
            long expected = await WriteLinearized(writer, model, streamed, capture, compressHints, counter, null, CancellationToken.None);
            MemorySink sink = new(); MemorySink? pass1 = capture ? new() : null;
            Assert.Equal(expected, await WriteLinearized(writer, model, streamed, capture, compressHints, sink, pass1, CancellationToken.None));
            if (pass1 is not null) { Assert.InRange(pass1.Length, 1, expected - 1); }
            output = sink.Bytes.ToArray();
            string raw = Encoding.Latin1.GetString(output);
            Assert.DoesNotContain("private title", raw, StringComparison.Ordinal);
            Assert.Equal(!metadata, raw.Contains("private metadata", StringComparison.Ordinal));
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0, governor.Snapshot().LiveMemoryBytes);
        using InMemoryQpdfHost host = new(); QpdfPath path = QpdfPath.From("linearized.pdf");
        host.AddInput(path, output, QpdfInputOwnership.Transfer);
        foreach (string password in new[] { "reader", "owner" })
        {
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, path, QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None))
            {
                Assert.Equal(2, document.PageCount);
                Assert.Equal(streamed, document.Engine.XRefEntries.Any(item => item.Value.Kind == XrefEntryKind.Compressed));
                QpdfObject Get(PdfDictionary dictionary, string name)
                { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return value; }
                QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
                PdfDictionary root = Assert.IsType<PdfDictionary>(Resolve(Get(document.Engine.Trailer, "Root")));
                PdfDictionary info = Assert.IsType<PdfDictionary>(Resolve(Get(document.Engine.Trailer, "Info")));
                Assert.Equal("private title"u8.ToArray(), Assert.IsType<PdfByteString>(Get(info, "Title")).Value);
                Assert.Equal("private metadata"u8.ToArray(), Assert.IsType<PdfStream>(Resolve(Get(root, "Metadata"))).Data);
                PdfDictionary pages = Assert.IsType<PdfDictionary>(Resolve(Get(root, "Pages")));
                PdfArray kids = Assert.IsType<PdfArray>(Get(pages, "Kids"));
                for (int index = 0; index < 2; index++)
                {
                    PdfDictionary page = Assert.IsType<PdfDictionary>(Resolve(kids[index]));
                    PdfStream content = Assert.IsType<PdfStream>(Resolve(Get(page, "Contents")));
                    Assert.Equal(index == 0 ? "q Q\n"u8.ToArray() : "q 1 0 0 1 1 1 cm Q\n"u8.ToArray(), content.Data);
                }
            }
            QpdfCheckResult check = await Qpdf.CheckAsync(host, path, new QpdfCheckOptions { Password = QpdfPassword.FromText(password) }, QpdfLimits.Default, CancellationToken.None);
            Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed);
            Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(check.Checks, item => item.Name == "linearization").Status);
        }
        await Assert.ThrowsAsync<QpdfSecurityException>(() => QpdfDocument.OpenAsync(host, path,
            QpdfPassword.FromText("wrong"), QpdfLimits.Default, CancellationToken.None).AsTask());
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("complete", false)]
    [InlineData("fault", false)]
    [InlineData("cancel", false)]
    [InlineData("complete", true)]
    [InlineData("fault", true)]
    [InlineData("cancel", true)]
    public async Task ProtectedLinearizationRefundsAfterPendingSinkCompletion(string outcome, bool streamed)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor);
        List<NumberedObject> model = LinearizedModel(governor, source, security);
        LinearizedWriter writer = new(QpdfLimits.Default, governor, security, 7, 6);
        PausedSink sink = new(outcome == "fault"); long before = governor.Snapshot().LiveMemoryBytes;
        Task<long> running = WriteLinearized(writer, model, streamed, true, true, sink, null, cancellation.Token).AsTask();
        try
        {
            await sink.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(running.IsCompleted); Assert.True(governor.Snapshot().LiveMemoryBytes > before);
            if (outcome == "cancel") { cancellation.Cancel(); }
        }
        finally
        {
            sink.Resume.TrySetResult();
            try { await running; }
            catch (IOException) when (outcome == "fault") { }
            catch (OperationCanceledException) when (outcome == "cancel") { }
        }
        if (outcome == "fault") { await Assert.ThrowsAsync<IOException>(() => running); }
        else if (outcome == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task EveryProtectedLinearizedWriterAdmissionFailureRefunds(bool capture, bool streamed)
    {
        (int before, int growths) = await Run(new(16 * 1024 * 1024), false);
        for (int growth = 1; growth <= growths; growth++)
        { _ = await Run(new(16 * 1024 * 1024) { RefuseOnGrowth = before + growth }, true); }

        async Task<(int Before, int Growths)> Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            using ParserMemoryScope source = ParserMemoryScope.Create(governor);
            using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor);
            List<NumberedObject> model = LinearizedModel(governor, source, security);
            LinearizedWriter writer = new(QpdfLimits.Default, governor, security, 7, 6);
            MemorySink sink = new(); MemorySink? pass1 = capture ? new() : null;
            int initial = budget.GrowthCalls; long reserved = governor.Snapshot().LiveMemoryBytes;
            Task<long> Write() => WriteLinearized(writer, model, streamed, true, true, sink, pass1, CancellationToken.None).AsTask();
            if (refuses) { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Write); }
            else { await Write(); }
            // The shared host reservation retains bounded block slack between
            // phases; live estimates must refund exactly before source cleanup.
            Assert.Equal(reserved, governor.Snapshot().LiveMemoryBytes);
            int attempts = budget.GrowthCalls - initial;
            security.Dispose(); source.Dispose(); Assert.Equal(0, budget.ReservedBytes);
            return (initial, attempts);
        }
    }

    private static List<NumberedObject> LinearizedModel(ResourceGovernor governor, ParserMemoryScope source, OutputSecurity security)
    {
        List<NumberedObject> model = Model(governor, source, security);
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
        PdfDictionary Dictionary(string text) => Assert.IsType<PdfDictionary>(parser.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(text)), 0, source));
        model[1] = new(2, Dictionary("<< /Type /Pages /Kids [3 0 R 8 0 R] /Count 2 >>"));
        model[2] = new(3, Dictionary("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /Font << /F1 10 0 R >> >> >>"));
        model[3] = new(4, new PdfStream(Dictionary("<< /F 11 0 R /Note (content header) >>"), "q Q\n"u8.ToArray()));
        model.Add(new(8, Dictionary("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 9 0 R /Resources << /Font << /F1 10 0 R >> >> >>")));
        model.Add(new(9, new PdfStream(Dictionary("<< >>"), "q 1 0 0 1 1 1 cm Q\n"u8.ToArray())));
        model.Add(new(10, Dictionary("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")));
        model.Add(new(11, PdfNull.Instance));
        return model;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InvalidProtectedLinearizationModelsFailBeforeWriting(bool missingDictionary, bool streamed)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor);
        List<NumberedObject> model = LinearizedModel(governor, source, security);
        LinearizedWriter writer = new(QpdfLimits.Default, governor, security, 7, missingDictionary ? 6 : 100);
        MemorySink sink = new();
        if (missingDictionary) { model.RemoveAll(item => item.Number == 7); }
        await Assert.ThrowsAsync<QpdfException>(() => WriteLinearized(writer, model, streamed, true, false, sink, null, CancellationToken.None).AsTask());
        Assert.Equal(0, sink.Length);
    }

    private static ValueTask<long> WriteLinearized(LinearizedWriter writer, List<NumberedObject> model, bool streamed,
        bool newline, bool compress, IByteSink sink, IByteSink? pass1, CancellationToken token)
        => streamed
            ? writer.WriteLinearizedStreamedAsync(new(2, 0), model, [new[] { 5, 10 }], 5,
                new byte[16], new byte[16], newline, compress, null, sink, pass1, token)
            : writer.WriteLinearizedAsync(new(2, 0), model, 5, new byte[16], new byte[16],
                newline, compress, null, sink, pass1, token);

    [Fact]
    public async Task EmptyProtectedLinearizedGroupsRetainEncryptionDuringClassicFallback()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] output;
        using (ParserMemoryScope source = ParserMemoryScope.Create(governor))
        using (OutputSecurity security = OutputSecurity.CreateRevision6("reader"u8, "owner"u8, -4, true, governor))
        {
            List<NumberedObject> model = LinearizedModel(governor, source, security);
            LinearizedWriter writer = new(QpdfLimits.Default, governor, security, 7, 6);
            MemorySink sink = new();
            await writer.WriteLinearizedStreamedAsync(new(2, 0), model, [new[] { 1, 2, 3, 7, 8 }], 5,
                new byte[16], new byte[16], true, true, null, sink, null, CancellationToken.None);
            output = sink.Bytes.ToArray();
        }
        Assert.Equal(0, governor.Snapshot().LiveMemoryBytes);
        Assert.DoesNotContain("private title", Encoding.Latin1.GetString(output), StringComparison.Ordinal);
        using InMemoryQpdfHost host = new(); QpdfPath path = QpdfPath.From("fallback.pdf");
        host.AddInput(path, output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, path, QpdfPassword.FromText("reader"), QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(2, document.PageCount); Assert.NotNull(document.Encryption);
        Assert.DoesNotContain(document.Engine.XRefEntries, item => item.Value.Kind == XrefEntryKind.Compressed);
        QpdfCheckResult check = await Qpdf.CheckAsync(host, path, new QpdfCheckOptions { Password = QpdfPassword.FromText("reader") }, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(check.Checks, item => item.Name == "linearization").Status);
    }
}
