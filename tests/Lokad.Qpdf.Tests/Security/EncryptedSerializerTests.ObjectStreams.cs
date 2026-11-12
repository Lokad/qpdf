using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Security;

public sealed partial class EncryptedSerializerTests
{
    private static ValueTask<long> WriteStreamed(PdfSerializer serializer, List<NumberedObject> model,
        IByteSink sink, bool compress, bool newline, CancellationToken token)
        => serializer.SerializeStreamedAsync(new(2, 0), model, [new[] { 3, 5 }], compress, null,
            5, new byte[16], new byte[16], newline, sink, token);

    [Theory]
    [InlineData("missing")]
    [InlineData("encryption")]
    [InlineData("signature")]
    [InlineData("timestamp")]
    [InlineData("untyped-signature")]
    public async Task EncryptedObjectStreamPlanRejectsProtectedDictionaryMembersBeforeWriting(string kind)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor);
        List<NumberedObject> model = Model(governor, source, security);
        if (kind == "missing") { model.RemoveAt(model.Count - 1); }
        else if (kind is "signature" or "timestamp" or "untyped-signature")
        {
            string type = kind == "signature" ? "/Type /Sig" : kind == "timestamp" ? "/Type /DocTimeStamp" : "";
            PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
            model[4] = new(5, parser.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(
                "<< " + type + " /Filter /Adobe.PPKLite /ByteRange [0 1 2 3] /Contents <01020304> >>")), 0, source));
        }
        PdfSerializer serializer = Serializer(governor, security, model);
        MemorySink sink = new(); long before = governor.Snapshot().LiveMemoryBytes;
        await Assert.ThrowsAsync<QpdfException>(() => serializer.SerializeStreamedAsync(new(2, 0), model,
            [new[] { kind == "encryption" ? 7 : 5 }], false, null, 5, new byte[16], new byte[16],
            true, sink, CancellationToken.None).AsTask());
        Assert.Equal(0, sink.Length); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, "complete")]
    [InlineData(false, "fault")]
    [InlineData(false, "cancel")]
    [InlineData(true, "complete")]
    [InlineData(true, "fault")]
    [InlineData(true, "cancel")]
    public async Task EncryptedContainerRemainsAdmittedUntilPendingWriteSettles(bool compress, string outcome)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor);
        List<NumberedObject> model = Model(governor, source, security);
        PdfSerializer serializer = Serializer(governor, security, model);
        PausedContainerSink sink = new(outcome == "fault");
        long before = governor.Snapshot().LiveMemoryBytes;
        Task<long> running = WriteStreamed(serializer, model, sink, compress, true, cancellation.Token).AsTask();
        try
        {
            await sink.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(running.IsCompleted); Assert.True(governor.Snapshot().LiveMemoryBytes > before);
            byte[] key = new byte[32];
            Assert.Equal(PasswordAuthentication.Owner, Aes256Encryption.Authenticate(security.Dictionary, "owner"u8, key, governor));
            using Aes aes = Aes.Create(); aes.SetKey(key);
            byte[] plain = aes.DecryptCbc(sink.Ciphertext.Span[16..], sink.Ciphertext.Span[..16], PaddingMode.PKCS7);
            CryptographicOperations.ZeroMemory(key);
            if (compress)
            {
                using MemoryStream input = new(plain); using ZLibStream decoder = new(input, CompressionMode.Decompress);
                using MemoryStream output = new(); decoder.CopyTo(output); plain = output.ToArray();
            }
            string text = Encoding.Latin1.GetString(plain);
            Assert.StartsWith("3 0 5 ", text, StringComparison.Ordinal);
            Assert.Contains("/Title (private title)", text, StringComparison.Ordinal);
            Assert.Contains("/Nested [(inner) <00FF10>]", text, StringComparison.Ordinal);
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
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EveryEncryptedObjectStreamAdmissionFailureRefundsTemporaryStorage(bool compress, bool count)
    {
        (int before, int growths) = await Run(new(16 * 1024 * 1024), false);
        for (int growth = 1; growth <= growths; growth++)
        { _ = await Run(new(16 * 1024 * 1024) { RefuseOnGrowth = before + growth }, true); }

        async Task<(int Before, int Growths)> Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            using (ParserMemoryScope source = ParserMemoryScope.Create(governor))
            using (OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor))
            {
                List<NumberedObject> model = Model(governor, source, security);
                PdfSerializer serializer = Serializer(governor, security, model);
                IByteSink sink = count ? new CountingSink() : new MemorySink();
                int initial = budget.GrowthCalls; long reserved = budget.ReservedBytes;
                if (refuses)
                { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => WriteStreamed(serializer, model, sink, compress, true, CancellationToken.None).AsTask()); }
                else { await WriteStreamed(serializer, model, sink, compress, true, CancellationToken.None); }
                Assert.Equal(reserved, budget.ReservedBytes);
                int attempts = budget.GrowthCalls - initial;
                security.Dispose(); source.Dispose(); Assert.Equal(0, budget.ReservedBytes);
                return (initial, attempts);
            }
        }
    }

    [Fact]
    public async Task CountingUncompressedEncryptedContainerNeedsNoPayloadSizedBuffer()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 128 * 1024 };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor);
        List<NumberedObject> model = Model(governor, source, security);
        // Caller-owned fixture data is deliberately larger than the entire working allowance.
        model[4] = new(5, new PdfByteString(new byte[1024 * 1024], true));
        PdfSerializer serializer = Serializer(governor, security, model);
        long before = governor.Snapshot().LiveMemoryBytes;
        long counted = await WriteStreamed(serializer, model, new CountingSink(), false, true, CancellationToken.None);
        Assert.True(counted > 2 * 1024 * 1024);
        Assert.True(governor.Snapshot().PeakMemoryBytes < limits.MaxMemoryBytes);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class PausedContainerSink(bool fail) : IByteSink
    {
        private bool _container;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ReadOnlyMemory<byte> Ciphertext;
        public long Length { get; private set; }

        public async ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken token)
        {
            if (_container && !Started.Task.IsCompleted)
            {
                Ciphertext = chunk; Started.TrySetResult(); await Resume.Task;
                if (fail) { throw new IOException("Fixture container write failure."); }
            }
            _container |= chunk.Span.IndexOf("/Type /ObjStm"u8) >= 0;
            token.ThrowIfCancellationRequested(); Length += chunk.Length;
        }
    }
}
