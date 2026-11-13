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
    private static readonly string[] CopyModes = ["classic", "raw", "deflate", "linear", "linear-flate", "linear-stream", "linear-stream-flate", "qdf", "qdf-stream"];

    public static IEnumerable<object[]> Revision4OutputCases()
    {
        foreach (object[] vector in Revision4EncryptionTests.Vectors())
        foreach (string mode in CopyModes) { yield return [.. vector, mode]; }
    }

    [Theory]
    [MemberData(nameof(Revision4OutputCases))]
    public async Task Revision4UsesFinalObjectNumbersAndRetainsBothPasswords(string file, string dictionary,
        string userHex, string ownerHex, string keyHex, string idHex, string mode)
    {
        Assert.NotEmpty(file); Assert.NotEmpty(keyHex);
        foreach (string passwordHex in new[] { userHex, ownerHex })
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            MemorySink sink = new(); byte[] identifier = Convert.FromHexString(idHex);
            using (OutputSecurity security = CopyRevision4(dictionary, identifier, Convert.FromHexString(passwordHex), governor))
            using (ParserMemoryScope source = ParserMemoryScope.Create(governor))
            {
                List<NumberedObject> model = LinearizedModel(governor, source, security);
                long before = governor.Snapshot().LiveMemoryBytes;
                long count = await WriteCopied(mode, security, model, identifier, governor, new CountingSink(), CancellationToken.None);
                Assert.Equal(count, await WriteCopied(mode, security, model, identifier, governor, sink, CancellationToken.None));
                Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
                Assert.DoesNotContain("private title", Encoding.Latin1.GetString(sink.Bytes.ToArray()), StringComparison.Ordinal);
            }
            Assert.Equal(0, governor.Snapshot().LiveMemoryBytes);
            using InMemoryQpdfHost host = new(); QpdfPath path = QpdfPath.From("preserved.pdf");
            host.AddInput(path, sink.Bytes.ToArray(), QpdfInputOwnership.Transfer);
            foreach (string reopenedHex in new[] { userHex, ownerHex })
            {
                QpdfPassword password = QpdfPassword.FromBytes(Convert.FromHexString(reopenedHex));
                await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, path, password, QpdfLimits.Default, CancellationToken.None))
                {
                    Assert.Equal(2, document.PageCount);
                    Assert.Equal(4, Assert.IsType<QpdfEncryptionInfo>(document.Encryption).Revision);
                    Assert.Equal(mode.Contains("stream", StringComparison.Ordinal) || mode is "raw" or "deflate",
                        document.Engine.XRefEntries.Any(item => item.Value.Kind == XrefEntryKind.Compressed));
                    Assert.Equal(identifier, EncryptionDictionary.ReadIdentifier(document.Engine.Trailer, governor).ToArray());
                    QpdfObject Get(PdfDictionary header, string name)
                    { Assert.True(header.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return value; }
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
                QpdfCheckResult check = await Qpdf.CheckAsync(host, path, new QpdfCheckOptions { Password = password }, QpdfLimits.Default, CancellationToken.None);
                Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed);
                if (mode.StartsWith("linear", StringComparison.Ordinal))
                { Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(check.Checks, item => item.Name == "linearization").Status); }
            }
            await Assert.ThrowsAsync<QpdfSecurityException>(() => QpdfDocument.OpenAsync(host, path,
                QpdfPassword.FromText("wrong password"), QpdfLimits.Default, CancellationToken.None).AsTask());
            Assert.Equal(0, host.OpenLeaseCount);
        }
    }

    [Fact]
    public async Task ChangedPreservedIdentifierIsRejectedBeforeEveryOutputMode()
    {
        object[] vector = Revision4EncryptionTests.Vectors().First();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] identifier = Convert.FromHexString((string)vector[5]);
        using OutputSecurity security = CopyRevision4((string)vector[1], identifier, "reader"u8, governor);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        List<NumberedObject> model = LinearizedModel(governor, source, security);
        identifier[0] ^= 1;
        foreach (string mode in CopyModes)
        {
            MemorySink sink = new(); long before = governor.Snapshot().LiveMemoryBytes;
            await Assert.ThrowsAsync<QpdfException>(() => WriteCopied(mode, security, model, identifier, governor, sink, CancellationToken.None).AsTask());
            Assert.Equal(0, sink.Length); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        }
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("deflate")]
    [InlineData("linear-stream-flate")]
    [InlineData("qdf-stream")]
    public async Task Revision4EmissionPreservesAdmissionFailures(string mode)
    {
        (int before, int growths) = await Run(new(4194304), false);
        Assert.True(growths > 0);
        // Classic owns the legacy-key sweep; other writer storage is swept by
        // the R6 physical writer owners. Keep their early/late legacy boundaries.
        IEnumerable<int> refusals = mode == "classic" ? Enumerable.Range(1, growths) : new[] { 1, growths };
        foreach (int growth in refusals)
        { _ = await Run(new(4194304) { RefuseOnGrowth = before + growth }, true); }

        async Task<(int Before, int Growths)> Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            object[] vector = Revision4EncryptionTests.Vectors().First(); byte[] identifier = Convert.FromHexString((string)vector[5]);
            using OutputSecurity security = CopyRevision4((string)vector[1], identifier, "reader"u8, governor);
            using ParserMemoryScope source = ParserMemoryScope.Create(governor);
            List<NumberedObject> model = LinearizedModel(governor, source, security);
            PdfStream content = Assert.IsType<PdfStream>(model[3].Value);
            model[3] = new(4, new PdfStream(content.Dictionary, new byte[65537]));
            int before = budget.GrowthCalls; long live = governor.Snapshot().LiveMemoryBytes;
            Task<long> WriteOutput() => WriteCopied(mode, security, model, identifier, governor, new MemorySink(), CancellationToken.None).AsTask();
            if (refuses) { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(WriteOutput); }
            else { _ = await WriteOutput(); }
            Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
            int growths = budget.GrowthCalls - before;
            source.Dispose(); security.Dispose(); Assert.Equal(0, budget.ReservedBytes);
            return (before, growths);
        }
    }

    private static OutputSecurity CopyRevision4(string dictionary, ReadOnlySpan<byte> identifier,
        ReadOnlySpan<byte> password, ResourceGovernor governor)
    {
        using ParserMemoryScope input = ParserMemoryScope.Create(governor);
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
        PdfDictionary encryption = Assert.IsType<PdfDictionary>(parser.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(dictionary)), 0, input));
        _ = DocumentSecurity.TryCreate(encryption, identifier, password, governor, out DocumentSecurity? authenticated);
        using (authenticated) { Assert.NotNull(authenticated); return authenticated.PrepareOutput(identifier); }
    }

    private static ValueTask<long> WriteCopied(string mode, OutputSecurity security, List<NumberedObject> model,
        byte[] identifier, ResourceGovernor governor, IByteSink sink, CancellationToken cancellationToken)
    {
        PdfSerializer serializer = Serializer(governor, security, model);
        LinearizedWriter linear = new(QpdfLimits.Default, governor, security, 7, 6);
        byte[] second = "new identifier"u8.ToArray();
        return mode switch
        {
            "classic" => serializer.SerializeAsync(new(2, 0), model, 5, identifier, second, false, sink, cancellationToken),
            "raw" or "deflate" => serializer.SerializeStreamedAsync(new(2, 0), model, [new[] { 5, 10 }], mode == "deflate", null,
                5, identifier, second, false, sink, cancellationToken),
            "qdf" or "qdf-stream" => serializer.SerializeQdfAsync(new(2, 0), model, mode == "qdf-stream" ? [new[] { 5, 10 }] : null,
                null, 5, identifier, second, new(true, false), false, sink, cancellationToken),
            "linear" or "linear-flate" => linear.WriteLinearizedAsync(new(2, 0), model, 5, identifier, second, false,
                mode == "linear-flate", null, sink, null, cancellationToken),
            _ => linear.WriteLinearizedStreamedAsync(new(2, 0), model, [new[] { 5, 10 }], 5, identifier, second, false,
                mode == "linear-stream-flate", null, sink, null, cancellationToken),
        };
    }
}
