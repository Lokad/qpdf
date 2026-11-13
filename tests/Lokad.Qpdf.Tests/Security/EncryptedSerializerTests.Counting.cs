using System.Reflection;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Security;

public sealed partial class EncryptedSerializerTests
{
    [Theory]
    [InlineData("classic")]
    [InlineData("deflate")]
    [InlineData("qdf")]
    [InlineData("qdf-stream")]
    public async Task SizeOnlyPassDoesNotReadThePayloadKey(string mode)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope source = ParserMemoryScope.Create(governor))
        using (OutputSecurity security = OutputSecurity.CreateRevision6("reader"u8, "owner"u8, -4, false, governor))
        {
            List<NumberedObject> model = LinearizedModel(governor, source, security);
            // Withhold only our own context's key. Any object-key derivation or
            // payload cipher call must fail; the positive emission control proves it.
            FieldInfo keyField = typeof(OutputSecurity).GetField("_key", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Missing owned payload key.");
            object? key = keyField.GetValue(security);
            Assert.NotNull(key);
            foreach (int bytes in new[] { 1, 65537 })
            {
                PdfStream original = Assert.IsType<PdfStream>(model[3].Value);
                model[3] = new(4, new PdfStream(original.Dictionary, new byte[bytes]));
                long before = governor.Snapshot().LiveMemoryBytes;
                long count;
                try
                {
                    keyField.SetValue(security, null);
                    count = await WriteCopied(mode, security, model, new byte[16], governor, new CountingSink(), CancellationToken.None);
                    await Assert.ThrowsAsync<ObjectDisposedException>(() => WriteCopied(mode, security, model,
                        new byte[16], governor, new MemorySink(), CancellationToken.None).AsTask());
                }
                finally { keyField.SetValue(security, key); }
                MemorySink sink = new();
                Assert.Equal(count, await WriteCopied(mode, security, model, new byte[16], governor, sink, CancellationToken.None));
                Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
            }
        }
        Assert.Equal(0, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task OrdinaryObjectCountsMatchCiphertextAtPaddingBoundaries()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope source = ParserMemoryScope.Create(governor))
        using (OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor))
        {
            List<NumberedObject> model = Model(governor, source, security);
            PdfSerializer serializer = Serializer(governor, security, model);
            foreach (int length in new[] { 0, 1, 15, 16, 17, 31, 32, 33 })
            {
                byte[] bytes = Enumerable.Range(0, length).Select(index => (byte)(index * 29)).ToArray();
                PdfDictionary signature = new(new Dictionary<PdfName, QpdfObject>
                {
                    [PdfName.FromAscii("Type")] = PdfName.FromAscii("Sig"),
                    [PdfName.FromAscii("Contents")] = new PdfByteString(bytes, true),
                    [PdfName.FromAscii("Reason")] = new PdfByteString(bytes, false),
                });
                PdfArray direct = new([new PdfByteString(bytes, false), new PdfByteString(bytes, true), signature]);
                foreach (QpdfObject value in new QpdfObject[] { direct, new PdfStream(signature, bytes) })
                {
                    CountingSink counter = new(); MemorySink sink = new();
                    await serializer.AppendNumberedObjectAsync(4, value, counter, true, CancellationToken.None);
                    await serializer.AppendNumberedObjectAsync(4, value, sink, true, CancellationToken.None);
                    Assert.Equal(sink.Length, counter.Length);
                }
            }
        }
        Assert.Equal(0, governor.Snapshot().LiveMemoryBytes);
    }
}
