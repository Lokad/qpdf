using System.Text;
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
    [MemberData(nameof(Revision6EncryptionTests.Vectors), MemberType = typeof(Revision6EncryptionTests))]
    public async Task CopiedNativeProtectionSurvivesSerializationAndSourceClosure(string file, string dictionary,
        string userHex, string ownerHex, string keyHex)
    {
        Assert.NotEmpty(file); Assert.NotEmpty(keyHex);
        foreach (string passwordHex in new[] { userHex, ownerHex })
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            OutputSecurity output;
            using (ParserMemoryScope input = ParserMemoryScope.Create(governor))
            {
                PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
                PdfDictionary encryption = Assert.IsType<PdfDictionary>(parser.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(dictionary)), 0, input));
                _ = DocumentSecurity.TryCreate(encryption, [], Convert.FromHexString(passwordHex), governor, out DocumentSecurity? authenticated);
                using (authenticated)
                {
                    Assert.NotNull(authenticated); output = authenticated.PrepareOutput("source identifier"u8);
                }
            }
            MemorySink sink = new();
            using (output)
            using (ParserMemoryScope modelMemory = ParserMemoryScope.Create(governor))
            {
                List<NumberedObject> model = Model(governor, modelMemory, output);
                PdfSerializer serializer = Serializer(governor, output, model);
                byte[] identifier = Assert.IsType<ReadOnlyMemory<byte>>(output.PreservedIdentifier).ToArray();
                ValueTask<long> WriteCopied(IByteSink target) => serializer.SerializeAsync(new(2, 0), model, 5,
                    identifier, "new identifier"u8.ToArray(), true, target, CancellationToken.None);
                long count = await WriteCopied(new CountingSink());
                Assert.Equal(count, await WriteCopied(sink));
            }
            Assert.Equal(0, governor.Snapshot().LiveMemoryBytes);
            using InMemoryQpdfHost host = new(); QpdfPath path = QpdfPath.From("copy.pdf");
            host.AddInput(path, sink.Bytes.ToArray(), QpdfInputOwnership.Transfer);
            foreach (string reopenedPassword in new[] { userHex, ownerHex })
            {
                await using QpdfDocument reopened = await QpdfDocument.OpenAsync(host, path,
                    QpdfPassword.FromBytes(Convert.FromHexString(reopenedPassword)), QpdfLimits.Default, CancellationToken.None);
                Assert.Equal(1, reopened.PageCount);
                Assert.Equal("q Q\n"u8.ToArray(), Assert.IsType<PdfStream>(reopened.GetObject(4)).Data);
                PdfDictionary info = Assert.IsType<PdfDictionary>(reopened.GetObject(5));
                Assert.True(info.TryGet(PdfName.FromAscii("Title"), out QpdfObject title));
                Assert.Equal("private title"u8.ToArray(), Assert.IsType<PdfByteString>(title).Value);
            }
            await Assert.ThrowsAsync<QpdfSecurityException>(() => QpdfDocument.OpenAsync(host, path,
                QpdfPassword.FromText("wrong password"), QpdfLimits.Default, CancellationToken.None).AsTask());
        }
    }
}
