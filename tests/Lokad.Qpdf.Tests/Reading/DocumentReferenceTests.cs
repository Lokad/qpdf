using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class DocumentReferenceTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input");

    private static async Task<byte[]> Fixture(string name)
        => await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static byte[] PrivateObject(string body, int generation)
        => PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 20] >>", null),
            (4, body, null)], 1, new Dictionary<int, int> { [4] = generation });

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(65535)]
    public async Task ExactPairsCheckGenerationBeforeAndAfterCaching(int generation)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, PrivateObject("<< /Self 4 " + generation + " R /Marker (current) >>", generation), QpdfInputOwnership.Transfer);
        QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None);
        PdfReference live = new(4, generation), stale = new(4, generation == 0 ? 1 : 0);
        Assert.Same(PdfNull.Instance, document.GetObject(stale));
        PdfDictionary value = Assert.IsType<PdfDictionary>(document.GetObject(live));
        Assert.Same(value, document.GetObject(4));
        Assert.Same(value, document.GetObject(live));
        Assert.Same(PdfNull.Instance, document.GetObject(stale));
        Assert.True(value.TryGet(PdfName.FromAscii("Self"), out QpdfObject self));
        Assert.Equal(live, Assert.IsType<PdfReference>(self));
        Assert.Same(value, document.GetObject((PdfReference)self));
        Assert.Same(PdfNull.Instance, document.GetObject(new PdfReference(99, 0)));
        Assert.Throws<QpdfParseException>(() => document.GetObject(99));
        await document.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => document.GetObject(live));
        Assert.True(value.TryGet(PdfName.FromAscii("Self"), out self));
        Assert.Equal(live, self);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task IncrementalReuseKeepsCurrentStaleFreedAndMissingEdgesDistinct()
    {
        byte[] input = await Fixture("reference-pairs-1page.pdf");
        byte[] original = input.ToArray();
        using InMemoryQpdfHost host = new(); host.AddInput(Input, input, QpdfInputOwnership.Copy);
        PdfByteString current;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            PdfReference Edge(string name)
            {
                Assert.True(catalog.TryGet(PdfName.FromAscii(name), out QpdfObject raw));
                return Assert.IsType<PdfReference>(raw);
            }
            Assert.Same(PdfNull.Instance, document.GetObject(Edge("Stale")));
            Assert.Same(PdfNull.Instance, document.GetObject(Edge("Freed")));
            Assert.Same(PdfNull.Instance, document.GetObject(Edge("Missing")));
            current = Assert.IsType<PdfByteString>(document.GetObject(Edge("Live")));
            Assert.Equal("current", Encoding.ASCII.GetString(current.Value));
            Assert.Same(current, document.GetObject(4));
            Assert.Same(PdfNull.Instance, document.GetObject(Edge("Stale")));
            Assert.Same(PdfNull.Instance, document.GetObject(new PdfReference(5, 0)));
            Assert.Same(PdfNull.Instance, document.GetObject(new PdfReference(0, 65535)));
            Assert.Equal(1, document.PageCount);
        }
        Assert.Equal("current", Encoding.ASCII.GetString(current.Value));
        Assert.Equal(original, input);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CompressedMembersRequireGenerationZeroEvenWhenCached()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, await Fixture("objstm-1page.pdf"), QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None))
        {
            Assert.Same(PdfNull.Instance, document.GetObject(new PdfReference(6, 1)));
            PdfDictionary value = Assert.IsType<PdfDictionary>(document.GetObject(new PdfReference(6, 0)));
            Assert.Same(value, document.GetObject(6));
            Assert.Same(PdfNull.Instance, document.GetObject(new PdfReference(6, 1)));
            Assert.True(value.TryGet(PdfName.FromAscii("Type"), out QpdfObject type));
            Assert.Equal(PdfName.FromAscii("Font"), type);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CancellationCoversCachedColdAndDeadPairs()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, await Fixture("reference-pairs-1page.pdf"), QpdfInputOwnership.Transfer);
        using CancellationTokenSource cancel = new();
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, cancel.Token))
        {
            document.GetObject(new PdfReference(1, 0));
            cancel.Cancel();
            foreach (PdfReference reference in new PdfReference[] { new(1, 0), new(4, 1), new(4, 0), new(5, 1), new(99, 0) })
            { Assert.Throws<OperationCanceledException>(() => document.GetObject(reference)); }
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SharedRefusalLeavesTheDocumentUsableAndRefundsItsOwner()
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(Input, PrivateObject("(" + new string('A', 65536) + ")", 7), QpdfInputOwnership.Transfer);
        using RecordingMemoryBudget budget = new(16777216L);
        using (IMemoryScope owner = budget.CreateScope())
        {
            MemoryBudgetHost host = new(storage, owner);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None))
            {
                budget.RefuseGrowth = true;
                Assert.Same(PdfNull.Instance, document.GetObject(new PdfReference(4, 0)));
                Assert.Same(PdfNull.Instance, document.GetObject(new PdfReference(99, 0)));
                Assert.Throws<QpdfMemoryAdmissionException>(() => document.GetObject(new PdfReference(4, 7)));
                budget.RefuseGrowth = false;
                Assert.Equal(65536, Assert.IsType<PdfByteString>(document.GetObject(new PdfReference(4, 7))).Value.Length);
            }
            Assert.Equal(0L, storage.OpenLeaseCount);
        }
        Assert.Equal(0L, budget.ReservedBytes);
    }

    [Fact]
    public async Task LocalContainerCeilingsStillApplyToLazyLoads()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, PrivateObject("[0 0 0 0 0 0 0 0 0]", 0), QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Input,
            QpdfLimits.Default with { MaxContainerSize = 8 }, CancellationToken.None))
        {
            QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => document.GetObject(new PdfReference(4, 0)));
            Assert.Equal(nameof(QpdfLimits.MaxContainerSize), error.LimitName);
            Assert.IsType<PdfDictionary>(document.GetObject(new PdfReference(1, 0)));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task EscapedArraysAndStreamsKeepTheirFundingAndDefensiveCopies()
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(Input, await Fixture("reference-pairs-1page.pdf"), QpdfInputOwnership.Transfer);
        using RecordingMemoryBudget budget = new(16777216L);
        using (IMemoryScope owner = budget.CreateScope())
        {
            QpdfDocument document = await QpdfDocument.OpenAsync(new MemoryBudgetHost(storage, owner), Input, CancellationToken.None);
            PdfArray pairs = Assert.IsType<PdfArray>(document.GetObject(new PdfReference(7, 0)));
            PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(pairs[4])));
            long before = budget.ReservedBytes;
            await document.DisposeAsync();
            Assert.Equal(0L, storage.OpenLeaseCount);
            Assert.InRange(budget.ReservedBytes, 1L, before - 1L);
            Assert.Equal(new PdfReference(4, 1), Assert.IsType<PdfReference>(pairs[0]));
            byte[] copy = stream.Data; Array.Fill(copy, (byte)0);
            Assert.Equal("q Q"u8.ToArray(), stream.Data);
            Assert.True(stream.Dictionary.TryGet(PdfName.FromAscii("Marker"), out QpdfObject marker));
            Assert.Equal("kept", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(marker).Value));
        }
        Assert.Equal(0L, budget.ReservedBytes);
    }
}
