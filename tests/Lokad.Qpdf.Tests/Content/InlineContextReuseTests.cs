using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Content;

public sealed class InlineContextReuseTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output");
    private static readonly byte[] Samples = Enumerable.Range(0, 65536).Select(i => (byte)i).ToArray();
    private sealed record Fixture(byte[] Pdf, byte[] Program, byte[] Encoded);

    private static Fixture Source(int pages, string kind)
    {
        byte[] program = [.. "q BI /W 256 /H 256 /BPC 8 /CS /Local ID "u8, .. Samples, .. " EI Q\n"u8];
        if (kind == "warnings") { program = [.. program, .. "BI /W 1 /H 1 /BPC 8 /CS /Missing /F /AHx ID 61> EI\n"u8]; }
        using MemoryStream buffer = new();
        using (ZLibStream encoder = new(buffer, CompressionLevel.SmallestSize, true)) { encoder.Write(program); }
        byte[] encoded = buffer.ToArray();
        byte[] palette = Enumerable.Range(0, 256).SelectMany(i => new byte[] { (byte)i, 0, 0 }).ToArray();
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /AuditStream 3 0 R /AuditResources 4 0 R >>", null),
            (2, $"<< /Type /Pages /Count {(kind == "prior-warning" ? pages + 1 : pages)} /Kids ["
                + string.Join(' ', Enumerable.Range(10, pages).Select(i => $"{i} 0 R")) + "] >>", null),
            (3, $"<< /Length {encoded.Length} /Filter /FlateDecode >>", encoded),
            (4, "<< /ColorSpace << /Local /DeviceGray >> >>", null),
            (5, "<< /ColorSpace << /Local [/Indexed /DeviceRGB 255 <" + Convert.ToHexString(palette) + ">] >> /XObject << /IIm1 6 0 R >> >>", null),
            (6, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Length 1 >>", "Z"u8.ToArray()),
            (7, "[3 0 R 8 0 R]", null), (8, "<< /Length 0 >>", []), (9, "null", null)
        ];
        for (int index = 0; index < pages; index++)
        {
            int contents = kind == "arrays" ? 7 : kind == "contents" ? 10 + pages + index : 3;
            int resources = kind == "resources" && index % 2 == 1 ? 5 : 4;
            if (kind == "contents") { objects.Add((contents, $"<< /Length {encoded.Length} /Filter /FlateDecode >>", encoded)); }
            objects.Add((10 + index, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 256 256] /Resources {resources} 0 R /Contents {contents} 0 R >>", null));
        }
        return new(PdfTestDocument.Assemble(objects, 1), program, encoded);
    }

    private static string[] Arguments(bool quiet)
        => [Input.Value, "--externalize-inline-images", "--ii-min-bytes=0", "--decode-level=none", "--compress-streams=n",
            "--remove-unreferenced-resources=no", .. quiet ? new[] { "--no-warn" } : Array.Empty<string>(), Output.Value];
    private static QpdfObject Get(PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference) : value;

    [Theory]
    [InlineData(1, "shared", 1)]
    [InlineData(16, "shared", 1)]
    [InlineData(16, "arrays", 1)]
    [InlineData(16, "resources", 2)]
    [InlineData(2, "contents", 2)]
    [InlineData(3, "prior-warning", 1)]
    public async Task ReusesOnlyTheSameImmutableContentAndResourceContext(int pages, string kind, int contexts)
    {
        Fixture fixture = Source(pages, kind); byte[] before = (byte[])fixture.Pdf.Clone();
        using InMemoryQpdfHost host = new(); host.AddInput(Input, fixture.Pdf, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(Arguments(false)), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(kind == "prior-warning" ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(contexts * fixture.Program.LongLength, result.Counters.TotalDecodedBytes);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(before, fixture.Pdf);
        await Verify(host, fixture, pages, kind, contexts);
    }

    private static async Task Verify(InMemoryQpdfHost host, Fixture fixture, int pages, string kind, int contexts)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        Assert.True(bytes.Length < contexts * 70000 + 40000);
        host.AddInput(Output, bytes, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Output, CancellationToken.None))
        {
            HashSet<PdfReference> programs = [], images = [];
            HashSet<PdfDictionary> headers = new(ReferenceEqualityComparer.Instance);
            for (int index = 0; index < pages; index++)
            {
                PdfDictionary page = document.PageReferences[index].Page; Assert.True(headers.Add(page));
                PdfReference contents = Assert.IsType<PdfReference>(Get(page, "Contents")); programs.Add(contents);
                PdfDictionary resources = Assert.IsType<PdfDictionary>(Resolve(document, Get(page, "Resources")));
                PdfDictionary xobjects = Assert.IsType<PdfDictionary>(Resolve(document, Get(resources, "XObject")));
                bool indexed = kind == "resources" && index % 2 == 1;
                PdfReference image = Assert.IsType<PdfReference>(Get(xobjects, indexed ? "IIm2" : "IIm1")); images.Add(image);
                PdfStream value = Assert.IsType<PdfStream>(document.GetObject(image));
                Assert.Equal(65537, value.Data.Length); Assert.Equal(Samples, value.Data.AsSpan(0, Samples.Length).ToArray());
                Assert.Equal((byte)' ', value.Data[^1]);
                QpdfObject color = Get(value.Dictionary, "ColorSpace");
                if (indexed)
                {
                    Assert.Equal(PdfName.FromAscii("Indexed"), Assert.IsType<PdfArray>(color)[0]);
                    Assert.Equal("Z"u8.ToArray(), Assert.IsType<PdfStream>(Resolve(document, Get(xobjects, "IIm1"))).Data);
                }
                else { Assert.Equal(PdfName.FromAscii("DeviceGray"), color); }
                string program = Encoding.ASCII.GetString(Assert.IsType<PdfStream>(document.GetObject(contents)).Data);
                Assert.Contains(indexed ? "/IIm2 Do" : "/IIm1 Do", program); Assert.DoesNotContain(" BI ", program);
            }
            Assert.Equal(contexts, programs.Count); Assert.Equal(contexts, images.Count);
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(document, Get(document.Engine.Trailer, "Root")));
            Assert.Equal(fixture.Encoded, Assert.IsType<PdfStream>(Resolve(document, Get(catalog, "AuditStream"))).Data);
            Assert.False(Assert.IsType<PdfDictionary>(Resolve(document, Get(catalog, "AuditResources"))).TryGet(PdfName.FromAscii("XObject"), out _));
        }
        Assert.True((await Qpdf.CheckAsync(host, Output, CancellationToken.None)).Passed);
        using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.Equal(pages, independent.NumberOfPages); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task JobAdapterSharesTheCompletedContext()
    {
        Fixture fixture = Source(3, "shared"); using InMemoryQpdfHost host = new(); host.AddInput(Input, fixture.Pdf, QpdfInputOwnership.Transfer);
        string job = JsonSerializer.Serialize(new { inputFile = Input.Value, outputFile = Output.Value, externalizeInlineImages = true,
            iiMinBytes = "0", decodeLevel = "none", compressStreams = "n", removeUnreferencedResources = "no" });
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(fixture.Program.LongLength, result.Counters.TotalDecodedBytes);
        await Verify(host, fixture, 3, "shared", 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodeCeilingsChargeActualDistinctContexts(bool differentResources)
    {
        Fixture fixture = Source(16, differentResources ? "resources" : "shared");
        using InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(fixture.Pdf, Output);
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxDecodedBytes = 100000 });
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(Arguments(false)), options, CancellationToken.None);
        Assert.Equal(differentResources ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
        if (differentResources)
        {
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
            Assert.True(host.TryGetOutput(Output, out byte[] prior)); Assert.Equal(new byte[] { 17, 23, 41 }, prior);
        }
        else { Assert.Equal(fixture.Program.LongLength, result.Counters.TotalDecodedBytes); }
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialConversionsRetainPerPageDiagnosticsRegardlessOfSuppression(bool quiet)
    {
        Fixture fixture = Source(3, "warnings"); using InMemoryQpdfHost host = new(); host.AddInput(Input, fixture.Pdf, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(Arguments(quiet)), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Equal(3L * fixture.Program.Length, result.Counters.TotalDecodedBytes);
        Assert.Equal(quiet ? 0 : 3, result.Diagnostics.Count(d => d.Message.Contains("Unable to resolve image colorspace", StringComparison.Ordinal)));
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(Output, bytes, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Output, CancellationToken.None))
        {
            HashSet<PdfReference> contents = [];
            foreach (var location in document.PageReferences)
            {
                PdfReference link = Assert.IsType<PdfReference>(Get(location.Page, "Contents")); contents.Add(link);
                Assert.Contains("/Missing", Encoding.ASCII.GetString(Assert.IsType<PdfStream>(document.GetObject(link)).Data));
            }
            Assert.Equal(3, contents.Count);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public void CacheUsesReferenceIdentityAndBoundsItsBorrowedIndex()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
        PdfDictionary resources = new(new Dictionary<PdfName, QpdfObject>());
        PdfStream[] sources = Enumerable.Range(0, 33).Select(_ => new PdfStream(resources, "q Q"u8.ToArray())).ToArray();
        using (ExternalizedPageContents cache = ExternalizedPageContents.Create(governor, CancellationToken.None))
        {
            for (int index = 0; index < 32; index++) { cache.Remember(sources[index], resources, new(new(index + 1, 0), resources)); }
            long live = governor.CounterValues.LiveMemoryBytes;
            Assert.False(cache.TryGet(new PdfStream(resources, "q Q"u8.ToArray()), resources, out _));
            Assert.False(cache.TryGet(sources[0], new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), out _));
            Assert.True(cache.TryGet(sources[0], resources, out _));
            cache.Remember(sources[32], resources, new(new(33, 0), resources));
            Assert.False(cache.TryGet(sources[1], resources, out _)); Assert.True(cache.TryGet(sources[0], resources, out _));
            Assert.Equal(live, governor.CounterValues.LiveMemoryBytes);
        }
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CacheHitChecksBothCancellationTokens(bool caller)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using (ExternalizedPageContents cache = ExternalizedPageContents.Create(governor, caller ? cancellation.Token : CancellationToken.None))
        {
            PdfDictionary resources = new(new Dictionary<PdfName, QpdfObject>()); PdfStream source = new(resources, []);
            cache.Remember(source, resources, new(new(1, 0), resources)); cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => cache.TryGet(source, resources, out _));
        }
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    [Fact]
    public Task SharedResultRetiresOnEveryAdmissionAndPublicationFailure()
        => PreparationAdmissionChecks.VerifyAsync(() => PreparationAdmissionChecks.StorageAsync(Source(3, "shared").Pdf, Output),
            (host, token) => Qpdf.ExecuteAsync(host, new(Arguments(false)), token), Output, 256, ["create", "write", "commit", "cancel", "close"]);
}
