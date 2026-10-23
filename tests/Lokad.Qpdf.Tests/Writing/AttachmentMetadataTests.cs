using System.Text;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentMetadataTests
{
    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] items)
        => new(items.ToDictionary(item => PdfName.FromAscii(item.Key), item => item.Value));

    private static DocumentEngine Prepared(string field, byte[] bytes)
    {
        PdfByteString large = new(bytes, false), small = new("K"u8.ToArray(), false);
        PdfDictionary parameters = Dictionary((field, large));
        PdfStream stream = PdfStream.FromOwnedData(Dictionary(("Params", parameters),
            ("Subtype", field == "Subtype" ? new PdfName(bytes) : PdfName.FromAscii("text/plain"))), "X"u8.ToArray());
        PdfDictionary spec = Dictionary(("EF", Dictionary(("UF", stream))), ("UF", field == "UF" ? large : small),
            ("Desc", field == "Desc" ? large : small));
        PdfDictionary tree = Dictionary(("Names", new PdfArray([field == "key" ? large : small, spec])));
        PdfDictionary trailer = Dictionary(("Root", Dictionary(("Names", Dictionary(("EmbeddedFiles", tree))))));
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        return new(ReadOnlyMemory<byte>.Empty, new XRefTable(0L, new Dictionary<int, XrefEntry>(), trailer, [trailer]),
            QpdfLimits.Default, construction, new(construction), QpdfRecoveryMode.Strict);
    }

    [Theory]
    [InlineData("key", false)]
    [InlineData("UF", false)]
    [InlineData("Desc", false)]
    [InlineData("CreationDate", false)]
    [InlineData("ModDate", false)]
    [InlineData("CheckSum", false)]
    [InlineData("Subtype", false)]
    [InlineData("key", true)]
    [InlineData("UF", true)]
    [InlineData("Desc", true)]
    [InlineData("ModDate", true)]
    public void LargeMetadataRefusesBeforePayloadMaterialization(string field, bool utf8)
    {
        byte[] text = Encoding.ASCII.GetBytes(new string('A', 1048576));
        using DocumentEngine engine = Prepared(field, utf8 ? [0xef, 0xbb, 0xbf, .. text] : text);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(71L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        long before = GC.GetAllocatedBytesForCurrentThread();
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            Assert.Throws<QpdfLimitException>(() => EmbeddedFiles.ReadAttachments(engine, QpdfLimits.Default, governor,
                new(governor), memory, CancellationToken.None));
        }
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 131072L);
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DecodedMetadataStaysFundedUntilItsOwnerCloses(bool canceled, bool utf8)
    {
        byte[] raw = utf8 ? [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("Euro\u20ac\ud83d\ude00")]
            : [0xfe, 0xff, .. Encoding.BigEndianUnicode.GetBytes("Euro\u20ac\ud83d\ude00")];
        using DocumentEngine engine = Prepared("UF", raw);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            if (canceled)
            {
                Assert.ThrowsAny<OperationCanceledException>(() => EmbeddedFiles.ReadAttachments(engine, QpdfLimits.Default,
                    governor, new(governor), memory, new CancellationToken(true)));
            }
            else
            {
                EmbeddedAttachment attachment = Assert.Single(EmbeddedFiles.ReadAttachments(engine, QpdfLimits.Default,
                    governor, new(governor), memory, CancellationToken.None));
                Assert.Equal("Euro\u20ac\ud83d\ude00", attachment.FileName);
                Assert.True(memory.ReservedBytes >= GovernedText.StringBytes(attachment.FileName.Length));
            }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    internal static byte[] Fixture(string name, string parameters) => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 4 0 R >> >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>", null),
        (4, "<< /Names [(K) 5 0 R] >>", null),
        (5, "<< /Type /Filespec /UF (" + name + ") /Desc (description) /EF << /UF 6 0 R >> >>", null),
        (6, "<< /Type /EmbeddedFile /Length 1 /Subtype /text#2Fplain /Params << " + parameters + " >> >>", "X"u8.ToArray())], 1);

    private sealed class Host(IQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }

    private static byte[] ListingFixture(int count, int malformed)
    {
        string names = string.Join(' ', Enumerable.Range(0, count).Select(index => $"(K-{index:D3}) 5 0 R")
            .Concat(Enumerable.Range(0, malformed).Select(index => $"(zbad-{index:D3}) 7 0 R")));
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 4 0 R >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>", null),
            (4, "<< /Names [" + names + "] >>", null),
            (5, "<< /Type /Filespec /UF (name) /Desc (description) /EF << /UF 6 0 R >> >>", null),
            (6, "<< /Type /EmbeddedFile /Length 1 /Params << /Size 1 >> >>", "X"u8.ToArray()),
            (7, "42", null)
        ], 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65)]
    [InlineData(129)]
    public async Task TypedListingDoesNotSpendPresentationQuotas(int count)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), ListingFixture(count, 0), QpdfInputOwnership.Transfer);
        IReadOnlyList<QpdfAttachmentInfo> listing = await Qpdf.ListAttachmentsAsync(storage, QpdfPath.From("input"),
            QpdfLimits.Default with { MaxDiagnostics = 1L, MaxDiagnosticBytes = 1L }, CancellationToken.None);
        Assert.Equal(count, listing.Count); Assert.Equal(0L, storage.OpenLeaseCount);
        storage.Dispose();
        foreach (QpdfAttachmentInfo info in listing)
        {
            Assert.Equal("name", info.FileName); Assert.Equal("description", info.Description);
            Assert.Equal(6, info.StreamObjectNumber); Assert.Equal(1L, info.Size);
        }
    }

    [Fact]
    public async Task GenuineWarningsRemainBoundedDuringTypedListing()
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("one"), ListingFixture(1, 1), QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("two"), ListingFixture(1, 2), QpdfInputOwnership.Transfer);
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnostics = 1L };
        Assert.Single(await Qpdf.ListAttachmentsAsync(storage, QpdfPath.From("one"), limits, CancellationToken.None));
        QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() =>
            Qpdf.ListAttachmentsAsync(storage, QpdfPath.From("two"), limits, CancellationToken.None).AsTask());
        Assert.Equal(nameof(QpdfLimits.MaxDiagnostics), failure.LimitName);
        Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandListingStillEnforcesPresentationQuota(bool job)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), ListingFixture(1, 0), QpdfInputOwnership.Transfer);
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxDiagnostics = 1L });
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(storage, """{"inputFile":"input","listAttachments":true}""", options, CancellationToken.None)
            : await Qpdf.ExecuteAsync(storage, new QpdfCommandInvocation(["input", "--list-attachments"]), options, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(1L, result.Counters.DiagnosticCount);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Fact]
    public async Task TypedListingAliasesSurviveInputAndHostClosureWithOnlyResultStorageRetained()
    {
        RecordingMemoryBudget root = new(4194304L); using IMemoryScope caller = root.CreateScope();
        using InMemoryQpdfHost storage = new(); string name = new('N', 4096);
        storage.AddInput(QpdfPath.From("input"), Fixture(name, "/CreationDate (D:20240102030405Z) /ModDate (D:20250607080910Z) /CheckSum <0102>"), QpdfInputOwnership.Transfer);
        IReadOnlyList<QpdfAttachmentInfo> listing = await Qpdf.ListAttachmentsAsync(new Host(storage, caller), QpdfPath.From("input"), CancellationToken.None);
        QpdfAttachmentInfo info = Assert.Single(listing); QpdfAttachmentInfo clone = info with { };
        Assert.Equal(0L, storage.OpenLeaseCount); storage.Dispose();
        Assert.Equal(name, info.FileName); Assert.Equal("text/plain", info.MimeType); Assert.Equal("0102", info.Checksum);
        Assert.Equal("D:20250607080910Z", clone.ModDate);
        Assert.InRange(root.ReservedBytes, 2L * name.Length, 2L * name.Length + 2048L);
        caller.Dispose(); Assert.Equal(0L, root.ReservedBytes); GC.KeepAlive(info); GC.KeepAlive(clone);
    }

    [Fact]
    public async Task EveryTypedListingAdmissionRefusalRefundsUnreturnedMetadata()
    {
        using InMemoryQpdfHost storage = new(); storage.AddInput(QpdfPath.From("input"), Fixture(new string('N', 4096), ""), QpdfInputOwnership.Transfer);
        RecordingMemoryBudget baseline = new(4194304L);
        using (IMemoryScope caller = baseline.CreateScope()) { await Qpdf.ListAttachmentsAsync(new Host(storage, caller), QpdfPath.From("input"), CancellationToken.None); }
        int growths = baseline.GrowthCalls;
        for (int fail = 1; fail <= growths; fail++)
        {
            RecordingMemoryBudget root = new(4194304L) { RefuseOnGrowth = fail }; using IMemoryScope caller = root.CreateScope();
            await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => Qpdf.ListAttachmentsAsync(new Host(storage, caller), QpdfPath.From("input"), CancellationToken.None).AsTask());
            Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(0L, root.ReservedBytes);
        }
    }
}
