using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonPageAssemblyTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Source = QpdfPath.From("source"), Report = QpdfPath.From("report");

    [Theory]
    [InlineData("reorder", "argv", 2, "A2,A1", 2)]
    [InlineData("duplicate", "argv", 2, "A1,A1,A2", 1)]
    [InlineData("omit", "argv", 2, "A2", 0)]
    [InlineData("merge", "argv", 2, "A1,B1,A2", 1)]
    [InlineData("collate", "argv", 2, "A1,B1,A2,B2", 1)]
    [InlineData("empty", "argv", 2, "B2,B1", 0)]
    [InlineData("reorder", "job", 1, "A2,A1", 2)]
    [InlineData("duplicate", "mixed", 2, "A1,A1,A2", 1)]
    public async Task AssembledPagesAndObjectExportsKeepTheSameGraph(string mode, string route, int version, string tags, int outlinePage)
    {
        using InMemoryQpdfHost host = Prepare(false);
        using JsonDocument report = await Run(host, mode, route, version, "none", null);
        JsonElement body = report.RootElement, pages = body.GetProperty("pages"), objects = version == 2 ? body.GetProperty("qpdf")[1] : body.GetProperty("objects");
        string[] actual = pages.EnumerateArray().Select(page =>
        {
            string reference = page.GetProperty("object").GetString() ?? throw new InvalidOperationException();
            JsonElement value = objects.GetProperty((version == 2 ? "obj:" : "") + reference);
            if (version == 2) { value = value.GetProperty("value"); }
            Assert.Equal("/Page", value.GetProperty("/Type").GetString());
            return value.GetProperty("/Tag").GetString()?.Replace("u:", "", StringComparison.Ordinal) ?? throw new InvalidOperationException();
        }).ToArray();
        Assert.Equal(tags.Split(','), actual);
        Assert.Equal(pages.GetArrayLength(), pages.EnumerateArray().Select(p => p.GetProperty("object").GetString()).Distinct().Count());
        if (mode == "empty") { Assert.Empty(body.GetProperty("outlines").EnumerateArray()); }
        else
        {
            JsonElement outline = Assert.Single(body.GetProperty("outlines").EnumerateArray());
            if (outlinePage == 0) { Assert.Equal(JsonValueKind.Null, outline.GetProperty("destpageposfrom1").ValueKind); }
            else
            {
                Assert.Equal(outlinePage, outline.GetProperty("destpageposfrom1").GetInt32());
                Assert.Equal(pages[outlinePage - 1].GetProperty("object").GetString(), outline.GetProperty("dest")[0].GetString());
            }
        }
        if (version == 2)
        {
            int max = objects.EnumerateObject().Where(p => p.Name.StartsWith("obj:", StringComparison.Ordinal)).Max(p => int.Parse(p.Name.AsSpan(4, p.Name.IndexOf(' ') - 4), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(max, body.GetProperty("qpdf")[0].GetProperty("maxobjectid").GetInt32());
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task PageLabelsFollowTheAssembledSourceOccurrences()
    {
        using InMemoryQpdfHost host = Prepare(true);
        using JsonDocument report = await Run(host, "collate", "argv", 2, "none", null);
        string?[] labels = report.RootElement.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("label").GetProperty("/P").GetString()).ToArray();
        Assert.Equal(new[] { "u:A1", "u:B1", "u:A2", "u:B2" }, labels);
        JsonElement objects = report.RootElement.GetProperty("qpdf")[1];
        JsonElement catalog = Resolve(objects, objects.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        JsonElement original = Resolve(objects, catalog.GetProperty("/Audit"));
        Assert.Equal("u:A2", original.GetProperty("/Nums")[3].GetProperty("/P").GetString());
        Assert.NotEqual(catalog.GetProperty("/Audit").GetString(), catalog.GetProperty("/PageLabels").GetString());
    }

    [Theory]
    [InlineData("inline")]
    [InlineData("file")]
    public async Task AssembledExportsCanCreatePdfsAfterAllInputsClose(string streams)
    {
        using InMemoryQpdfHost host = Prepare(true);
        using JsonDocument report = await Run(host, "collate", "argv", 2, streams, streams == "file" ? "payload" : null);
        Assert.Equal(0L, host.OpenLeaseCount);
        if (streams == "file")
        {
            foreach (JsonProperty item in report.RootElement.GetProperty("qpdf")[1].EnumerateObject())
            {
                if (!item.Value.TryGetProperty("stream", out JsonElement stream)) { continue; }
                string path = stream.GetProperty("datafile").GetString() ?? throw new InvalidOperationException();
                Assert.True(host.TryGetOutput(QpdfPath.From(path), out byte[] bytes));
                host.AddInput(QpdfPath.From(path), bytes, QpdfInputOwnership.Transfer);
            }
        }
        host.AddInput(QpdfPath.From("json"), System.Text.Encoding.UTF8.GetBytes(report.RootElement.GetRawText()), QpdfInputOwnership.Transfer);
        QpdfJsonImportResult created = await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("json"), QpdfPath.From("restored"), CancellationToken.None);
        Assert.Equal(4, created.PageCount); Assert.True(host.TryGetOutput(QpdfPath.From("restored"), out byte[] pdf));
        host.AddInput(QpdfPath.From("restored"), pdf, QpdfInputOwnership.Transfer);
        QpdfJsonResult inspected = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("restored"), new QpdfJsonOptions(2, ["pages", "outlines"]), CancellationToken.None);
        using JsonDocument final = JsonDocument.Parse(inspected.Json);
        Assert.Equal(new[] { "u:A1", "u:B1", "u:A2", "u:B2" }, final.RootElement.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("label").GetProperty("/P").GetString()));
        Assert.Equal(1, final.RootElement.GetProperty("outlines")[0].GetProperty("destpageposfrom1").GetInt32());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task MinimalJobOutputExportsTheAssembledPageTree()
    {
        using InMemoryQpdfHost host = Prepare(false);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input","jsonOutput":"latest","pages":[{"file":".","range":"2,1"}]}""", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        using JsonDocument report = JsonDocument.Parse(result.OutputText ?? throw new InvalidOperationException());
        Assert.False(report.RootElement.TryGetProperty("pages", out _));
        Assert.True(report.RootElement.GetProperty("qpdf")[0].GetProperty("calledgetallpages").GetBoolean());
        Assert.True(report.RootElement.GetProperty("qpdf")[0].GetProperty("pushedinheritedpageresources").GetBoolean());
        JsonElement objects = report.RootElement.GetProperty("qpdf")[1];
        JsonElement catalog = Resolve(objects, objects.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        JsonElement pages = Resolve(objects, catalog.GetProperty("/Pages"));
        Assert.Equal(new[] { "u:A2", "u:A1" }, pages.GetProperty("/Kids").EnumerateArray().Select(p => Resolve(objects, p).GetProperty("/Tag").GetString()));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SourceActionsFormsAndLayersKeepTheirMeaningAcrossCopies()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, ContextFixture("A", false), QpdfInputOwnership.Transfer);
        host.AddInput(Source, ContextFixture("B", true), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1,1", "source", "1", "--", "--json=2"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        using JsonDocument report = JsonDocument.Parse(result.OutputText ?? throw new InvalidOperationException());
        JsonElement objects = report.RootElement.GetProperty("qpdf")[1], pages = report.RootElement.GetProperty("pages");
        JsonElement fields = report.RootElement.GetProperty("acroform").GetProperty("fields");
        Assert.Equal(new[] { "u:A", "u:A", "u:B" }, fields.EnumerateArray().Select(f => f.GetProperty("value").GetString()));
        Assert.Equal(new[] { 2, 2, 0 }, fields.EnumerateArray().Select(f => f.GetProperty("quadding").GetInt32()));
        Assert.Equal(3, fields.EnumerateArray().Select(f => f.GetProperty("annotation").GetProperty("object").GetString()).Distinct().Count());
        for (int index = 0; index < 3; index++)
        {
            JsonElement annotation = Resolve(objects, fields[index].GetProperty("annotation").GetProperty("object"));
            Assert.Equal(pages[index].GetProperty("object").GetString(), annotation.GetProperty("/P").GetString());
        }
        Assert.Equal("3 4 R", pages[0].GetProperty("object").GetString());
        JsonElement foreignPage = Resolve(objects, pages[2].GetProperty("object"));
        JsonElement link = Resolve(objects, Resolve(objects, foreignPage.GetProperty("/Annots"))[1]);
        JsonElement action = Resolve(objects, link.GetProperty("/A"));
        Assert.Equal("u:https://b.example/root/relative", action.GetProperty("/URI").GetString());
        Assert.Equal(pages[2].GetProperty("object").GetString(), Resolve(objects, action.GetProperty("/Next")).GetProperty("/D")[0].GetString());
        JsonElement catalog = Resolve(objects, objects.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        JsonElement form = Resolve(objects, catalog.GetProperty("/AcroForm"));
        JsonElement fonts = Resolve(objects, Resolve(objects, form.GetProperty("/DR")).GetProperty("/Font"));
        for (int index = 0; index < 3; index++)
        {
            JsonElement field = Resolve(objects, fields[index].GetProperty("object"));
            string appearance = Resolve(objects, field.TryGetProperty("/DA", out JsonElement own) ? own : form.GetProperty("/DA")).GetString() ?? throw new InvalidOperationException();
            string[] tokens = appearance[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int select = Array.IndexOf(tokens, "Tf"); Assert.True(select >= 2);
            string font = tokens[select - 2];
            Assert.Equal(index == 2 ? "/Courier" : "/Helvetica", Resolve(objects, fonts.GetProperty(font)).GetProperty("/BaseFont").GetString());
        }
        JsonElement optional = Resolve(objects, catalog.GetProperty("/OCProperties"));
        Assert.Equal(2, optional.GetProperty("/OCGs").GetArrayLength());
        JsonElement disabled = optional.GetProperty("/D").GetProperty("/OFF");
        JsonElement resources = Resolve(objects, foreignPage.GetProperty("/Resources"));
        Assert.Equal(Resolve(objects, resources.GetProperty("/Properties")).GetProperty("/L").GetString(), Assert.Single(disabled.EnumerateArray()).GetString());
    }

    [Fact]
    public async Task ExportRetainsTheSelectedSourcesVersionFloor()
    {
        using InMemoryQpdfHost host = Prepare(false);
        byte[] higher = Fixture("B", false); "1.7"u8.CopyTo(higher.AsSpan(5, 3));
        host.AddInput(Source, higher, QpdfInputOwnership.Transfer);
        using JsonDocument report = await Run(host, "merge", "argv", 2, "inline", null);
        Assert.Equal("1.7", report.RootElement.GetProperty("qpdf")[0].GetProperty("pdfversion").GetString());
        host.AddInput(QpdfPath.From("json"), System.Text.Encoding.UTF8.GetBytes(report.RootElement.GetRawText()), QpdfInputOwnership.Transfer);
        await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("json"), QpdfPath.From("restored"), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("restored"), out byte[] pdf));
        Assert.StartsWith("%PDF-1.7", System.Text.Encoding.ASCII.GetString(pdf)); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, "reader")]
    [InlineData(true, "reader")]
    [InlineData(false, "wrong")]
    [InlineData(true, "wrong")]
    public async Task EachEncryptedInputUsesItsOwnCredential(bool primary, string password)
    {
        using InMemoryQpdfHost host = Prepare(false);
        QpdfExecutionResult encrypted = await Qpdf.ExecuteAsync(host, new([primary ? "input" : "source", "--encrypt", "reader", "owner", "256", "--", "protected"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, encrypted.ExitCode); Assert.True(host.TryGetOutput(QpdfPath.From("protected"), out byte[] pdf));
        host.AddInput(primary ? Input : Source, pdf, QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Report, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result;
        if (primary)
        {
            result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "report", password,
                pages = new[] { new { file = ".", range = "1" }, new { file = "source", range = "1" } }, json = "2", jsonStreamData = "inline" }), CancellationToken.None);
        }
        else
        {
            result = await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1", "source", "--password=" + password, "1", "--", "--json=2", "--json-stream-data=inline", "report"]), CancellationToken.None);
        }
        Assert.True(host.TryGetOutput(Report, out byte[] bytes)); Assert.Equal(0L, host.OpenLeaseCount);
        if (password == "wrong")
        {
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidPassword);
            Assert.Equal("prior"u8.ToArray(), bytes); return;
        }
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        using JsonDocument report = JsonDocument.Parse(bytes);
        Assert.Equal(primary, report.RootElement.GetProperty("encrypt").GetProperty("encrypted").GetBoolean());
        Assert.Equal(2, report.RootElement.GetProperty("pages").GetArrayLength());
        JsonElement objects = report.RootElement.GetProperty("qpdf")[1];
        foreach (JsonElement page in report.RootElement.GetProperty("pages").EnumerateArray())
        {
            string reference = page.GetProperty("contents")[0].GetString() ?? throw new InvalidOperationException();
            Assert.Equal("q Q"u8.ToArray(), Convert.FromBase64String(objects.GetProperty("obj:" + reference).GetProperty("stream").GetProperty("data").GetString() ?? ""));
        }
    }

    [Theory]
    [InlineData("report")]
    [InlineData("sidecar")]
    public async Task NoPublicationCanReplaceAConsumedSecondaryInput(string target)
    {
        using InMemoryQpdfHost host = Prepare(false);
        string prefix = target == "sidecar" ? "payload" : "unused";
        string source = target == "sidecar" ? "payload-5" : "source";
        host.AddInput(QpdfPath.From(source), Fixture("B", false), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From(source), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        List<string> argv = ["input", "--pages", ".", "1", source, "1", "--", "--json=2"];
        if (target == "sidecar") { argv.AddRange(["--json-stream-data=file", "--json-stream-prefix=" + prefix]); }
        else { argv.Add(source); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode); Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments);
        Assert.True(host.TryGetOutput(QpdfPath.From(source), out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public Task AssembledJsonRefundsAdmissionAndPreservesPriorReports()
    {
        async Task<InMemoryQpdfHost> PrepareAsync()
        {
            InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(Fixture("A", true), Report);
            host.AddInput(Source, Fixture("B", true), QpdfInputOwnership.Transfer); return host;
        }
        return PreparationAdmissionChecks.VerifyAsync(PrepareAsync, (host, token) => Qpdf.ExecuteAsync(host,
            new(["input", "--pages", ".", "2,1,1", "source", "1", "--", "--json=2", "report"]), token), Report, 160, ["write", "cancel"]);
    }

    [Fact]
    public Task ContextAssemblyRefundsAdmissionAndPreservesPriorReports()
    {
        async Task<InMemoryQpdfHost> PrepareAsync()
        {
            InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(ContextFixture("A", false), Report);
            host.AddInput(Source, ContextFixture("B", true), QpdfInputOwnership.Transfer); return host;
        }
        return PreparationAdmissionChecks.VerifyAsync(PrepareAsync, (host, token) => Qpdf.ExecuteAsync(host,
            new(["input", "--pages", ".", "1,1", "source", "1", "--", "--json=2", "--json-stream-data=file", "--json-stream-prefix=payload", "report"]), token), Report, 512, ["write", "cancel"]);
    }

    [Theory]
    [InlineData("input-close", "success")]
    [InlineData("input-close", "fault")]
    [InlineData("input-close", "cancel")]
    [InlineData("write", "success")]
    [InlineData("write", "cancel")]
    public async Task AssembledBackingLivesThroughPendingSourceCloseAndSidecarWrite(string phase, string outcome)
    {
        byte[] primary = Fixture("A", true), secondary = Fixture("B", true);
        InMemoryQpdfHost storage = await PreparationAdmissionChecks.StorageAsync(primary, Report);
        storage.AddInput(Source, secondary, QpdfInputOwnership.Transfer);
        using RecordingJobHost host = new(storage, phase, phase == "input-close" ? Source : QpdfPath.From("payload-5"), outcome == "fault" ? "fault" : "success", true);
        using CancellationTokenSource cancellation = new();
        Task<QpdfExecutionResult> running = Qpdf.ExecuteAsync(host,
            new(["input", "--pages", ".", "1", "source", "1", "--", "--json=2", "--json-stream-data=file", "--json-stream-prefix=payload", "report"]), cancellation.Token).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(running.IsCompleted);
            if (phase == "input-close")
            {
                Assert.True(host.Budget.ReservedBytes >= primary.Length + secondary.Length);
                Assert.Empty(host.CreatedPaths);
            }
            else
            {
                Assert.Equal("q Q"u8.ToArray(), host.PendingWrite.ToArray());
                Assert.True(host.Budget.ReservedBytes >= host.PendingWrite.Length);
                Assert.Equal(1L, storage.OpenLeaseCount); Assert.Contains(Report, host.Committed);
            }
            if (outcome == "cancel") { cancellation.Cancel(); }
            host.Resume(); QpdfExecutionResult result = await running;
            Assert.Equal(outcome == "success" ? QpdfExecutionStatus.Completed : outcome == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(0L, storage.OpenLeaseCount);
            Assert.True(storage.TryGetOutput(Report, out byte[] report));
            if (phase == "input-close" && outcome != "success") { Assert.Equal(new byte[] { 17, 23, 41 }, report); }
            else { using JsonDocument json = JsonDocument.Parse(report); Assert.Equal(2, json.RootElement.GetProperty("pages").GetArrayLength()); }
            if (outcome == "success")
            { Assert.True(storage.TryGetOutput(QpdfPath.From("payload-5"), out byte[] bytes)); Assert.Equal("q Q"u8.ToArray(), bytes); }
            host.Dispose(); Assert.Equal(0L, host.Budget.ReservedBytes); Assert.Equal(0, host.Budget.LiveReservations);
        }
        finally { host.Resume(); await running; }
    }

    internal static byte[] ContextFixture(string prefix, bool disabled)
        => PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 8 0 R /URI << /Base (https://" + prefix.ToLowerInvariant() + ".example/root/) >> /Names << /Dests << /Names [(Chapter) [3 4 R /Fit]] >> >> /OCProperties 11 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 4 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Contents 5 0 R /Annots [6 0 R 9 0 R] /Resources << /Properties << /L 12 0 R >> >> >>", null),
            (4, "null", null),
            (5, "<< /Length 19 >>", "/OC /L BDC q Q EMC\n"u8.ToArray()),
            (6, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (" + prefix + ") /P 3 4 R /Rect [0 0 60 20] /AP << /N 7 0 R >> >>", null),
            (7, "<< /Length 3 /Type /XObject /Subtype /Form /BBox [0 0 60 20] /Resources << /Font << /F 10 0 R >> >> >>", "q Q"u8.ToArray()),
            (8, "<< /Fields [6 0 R] /DA (/F 10 Tf 0 g) /Q " + (disabled ? "0" : "2") + " /DR << /Font << /F 10 0 R >> >> >>", null),
            (9, "<< /Type /Annot /Subtype /Link /Rect [0 20 40 40] /P 3 4 R /A << /S /URI /URI (relative) /Next << /S /GoTo /D (Chapter) >> >> >>", null),
            (10, "<< /Type /Font /Subtype /Type1 /BaseFont /" + (disabled ? "Courier" : "Helvetica") + " >>", null),
            (11, "<< /OCGs [12 0 R] /D << /BaseState /ON /OFF [" + (disabled ? "12 0 R" : "") + "] >> >>", null),
            (12, "<< /Type /OCG /Name (Layer) >>", null)], 1, new Dictionary<int, int> { [3] = 4 }, "1.7", "");

    private static JsonElement Resolve(JsonElement objects, JsonElement value)
        => value.ValueKind == JsonValueKind.String && value.GetString() is string reference && reference.EndsWith(" R", StringComparison.Ordinal)
            ? objects.GetProperty("obj:" + reference).GetProperty("value") : value;

    internal static byte[] Fixture(string prefix, bool labels)
        => PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R /Names << /Dests 8 0 R >>" + (labels ? " /PageLabels 9 0 R /Audit 9 0 R" : "") + " >>", null),
            (2, "<< /Type /Pages /Count 2 /Kids [3 4 R 4 0 R] /MediaBox [0 0 80 40] /Resources << >> >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /Tag (" + prefix + "1) /Contents 5 0 R >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /Tag (" + prefix + "2) /Contents 5 0 R >>", null),
            (5, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (6, "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>", null),
            (7, "<< /Title (" + prefix + ") /Parent 6 0 R /Dest (Chapter) >>", null),
            (8, "<< /Names [(Chapter) [3 4 R /Fit]] >>", null),
            (9, "<< /Nums [0 << /P (" + prefix + "1) >> 1 << /P (" + prefix + "2) >>] >>", null)], 1, new Dictionary<int, int> { [3] = 4 });

    private static InMemoryQpdfHost Prepare(bool labels)
    {
        InMemoryQpdfHost host = new(); host.AddInput(Input, Fixture("A", labels), QpdfInputOwnership.Transfer);
        host.AddInput(Source, Fixture("B", labels), QpdfInputOwnership.Transfer); return host;
    }

    private static async Task<JsonDocument> Run(InMemoryQpdfHost host, string mode, string route, int version, string streams, string? prefix)
    {
        var sources = mode switch
        {
            "merge" => new[] { new { file = ".", range = "1" }, new { file = "source", range = "1" }, new { file = ".", range = "2" } },
            "collate" => [new { file = ".", range = "1-2" }, new { file = "source", range = "1-2" }],
            "empty" => [new { file = "source", range = "2,1" }],
            _ => [new { file = ".", range = mode == "duplicate" ? "1,1,2" : mode == "omit" ? "2" : "2,1" }]
        };
        string[] keys = version == 2 ? ["pages", "pagelabels", "outlines", "qpdf"] : ["pages", "pagelabels", "outlines", "objects", "objectinfo"];
        Dictionary<string, object> job = new() { ["json"] = version.ToString(System.Globalization.CultureInfo.InvariantCulture), ["jsonKey"] = keys,
            ["pages"] = sources, ["jsonStreamData"] = streams };
        if (mode == "empty") { job["empty"] = true; } else { job["inputFile"] = "input"; }
        if (mode == "collate") { job["collate"] = "1"; }
        if (prefix is not null) { job["jsonStreamPrefix"] = prefix; }
        QpdfExecutionResult result;
        if (route == "job") { result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None); }
        else if (route == "mixed")
        {
            host.AddInput(QpdfPath.From("job"), System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(job)), QpdfInputOwnership.Transfer);
            result = await Qpdf.ExecuteAsync(host, new(["--job-json-file=job"]), CancellationToken.None);
        }
        else
        {
            List<string> argv = [mode == "empty" ? "--empty" : "input", "--pages"];
            foreach (var source in sources) { argv.AddRange([source.file, source.range]); }
            argv.AddRange(["--", "--json=" + version, "--json-stream-data=" + streams]);
            foreach (string key in keys) { argv.Add("--json-key=" + key); }
            if (mode == "collate") { argv.Add("--collate"); }
            if (prefix is not null) { argv.Add("--json-stream-prefix=" + prefix); }
            result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        }
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        return JsonDocument.Parse(result.OutputText ?? throw new InvalidOperationException("Missing JSON."));
    }
}
