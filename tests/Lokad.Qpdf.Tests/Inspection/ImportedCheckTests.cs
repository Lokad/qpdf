using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class ImportedCheckTests
{
    private static readonly QpdfPath Graph = QpdfPath.From("graph"), Source = QpdfPath.From("source");
    private static readonly QpdfPath Payload = QpdfPath.From("payload"), Output = QpdfPath.From("output");
    private static readonly QpdfPath FollowingPatch = QpdfPath.From("patch");
    private static readonly byte[] Program = "q\n0 0 31 40 re f\nQ\n"u8.ToArray();
    private static readonly byte[] AttachmentBytes = [0, 254, 128, 10, 255, 32, 26, 1, 127];
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

    private const string AttachmentGraph = """
        {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
          "obj:17 9 R":{"value":{"/Type":"/Catalog","/Pages":"113 5 R",
            "/Names":{"/EmbeddedFiles":{"/Names":["u:key","41 2 R"]}}}},
          "obj:113 5 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["19 7 R"]}},
          "obj:19 7 R":{"value":{"/Type":"/Page","/Parent":"113 5 R","/MediaBox":[0,0,100,100],
            "/Contents":"23 4 R","/Resources":{}}},
          "obj:23 4 R":{"stream":{"dict":{},"data":"cQpRCg=="}},
          "obj:41 2 R":{"value":{"/Type":"/Filespec","/F":"u:example.bin","/EF":{"/F":"67 3 R"}}},
          "obj:67 3 R":{"stream":{"dict":{"/Type":"/EmbeddedFile"},"datafile":"payload"}},
          "trailer":{"value":{"/Root":"17 9 R","/Size":114}}
        }]}
        """;

    private const string RoleGraph = """
        {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
          "obj:17 9 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/AcroForm":"5 0 R",
            "/Metadata":"7 0 R","/PageLabels":"8 0 R","/Audit":"9 0 R","/MarkInfo":{"/Marked":true},
            "/StructTreeRoot":{"/Type":"/StructTreeRoot","/K":[]}}},
          "obj:2 0 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["3 0 R"]}},
          "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,100,100]}},
          "obj:5 0 R":{"value":{"/Fields":[]}},
          "obj:6 0 R":{"value":{"/ModDate":"u:D:20240101000000Z","/Title":"u:original"}},
          "obj:7 0 R":{"stream":{"dict":{"/Type":"/Metadata","/Subtype":"/XML"},"data":"PHRlc3QvPg=="}},
          "obj:8 0 R":{"value":{"/Nums":[0,{"/S":"/D","/P":"u:old-"}]}},
          "obj:9 0 R":{"value":{"/StoredCatalog":"17 9 R","/StoredInfo":"6 0 R","/StoredLabels":"8 0 R"}},
          "trailer":{"value":{"/Root":"17 9 R","/Info":"6 0 R","/Size":18}}
        }]}
        """;

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("compose")]
    public async Task BoundCatalogRemovalsApplyBeforeCheckedListing(string operation)
    {
        using InMemoryQpdfHost storage = new();
        AddJson(storage, Encoding.UTF8.GetBytes(RoleGraph));
        storage.AddInput(Source, Pdf(false, false), QpdfInputOwnership.Transfer);
        storage.AddInput(FollowingPatch, Encoding.UTF8.GetBytes(RoleGraph.Replace("\"/Audit\":\"9 0 R\"",
            "\"/Audit\":\"9 0 R\",\"/Patched\":true", StringComparison.Ordinal)), QpdfInputOwnership.Transfer);
        string[] settings = operation switch
        {
            "create" => ["graph", "--json-input"],
            "update" => ["source", "--update-from-json=graph"],
            _ => ["graph", "--json-input", "--update-from-json=patch"],
        };
        ObservingHost host = new(storage, null);
        async Task<string> Show(string selector, params string[] flags)
        {
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([.. settings, .. flags,
                "--show-object=" + selector]), CancellationToken.None);
            AssertClosed(storage, host, result);
            return ObjectListing(result);
        }

        string active = await Show("17,9", "--check", "--remove-metadata", "--remove-acroform", "--remove-structure");
        Assert.Contains("/Audit 9 0 R", active, StringComparison.Ordinal);
        Assert.Equal(operation == "compose", active.Contains("/Patched true", StringComparison.Ordinal));
        string unedited = await Show("17,9");
        foreach (string role in new[] { "/Metadata", "/AcroForm", "/MarkInfo", "/StructTreeRoot" })
        {
            Assert.Contains(role, unedited, StringComparison.Ordinal);
            Assert.DoesNotContain(role, active, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task BoundInfoAndLabelRemovalsKeepStoredAliases()
    {
        using InMemoryQpdfHost storage = new();
        AddJson(storage, Encoding.UTF8.GetBytes(RoleGraph));
        ObservingHost host = new(storage, null);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """
            {"inputFile":"graph","jsonInput":"","removeInfo":"","removePageLabels":true,"showObject":"trailer"}
            """, CancellationToken.None);
        AssertClosed(storage, host, result);
        string trailer = ObjectListing(result);
        Assert.Contains("/Info << /ModDate (D:20240101000000Z) >>", trailer, StringComparison.Ordinal);
        Assert.Contains("/Audit 9 0 R", trailer, StringComparison.Ordinal);
        foreach (string retired in new[] { "/Title", "/Metadata", "/PageLabels" })
        { Assert.DoesNotContain(retired, trailer, StringComparison.Ordinal); }

        string[] flags = ["graph", "--json-input", "--remove-info", "--remove-page-labels"];
        foreach ((string selector, string retained) in new[] {
            ("6", "/Title (original)"), ("8", "/P (old-)"), ("9", "/StoredCatalog 17 9 R") })
        {
            result = await Qpdf.ExecuteAsync(host, new([.. flags, "--show-object=" + selector]), CancellationToken.None);
            Assert.Contains(retained, ObjectListing(result), StringComparison.Ordinal);
            AssertClosed(storage, host, result);
        }
        result = await Qpdf.ExecuteAsync(host, new([.. flags, "--show-object=17,0"]), CancellationToken.None);
        Assert.Equal("null", ObjectListing(result));
        AssertClosed(storage, host, result);
    }

    private static string ObjectListing(QpdfExecutionResult result)
    {
        Assert.True(result.ExitCode == QpdfExitCode.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.Message)));
        return Assert.Single(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.ObjectListing).Message;
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAndPatchInspectionsUseTheEffectiveGraph(bool damaged)
    {
        using InMemoryQpdfHost storage = new();
        AddJson(storage, Encoding.UTF8.GetBytes(Creation));
        byte[] patch = damaged
            ? """{"qpdf":[{"jsonversion":2},{"obj:23 4 R":{"stream":{"dict":{"/Filter":"/FlateDecode"}}},"obj:47 8 R":{"value":"u:declared-second"}}]}"""u8.ToArray()
            : """{"qpdf":[{"jsonversion":2},{"obj:47 8 R":{"value":"u:declared-second"}}]}"""u8.ToArray();
        storage.AddInput(FollowingPatch, patch, QpdfInputOwnership.Transfer);
        ObservingHost host = new(storage, null);
        QpdfExecutionResult result = damaged
            ? await Qpdf.ExecuteJobAsync(host,
                """{"inputFile":"graph","jsonInput":"","updateFromJson":"patch","check":"","showNpages":"","showObject":"47,8"}""",
                CancellationToken.None)
            : await Qpdf.ExecuteAsync(host,
                new(["graph", "--json-input", "--update-from-json=patch", "--check", "--show-npages", "--show-object=47,8"]),
                CancellationToken.None);
        Assert.Equal(damaged ? QpdfExitCode.Error : QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(new[] { "graph", "patch" }, host.Inputs);
        AssertClosed(storage, host, result);
        if (damaged)
        {
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.CheckFailed && d.Message.Contains("stream 23", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Diagnostics, d => d.Code is QpdfDiagnosticCode.ObjectListing or QpdfDiagnosticCode.PageCount);
            return;
        }

        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.CheckSkipped && d.Message.Contains("cross-reference offsets", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount && d.Message == "graph: 1");
        Assert.Equal("(declared-second)", Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message);
        QpdfExecutionResult stale = await Qpdf.ExecuteJobAsync(storage,
            """{"inputFile":"graph","jsonInput":"","updateFromJson":"patch","showObject":"47,7"}""", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, stale.ExitCode);
        Assert.Equal("null", Assert.Single(stale.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message);
    }

    [Fact]
    public async Task StandaloneListingsUseTheBoundPairsWithoutDecodingUnrelatedContent()
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(Source, Pdf(false, false), QpdfInputOwnership.Transfer);
        AddJson(storage, Patch(true));
        ObservingHost host = new(storage, null);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["source", "--update-from-json=graph", "--show-npages", "--show-pages", "--show-object=4,7"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount && d.Message == "source: 1");
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageListing && d.Message == "    4 7 R");
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing && d.Message.Contains("patched-graph", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is QpdfDiagnosticCode.CheckSkipped or QpdfDiagnosticCode.CheckFailed);
        Assert.Equal(0L, result.Counters.TotalDecodedBytes);
        Assert.Equal(new[] { "source", "graph" }, host.Inputs);
        AssertClosed(storage, host, result);

        QpdfExecutionResult stale = await Qpdf.ExecuteAsync(storage,
            new(["source", "--update-from-json=graph", "--show-object=4,6"]), CancellationToken.None);
        Assert.Equal("null", Assert.Single(stale.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message);
    }

    [Fact]
    public async Task StandaloneObjectStreamReturnsTheExactDeclaredPayload()
    {
        using InMemoryQpdfHost storage = new(); AddJson(storage, Encoding.UTF8.GetBytes(Creation));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(storage,
            new(["graph", "--json-input", "--show-object=23,4", "--raw-stream-data"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal("q\nQ\n"u8.ToArray(), Assert.IsType<ReadOnlyMemory<byte>>(result.OutputBytes).ToArray());
        Assert.Equal(0, storage.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AttachmentsShareBoundListingAndExtractionSuppression(bool check, bool damage)
    {
        using InMemoryQpdfHost storage = new();
        SeedAttachment(storage, damage);
        ObservingHost host = new(storage, null);
        Dictionary<string, string> options = new()
        {
            ["inputFile"] = Graph.Value, ["jsonInput"] = "", ["listAttachments"] = "", ["showAttachment"] = "key",
        };
        if (check) { options["check"] = ""; }
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(options), CancellationToken.None);
        Assert.Equal(damage ? QpdfExitCode.Error : QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(new[] { "graph", "payload" }, host.Inputs);
        Assert.Equal(0, host.Outputs);
        Assert.Equal(0, storage.OpenLeaseCount);
        if (damage)
        {
            Assert.Null(result.OutputBytes);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.CheckFailed);
            Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.AttachmentListing);
            return;
        }
        Assert.Equal(check, result.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.CheckSkipped));
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.AttachmentListing && d.Message == "key -> 67,3");
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.AttachmentListing && d.Message == "  filename: example.bin");
        Assert.Equal(AttachmentBytes, Assert.IsType<ReadOnlyMemory<byte>>(result.OutputBytes).ToArray());
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("graph-alias")]
    [InlineData("sidecar-alias")]
    [InlineData("limit")]
    public async Task NamedAttachmentUsesAClosedBoundGraphAndProtectsPriorOutput(string scenario)
    {
        using InMemoryQpdfHost storage = new(); SeedAttachment(storage, false);
        byte[] prior = "prior output"u8.ToArray();
        await using (IQpdfOutputTransaction seed = await storage.CreateOutputAsync(Output, CancellationToken.None))
        {
            await seed.WriteAsync(prior, CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }
        ObservingHost host = new(storage, null);
        if (scenario == "graph-alias") { host.Aliases.Add(Output, Graph); }
        if (scenario == "sidecar-alias") { host.Aliases.Add(Output, Payload); }
        QpdfLimits limits = scenario == "limit"
            ? QpdfLimits.Default with { MaxOutputBytes = AttachmentBytes.Length - 1 } : QpdfLimits.Default;
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["graph", "--json-input", "--show-attachment=key", "output"]),
            new QpdfExecutionOptions(limits), CancellationToken.None);
        Assert.True(storage.TryGetOutput(Output, out byte[] output));
        Assert.Null(result.OutputBytes);
        Assert.Equal(0, storage.OpenLeaseCount);
        Assert.Equal(0, result.Counters.OpenHandles);
        if (scenario == "publish")
        {
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.Equal(new[] { "graph", "payload", "output" }, host.Inputs);
            Assert.Equal(1, host.Outputs);
            Assert.Equal(0, host.OpenLeasesAtStaging);
            Assert.Equal(AttachmentBytes, output);
            Assert.Equal(AttachmentBytes.Length, result.Counters.TotalOutputBytes);
        }
        else
        {
            Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Equal(0, host.Outputs);
            Assert.Equal(prior, output);
            if (scenario is "graph-alias" or "sidecar-alias")
            {
                Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments && d.Message.Contains("aliases an input", StringComparison.Ordinal));
            }
            else { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodedBoundAttachmentSurvivesSourceRetirement(bool named)
    {
        ReadOnlyMemory<byte> bytes;
        using (InMemoryQpdfHost storage = new())
        {
            string graph = AttachmentGraph.Replace("\"/Type\":\"/EmbeddedFile\"", "\"/Type\":\"/EmbeddedFile\",\"/Filter\":\"/ASCIIHexDecode\"", StringComparison.Ordinal);
            AddJson(storage, Encoding.UTF8.GetBytes(graph));
            storage.AddInput(Payload, Encoding.ASCII.GetBytes(Convert.ToHexString(AttachmentBytes) + ">"), QpdfInputOwnership.Transfer);
            ObservingHost host = new(storage, null);
            string[] operation = ["graph", "--json-input", "--show-attachment=key"];
            QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = AttachmentBytes.Length };
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(named ? [.. operation, Output.Value] : operation),
                new(limits), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.Equal(AttachmentBytes.Length, result.Counters.TotalDecodedBytes);
            Assert.Equal(AttachmentBytes.Length, result.Counters.TotalOutputBytes);
            Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.Equal(0L, storage.OpenLeaseCount);
            if (named)
            {
                Assert.Null(result.OutputBytes);
                Assert.Equal(1, host.Outputs);
                Assert.Equal(0L, host.OpenLeasesAtStaging);
                Assert.True(storage.TryGetOutput(Output, out byte[] output));
                bytes = output;
            }
            else
            {
                Assert.Equal(0, host.Outputs);
                bytes = Assert.IsType<ReadOnlyMemory<byte>>(result.OutputBytes);
            }
        }
        Assert.Equal(AttachmentBytes, bytes.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("patch")]
    [InlineData("second-payload")]
    public async Task ComposedNamedExtractionProtectsThePatchAndItsSidecar(string? alias)
    {
        using InMemoryQpdfHost storage = new();
        SeedAttachment(storage, false);
        storage.AddInput(FollowingPatch,
            """{"qpdf":[{"jsonversion":2},{"obj:67 3 R":{"stream":{"dict":{"/Type":"/EmbeddedFile"},"datafile":"second-payload"}}}]}"""u8.ToArray(),
            QpdfInputOwnership.Transfer);
        QpdfPath secondSidecar = QpdfPath.From("second-payload");
        byte[] replacement = [255, 0, 127, 238];
        storage.AddInput(secondSidecar, replacement, QpdfInputOwnership.Copy);
        byte[] prior = "prior output"u8.ToArray();
        await using (IQpdfOutputTransaction seed = await storage.CreateOutputAsync(Output, CancellationToken.None))
        {
            await seed.WriteAsync(prior, CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }

        ObservingHost host = new(storage, null);
        if (alias is not null) { host.Aliases.Add(Output, QpdfPath.From(alias)); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["graph", "--json-input", "--update-from-json=patch", "--show-attachment=key", "output"]), CancellationToken.None);
        Assert.Equal(alias is null ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
        Assert.Contains("second-payload", host.Inputs);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0, storage.OpenLeaseCount);
        Assert.True(storage.TryGetOutput(Output, out byte[] bytes));
        if (alias is null)
        {
            Assert.Equal(1, host.Outputs);
            Assert.Equal(0, host.OpenLeasesAtStaging);
            Assert.Equal(replacement, bytes);
        }
        else
        {
            Assert.Equal(0, host.Outputs);
            Assert.Equal(prior, bytes);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments && d.Message.Contains("aliases an input", StringComparison.Ordinal));
        }
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
            "--update-from-json=graph", "--show-encryption", "--show-npages"]), CancellationToken.None);
        Assert.Equal(valid ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
        if (valid)
        {
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.EncryptionReport && d.Message == "R = 6");
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount && d.Message == "source: 1");
            Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.CheckSkipped);
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

    private static void SeedAttachment(InMemoryQpdfHost host, bool damage)
    {
        string json = damage
            ? AttachmentGraph.Replace("\"dict\":{},\"data\":\"cQpRCg==\"", "\"dict\":{\"/Filter\":\"/FlateDecode\"},\"data\":\"cQpRCg==\"", StringComparison.Ordinal)
            : AttachmentGraph;
        AddJson(host, Encoding.UTF8.GetBytes(json));
        host.AddInput(Payload, AttachmentBytes, QpdfInputOwnership.Copy);
    }

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
        internal Dictionary<QpdfPath, QpdfPath> Aliases { get; } = [];
        internal int Outputs { get; private set; }
        internal long OpenLeasesAtStaging { get; private set; }

        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            Inputs.Add(path.Value);
            IQpdfInput input = await storage.OpenInputAsync(Aliases.GetValueOrDefault(path, path), token);
            if (path == Graph) { cancellation?.Cancel(); }
            return input;
        }

        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            Outputs++;
            OpenLeasesAtStaging = storage.OpenLeaseCount;
            return storage.CreateOutputAsync(path, token);
        }
    }
}
