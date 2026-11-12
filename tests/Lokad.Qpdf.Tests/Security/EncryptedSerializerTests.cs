using System.Text;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Security;

public sealed partial class EncryptedSerializerTests
{
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, true)]
    public async Task OutputCountsExactlyAndAuthenticatesBothPasswords(bool metadata, bool streamed, bool compress, bool newline)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] output;
        using (ParserMemoryScope source = ParserMemoryScope.Create(governor))
        using (OutputSecurity security = OutputSecurity.CreateRevision6("reader"u8, "owner"u8, -3388, metadata, governor))
        {
            List<NumberedObject> model = Model(governor, source, security);
            PdfSerializer serializer = Serializer(governor, security, model);
            ValueTask<long> Serialize(IByteSink target) => streamed
                ? WriteStreamed(serializer, model, target, compress, newline, CancellationToken.None)
                : Write(serializer, model, target);
            CountingSink counter = new(); long expected = await Serialize(counter);
            long before = governor.Snapshot().LiveMemoryBytes;
            MemorySink sink = new(); Assert.Equal(expected, await Serialize(sink)); output = sink.Bytes.ToArray();
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
            string raw = Encoding.Latin1.GetString(output);
            Assert.DoesNotContain("private title", raw, StringComparison.Ordinal);
            Assert.Equal(!metadata, raw.Contains("private metadata", StringComparison.Ordinal));
        }
        Assert.Equal(0, governor.Snapshot().LiveMemoryBytes);
        using InMemoryQpdfHost host = new(); QpdfPath path = QpdfPath.From("encrypted.pdf");
        host.AddInput(path, output, QpdfInputOwnership.Transfer);
        foreach (string password in new[] { "reader", "owner" })
        {
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, path, QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None))
            {
                Assert.Equal(1, document.PageCount); Assert.Equal(metadata, Assert.IsType<QpdfEncryptionInfo>(document.Encryption).EncryptMetadata);
                PdfDictionary info = Assert.IsType<PdfDictionary>(document.GetObject(5));
                Assert.True(info.TryGet(PdfName.FromAscii("Title"), out QpdfObject title));
                Assert.Equal("private title", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(title).Value));
                Assert.True(info.TryGet(PdfName.FromAscii("Nested"), out QpdfObject nested));
                PdfArray values = Assert.IsType<PdfArray>(nested);
                Assert.Equal("inner"u8.ToArray(), Assert.IsType<PdfByteString>(values[0]).Value);
                Assert.Equal(new byte[] { 0, 255, 16 }, Assert.IsType<PdfByteString>(values[1]).Value);
                Assert.Equal("q Q\n"u8.ToArray(), Assert.IsType<PdfStream>(document.GetObject(4)).Data);
                Assert.Equal("private metadata"u8.ToArray(), Assert.IsType<PdfStream>(document.GetObject(6)).Data);
            }
            QpdfCheckResult check = await Qpdf.CheckAsync(host, path, new QpdfCheckOptions { Password = QpdfPassword.FromText(password) }, QpdfLimits.Default, CancellationToken.None);
            Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed);
        }
        await Assert.ThrowsAsync<QpdfSecurityException>(() => QpdfDocument.OpenAsync(host, path,
            QpdfPassword.FromText("wrong"), QpdfLimits.Default, CancellationToken.None).AsTask());
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task MissingEncryptionDictionaryFailsBeforeWritingAnyBytes()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor);
        List<NumberedObject> model = Model(governor, source, security);
        PdfSerializer serializer = Serializer(governor, security, model); model.RemoveAt(model.Count - 1);
        MemorySink sink = new();
        await Assert.ThrowsAsync<QpdfException>(() => Write(serializer, model, sink).AsTask());
        Assert.Equal(0, sink.Length);
    }

    [Fact]
    public async Task QdfEncryptionDictionaryCannotBeCompressed()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor);
        List<NumberedObject> model = Model(governor, source, security);
        PdfSerializer serializer = Serializer(governor, security, model);
        MemorySink sink = new();
        await Assert.ThrowsAsync<QpdfException>(() => serializer.SerializeQdfAsync(new(2, 0), model,
            [new[] { 7 }], null, 5, new byte[16], new byte[16], new(true, false), true, sink, CancellationToken.None).AsTask());
        Assert.Equal(0, sink.Length);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("fault")]
    [InlineData("cancel")]
    public async Task CiphertextStorageStaysAdmittedThroughPendingWrites(string outcome)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor);
        List<NumberedObject> model = Model(governor, source, security);
        PdfSerializer serializer = Serializer(governor, security, model);
        PausedSink sink = new(outcome == "fault");
        long before = governor.Snapshot().LiveMemoryBytes;
        Task<long> running = serializer.SerializeAsync(new(2, 0), model, 5, new byte[16], new byte[16], true, sink, cancellation.Token).AsTask();
        await sink.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(running.IsCompleted); Assert.True(governor.Snapshot().LiveMemoryBytes > before);
            byte[] key = new byte[32];
            Assert.Equal(PasswordAuthentication.Owner, Revision6Encryption.Authenticate(security.Dictionary, "owner"u8, key, governor));
            using System.Security.Cryptography.Aes aes = System.Security.Cryptography.Aes.Create(); aes.SetKey(key);
            Assert.Equal("q Q\n"u8.ToArray(), aes.DecryptCbc(sink.Ciphertext.Span[16..], sink.Ciphertext.Span[..16], System.Security.Cryptography.PaddingMode.PKCS7));
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
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
        else { await running; }
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class PausedSink(bool fail) : IByteSink
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ReadOnlyMemory<byte> Ciphertext;
        public long Length { get; private set; }
        public async ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken token)
        {
            if (chunk.Length == 32 && !Started.Task.IsCompleted)
            {
                Ciphertext = chunk; Started.TrySetResult(); await Resume.Task;
                if (fail) { throw new IOException("Fixture write failure."); }
            }
            token.ThrowIfCancellationRequested(); Length += chunk.Length;
        }
    }

    private static PdfSerializer Serializer(ResourceGovernor governor, OutputSecurity security, List<NumberedObject> model)
        => new(QpdfLimits.Default, governor, security, 7, 6,
            new PoolResolver(model.ToDictionary(item => item.Number, item => item.Value)));

    private static ValueTask<long> Write(PdfSerializer serializer, List<NumberedObject> model, IByteSink sink)
        => serializer.SerializeAsync(new(2, 0), model, 5, new byte[16], new byte[16], true, sink, CancellationToken.None);

    private static List<NumberedObject> Model(ResourceGovernor governor, ParserMemoryScope source, OutputSecurity security)
    {
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
        PdfDictionary Dictionary(string text) => Assert.IsType<PdfDictionary>(parser.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(text)), 0, source));
        return [
            new(1, Dictionary("<< /Type /Catalog /Pages 2 0 R /Metadata 6 0 R >>")),
            new(2, Dictionary("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")),
            new(3, Dictionary("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << >> >>")),
            new(4, new PdfStream(Dictionary("<< >>"), "q Q\n"u8.ToArray())),
            new(5, Dictionary("<< /Title (private title) /Nested [(inner) <00ff10>] >>")),
            new(6, new PdfStream(Dictionary("<< /Type /Metadata /Subtype /XML >>"), "private metadata"u8.ToArray())),
            new(7, security.Dictionary)
        ];
    }

    private sealed class MemorySink : IByteSink
    {
        internal MemoryStream Bytes { get; } = new();
        public long Length => Bytes.Length;
        public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Bytes.Write(chunk.Span); return ValueTask.CompletedTask; }
    }
}
