using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Content;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonArrayReaderTests
{
    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }

    internal static byte[] LargeStream()
    {
        byte[] data = new byte[2 * 1048576]; data.AsSpan().Fill((byte)' ');
        return RotationContentTests.Fixture("4 0 R", "[]", data);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task CustomHostPageJsonDoesNotMaterializeAStreamPayload(string route)
    {
        using InMemoryQpdfHost storage = new(); storage.AddInput(QpdfPath.From("input"), LargeStream(), QpdfInputOwnership.Transfer);
        RecordingMemoryBudget root = new(8 * 1048576L); using IMemoryScope caller = root.CreateScope(); Host host = new(storage, caller);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 3 * 1048576L };
        QpdfExecutionCounters counters; string json;
        if (route == "typed")
        {
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, ["pages"]), limits, CancellationToken.None);
            counters = result.Counters; json = result.Json;
        }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","json":"2","jsonKey":["pages"]}""", new QpdfExecutionOptions(limits), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--json=2", "--json-key=pages", "--", "output"]), new QpdfExecutionOptions(limits), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); counters = result.Counters;
            Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output)); json = Encoding.UTF8.GetString(output);
        }
        Assert.InRange(counters.PeakMemoryBytes, 2 * 1048576L, limits.MaxMemoryBytes);
        Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(0L, counters.OpenHandles); storage.Dispose();
        using JsonDocument report = JsonDocument.Parse(json); JsonElement contents = report.RootElement.GetProperty("pages")[0].GetProperty("contents");
        Assert.Equal("4 0 R", Assert.Single(contents.EnumerateArray()).GetString());
        Assert.InRange(root.ReservedBytes, 1L, 4096L); caller.Dispose(); Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MismatchedObjectHeadersStillUseReaderValidation(bool array)
    {
        byte[] input = RotationContentTests.Fixture(array ? "5 0 R" : "4 0 R", "[4 0 R]", "q"u8.ToArray());
        string number = array ? "5" : "4";
        input = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(input).Replace(number + " 0 obj", "9 0 obj", StringComparison.Ordinal));
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, ["pages"]), CancellationToken.None).AsTask());
        Assert.Equal(0L, host.OpenLeaseCount); Assert.False(host.TryGetOutput(QpdfPath.From("output"), out _));
    }

    [Fact]
    public async Task CompressedArraysUseTheOrdinaryReaderAndKeepStreamReferences()
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), RotationContentTests.Fixture("5 0 R", "[4 0 R 4 0 R]", "q"u8.ToArray()), QpdfInputOwnership.Transfer);
        QpdfExecutionResult packed = await Qpdf.ExecuteAsync(host, new(["input", "--object-streams=generate", "--", "packed"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, packed.ExitCode); Assert.True(host.TryGetOutput(QpdfPath.From("packed"), out byte[] input)); host.AddInput(QpdfPath.From("again"), input, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None))
        {
            Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject value));
            PdfReference reference = Assert.IsType<PdfReference>(value);
            Assert.Equal(XrefEntryKind.Compressed, document.Engine.XRefEntries[reference.ObjectNumber].Kind);
            PdfArray cached = Assert.IsType<PdfArray>(document.GetObject(reference.ObjectNumber));
            Assert.Same(cached, document.Engine.ResolveArray(reference));
            Assert.Null(document.Engine.ResolveArray(new PdfReference(reference.ObjectNumber, 1)));
        }
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("again"), QpdfPath.From("json"), new QpdfJsonOptions(2, ["pages"]), CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(result.Json); JsonElement contents = json.RootElement.GetProperty("pages")[0].GetProperty("contents");
        Assert.Equal(2, contents.GetArrayLength()); Assert.Equal(contents[0].GetString(), contents[1].GetString()); Assert.Equal(0L, host.OpenLeaseCount);
    }
}
