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
    [MemberData(nameof(Revision6EncryptionTests.Vectors), MemberType = typeof(Revision6EncryptionTests))]
    public void NativeDocumentsDecryptAtTheObjectBoundary(string file, string dictionary,
        string userHex, string ownerHex, string keyHex)
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
                Assert.Equal(-3388, engine.Security.Permissions);
                PdfDictionary catalog = ResolveDictionary(Get(engine.Trailer, "Root"), engine);
                PdfDictionary pages = ResolveDictionary(Get(catalog, "Pages"), engine);
                Assert.Equal(1L, Assert.IsType<PdfInteger>(Get(pages, "Count")).Value);
                PdfArray kids = Assert.IsType<PdfArray>(Get(pages, "Kids"));
                PdfDictionary page = ResolveDictionary(kids[0], engine);
                PdfStream content = ResolveStream(Get(page, "Contents"), engine);
                Assert.Equal("BT (Page) Tj ET", Encoding.ASCII.GetString(Decode(content, engine, governor)));
                PdfDictionary info = ResolveDictionary(Get(engine.Trailer, "Info"), engine);
                // The native info dictionary is an object-stream member. Its
                // strings are plaintext after the enclosing stream decrypts.
                Assert.Equal("TitleText", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Get(info, "Title")).ValueSpan));
                PdfStream metadata = ResolveStream(Get(catalog, "Metadata"), engine);
                Assert.Contains("<x:xmpmeta>Meta</x:xmpmeta>", Encoding.ASCII.GetString(Decode(metadata, engine, governor)), StringComparison.Ordinal);
                Assert.Equal(id, Assert.IsType<PdfByteString>(identifiers[0]).Value);
                PdfDictionary rawEncryption = Assert.IsType<PdfDictionary>(engine.Load(encrypt.ObjectNumber));
                Assert.Equal(48, Assert.IsType<PdfByteString>(Get(rawEncryption, "U")).ValueSpan.Length);
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

    [Fact]
    public void EveryReaderAdmissionFailureRefundsTheDocumentAndSecurityContext()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r6-ascii.pdf"));
        RecordingMemoryBudget baseline = new(1048576);
        Run(baseline, false);
        Assert.True(baseline.GrowthCalls > 2);
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

    [Fact]
    public void NativeStreamBytesArePlainBeforeOrdinaryFilterProcessing()
    {
        using JsonDocument vectors = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r6-vectors.json")));
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
