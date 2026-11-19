using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class AttachmentEditInspectionTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input");
    private static readonly QpdfPath Payload = QpdfPath.From("payload");
    private static readonly QpdfPath Report = QpdfPath.From("report");
    private static readonly byte[] Original = [0, 254, 92, 10];
    private static readonly byte[] Added = [65, 0, 255, 40];
    private static readonly byte[] Prior = [17, 23, 41];

    private const string FileSpec = "<< /Type /Filespec /F (original.bin) /EF << /F 6 9 R >> >>";

    private static byte[] Fixture(string version, bool directSpec) => PdfTestDocument.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /Names 4 0 R /AuditNames 4 0 R /AuditCatalog 8 0 R /AcroForm << /Fields [] >> >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
        (4, "<< /EmbeddedFiles << /Names [(seed) " + (directSpec ? FileSpec : "5 7 R") + "] >> /AuditCategory 7 0 R >>", null),
        (5, FileSpec, null),
        (6, "<< /Type /EmbeddedFile /Length 4 >>", Original),
        (7, "<< /Names [] >>", null),
        (8, "<< /SavedCatalog 1 0 R >>", null),
    ], 1, new Dictionary<int, int> { [5] = 7, [6] = 9 }, version, "");

    private static async Task<InMemoryQpdfHost> PrepareAsync()
    {
        InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(Fixture("1.4", false), Report);
        host.AddInput(Payload, Added, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("donor"), Fixture("2.0", false), QpdfInputOwnership.Transfer);
        return host;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectionProjectsEditsAndKeepsTheOriginalNameTree(bool minimalJob)
    {
        using InMemoryQpdfHost host = await PrepareAsync();
        QpdfExecutionResult result = minimalJob
            ? await Qpdf.ExecuteJobAsync(host, """
                {"inputFile":"input","outputFile":"report","jsonOutput":"","removeAttachment":["seed"],
                 "addAttachment":[{"file":"payload","key":"added","filename":"new.bin"}],
                 "copyAttachmentsFrom":[{"file":"donor","prefix":"copy-"}]}
                """, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--json", "--json-stream-data=inline", "--remove-attachment=seed",
                "--add-attachment", "payload", "--key=added", "--filename=new.bin", "--",
                "--copy-attachments-from", "donor", "--prefix=copy-", "--"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(Report, out byte[] bytes));
        if (!minimalJob)
        {
            Assert.Equal(Prior, bytes);
            bytes = Encoding.UTF8.GetBytes(Assert.IsType<string>(result.OutputText));
        }
        using JsonDocument json = JsonDocument.Parse(bytes);
        JsonElement objects = json.RootElement.GetProperty("qpdf")[1];
        JsonElement Value(JsonElement reference) => reference.ValueKind == JsonValueKind.String
            ? objects.GetProperty("obj:" + reference.GetString()).GetProperty("value") : reference;
        JsonElement catalog = Value(objects.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        JsonElement names = Value(catalog.GetProperty("/Names"));
        Assert.Equal("7 0 R", names.GetProperty("/AuditCategory").GetString());
        JsonElement active = Value(names.GetProperty("/EmbeddedFiles")).GetProperty("/Names");
        Assert.Equal(new[] { "u:added", "u:copy-seed" }, new[] { active[0].GetString(), active[2].GetString() });
        Assert.Equal(4, active.GetArrayLength());
        JsonElement old = Value(catalog.GetProperty("/AuditNames")).GetProperty("/EmbeddedFiles").GetProperty("/Names");
        Assert.Equal("u:seed", old[0].GetString());
        Assert.Equal("5 7 R", old[1].GetString());
        Assert.Equal("2.0", json.RootElement.GetProperty("qpdf")[0].GetProperty("pdfversion").GetString());
        Assert.Equal(Original, objects.GetProperty("obj:6 9 R").GetProperty("stream").GetProperty("data").GetBytesFromBase64());
        if (!minimalJob)
        {
            JsonElement entries = json.RootElement.GetProperty("attachments");
            Assert.Equal(new[] { "added", "copy-seed" }, entries.EnumerateObject().Select(entry => entry.Name));
            Assert.Equal("new.bin", entries.GetProperty("added").GetProperty("preferredname").GetString());
        }

        QpdfPath report = QpdfPath.From("uploaded-report"), recreated = QpdfPath.From("recreated"), readback = QpdfPath.From("readback");
        host.AddInput(report, bytes, QpdfInputOwnership.Transfer);
        await Qpdf.CreatePdfFromJsonAsync(host, report, recreated, CancellationToken.None);
        Assert.True(host.TryGetOutput(recreated, out byte[] pdf));
        host.AddInput(readback, pdf, QpdfInputOwnership.Transfer);
        foreach ((string key, byte[] expected) in new[] { ("added", Added), ("copy-seed", Original) })
        {
            QpdfExecutionResult extracted = await Qpdf.ExecuteAsync(host, new([readback.Value, "--show-attachment=" + key]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, extracted.ExitCode);
            Assert.Equal(expected, Assert.IsType<ReadOnlyMemory<byte>>(extracted.OutputBytes).ToArray());
        }
        Assert.Equal("seed", Assert.Single(await Qpdf.ListAttachmentsAsync(host, Input, CancellationToken.None)).Key);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task AdditionRetainsAnExistingDirectFilespec()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture("1.4", true), QpdfInputOwnership.Transfer);
        host.AddInput(Payload, Added, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--json", "--json-key=attachments", "--add-attachment", "payload", "--"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(result.OutputText));
        JsonElement seed = json.RootElement.GetProperty("attachments").GetProperty("seed");
        Assert.Equal(JsonValueKind.Null, seed.GetProperty("filespec").ValueKind);
        Assert.Equal("6 9 R", seed.GetProperty("preferredcontents").GetString());
    }

    [Fact]
    public async Task TextInspectionCanRemoveAndExtractFromTheEditedCollection()
    {
        using InMemoryQpdfHost host = await PrepareAsync();
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """
            {"inputFile":"input","check":"","showObject":"1","listAttachments":"","showAttachment":"added","removeAttachment":["seed"],
             "addAttachment":[{"file":"payload","key":"added"}]}
            """, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains("/PageMode /UseAttachments", CatalogListing(result), StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.AttachmentListing && item.Message.StartsWith("added ->", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.AttachmentListing && item.Message.StartsWith("seed ->", StringComparison.Ordinal));
        Assert.Equal(Added, Assert.IsType<ReadOnlyMemory<byte>>(result.OutputBytes).ToArray());
        QpdfExecutionResult missing = await Qpdf.ExecuteAsync(host, new(["input", "--remove-attachment=missing", "--list-attachments"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, missing.ExitCode);
        Assert.DoesNotContain(missing.Diagnostics, item => item.Code == QpdfDiagnosticCode.AttachmentListing);
        Assert.Equal("seed", Assert.Single(await Qpdf.ListAttachmentsAsync(host, Input, CancellationToken.None)).Key);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task TextInspectionSelectsTheFinalCatalogAndPreservesPrivateAliases()
    {
        using InMemoryQpdfHost host = await PrepareAsync();
        string[] copy = ["--copy-attachments-from", "donor", "--prefix=copy-", "--"];
        async Task<string> Show(string selector, string[] settings)
        {
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", .. settings,
                "--show-object=" + selector]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.Equal(0L, result.Counters.OpenHandles);
            return CatalogListing(result);
        }

        string active = await Show("1,0", copy);
        Assert.Contains("/PageMode /UseAttachments", active, StringComparison.Ordinal);
        Assert.Contains("/AuditNames 4 0 R", active, StringComparison.Ordinal);
        Assert.DoesNotContain(" /Names 4 0 R", active, StringComparison.Ordinal);
        string trailer = await Show("trailer", copy);
        Match root = Regex.Match(trailer, @"/Root (\d+) (\d+) R");
        Assert.True(root.Success, trailer);
        string activeSelector = root.Groups[1].Value + "," + root.Groups[2].Value;
        Assert.NotEqual("1,0", activeSelector);
        Assert.Equal(active, await Show(activeSelector, copy));
        Assert.Equal("<< /SavedCatalog 1 0 R >>", await Show("8", copy));

        string combined = await Show("1,0", [.. copy, "--remove-acroform"]);
        Assert.Contains("/PageMode /UseAttachments", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("/AcroForm", combined, StringComparison.Ordinal);
        string original = await Show("1,0", []);
        Assert.Contains("/AcroForm", original, StringComparison.Ordinal);
        Assert.Contains(" /Names 4 0 R", original, StringComparison.Ordinal);
        Assert.DoesNotContain("/UseAttachments", original, StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static string CatalogListing(QpdfExecutionResult result)
        => Assert.Single(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.ObjectListing).Message;

    [Theory]
    [InlineData("alias")]
    [InlineData("canceled")]
    [InlineData("limited")]
    public async Task EditedReportKeepsThePriorTargetOnRefusal(string mode)
    {
        InMemoryQpdfHost storage = await PrepareAsync();
        using RecordingJobHost host = new(storage, mode == "canceled" ? "read" : null, Payload, "success", true);
        IQpdfHost observed = mode == "alias" ? new OutputAliasHost(host, Report, Payload) : host;
        QpdfLimits limits = mode == "limited" ? QpdfLimits.Default with { MaxOutputBytes = 64 } : QpdfLimits.Default;
        using CancellationTokenSource cancellation = new();
        Task<QpdfExecutionResult> call = Qpdf.ExecuteAsync(observed,
            new(["input", "--json-output", "--add-attachment", "payload", "--", "report"]), new(limits), cancellation.Token).AsTask();
        if (mode == "canceled")
        {
            try
            {
                Assert.Same(host.Entered, await Task.WhenAny(host.Entered, call).WaitAsync(TimeSpan.FromSeconds(10)));
                cancellation.Cancel();
            }
            finally { host.Resume(); }
        }
        QpdfExecutionResult result = await call;
        Assert.Equal(mode == "canceled" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        if (mode == "alias") { Assert.Contains(result.Diagnostics, item => item.Message.Contains("aliases an input", StringComparison.Ordinal)); }
        if (mode == "limited") { Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded); }
        Assert.Empty(host.CreatedPaths);
        Assert.True(storage.TryGetOutput(Report, out byte[] bytes));
        Assert.Equal(Prior, bytes);
        Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Fact]
    public Task EditedReportRefundsEachRefusedAdmission()
        => PreparationAdmissionChecks.VerifyAsync(PrepareAsync,
            (host, token) => Qpdf.ExecuteAsync(host,
                new(["input", "--json-output", "--remove-attachment=seed", "--add-attachment", "payload", "--",
                    "--copy-attachments-from", "donor", "--prefix=copy-", "--", "report"]), token),
            Report, 2048, []);
}
