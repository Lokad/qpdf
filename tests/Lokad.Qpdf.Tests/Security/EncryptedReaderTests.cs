using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class EncryptedReaderTests
{
    [Theory]
    [InlineData("security-r4-linearized.pdf")]
    [InlineData("security-r6-linearized.pdf")]
    public void NativeLinearizedFilesDecryptPageContentsAndHintTables(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        byte[] original = (byte[])data.Clone();
        foreach (string password in new[] { "reader", "fixture-owner" })
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            using (DocumentEngine engine = Open(data, governor))
            {
                PdfReference encrypt = Assert.IsType<PdfReference>(Get(engine.Trailer, "Encrypt"));
                Assert.NotEqual(PasswordAuthentication.None, engine.Authenticate(encrypt, Encoding.ASCII.GetBytes(password)));
                Assert.Contains(engine.XRefEntries, entry => entry.Value.Kind == XrefEntryKind.Compressed);
                PdfDictionary catalog = ResolveDictionary(Get(engine.Trailer, "Root"), engine);
                PdfDictionary pages = ResolveDictionary(Get(catalog, "Pages"), engine);
                PdfArray children = Assert.IsType<PdfArray>(Get(pages, "Kids")); Assert.Equal(2, children.Count);
                for (int index = 0; index < 2; index++)
                {
                    PdfDictionary page = ResolveDictionary(children[index], engine);
                    byte[] content = Decode(ResolveStream(Get(page, "Contents"), engine), engine, governor);
                    Assert.Equal("BT /F1 24 Tf 100 700 Td (" + (index == 0 ? "First" : "Second") + ") Tj ET",
                        Encoding.ASCII.GetString(content).Trim());
                }
                LinearizationInspection inspection = LinearizationCheck.Inspect(engine, 2, QpdfLimits.Default, governor, CancellationToken.None);
                Assert.Equal(LinearizationStatus.Valid, inspection.Status); Assert.True(inspection.SharedGroups > 0);
            }
            Assert.Equal(0, governor.Snapshot().LiveMemoryBytes); Assert.Equal(original, data);
        }
    }

    public static IEnumerable<object[]> ReaderVectors()
        => Revision6EncryptionTests.Vectors().Select(vector => vector.Append((object)6).ToArray())
            .Concat(Revision4EncryptionTests.Vectors().Concat(Revision4EncryptionTests.Rc4Vectors()).Select(vector => vector.Take(5).Append((object)4).ToArray()));

    [Theory]
    [MemberData(nameof(ReaderVectors))]
    public void NativeDocumentsDecryptAtTheObjectBoundary(string file, string dictionary,
        string userHex, string ownerHex, string keyHex, int revision)
    {
        Assert.NotEmpty(dictionary); Assert.NotEmpty(keyHex);
        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        byte[] original = (byte[])data.Clone();
        foreach (string password in new[] { userHex, ownerHex })
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            using (DocumentEngine engine = Open(data, governor))
            {
                PdfReference encrypt = Assert.IsType<PdfReference>(Get(engine.Trailer, "Encrypt"));
                PdfArray identifiers = Assert.IsType<PdfArray>(Get(engine.Trailer, "ID"));
                byte[] id = Assert.IsType<PdfByteString>(identifiers[0]).Value;
                Assert.Equal(password == ownerHex ? PasswordAuthentication.Owner : PasswordAuthentication.User,
                    engine.Authenticate(encrypt, Convert.FromHexString(password)));
                Assert.NotNull(engine.Security);
                Assert.Equal(revision, engine.Security.Revision);
                Assert.Equal(-3388, engine.Security.Permissions);
                PdfDictionary catalog = ResolveDictionary(Get(engine.Trailer, "Root"), engine);
                PdfDictionary pages = ResolveDictionary(Get(catalog, "Pages"), engine);
                Assert.Equal(1L, Assert.IsType<PdfInteger>(Get(pages, "Count")).Value);
                PdfArray kids = Assert.IsType<PdfArray>(Get(pages, "Kids"));
                PdfDictionary page = ResolveDictionary(kids[0], engine);
                PdfStream content = ResolveStream(Get(page, "Contents"), engine);
                Assert.Equal("BT (Page) Tj ET", Encoding.ASCII.GetString(Decode(content, engine, governor)));
                PdfDictionary info = ResolveDictionary(Get(engine.Trailer, "Info"), engine);
                // Ordinary strings and object-stream members take distinct
                // decryption paths and both must expose the native plaintext.
                Assert.Equal("TitleText", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Get(info, "Title")).ValueSpan));
                PdfStream metadata = ResolveStream(Get(catalog, "Metadata"), engine);
                Assert.Contains("<x:xmpmeta>Meta</x:xmpmeta>", Encoding.ASCII.GetString(Decode(metadata, engine, governor)), StringComparison.Ordinal);
                Assert.Equal(id, Assert.IsType<PdfByteString>(identifiers[0]).Value);
                PdfDictionary rawEncryption = Assert.IsType<PdfDictionary>(engine.Load(encrypt.ObjectNumber));
                Assert.Equal(revision == 6 ? 48 : 32, Assert.IsType<PdfByteString>(Get(rawEncryption, "U")).ValueSpan.Length);
                foreach ((int number, XrefEntry entry) in engine.XRefEntries)
                {
                    if (entry.Kind != XrefEntryKind.Free) { _ = engine.Load(number); }
                }
            }
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(original, data);
        }
    }

    [Fact]
    public void AuthenticationRetryAndTransferredPlaintextKeepTheirSeparateLifetimes()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r6-ascii.pdf"));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope retained = ParserMemoryScope.Create(governor))
        {
            PdfByteString title;
            PdfStream content;
            using (DocumentEngine engine = Open(data, governor))
            {
                engine.BorrowImmutableStreamData = true;
                PdfReference encrypt = Assert.IsType<PdfReference>(Get(engine.Trailer, "Encrypt"));
                Assert.Equal(PasswordAuthentication.None, engine.Authenticate(encrypt, "wrong"u8));
                Assert.Null(engine.Security);
                Assert.Equal(PasswordAuthentication.User, engine.Authenticate(encrypt, "reader"u8));
                PdfDictionary info = ResolveDictionary(Get(engine.Trailer, "Info"), engine);
                title = Assert.IsType<PdfByteString>(Get(info, "Title"));
                PdfDictionary catalog = ResolveDictionary(Get(engine.Trailer, "Root"), engine);
                PdfDictionary pages = ResolveDictionary(Get(catalog, "Pages"), engine);
                PdfDictionary page = ResolveDictionary(Assert.IsType<PdfArray>(Get(pages, "Kids"))[0], engine);
                content = ResolveStream(Get(page, "Contents"), engine);
                engine.TransferExclusiveValuesTo(retained);
            }
            Assert.Equal("TitleText", Encoding.ASCII.GetString(title.ValueSpan));
            using FilterPlan filters = FilterChain.Resolve(content.Dictionary, null, governor, CancellationToken.None);
            Assert.Equal("BT (Page) Tj ET", Encoding.ASCII.GetString(FilterChain.Decode(content.Data, filters, QpdfLimits.Default, governor, retained)));
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("security-r4-incremental.pdf", 4, "UpdatedTitle")]
    [InlineData("security-r6-incremental.pdf", 6, "UpdatedTitle")]
    [InlineData("security-r6-hybrid.pdf", 6, "TitleText")]
    [InlineData("security-r6-hybrid-incremental.pdf", 6, "UpdatedTitle")]
    public async Task CrossReferenceRevisionsUseNewestPlaintextAndOriginalEncryptedStreams(string file, int revision, string title)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        byte[] original = (byte[])data.Clone();
        foreach (string password in new[] { "reader", "fixture-owner" })
        {
            using InMemoryQpdfHost host = new();
            QpdfPath path = QpdfPath.From(file); host.AddInput(path, data, QpdfInputOwnership.Transfer);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, path,
                QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None))
            {
                Assert.Equal(1, document.PageCount); Assert.Empty(document.Warnings);
                Assert.Equal(revision, Assert.IsType<QpdfEncryptionInfo>(document.Encryption).Revision);
                DocumentEngine engine = document.Engine;
                PdfDictionary info = ResolveDictionary(Get(engine.Trailer, "Info"), engine);
                Assert.Equal(title, Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Get(info, "Title")).ValueSpan));
                if (title == "UpdatedTitle") { Assert.Equal(2L, Assert.IsType<PdfInteger>(Get(info, "Revision")).Value); }
                ResourceGovernor decoding = new(QpdfLimits.Default, CancellationToken.None);
                PdfStream content = ResolveStream(Get(document.PageReferences[0].Page, "Contents"), engine);
                Assert.Equal("BT (Page) Tj ET", Encoding.ASCII.GetString(Decode(content, engine, decoding)));
                Assert.Equal(0L, decoding.Snapshot().LiveMemoryBytes);
                foreach ((int number, XrefEntry entry) in engine.XRefEntries)
                { if (entry.Kind != XrefEntryKind.Free) { _ = document.GetObject(number); } }
            }
            Assert.Equal(original, data);
        }
    }

    [Theory]
    [InlineData("security-r6-ascii.pdf")]
    [InlineData("security-r4-ascii.pdf")]
    [InlineData("security-r4-incremental.pdf")]
    [InlineData("security-r6-hybrid-incremental.pdf")]
    public void EveryReaderAdmissionFailureRefundsTheDocumentAndSecurityContext(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        RecordingMemoryBudget baseline = new(1048576);
        Run(baseline, false);
        Assert.True(baseline.GrowthCalls > 1);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++)
        {
            RecordingMemoryBudget refusing = new(1048576) { RefuseOnGrowth = growth };
            Run(refusing, true);
            Assert.Equal(growth, refusing.GrowthCalls);
        }

        void Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            void Read()
            {
                using DocumentEngine engine = Open(data, governor);
                PdfReference encrypt = Assert.IsType<PdfReference>(Get(engine.Trailer, "Encrypt"));
                Assert.Equal(PasswordAuthentication.User, engine.Authenticate(encrypt, "reader"u8));
                foreach ((int number, XrefEntry entry) in engine.XRefEntries)
                { if (entry.Kind != XrefEntryKind.Free) { _ = engine.Load(number); } }
            }
            if (refuses) { Assert.Throws<QpdfMemoryAdmissionException>(Read); }
            else { Read(); }
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(0L, budget.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("security-r6-vectors.json")]
    [InlineData("security-r4-vectors.json")]
    [InlineData("security-r4-rc4-vectors.json")]
    public void NativeStreamBytesArePlainBeforeOrdinaryFilterProcessing(string vectorFile)
    {
        using JsonDocument vectors = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", vectorFile)));
        foreach (JsonElement vector in vectors.RootElement.EnumerateArray())
        {
            string file = vector.GetProperty("file").GetString() ?? throw new InvalidDataException();
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            using DocumentEngine engine = Open(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), governor);
            PdfReference encrypt = Assert.IsType<PdfReference>(Get(engine.Trailer, "Encrypt"));
            byte[] password = Convert.FromHexString(vector.GetProperty("userHex").GetString() ?? throw new InvalidDataException());
            Assert.Equal(PasswordAuthentication.User, engine.Authenticate(encrypt, password));
            foreach (JsonElement stream in vector.GetProperty("streams").EnumerateArray())
            {
                PdfStream plain = Assert.IsType<PdfStream>(engine.Load(stream.GetProperty("number").GetInt32()));
                byte[] expected = Convert.FromBase64String(stream.GetProperty("plainBase64").GetString() ?? throw new InvalidDataException());
                Assert.Equal(expected, plain.Data);
                Assert.Equal(expected.Length, Assert.IsType<PdfInteger>(Get(plain.Dictionary, "Length")).Value);
            }
        }
    }

    private static DocumentEngine Open(byte[] data, ResourceGovernor governor)
    {
        DiagnosticBag diagnostics = new(governor);
        long inputBytes = data.Length + GovernedBufferStream.ArrayOverheadBytes;
        governor.ReserveMemory(inputBytes);
        XRefTable? table = null;
        try
        {
            table = XRefTable.Parse(data, QpdfLimits.Default, governor, diagnostics, QpdfRecoveryMode.Strict, false);
            return DocumentEngine.CreateOwned(data, table, QpdfLimits.Default, governor, diagnostics, QpdfRecoveryMode.Strict, inputBytes, false, false, null);
        }
        catch { table?.Dispose(); governor.ReleaseMemory(inputBytes); throw; }
    }

    private static QpdfObject Get(PdfDictionary dictionary, string name)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return value; }

    private static PdfDictionary ResolveDictionary(QpdfObject value, DocumentEngine engine)
        => Assert.IsType<PdfDictionary>(value is PdfReference reference ? engine.Load(reference.ObjectNumber) : value);

    private static PdfStream ResolveStream(QpdfObject value, DocumentEngine engine)
        => Assert.IsType<PdfStream>(engine.Load(Assert.IsType<PdfReference>(value).ObjectNumber));

    private static byte[] Decode(PdfStream stream, IIndirectResolver resolver, ResourceGovernor governor)
    {
        using FilterPlan plan = FilterChain.ResolveScoped(stream.Dictionary, resolver, governor, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        return FilterChain.Decode(stream.Data, plan, QpdfLimits.Default, governor, memory);
    }
}
