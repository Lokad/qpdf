using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class ImportedCheckTests
{
    private static readonly QpdfPath Graph = QpdfPath.From("graph"), Source = QpdfPath.From("source");
    private static readonly byte[] Program = "q\n0 0 31 40 re f\nQ\n"u8.ToArray();
    private const string Creation = """
        {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
          "obj:17 9 R":{"value":{"/Type":"/Catalog","/Pages":"113 5 R"}},
          "obj:19 7 R":{"value":{"/Type":"/Page","/Parent":"113 5 R","/Contents":"23 4 R","/MediaBox":[0,0,100,100],"/Resources":{"/XObject":{"/I":"61 8 R"}}}},
          "obj:113 5 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["19 7 R"]}},
          "obj:23 4 R":{"stream":{"dict":{},"data":"cQpRCg=="}},
          "obj:61 8 R":{"stream":{"dict":{"/Subtype":"/Image","/Width":1,"/Height":1},"data":"AA=="}},
          "obj:631 4 R":{"stream":{"dict":{"/Filter":"/FlateDecode"},"data":"YQ=="}},
          "trailer":{"value":{"/Root":"17 9 R","/Size":632}}
        }]}
        """;

    [Fact]
    public async Task CreationChecksReachableStreamsAndListsExactOriginalPairs()
    {
        using InMemoryQpdfHost storage = new(); AddJson(storage, Encoding.UTF8.GetBytes(Creation));
        ObservingHost host = new(storage, null);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([
            "graph", "--json-input", "--show-pages", "--with-images", "--show-object=19,7", "--show-npages", "--check"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        QpdfDiagnostic[] reports = result.Diagnostics.ToArray();
        int skipped = Array.FindIndex(reports, d => d.Code == QpdfDiagnosticCode.CheckSkipped && d.Message.Contains("cross-reference offsets", StringComparison.Ordinal));
        int count = Array.FindIndex(reports, d => d.Code == QpdfDiagnosticCode.PageCount && d.Message == "graph: 1");
        int value = Array.FindIndex(reports, d => d.Code == QpdfDiagnosticCode.ObjectListing && d.Message.Contains("/Contents 23 4 R", StringComparison.Ordinal));
        int page = Array.FindIndex(reports, d => d.Code == QpdfDiagnosticCode.PageListing && d.Message == "page 1: 19 7 R");
        Assert.True(skipped >= 0 && skipped < count && count < value && value < page);
        Assert.Contains(reports, d => d.Code == QpdfDiagnosticCode.PageListing && d.Message.Contains("/I: 61 8 R, 1 x 1", StringComparison.Ordinal));
        Assert.Contains(reports, d => d.Code == QpdfDiagnosticCode.PageListing && d.Message == "    23 4 R");
        Assert.Equal(new[] { "graph" }, host.Inputs);
        AssertClosed(storage, host, result);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task CheckUsesThePatchedStreamBeforeFollowUpInspection(bool repair, bool job)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(Source, Pdf(repair, false), QpdfInputOwnership.Transfer);
        AddJson(storage, Patch(!repair));
        ObservingHost host = new(storage, null);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"source","updateFromJson":"graph","showPages":"","check":"","showObject":"4,7"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["source", "--update-from-json=graph", "--show-pages", "--check", "--show-object=4,7"]), CancellationToken.None);
        Assert.Equal(repair ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
        if (repair)
        {
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing && d.Message.Contains("patched-graph", StringComparison.Ordinal));
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageListing && d.Message == "    4 7 R");
        }
        else
        {
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.CheckFailed && d.Message.Contains("stream 4", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Diagnostics, d => d.Code is QpdfDiagnosticCode.ObjectListing or QpdfDiagnosticCode.PageListing);
        }
        Assert.Equal(new[] { "source", "graph" }, host.Inputs);
        AssertClosed(storage, host, result);
    }

    [Fact]
    public async Task PatchCanRepairThePageTreeBeforeTraversal()
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(Source, Pdf(false, true), QpdfInputOwnership.Transfer);
        AddJson(storage, """
            {"qpdf":[{"jsonversion":2},{"obj:3 0 R":{"value":{
              "/Type":"/Page","/Parent":"2 0 R","/Contents":"4 7 R","/MediaBox":[0,0,100,100],"/Resources":{}
            }}}]}
            """u8.ToArray());
        Assert.Equal(QpdfExitCode.Error, (await Qpdf.ExecuteAsync(storage, new(["source", "--check"]), CancellationToken.None)).ExitCode);
        ObservingHost host = new(storage, null);
        QpdfExecutionResult patched = await Qpdf.ExecuteAsync(host, new(["source", "--update-from-json=graph", "--check", "--show-npages"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, patched.ExitCode);
        Assert.Contains(patched.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount && d.Message == "source: 1");
        AssertClosed(storage, host, patched);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProtectedInputAuthenticatesBeforeThePatch(bool valid)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(Source, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r6-ascii.pdf")), QpdfInputOwnership.Transfer);
        AddJson(storage, """{"qpdf":[{"jsonversion":2},{}]}"""u8.ToArray());
        ObservingHost host = new(storage, null);
        string password = valid ? "reader" : "wrong";
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["source", "--password=" + password,
            "--update-from-json=graph", "--show-encryption", "--check"]), CancellationToken.None);
        Assert.Equal(valid ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
        if (valid)
        {
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.EncryptionReport && d.Message == "R = 6");
            Assert.Equal(new[] { "source", "graph" }, host.Inputs);
        }
        else
        {
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidPassword);
            Assert.Equal(new[] { "source" }, host.Inputs);
        }
        AssertClosed(storage, host, result);
    }

    [Theory]
    [InlineData("output")]
    [InlineData("--show-xref")]
    public async Task OutputAndPhysicalListingRejectBeforeOpeningInput(string option)
    {
        using InMemoryQpdfHost storage = new();
        ObservingHost host = new(storage, null);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["graph", "--json-input", "--check", option]), CancellationToken.None);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments);
        Assert.Empty(host.Inputs);
        AssertClosed(storage, host, result);
    }

    [Fact]
    public async Task LimitsAndCancellationCloseEveryInputWithoutPublication()
    {
        using InMemoryQpdfHost storage = new(); AddJson(storage, Encoding.UTF8.GetBytes(Creation));
        ObservingHost constrained = new(storage, null);
        QpdfExecutionResult limit = await Qpdf.ExecuteAsync(constrained, new(["graph", "--json-input", "--check"]),
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxContainerSize = 5 }), CancellationToken.None);
        Assert.Contains(limit.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        AssertClosed(storage, constrained, limit);

        storage.AddInput(Source, Pdf(true, false), QpdfInputOwnership.Transfer);
        AddJson(storage, Patch(false));
        using CancellationTokenSource cancellation = new();
        ObservingHost canceled = new(storage, cancellation);
        QpdfExecutionResult canceledResult = await Qpdf.ExecuteAsync(canceled,
            new(["source", "--update-from-json=graph", "--check"]), cancellation.Token);
        Assert.Equal(QpdfExecutionStatus.Canceled, canceledResult.Status);
        Assert.Equal(new[] { "source", "graph" }, canceled.Inputs);
        AssertClosed(storage, canceled, canceledResult);
    }

    private static byte[] Pdf(bool badFilter, bool badPage)
        => PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, $"<< /Type /{(badPage ? "Invalid" : "Page")} /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 7 R /Resources << >> >>", null),
            (4, $"<< /Length {Program.Length}{(badFilter ? " /Filter /FlateDecode" : "")} >>", Program)
        ], 1, new Dictionary<int, int> { [4] = 7 });

    private static byte[] Patch(bool damage)
    {
        Dictionary<string, string> dict = new() { ["/ReviewMarker"] = "u:patched-graph" };
        if (damage) { dict["/Filter"] = "/FlateDecode"; }
        Dictionary<string, object> objects = new() { ["obj:4 7 R"] = new { stream = new { dict, data = Convert.ToBase64String(Program) } } };
        return JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2 }, objects } });
    }

    private static void AddJson(InMemoryQpdfHost host, byte[] bytes) => host.AddInput(Graph, bytes, QpdfInputOwnership.Transfer);

    private static void AssertClosed(InMemoryQpdfHost storage, ObservingHost host, QpdfExecutionResult result)
    {
        Assert.Equal(0, host.Outputs);
        Assert.Equal(0, storage.OpenLeaseCount);
        Assert.Equal(0, result.Counters.OpenHandles);
        Assert.Null(result.OutputBytes);
    }

    private sealed class ObservingHost(InMemoryQpdfHost storage, CancellationTokenSource? cancellation) : IQpdfHost
    {
        internal List<string> Inputs { get; } = [];
        internal int Outputs { get; private set; }

        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            Inputs.Add(path.Value);
            IQpdfInput input = await storage.OpenInputAsync(path, token);
            if (path == Graph) { cancellation?.Cancel(); }
            return input;
        }

        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            Outputs++;
            return storage.CreateOutputAsync(path, token);
        }
    }
}
