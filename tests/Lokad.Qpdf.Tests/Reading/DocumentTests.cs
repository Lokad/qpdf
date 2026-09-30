using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class DocumentTests
{
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
