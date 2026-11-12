using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Security;

public sealed partial class EncryptedSerializerTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ProtectedQdfCountsCiphertextAndRemapsIndirectLengths(bool metadata, bool newline, bool streamed)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] output;
        using (ParserMemoryScope source = ParserMemoryScope.Create(governor))
        using (OutputSecurity security = OutputSecurity.CreateRevision6("reader"u8, "owner"u8, -3388, metadata, governor))
        {
            List<NumberedObject> model = LinearizedModel(governor, source, security);
            PdfSerializer serializer = Serializer(governor, security, model);
            long before = governor.Snapshot().LiveMemoryBytes;
            long count = await WriteQdf(serializer, model, new CountingSink(), newline, streamed, CancellationToken.None);
            MemorySink sink = new(); Assert.Equal(count, await WriteQdf(serializer, model, sink, newline, streamed, CancellationToken.None));
            output = sink.Bytes.ToArray(); string raw = Encoding.Latin1.GetString(output);
            Assert.Contains("%QDF-1.0", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("private title", raw, StringComparison.Ordinal);
            Assert.Equal(!metadata, raw.Contains("private metadata", StringComparison.Ordinal));
            Assert.Contains("/Length 5 0 R", raw, StringComparison.Ordinal);
            Assert.Contains("\n5 0 obj\n32\nendobj", raw, StringComparison.Ordinal);
            Assert.Contains("\n" + (streamed ? "7" : "8") + " 0 obj\n" + (metadata ? "48" : "16") + "\nendobj", raw, StringComparison.Ordinal);
            Assert.Equal(newline ? 0 : 3, raw.Split("%QDF: ignore_newline", StringSplitOptions.None).Length - 1);
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0, governor.Snapshot().LiveMemoryBytes);
        using InMemoryQpdfHost host = new(); QpdfPath path = QpdfPath.From("encrypted-qdf.pdf");
        host.AddInput(path, output, QpdfInputOwnership.Transfer);
        foreach (string password in new[] { "reader", "owner" })
        {
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, path, QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None))
            {
                Assert.Equal(2, document.PageCount);
                Assert.Equal(streamed, document.Engine.XRefEntries.Any(item => item.Value.Kind == XrefEntryKind.Compressed));
                Assert.Equal(metadata, Assert.IsType<QpdfEncryptionInfo>(document.Encryption).EncryptMetadata);
                Assert.Equal("q Q\n"u8.ToArray(), Assert.IsType<PdfStream>(document.GetObject(4)).Data);
                PdfDictionary info = Assert.IsType<PdfDictionary>(document.GetObject(streamed ? 14 : 6));
                Assert.True(info.TryGet(PdfName.FromAscii("Title"), out QpdfObject title));
                Assert.Equal("private title"u8.ToArray(), Assert.IsType<PdfByteString>(title).Value);
                Assert.Equal("private metadata"u8.ToArray(), Assert.IsType<PdfStream>(document.GetObject(streamed ? 6 : 7)).Data);
                Assert.Equal("q 1 0 0 1 1 1 cm Q\n"u8.ToArray(), Assert.IsType<PdfStream>(document.GetObject(streamed ? 10 : 11)).Data);
            }
            QpdfCheckResult check = await Qpdf.CheckAsync(host, path, new QpdfCheckOptions { Password = QpdfPassword.FromText(password) }, QpdfLimits.Default, CancellationToken.None);
            Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed);
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
    public async Task ProtectedQdfKeepsCiphertextAdmittedThroughPendingWrites(string outcome, bool streamed)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor);
        List<NumberedObject> model = LinearizedModel(governor, source, security);
        PdfSerializer serializer = Serializer(governor, security, model);
        PausedSink sink = new(outcome == "fault"); long before = governor.Snapshot().LiveMemoryBytes;
        Task<long> running = WriteQdf(serializer, model, sink, true, streamed, cancellation.Token).AsTask();
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
    public async Task EveryProtectedQdfAdmissionFailureRefunds(bool count, bool streamed)
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
            PdfSerializer serializer = Serializer(governor, security, model);
            IByteSink sink = count ? new CountingSink() : new MemorySink();
            int initial = budget.GrowthCalls; long reserved = governor.Snapshot().LiveMemoryBytes;
            Task<long> Write() => WriteQdf(serializer, model, sink, true, streamed, CancellationToken.None).AsTask();
            if (refuses) { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Write); }
            else { await Write(); }
            Assert.Equal(reserved, governor.Snapshot().LiveMemoryBytes);
            int attempts = budget.GrowthCalls - initial;
            security.Dispose(); source.Dispose(); Assert.Equal(0, budget.ReservedBytes);
            return (initial, attempts);
        }
    }

    private static ValueTask<long> WriteQdf(PdfSerializer serializer, List<NumberedObject> model, IByteSink sink,
        bool newline, bool streamed, CancellationToken token)
        => serializer.SerializeQdfAsync(new(2, 0), model, streamed ? [new[] { 5, 10 }] : null, null, 5, new byte[16], new byte[16],
            new(true, false), newline, sink, token);
}
