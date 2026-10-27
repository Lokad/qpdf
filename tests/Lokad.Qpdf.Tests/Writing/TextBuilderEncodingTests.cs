using System.Text.Json;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class TextBuilderEncodingTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (string kind in new[] { "label", "attachment" })
        foreach ((string text, string label, bool hex, string attachment) in new[]
        {
            ("A-", "412D", false, "412D"), ("\u0017 ~", "17207E", false, "17207E"),
            ("A\u0018B", "FEFF004100180042", true, "FEFF004100180042"),
            ("\u0019\u001a\u001b\u001c\u001d\u001e\u001f", "FEFF0019001A001B001C001D001E001F", true, "FEFF0019001A001B001C001D001E001F"),
            ("A\u007fB", "FEFF0041007F0042", true, "FEFF0041007F0042"),
            ("þÿA", "FEFF00FE00FF0041", true, "FEFF00FE00FF0041"),
            ("ÿþA", "FEFF00FF00FE0041", true, "FEFF00FF00FE0041"),
            ("ï»¿A", "FEFF00EF00BB00BF0041", true, "FEFF00EF00BB00BF0041"),
            ("Aþÿ", "41FEFF", true, "FEFF004100FE00FF"), ("Aï»¿", "41EFBBBF", true, "FEFF004100EF00BB00BF"),
            ("AÉ", "41C9", true, "FEFF004100C9"), ("日本", "FEFF65E5672C", true, "FEFF65E5672C"),
            ("A🎉", "FEFF0041D83CDF89", true, "FEFF0041D83CDF89"),
        }) { yield return [route, kind, text, kind == "label" ? label : attachment, kind == "label" ? hex : attachment.StartsWith("FEFF", StringComparison.Ordinal)]; }
    }

    private static InMemoryQpdfHost Storage()
    {
        InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), CheckTests.Assemble(CheckTests.SinglePage("<< /Length 3 >>", "q Q"u8.ToArray()), 1), QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("payload"), "X"u8.ToArray(), QpdfInputOwnership.Transfer); return storage;
    }

    private static async Task Build(IQpdfHost host, string route, string kind, string text, CancellationToken token)
    {
        if (route == "typed")
        {
            if (kind == "label")
            { await Qpdf.SelectPagesAsync(host, [new(QpdfPath.From("input"), PdfPageSelectionOptions.FromPages([1]))], QpdfPath.From("output"), new PdfPageMergeOptions { Labels = [new(1, PdfPageLabelStyle.Decimal, 1, text)] }, token); }
            else
            { await Qpdf.AddAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), [new("payload", text, text, text, null, null, null, false)], token); }
            return;
        }
        QpdfExecutionResult result = route == "argv"
            ? await Qpdf.ExecuteAsync(host, kind == "label" ? new(["input", "--set-page-labels", "1:D/1/" + text, "--", "output"])
                : new(["input", "--add-attachment", "payload", "--key=" + text, "--filename=" + text, "--description=" + text, "--", "output"]), token)
            : await Qpdf.ExecuteJobAsync(host, kind == "label" ? JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", setPageLabels = new[] { "1:D/1/" + text } })
                : JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", addAttachment = new[] { new { file = "payload", key = text, filename = text, description = text } } }), token);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    private static QpdfObject Get(QpdfDocument document, PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(document, value); }

    private static async Task<PdfByteString> ReadText(InMemoryQpdfHost storage, IQpdfHost host, string kind)
    {
        Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] pdf));
        storage.AddInput(QpdfPath.From("readback"), pdf, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        PdfDictionary tree = kind == "label" ? Assert.IsType<PdfDictionary>(Get(document, catalog, "PageLabels"))
            : Assert.IsType<PdfDictionary>(Get(document, Assert.IsType<PdfDictionary>(Get(document, catalog, "Names")), "EmbeddedFiles"));
        PdfArray pairs = Assert.IsType<PdfArray>(Get(document, tree, kind == "label" ? "Nums" : "Names"));
        PdfDictionary entry = Assert.IsType<PdfDictionary>(Resolve(document, pairs[1]));
        return Assert.IsType<PdfByteString>(Get(document, entry, kind == "label" ? "P" : "F"));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task BuildersPreserveTextAndExistingEncodingChoices(string route, string kind, string text, string bytes, bool hex)
    {
        using InMemoryQpdfHost storage = Storage(); await Build(storage, route, kind, text, CancellationToken.None);
        PdfByteString escaped = await ReadText(storage, storage, kind);
        if (kind == "attachment")
        {
            QpdfAttachmentInfo info = Assert.Single(await Qpdf.ListAttachmentsAsync(storage, QpdfPath.From("readback"), CancellationToken.None));
            Assert.Equal(text, info.Key); Assert.Equal(text, info.FileName); Assert.Equal(text, info.Description);
            await Qpdf.ExtractAttachmentAsync(storage, QpdfPath.From("readback"), text, QpdfPath.From("extracted"), CancellationToken.None);
            Assert.True(storage.TryGetOutput(QpdfPath.From("extracted"), out byte[] payload)); Assert.Equal("X"u8.ToArray(), payload);
        }
        Assert.Equal(0L, storage.OpenLeaseCount); storage.Dispose();
        ResourceGovernor decoding = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope decoded = ParserMemoryScope.Create(decoding);
        Assert.Equal(text, PdfText.Decode(escaped.ValueMemory, decoded, decoding, CancellationToken.None)); Assert.Equal(bytes, Convert.ToHexString(escaped.Value)); Assert.Equal(hex, escaped.Hex);
    }

    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget, string? fault) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            IQpdfOutputTransaction inner = await storage.CreateOutputAsync(path, token);
            return fault is null ? inner : new Transaction(inner, fault);
        }
        private sealed class Transaction(IQpdfOutputTransaction inner, string fault) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
                => fault == "write" ? ValueTask.FromException(new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed)) : inner.WriteAsync(bytes, token);
            public ValueTask CommitAsync(CancellationToken token)
                => fault == "commit" ? ValueTask.FromException(new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed)) : inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("label")]
    [InlineData("attachment")]
    public async Task BrokenSurrogatesRetainExistingReplacementPolicy(string kind)
    {
        using InMemoryQpdfHost storage = Storage(); await Build(storage, "typed", kind, "A\ud800B", CancellationToken.None);
        PdfByteString text = await ReadText(storage, storage, kind);
        ResourceGovernor decoding = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope decoded = ParserMemoryScope.Create(decoding);
        Assert.Equal("FEFF0041FFFD0042", Convert.ToHexString(text.Value)); Assert.Equal("A�B", PdfText.Decode(text.ValueMemory, decoded, decoding, CancellationToken.None));
        Assert.Equal(0L, storage.OpenLeaseCount);
    }

    private static async Task Seed(InMemoryQpdfHost storage)
    {
        await using IQpdfOutputTransaction prior = await storage.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("label")]
    [InlineData("attachment")]
    public async Task RefusalPreservesPriorOutputAndEscapedTextKeepsCallerOwnership(string kind)
    {
        string text = "þÿ" + new string('A', 32768); int growths;
        RecordingMemoryBudget root = new(64L << 20, false);
        using (IMemoryScope caller = root.CreateScope())
        {
            using InMemoryQpdfHost storage = Storage(); Host host = new(storage, caller, null);
            await Build(host, "typed", kind, text, CancellationToken.None); growths = root.GrowthCalls;
            PdfByteString escaped = await ReadText(storage, host, kind);
            Assert.Equal(0L, storage.OpenLeaseCount); storage.Dispose(); Assert.True(root.ReservedBytes > 0L);
            ResourceGovernor decoding = new(QpdfLimits.Default, CancellationToken.None);
            using ParserMemoryScope decoded = ParserMemoryScope.Create(decoding);
            Assert.Equal(text, PdfText.Decode(escaped.ValueMemory, decoded, decoding, CancellationToken.None));
        }
        Assert.Equal(0L, root.ReservedBytes); Assert.True(growths > 0);
        for (int point = 1; point <= growths; point++)
        {
            RecordingMemoryBudget refused = new(64L << 20, false) { RefuseOnGrowth = point };
            using (IMemoryScope caller = refused.CreateScope())
            {
                using InMemoryQpdfHost storage = Storage(); await Seed(storage);
                await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => Build(new Host(storage, caller, null), "typed", kind, text, CancellationToken.None));
                Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.Equal("prior"u8.ToArray(), prior);
                Assert.Equal(0L, storage.OpenLeaseCount);
            }
            Assert.Equal(0L, refused.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("label", "write")]
    [InlineData("label", "commit")]
    [InlineData("attachment", "write")]
    [InlineData("attachment", "commit")]
    public async Task OutputFailureKeepsPriorPublicationAndRetiresAdmission(string kind, string fault)
    {
        RecordingMemoryBudget root = new(64L << 20, false);
        using (IMemoryScope caller = root.CreateScope())
        {
            using InMemoryQpdfHost storage = Storage(); await Seed(storage);
            await Assert.ThrowsAsync<QpdfHostException>(() => Build(new Host(storage, caller, fault), "typed", kind, "A\u0018B", CancellationToken.None));
            Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.Equal("prior"u8.ToArray(), prior);
            Assert.Equal(0L, storage.OpenLeaseCount);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData("label", false)]
    [InlineData("label", true)]
    [InlineData("attachment", false)]
    [InlineData("attachment", true)]
    public void BothCancellationRoutesPreservePriorScratch(string kind, bool caller)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(71L);
        long live = governor.Snapshot().LiveMemoryBytes; cancellation.Cancel();
        CancellationToken token = caller ? cancellation.Token : CancellationToken.None;
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            if (kind == "label") { PageLabelStorage.Explicit(new("1", PdfPageLabelStyle.Decimal, 1, "A\u0018B"), governor, token); }
            else { AttachmentBuilder.BuildFilespecDictionary("A\u0018B", "A\u0018B", 1, governor, memory, token); }
        });
        Assert.Equal(71L, memory.ReservedBytes); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
    }
}
