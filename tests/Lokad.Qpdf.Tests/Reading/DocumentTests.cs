using System.Text;
using System.Runtime.InteropServices;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class DocumentTests
{
    private static byte[] SmallStreamWithLargeUnusedInput(byte[] payload)
    {
        List<(int, string, byte[]?)> objects = CheckTests.SinglePage($"<< /Length {payload.Length} >>", payload);
        objects.Add((6, new string(' ', 2 * 1024 * 1024) + "null", null));
        return CheckTests.Assemble(objects, 1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(256)]
    public async Task RetainedCustomHostChunksDoNotPinWholeInputs(int repetitions)
    {
        // Exercise both batched small payloads and direct payload writes.
        byte[] content = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("q Q\n", repetitions)));
        RetainingOutputHost host = new();
        host.Storage.AddInput(QpdfPath.From("input.pdf"), SmallStreamWithLargeUnusedInput(content), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["input.pdf", "--stream-data=preserve", "--decode-level=none",
                "--compress-streams=n", "--static-id", "output.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        // Surviving output aliases must not keep a material, mostly unused input
        // alive. This is a broad retention bound, not an exact object-size test.
        Assert.NotEmpty(host.Chunks);
        Assert.All(host.Chunks, chunk =>
        {
            Assert.True(MemoryMarshal.TryGetArray(chunk, out ArraySegment<byte> backing));
            Assert.NotNull(backing.Array);
            Assert.InRange(backing.Array.Length, chunk.Length, 65536);
        });
        Assert.True(host.Storage.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] output));
        Assert.Equal(output, host.Chunks.SelectMany(chunk => chunk.ToArray()).ToArray());
        Assert.True(output.AsSpan().IndexOf(content) >= 0);
        using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(1, independent.NumberOfPages);
        Assert.Equal(0L, host.Storage.OpenLeaseCount);
    }

    private sealed class RetainingOutputHost : IQpdfHost
    {
        internal InMemoryQpdfHost Storage { get; } = new();
        internal List<ReadOnlyMemory<byte>> Chunks { get; } = [];
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => Storage.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new RetainingTransaction(await Storage.CreateOutputAsync(path, token), Chunks);

        private sealed class RetainingTransaction(IQpdfOutputTransaction inner, List<ReadOnlyMemory<byte>> chunks)
            : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
            { chunks.Add(data); return inner.WriteAsync(data, token); }
            public ValueTask CommitAsync(CancellationToken token) => inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExclusiveStreamSlicesKeepWholeInputFundedAfterReaderClosure()
    {
        byte[] payload = "q Q\n"u8.ToArray();
        byte[] input = SmallStreamWithLargeUnusedInput(payload);
        InMemoryQpdfHost host = HostWith("input.pdf", input);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4L * 1024L * 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope job = ParserMemoryScope.Create(governor);
        PdfStream copied;
        await using (QpdfDocument document = await QpdfDocument.OpenCore(host, QpdfPath.From("input.pdf"),
            limits, governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict, CancellationToken.None, false))
        {
            PdfStream source = Assert.IsType<PdfStream>(document.GetObject(4));
            copied = source.WithDictionary(source.Dictionary);
            document.Engine.TransferExclusiveValuesTo(job);
        }
        Array.Fill(input, (byte)0);
        byte[] alias = copied.Data;
        Array.Fill(alias, (byte)0);
        Assert.Equal(payload, copied.Data);
        Assert.Equal(0L, host.OpenLeaseCount);
        // A tiny borrowed payload pins a material input. Its full backing must
        // stay admitted, rather than billing only the four-byte view.
        Assert.True(governor.Snapshot().LiveMemoryBytes >= 2L * 1024L * 1024L);
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), Assert.Throws<QpdfLimitException>(
            () => governor.ReserveMemory(3L * 1024L * 1024L)).LimitName);
    }

    [Theory]
    [InlineData("/Cata#6Cog")]
    [InlineData("/")]
    public async Task EscapedPageTypesAndEmptyCatalogTypeKeepReaderSemantics(string catalogType)
    {
        byte[] bytes = CheckTests.Assemble([
            (1, "<< /Ty#70e " + catalogType + " /Pages 2 0 R /Version /1.7 >>", null),
            (2, "<< /Ty#70e /P#61ges /K#69ds [3 0 R] /Count 1 >>", null),
            (3, "<< /Ty#70e /P#61ge /Parent 2 0 R /MediaBox [0 0 100 100] >>", null),
        ], 1);
        InMemoryQpdfHost host = HostWith("input.pdf", bytes);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input.pdf"), CancellationToken.None))
        {
            Assert.Equal(1, document.PageCount);
            Assert.Equal("1.7", document.Version.ToString());
            Assert.Empty(document.Warnings);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    private static async Task<QpdfDocument> OpenFile(string file)
    {
        InMemoryQpdfHost host = new();
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), file));
        QpdfPath path = QpdfPath.From("inputs/" + file);
        host.AddInput(path, bytes, QpdfInputOwnership.Copy);
        return await QpdfDocument.OpenAsync(host, path, CancellationToken.None);
    }

    private static async Task<byte[]> FixtureBytes(string file)
    {
        return await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), file));
    }

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    [Fact]
    public async Task OpensSinglePageFixture()
    {
        await using QpdfDocument document = await OpenFile("minimal-1page.pdf");
        Assert.Equal(1, document.PageCount);
        Assert.Equal("1.4", document.Version.ToString());
        Assert.Empty(document.Warnings);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        Assert.True(catalog.TryGet(new PdfName(Encoding.ASCII.GetBytes("Type")), out QpdfObject type));
        Assert.Equal(new PdfName(Encoding.ASCII.GetBytes("Catalog")), type);
        PdfDictionary page = Assert.IsType<PdfDictionary>(document.GetObject(3));
        Assert.True(page.TryGet(new PdfName(Encoding.ASCII.GetBytes("MediaBox")), out QpdfObject box));
        PdfArray mediaBox = Assert.IsType<PdfArray>(box);
        Assert.Equal(new PdfInteger("612", 612L), mediaBox[2]);
        PdfStream contents = Assert.IsType<PdfStream>(document.GetObject(4));
        Assert.NotEmpty(contents.Data);
    }

    [Fact]
    public async Task OpensTwoPageFixture()
    {
        await using QpdfDocument document = await OpenFile("minimal-2page.pdf");
        Assert.Equal(2, document.PageCount);
        Assert.Equal("1.4", document.Version.ToString());
    }

    [Fact]
    public async Task FreeEntriesReadAsNull()
    {
        await using QpdfDocument document = await OpenFile("minimal-1page.pdf");
        Assert.Equal(PdfNull.Instance, document.GetObject(0));
    }

    [Fact]
    public async Task UnknownObjectsAreRejected()
    {
        await using QpdfDocument document = await OpenFile("minimal-1page.pdf");
        Assert.Throws<QpdfParseException>(() => document.GetObject(99));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.GetObject(-1));
    }

    [Fact]
    public async Task TruncatedInputIsRejected()
    {
        await Assert.ThrowsAsync<QpdfParseException>(() => OpenFile("truncated-1page.pdf"));
    }

    [Fact]
    public async Task EncryptedDeclarationIsRejectedWithoutSecrets()
    {
        QpdfEncryptedInputException ex = await Assert.ThrowsAsync<QpdfEncryptedInputException>(() => OpenFile("encrypted-decl.pdf"));
        Assert.Equal("inputs/encrypted-decl.pdf", ex.Path.Value);
        Assert.DoesNotContain("password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CyclicPageTreeIsRejected()
    {
        await Assert.ThrowsAsync<QpdfParseException>(() => OpenFile("cyclic-pages.pdf"));
    }

    [Fact]
    public async Task CorruptStartXRefIsRejected()
    {
        byte[] bytes = await FixtureBytes("minimal-1page.pdf");
        byte[] marker = Encoding.ASCII.GetBytes("startxref\n398");
        int at = FindMarker(bytes, marker);
        Assert.True(at >= 0);
        bytes[at + 10] = (byte)'9';
        bytes[at + 11] = (byte)'9';
        bytes[at + 12] = (byte)'9';
        InMemoryQpdfHost host = HostWith("inputs/corrupt.pdf", bytes);
        await Assert.ThrowsAsync<QpdfParseException>(
            () => QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/corrupt.pdf"), CancellationToken.None).AsTask());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task MissingEndOfFileIsRejected()
    {
        byte[] bytes = await FixtureBytes("minimal-1page.pdf");
        byte[] stripped = bytes[..^6];
        InMemoryQpdfHost host = HostWith("inputs/stripped.pdf", stripped);
        await Assert.ThrowsAsync<QpdfParseException>(
            () => QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/stripped.pdf"), CancellationToken.None).AsTask());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CountMismatchWarnsButCountsTraversal()
    {
        byte[] bytes = await FixtureBytes("minimal-1page.pdf");
        byte[] marker = Encoding.ASCII.GetBytes("/Count 1");
        int at = FindMarker(bytes, marker);
        Assert.True(at >= 0);
        bytes[at + 7] = (byte)'9';
        InMemoryQpdfHost host = HostWith("inputs/mismatch.pdf", bytes);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/mismatch.pdf"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        Assert.Single(document.Warnings);
        Assert.Equal(QpdfDiagnosticCode.PageCountMismatch, document.Warnings[0].Code);
    }

    [Fact]
    public async Task CountersRecordOpenWork()
    {
        await using QpdfDocument document = await OpenFile("minimal-2page.pdf");
        QpdfExecutionCounters counters = document.Counters;
        Assert.True(counters.TotalWork > 0L);
        Assert.True(counters.PeakMemoryBytes > 0L);
        Assert.True(counters.HostOperations >= 2L);
    }

    [Fact]
    public async Task CancellationAbortsOpen()
    {
        InMemoryQpdfHost host = new();
        byte[] bytes = await FixtureBytes("minimal-1page.pdf");
        QpdfPath path = QpdfPath.From("inputs/doc.pdf");
        host.AddInput(path, bytes, QpdfInputOwnership.Copy);
        using CancellationTokenSource source = new();
        source.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => QpdfDocument.OpenAsync(host, path, source.Token).AsTask());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task InputBudgetIsEnforced()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxInputBytes = 10L };
        InMemoryQpdfHost host = new();
        byte[] bytes = await FixtureBytes("minimal-1page.pdf");
        QpdfPath path = QpdfPath.From("inputs/doc.pdf");
        host.AddInput(path, bytes, QpdfInputOwnership.Copy);
        await Assert.ThrowsAsync<QpdfLimitException>(
            () => QpdfDocument.OpenAsync(host, path, limits, CancellationToken.None).AsTask());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task TinyWorkBudgetFailsOpen()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 1L };
        InMemoryQpdfHost host = new();
        byte[] bytes = await FixtureBytes("minimal-1page.pdf");
        QpdfPath path = QpdfPath.From("inputs/doc.pdf");
        host.AddInput(path, bytes, QpdfInputOwnership.Copy);
        await Assert.ThrowsAsync<QpdfLimitException>(
            () => QpdfDocument.OpenAsync(host, path, limits, CancellationToken.None).AsTask());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task DisposedDocumentsRejectUse()
    {
        QpdfDocument document = await OpenFile("minimal-1page.pdf");
        await document.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => document.GetObject(1));
    }

    private static int FindMarker(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}
