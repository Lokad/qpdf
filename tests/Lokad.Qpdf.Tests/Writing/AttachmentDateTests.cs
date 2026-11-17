using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentDateTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output");

    private sealed class Host : IQpdfHost, IDisposable
    {
        internal readonly InMemoryQpdfHost Inner = new();
        internal int Opened, Created;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        { Opened++; return Inner.OpenInputAsync(path, token); }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Created++; return Inner.CreateOutputAsync(path, token); }
        public void Dispose() => Inner.Dispose();
    }

    private static async Task Seed(Host host)
    {
        host.Inner.AddInput(Input, PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 20] >>", null)], 1), QpdfInputOwnership.Transfer);
        host.Inner.AddInput(QpdfPath.From("payload"), "A"u8.ToArray(), QpdfInputOwnership.Transfer);
        await using IQpdfOutputTransaction prior = await host.Inner.CreateOutputAsync(Output, CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
    }

    private static void AssertPrior(Host host)
    {
        Assert.Equal(0, host.Opened); Assert.Equal(0, host.Created); Assert.Equal(0L, host.Inner.OpenLeaseCount);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior)); Assert.Equal("prior"u8.ToArray(), prior);
    }

    private static async Task Run(Host host, string route, string field, string? value, bool valid)
    {
        if (route == "typed")
        {
            Task<QpdfAttachmentEditResult> running = Qpdf.AddAttachmentsAsync(host, Input, Output,
                [new("payload", "K", "name", null, null, field == "creationdate" ? value : null, field == "moddate" ? value : null, false)], CancellationToken.None).AsTask();
            if (valid) { Assert.Equal(0L, (await running).Counters.OpenHandles); }
            else
            {
                QpdfParseException error = await Assert.ThrowsAsync<QpdfParseException>(async () => await running);
                Assert.Contains("full PDF timestamp", error.Message, StringComparison.Ordinal); Assert.True(error.Message.Length < 256);
            }
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--add-attachment", "payload", "--key=K", "--" + field + "=" + value, "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output",
                    addAttachment = new[] { new Dictionary<string, object?> { ["file"] = "payload", ["key"] = "K", [field] = value } } }), CancellationToken.None);
            Assert.Equal(valid ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
            if (!valid) { Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code); }
            Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
        if (!valid) { AssertPrior(host); return; }
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] bytes));
        using InMemoryQpdfHost reader = new(); reader.AddInput(Input, bytes, QpdfInputOwnership.Transfer);
        QpdfAttachmentInfo attachment = Assert.Single(await Qpdf.ListAttachmentsAsync(reader, Input, CancellationToken.None));
        Assert.Equal(field == "creationdate" ? value ?? "" : "", attachment.CreationDate);
        Assert.Equal(field == "moddate" ? value ?? "" : "", attachment.ModDate);
        await Qpdf.ExtractAttachmentAsync(reader, Input, "K", QpdfPath.From("extracted"), CancellationToken.None);
        Assert.True(reader.TryGetOutput(QpdfPath.From("extracted"), out byte[] payload)); Assert.Equal("A"u8.ToArray(), payload);
        Assert.Equal(0L, reader.OpenLeaseCount);
    }

    public static IEnumerable<object[]> NativeCases()
    {
        using Stream source = typeof(AttachmentDateTests).Assembly.GetManifestResourceStream("Lokad.Qpdf.Tests.Writing.AttachmentDateCases.json")
            ?? throw new InvalidOperationException("Missing timestamp cases.");
        using JsonDocument data = JsonDocument.Parse(source);
        foreach (JsonElement row in data.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return [row.GetProperty("name").GetString() ?? throw new InvalidOperationException("Missing case name."),
                row.GetProperty("value").GetString() ?? throw new InvalidOperationException("Missing timestamp."), row.GetProperty("valid").GetBoolean()];
        }
    }

    [Theory]
    [MemberData(nameof(NativeCases))]
    public async Task AuthoredDatesMatchNativeSyntax(string name, string value, bool valid)
    {
        Assert.NotEmpty(name);
        using Host host = new(); await Seed(host);
        await Run(host, "argv", "creationdate", value, valid);
    }

    [Theory]
    [InlineData("job", "creationdate", "D:20240102030405Z", true)]
    [InlineData("job", "moddate", "D:20240102030405-02'30'", true)]
    [InlineData("job", "creationdate", "", false)]
    [InlineData("job", "creationdate", "D:20240102030405\0", false)]
    [InlineData("job", "moddate", "D:20240102030405+05'30", false)]
    [InlineData("argv", "moddate", "D:20240102030405Z", true)]
    [InlineData("argv", "moddate", "not-a-date", false)]
    [InlineData("typed", "creationdate", "D:20240102030405Z", true)]
    [InlineData("typed", "moddate", "D:20240102030405+99'99'", true)]
    [InlineData("typed", "creationdate", "D:2024", false)]
    [InlineData("typed", "moddate", "not-a-date", false)]
    [InlineData("typed", "creationdate", "", true)]
    [InlineData("typed", "moddate", null, true)]
    public async Task AdaptersKeepDateFieldsAndOptionalTypedValues(string route, string field, string? value, bool valid)
    {
        using Host host = new(); await Seed(host);
        await Run(host, route, field, value, valid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidArgvSettersFailEvenWhenAValidValueFollows(bool modification)
    {
        using Host host = new(); await Seed(host);
        string setting = modification ? "--moddate=" : "--creationdate=";
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--add-attachment", "payload", setting + "bad", setting + "D:20240102030405Z", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode); AssertPrior(host);
    }

    [Theory]
    [InlineData("long")]
    [InlineData("nul")]
    public async Task InvalidTypedTextRejectsBeforeHostAccessWithBoundedDiagnostics(string kind)
    {
        using Host host = new(); await Seed(host);
        string value = kind == "long" ? new string('9', 65536) : "D:20240102030405\0";
        await Run(host, "typed", "moddate", value, false);
    }

    [Fact]
    public async Task CanceledTypedRequestsPreserveThePriorTargetWithoutAcquiringPayloads()
    {
        using Host host = new(); await Seed(host);
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Qpdf.AddAttachmentsAsync(host, Input, Output,
            [new("payload", "K", null, null, null, "D:20240102030405Z", null, false)], cancel.Token).AsTask());
        AssertPrior(host);
    }

    [Fact]
    public async Task ExistingMalformedDatesRemainReadableAndSurviveAnOrdinaryRewrite()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, AttachmentMetadataTests.Fixture("name", "/CreationDate (not-a-date) /ModDate (D:2024)"), QpdfInputOwnership.Transfer);
        QpdfAttachmentInfo original = Assert.Single(await Qpdf.ListAttachmentsAsync(host, Input, CancellationToken.None));
        QpdfExecutionResult rewritten = await Qpdf.ExecuteAsync(host, new(["input", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, rewritten.ExitCode);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("readback"), bytes, QpdfInputOwnership.Transfer);
        QpdfAttachmentInfo copied = Assert.Single(await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("readback"), CancellationToken.None));
        Assert.Equal("not-a-date", original.CreationDate); Assert.Equal("D:2024", original.ModDate);
        Assert.Equal(original.CreationDate, copied.CreationDate); Assert.Equal(original.ModDate, copied.ModDate);
        QpdfJsonResult json = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("readback"), QpdfPath.From("report"), new(2, ["attachments"]), CancellationToken.None);
        using JsonDocument report = JsonDocument.Parse(json.Json);
        JsonElement stream = report.RootElement.GetProperty("attachments").GetProperty("K").GetProperty("streams").GetProperty("/UF");
        Assert.Equal(JsonValueKind.Null, stream.GetProperty("creationdate").ValueKind);
        Assert.Equal(JsonValueKind.Null, stream.GetProperty("modificationdate").ValueKind);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
