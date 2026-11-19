using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonSplitTests
{
    private static readonly QpdfPath JsonPath = QpdfPath.From("graph.json"), Source = QpdfPath.From("source.pdf"),
        First = QpdfPath.From("part-1-2.pdf"), Last = QpdfPath.From("part-3.pdf");
    private static readonly QpdfPath Body = QpdfPath.From("body.bin");
    private static readonly byte[][] Programs = ["q 1 0 0 1 1 0 cm Q\n"u8.ToArray(), "q 1 0 0 1 2 0 cm Q\n"u8.ToArray(),
        "q 1 0 0 1 3 0 cm Q\n"u8.ToArray()];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CreationSplitsInheritedPagesAndContinuesLabels()
    {
        using InMemoryQpdfHost host = new();
        AddCreation(host, JsonPath, Body);
        QpdfExecutionResult result = await Create(host, JsonPath, QpdfLimits.Default, CancellationToken.None, "--qdf", "--normalize-content=n");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await AssertPart(host, First, 7, [0, 1], false);
        await AssertPart(host, Last, 9, [2], true);
        Assert.False(host.TryGetOutput(QpdfPath.From("part.pdf"), out byte[] _));
        Assert.Contains("%% Original object ID: 3 0", Encoding.Latin1.GetString(Output(host, First)), StringComparison.Ordinal);
        Assert.Contains("%% Original object ID: 5 0", Encoding.Latin1.GetString(Output(host, Last)), StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ReplacingAVisitedParentInTheBoundPoolInvalidatesItsAncestry()
    {
        using RecordingJobHost host = new(null, null, "success", true);
        await SeedOutputs(host.Inner);
        host.Inner.AddInput(JsonPath, """
            {"qpdf":[{"jsonversion":2,"pdfversion":"1.7"},{
              "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R"}},
              "obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["3 0 R","4 0 R","5 0 R"],"/Count":3,"/MediaBox":[0,0,10,10]}},
              "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"6 0 R"}},
              "obj:4 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R"}},
              "obj:5 0 R":{"value":{"/Type":"/Page","/Parent":"6 0 R"}},
              "obj:6 0 R":{"value":{"/Type":"/Pages","/Parent":"4 0 R"}},
              "trailer":{"value":{"/Root":"1 0 R","/Size":7}}
            }]}
            """u8.ToArray(), QpdfInputOwnership.Transfer);

        QpdfExecutionResult result = await Create(host, JsonPath, QpdfLimits.Default, CancellationToken.None);

        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.InvalidArguments
            && item.Message.Contains("Unresolvable page parent.", StringComparison.Ordinal));
        Assert.Empty(host.CreatedPaths);
        Assert.Equal("prior"u8.ToArray(), Output(host.Inner, First));
        Assert.Equal("prior"u8.ToArray(), Output(host.Inner, Last));
        ExecutionJobFixtures.AssertClosed(host, result);
    }

    [Fact]
    public async Task JobUpdateSplitsThePatchedPageOrder()
    {
        using InMemoryQpdfHost host = new();
        AddUpdate(host, Source);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """
            {"inputFile":"source.pdf","updateFromJson":"graph.json","splitPages":"2","outputFile":"part.pdf"}
            """, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await AssertPart(host, First, 7, [2, 0], true);
        await AssertPart(host, Last, 9, [1], false);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SplitCaptureContainsTheLastBoundGroup()
    {
        using InMemoryQpdfHost host = new();
        AddUpdate(host, Source);
        QpdfPath capture = QpdfPath.From("pass1.bin");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([
            Source.Value, "--update-from-json=graph.json", "--linearize", "--linearize-pass1=pass1.bin",
            "--compress-streams=n", "--split-pages=2", "part.pdf"]), CancellationToken.None);

        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await AssertPart(host, First, 7, [2, 0], true);
        await AssertPart(host, Last, 9, [1], false);
        byte[] pass1 = Output(host, capture);
        LinearizedWriterTests.AssertFirstPassMatchesLayout(Output(host, Last), pass1);
        Assert.True(pass1.AsSpan().IndexOf(Programs[1]) >= 0);
        Assert.True(pass1.AsSpan().IndexOf(Programs[0]) < 0);
        Assert.True(pass1.AsSpan().IndexOf(Programs[2]) < 0);
    }

    [Fact]
    public async Task SplitCaptureCannotAliasAnAcquiredSidecar()
    {
        using RecordingJobHost host = new(null, null, "success", true);
        await SeedOutputs(host.Inner);
        AddCreation(host.Inner, JsonPath, Body);
        QpdfPath capture = QpdfPath.From("pass1.bin");

        QpdfExecutionResult result = await Create(new OutputAliasHost(host, capture, Body), JsonPath,
            QpdfLimits.Default, CancellationToken.None, "--linearize", "--linearize-pass1=pass1.bin");

        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.InvalidArguments);
        Assert.Contains(capture.Value, diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(Body, host.OpenedPaths[^1]);
        Assert.Empty(host.CreatedPaths);
        Assert.Equal("prior"u8.ToArray(), Output(host.Inner, First));
        Assert.Equal("prior"u8.ToArray(), Output(host.Inner, Last));
        ExecutionJobFixtures.AssertClosed(host, result);
    }

    [Fact]
    public async Task AuthoredLabelsFollowThePatchedOrderAndContinueInLaterParts()
    {
        using InMemoryQpdfHost host = new();
        AddUpdate(host, Source);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """
            {"inputFile":"source.pdf","updateFromJson":"graph.json","splitPages":"2","outputFile":"part.pdf",
             "setPageLabels":["1:r/7/NEW-"]}
            """, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await AssertPart(host, First, 7, [2, 0], true, null, "r", "NEW-");
        await AssertPart(host, Last, 9, [1], false, null, "r", "NEW-");
    }

    [Fact]
    public async Task InvalidAuthoredLabelRefusesBeforeStagingAnyParts()
    {
        using RecordingJobHost host = new(null, null, "success", true);
        await SeedOutputs(host.Inner);
        AddCreation(host.Inner, JsonPath, Body);
        QpdfExecutionResult result = await Create(host, JsonPath, QpdfLimits.Default, CancellationToken.None,
            "--set-page-labels", "1:D", "4:r", "--");
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
        Assert.Contains("outside 1..3", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(host.CreatedPaths);
        Assert.Equal("prior"u8.ToArray(), Output(host.Inner, First));
        Assert.Equal("prior"u8.ToArray(), Output(host.Inner, Last));
        ExecutionJobFixtures.AssertClosed(host, result);
    }

    [Fact]
    public async Task SharedResourceMemberIsPackedInEachPart()
    {
        using InMemoryQpdfHost host = new();
        AddCreation(host, JsonPath, Body);
        QpdfExecutionResult result = await Create(host, JsonPath, QpdfLimits.Default, CancellationToken.None,
            "--object-streams=generate");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        foreach (QpdfPath output in new[] { First, Last })
        {
            QpdfPath read = QpdfPath.From("read-" + output.Value);
            host.AddInput(read, Output(host, output), QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, read, CancellationToken.None);
            PdfDictionary resources = Assert.IsType<PdfDictionary>(
                Resolve(document, Value(document.PageReferences[0].Page, "Resources")));
            PdfReference shared = Assert.IsType<PdfReference>(Value(resources, "AuditShared"));
            Assert.Equal(XrefEntryKind.Compressed, document.Engine.XRefEntries[shared.ObjectNumber].Kind);
            PdfDictionary member = Assert.IsType<PdfDictionary>(document.GetObject(shared));
            Assert.Equal(19L, Assert.IsType<PdfInteger>(Value(member, "Audit")).Value);
        }
    }

    [Fact]
    public async Task ZeroPagesLeaveTargetsUntouched()
    {
        using RecordingJobHost host = new(null, null, "success", true);
        await SeedOutputs(host.Inner);
        host.Inner.AddInput(JsonPath, """
            {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
              "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R"}},
              "obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":[],"/Count":0}},
              "trailer":{"value":{"/Root":"1 0 R"}}
            }]}
            """u8.ToArray(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Create(host, JsonPath, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(host.CreatedPaths);
        Assert.Equal("prior"u8.ToArray(), Output(host.Inner, First));
        Assert.Equal("prior"u8.ToArray(), Output(host.Inner, Last));
        ExecutionJobFixtures.AssertClosed(host, result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RepeatedPageWidgetsKeepTheirOwnPageAndRegistration(int groupSize)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(JsonPath, """
            {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
              "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R",
                "/AcroForm":{"/Fields":["4 0 R"],"/CO":["4 0 R"]}}},
              "obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["3 0 R","3 0 R"],"/Count":2}},
              "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,72,72],"/Resources":{},
                "/Annots":["5 0 R"]}},
              "obj:4 0 R":{"value":{"/FT":"/Tx","/T":"u:Review","/V":"u:Stored","/Kids":["5 0 R"]}},
              "obj:5 0 R":{"value":{"/Type":"/Annot","/Subtype":"/Widget","/Parent":"4 0 R","/P":"3 0 R",
                "/Rect":[0,0,10,10]}},
              "trailer":{"value":{"/Root":"1 0 R"}}
            }]}
            """u8.ToArray(), QpdfInputOwnership.Transfer);
        string size = groupSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new([JsonPath.Value, "--json-input", "--split-pages=" + size, "--object-streams=generate", "part.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);

        string[] targets = groupSize == 1 ? ["part-1.pdf", "part-2.pdf"] : ["part-1-2.pdf"];
        int occurrence = 0;
        foreach (string target in targets)
        {
            QpdfPath read = QpdfPath.From("check-" + target);
            host.AddInput(read, Output(host, QpdfPath.From(target)), QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, read, CancellationToken.None);
            Assert.Equal(groupSize, document.PageCount);
            Assert.Equal(new PdfVersion(1, 5), document.HeaderVersion);
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(document, Value(document.Engine.Trailer, "Root")));
            PdfDictionary form = Assert.IsType<PdfDictionary>(Resolve(document, Value(catalog, "AcroForm")));
            PdfArray fields = Assert.IsType<PdfArray>(Value(form, "Fields"));
            PdfArray order = Assert.IsType<PdfArray>(Value(form, "CO"));
            Assert.Equal(groupSize, fields.Count);
            Assert.Equal(groupSize, order.Count);
            HashSet<PdfReference> widgets = new();
            for (int i = 0; i < groupSize; i++)
            {
                var page = document.PageReferences[i];
                PdfArray annotations = Assert.IsType<PdfArray>(Value(page.Page, "Annots"));
                Assert.Equal(1, annotations.Count);
                PdfReference widgetId = Assert.IsType<PdfReference>(annotations[0]);
                Assert.True(widgets.Add(widgetId));
                Assert.Equal(XrefEntryKind.Compressed, document.Engine.XRefEntries[widgetId.ObjectNumber].Kind);
                PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(document, widgetId));
                Assert.Equal(page.Source, Value(widget, "P"));
                PdfReference fieldId = Assert.IsType<PdfReference>(Value(widget, "Parent"));
                Assert.Equal(fields[i], fieldId);
                Assert.Equal(order[i], fieldId);
                PdfDictionary field = Assert.IsType<PdfDictionary>(Resolve(document, fieldId));
                PdfArray fieldKids = Assert.IsType<PdfArray>(Value(field, "Kids"));
                Assert.Equal(1, fieldKids.Count);
                Assert.Equal(widgetId, fieldKids[0]);
                string expected = occurrence++ == 0 ? "Review" : "Review+1";
                Assert.Equal(expected, Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Value(field, "T")).ValueSpan));
            }
        }
        Assert.Equal(2, occurrence);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task InspectionAndReplacementRejectSplittingBeforeInput()
    {
        using RecordingJobHost host = new(null, null, "success", true);
        QpdfExecutionResult inspection = await Qpdf.ExecuteAsync(host,
            new([JsonPath.Value, "--json-input", "--check", "--split-pages"]), CancellationToken.None);
        QpdfExecutionResult replacement = await Qpdf.ExecuteAsync(host,
            new([Source.Value, "--update-from-json=graph.json", "--replace-input", "--split-pages"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(inspection.Diagnostics).Code);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(replacement.Diagnostics).Code);
        Assert.Empty(host.OpenedPaths);
        Assert.Empty(host.CreatedPaths);
    }

    [Theory]
    [InlineData("source", false)]
    [InlineData("json", false)]
    [InlineData("sidecar", false)]
    [InlineData("sidecar", true)]
    public async Task ExpandedTargetsCannotAliasInputs(string role, bool identityAlias)
    {
        using RecordingJobHost host = new(null, null, "success", true);
        await SeedOutputs(host.Inner);
        QpdfPath input = role == "source" ? Last : Source;
        QpdfPath json = role == "json" ? Last : JsonPath;
        QpdfPath payload = role == "sidecar" && !identityAlias ? Last : Body;
        if (role == "source") { AddUpdate(host.Inner, input); }
        else { AddCreation(host.Inner, json, payload); }
        IQpdfHost operation = identityAlias ? new OutputAliasHost(host, Last, payload) : host;
        QpdfExecutionResult result = role == "source"
            ? await Qpdf.ExecuteAsync(operation, new([input.Value, "--update-from-json=graph.json", "--split-pages=2", "part.pdf"]), CancellationToken.None)
            : await Create(operation, json, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments && d.Message.Contains("aliases an input", StringComparison.Ordinal));
        Assert.Empty(host.CreatedPaths);
        Assert.Equal("prior"u8.ToArray(), Output(host.Inner, First));
        Assert.Equal("prior"u8.ToArray(), Output(host.Inner, Last));
        ExecutionJobFixtures.AssertClosed(host, result);
    }

    [Fact]
    public async Task LastCommitFailureKeepsTheFirstCompletedPart()
    {
        using RecordingJobHost host = new("commit", Last, "fault", true);
        await SeedOutputs(host.Inner);
        AddCreation(host.Inner, JsonPath, Body);
        Task<QpdfExecutionResult> pending = Create(host, JsonPath, QpdfLimits.Default, CancellationToken.None);
        try
        {
            await host.Entered.WaitAsync(Timeout);
            Assert.Equal(First, Assert.Single(host.Committed));
            Assert.Equal("prior"u8.ToArray(), Output(host.Inner, Last));
            host.Resume();
            QpdfExecutionResult result = await pending.WaitAsync(Timeout);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(First, Assert.Single(host.Committed));
            await AssertPart(host.Inner, First, 7, [0, 1], false);
            Assert.Equal("prior"u8.ToArray(), Output(host.Inner, Last));
            ExecutionJobFixtures.AssertClosed(host, result);
        }
        finally { host.Resume(); await pending; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationRefusalAndCanceledWriteKeepBothPriorTargets(bool cancel)
    {
        using RecordingJobHost host = new(cancel ? "write" : null, First, "success", true);
        await SeedOutputs(host.Inner);
        AddCreation(host.Inner, JsonPath, Body);
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = cancel ? QpdfLimits.Default : QpdfLimits.Default with { MaxOutputBytes = 64 };
        Task<QpdfExecutionResult> pending = Create(host, JsonPath, limits, cancellation.Token);
        try
        {
            if (cancel) { await host.Entered.WaitAsync(Timeout); cancellation.Cancel(); }
            QpdfExecutionResult result = await pending.WaitAsync(Timeout);
            Assert.Equal(cancel ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            if (!cancel) { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded); }
            Assert.Equal(cancel ? new[] { First } : Array.Empty<QpdfPath>(), host.CreatedPaths);
            Assert.Empty(host.Committed);
            Assert.Equal("prior"u8.ToArray(), Output(host.Inner, First));
            Assert.Equal("prior"u8.ToArray(), Output(host.Inner, Last));
            ExecutionJobFixtures.AssertClosed(host, result);
        }
        finally { host.Resume(); await pending; }
    }

    [Fact]
    public async Task FreshProtectionUsesDifferentAuthenticatedKeysForTheParts()
    {
        using InMemoryQpdfHost host = new();
        AddCreation(host, JsonPath, Body);
        QpdfExecutionResult result = await Create(host, JsonPath, QpdfLimits.Default, CancellationToken.None,
            "--encrypt", "reader", "owner", "256", "--", "--object-streams=generate", "--linearize");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        QpdfPassword reader = QpdfPassword.FromText("reader");
        await AssertPart(host, First, 7, [0, 1], false, reader);
        await AssertPart(host, Last, 9, [2], true, reader);
        byte[] key1 = await ReadKey(host, First);
        byte[] key2 = await ReadKey(host, Last);
        try { Assert.False(key1.AsSpan().SequenceEqual(key2)); }
        finally { Array.Clear(key1); Array.Clear(key2); }
    }

    private static Task<QpdfExecutionResult> Create(IQpdfHost host, QpdfPath input, QpdfLimits limits, CancellationToken token,
        params string[] extra)
        => Qpdf.ExecuteAsync(host, new([input.Value, "--json-input", "--split-pages=2", .. extra, "part.pdf"]),
            new QpdfExecutionOptions(limits), token).AsTask();

    private static async Task SeedOutputs(InMemoryQpdfHost host)
    {
        foreach (QpdfPath path in new[] { First, Last })
        {
            await using IQpdfOutputTransaction target = await host.CreateOutputAsync(path, CancellationToken.None);
            await target.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await target.CommitAsync(CancellationToken.None);
        }
    }

    private static void AddCreation(InMemoryQpdfHost host, QpdfPath json, QpdfPath payload)
    {
        string first = Convert.ToBase64String(Programs[0]), second = Convert.ToBase64String(Programs[1]);
        host.AddInput(payload, Programs[2], QpdfInputOwnership.Copy);
        host.AddInput(json, Encoding.UTF8.GetBytes($$$$"""
            {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
              "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/AuditPage":"5 0 R",
                "/PageLabels":{"/Nums":[0,{"/S":"/D","/St":7,"/P":"u:Audit-"}]}}},
              "obj:2 0 R":{"value":{"/Type":"/Pages","/Count":3,"/Kids":["9 0 R","5 0 R"],"/MediaBox":[0,0,100,200],
                "/Resources":{"/AuditShared":"10 0 R"}}},
              "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"9 0 R","/Contents":"6 0 R"}},
              "obj:4 0 R":{"value":{"/Type":"/Page","/Parent":"9 0 R","/Contents":"7 0 R"}},
              "obj:5 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/Contents":"8 0 R"}},
              "obj:6 0 R":{"stream":{"dict":{},"data":"{{{{first}}}}"}},
              "obj:7 0 R":{"stream":{"dict":{},"data":"{{{{second}}}}"}},
              "obj:8 0 R":{"stream":{"dict":{},"datafile":{{{{JsonSerializer.Serialize(payload.Value)}}}}}},
              "obj:9 0 R":{"value":{"/Type":"/Pages","/Parent":"2 0 R","/Count":2,"/Kids":["3 0 R","4 0 R"],"/MediaBox":[0,0,120,240]}},
              "obj:10 0 R":{"value":{"/Audit":19}},
              "trailer":{"value":{"/Root":"1 0 R"}}
            }]}
            """), QpdfInputOwnership.Transfer);
    }

    private static void AddUpdate(InMemoryQpdfHost host, QpdfPath input)
    {
        host.AddInput(input, PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AuditPage 5 0 R /PageLabels << /Nums [0 << /S /D /St 7 /P (Audit-) >>] >> >>", null),
            (2, "<< /Type /Pages /Count 3 /Kids [9 0 R 5 0 R] /MediaBox [0 0 100 200] /Resources << >> >>", null),
            (3, "<< /Type /Page /Parent 9 0 R /Contents 6 0 R >>", null),
            (4, "<< /Type /Page /Parent 9 0 R /Contents 7 0 R >>", null),
            (5, "<< /Type /Page /Parent 2 0 R /Contents 8 0 R >>", null),
            (6, $"<< /Length {Programs[0].Length} >>", Programs[0]),
            (7, $"<< /Length {Programs[1].Length} >>", Programs[1]),
            (8, $"<< /Length {Programs[2].Length} >>", Programs[2]),
            (9, "<< /Type /Pages /Parent 2 0 R /Count 2 /Kids [3 0 R 4 0 R] /MediaBox [0 0 120 240] >>", null),
        ], 1), QpdfInputOwnership.Transfer);
        host.AddInput(JsonPath, """
            {"qpdf":[{"jsonversion":2},{"obj:2 0 R":{"value":{"/Type":"/Pages","/Count":3,
                "/Kids":["5 0 R","9 0 R"],"/MediaBox":[0,0,100,200],"/Resources":{}}}}]}
            """u8.ToArray(), QpdfInputOwnership.Transfer);
    }

    private static async Task AssertPart(InMemoryQpdfHost host, QpdfPath path, long start, int[] expected, bool retainsAudit)
        => await AssertPart(host, path, start, expected, retainsAudit, null);

    private static Task AssertPart(InMemoryQpdfHost host, QpdfPath path, long start, int[] expected, bool retainsAudit, QpdfPassword? password)
        => AssertPart(host, path, start, expected, retainsAudit, password, "D", "Audit-");

    private static async Task AssertPart(InMemoryQpdfHost host, QpdfPath path, long start, int[] expected, bool retainsAudit,
        QpdfPassword? password, string style, string prefix)
    {
        QpdfPath read = QpdfPath.From("check-" + path.Value);
        host.AddInput(read, Output(host, path), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, read, password, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(expected.Length, document.PageCount);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(document, Value(document.Engine.Trailer, "Root")));
        PdfDictionary labels = Assert.IsType<PdfDictionary>(Value(catalog, "PageLabels"));
        PdfArray nums = Assert.IsType<PdfArray>(Value(labels, "Nums"));
        Assert.Equal(0L, Assert.IsType<PdfInteger>(nums[0]).Value);
        PdfDictionary label = Assert.IsType<PdfDictionary>(nums[1]);
        Assert.Equal(style, Encoding.ASCII.GetString(Assert.IsType<PdfName>(Value(label, "S")).RawSpan));
        Assert.Equal(prefix, Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Value(label, "P")).ValueSpan));
        Assert.Equal(start, Assert.IsType<PdfInteger>(Value(label, "St")).Value);
        Assert.Equal(retainsAudit, Value(catalog, "AuditPage") is PdfReference);
        for (int i = 0; i < expected.Length; i++)
        {
            int index = expected[i];
            PdfDictionary page = document.PageReferences[i].Page;
            if (index == 2) { Assert.Equal(document.PageReferences[i].Source, Value(catalog, "AuditPage")); }
            PdfArray box = Assert.IsType<PdfArray>(Value(page, "MediaBox"));
            Assert.Equal(index == 2 ? 100L : 120L, Assert.IsType<PdfInteger>(box[2]).Value);
            Assert.IsType<PdfDictionary>(Resolve(document, Value(page, "Resources")));
            Assert.Equal(Programs[index], StreamObservation.Decode(Assert.IsType<PdfStream>(Resolve(document, Value(page, "Contents")))));
        }
    }

    private static async Task<byte[]> ReadKey(InMemoryQpdfHost host, QpdfPath path)
    {
        QpdfPath read = QpdfPath.From("key-" + path.Value);
        host.AddInput(read, Output(host, path), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, read, QpdfPassword.FromText("owner"), QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(6, document.Encryption?.Revision);
        PdfDictionary dictionary = Assert.IsType<PdfDictionary>(Resolve(document, Value(document.Engine.Trailer, "Encrypt")));
        byte[] key = new byte[32];
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(PasswordAuthentication.Owner, Aes256Encryption.Authenticate(dictionary, "owner"u8, key, governor));
        return key;
    }

    private static byte[] Output(InMemoryQpdfHost host, QpdfPath path)
    {
        Assert.True(host.TryGetOutput(path, out byte[] output), path.Value);
        return output;
    }

    private static QpdfObject Value(PdfDictionary dictionary, string key)
        => dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value) ? value : PdfNull.Instance;

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference) : value;

}
