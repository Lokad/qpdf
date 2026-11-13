using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class EncryptedJsonTests
{
    [Theory]
    [InlineData("security-r4-ascii.pdf", "reader", 4, false)]
    [InlineData("security-r4-ascii.pdf", "fixture-owner", 4, true)]
    [InlineData("security-r6-ascii.pdf", "reader", 6, false)]
    [InlineData("security-r6-ascii.pdf", "fixture-owner", 6, true)]
    [InlineData("security-r4-empty.pdf", null, 4, false)]
    [InlineData("security-r6-empty.pdf", null, 6, false)]
    [InlineData("security-r4-pdfdoc.pdf", "café•", 4, false)]
    public async Task TypedArgvAndJobExportAuthenticatedFacts(string file, string? password, int revision, bool owner)
    {
        foreach (int version in new[] { 1, 2 })
        foreach (string route in new[] { "typed", "argv", "job" })
        {
            using InMemoryQpdfHost host = new(); Add(host, file);
            string text;
            if (route == "typed")
            {
                QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From(file), QpdfPath.From("out.json"),
                    new QpdfJsonOptions(version, ["encrypt", "pages"])
                    { Password = password is null ? null : QpdfPassword.FromText(password) }, CancellationToken.None);
                text = result.Json;
            }
            else
            {
                QpdfExecutionResult result;
                if (route == "argv")
                {
                    List<string> arguments = [file, "--json=" + version, "--json-key=encrypt", "--json-key=pages"];
                    if (password is not null) { arguments.Add("--password=" + password); }
                    result = await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
                }
                else
                {
                    Dictionary<string, object> fields = new() { ["inputFile"] = file, ["json"] = version.ToString(), ["jsonKey"] = new[] { "encrypt", "pages" } };
                    if (password is not null) { fields["password"] = password; }
                    result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None);
                }
                Assert.Equal(QpdfExecutionStatus.Completed, result.Status); text = Assert.IsType<string>(result.OutputText);
            }
            using JsonDocument json = JsonDocument.Parse(text);
            Assert.Single(json.RootElement.GetProperty("pages").EnumerateArray());
            JsonElement encrypt = json.RootElement.GetProperty("encrypt");
            Assert.True(encrypt.GetProperty("encrypted").GetBoolean());
            Assert.Equal(owner, encrypt.GetProperty("ownerpasswordmatched").GetBoolean());
            Assert.Equal(!owner, encrypt.GetProperty("userpasswordmatched").GetBoolean());
            Assert.Equal(JsonValueKind.Null, encrypt.GetProperty("recovereduserpassword").ValueKind);
            JsonElement parameters = encrypt.GetProperty("parameters");
            Assert.Equal(revision, parameters.GetProperty("R").GetInt32());
            Assert.Equal(revision == 4 ? 4 : 5, parameters.GetProperty("V").GetInt32());
            Assert.Equal(-3388, parameters.GetProperty("P").GetInt32());
            Assert.Equal(revision == 4 ? 128 : 256, parameters.GetProperty("bits").GetInt32());
            Assert.Equal(JsonValueKind.Null, parameters.GetProperty("key").ValueKind);
            foreach (string field in new[] { "method", "stringmethod", "streammethod", "filemethod" })
            { Assert.Equal(revision == 4 ? "AESv2" : "AESv3", parameters.GetProperty(field).GetString()); }
            JsonElement capabilities = encrypt.GetProperty("capabilities");
            Assert.Equal(9, capabilities.EnumerateObject().Count());
            foreach (JsonProperty capability in capabilities.EnumerateObject())
            { Assert.Equal(capability.Name is "accessibility" or "printlow", capability.Value.GetBoolean()); }
            Assert.True(capabilities.TryGetProperty(version == 1 ? "moddifyannotations" : "modifyannotations", out _));
            Assert.DoesNotContain("fixture-owner", text, StringComparison.Ordinal); Assert.DoesNotContain("reader", text, StringComparison.Ordinal);
            Assert.Equal(0, host.OpenLeaseCount);
        }
    }

    [Theory]
    [InlineData("inline")]
    [InlineData("file")]
    public async Task JsonStreamModesExportDecryptedPayloads(string mode)
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-r6-ascii.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(
            ["security-r6-ascii.pdf", "--password=reader", "--json", "--json-key=qpdf", "--json-object=7",
                "--json-stream-data=" + mode, "--json-stream-prefix=side"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(result.OutputText));
        JsonElement stream = json.RootElement.GetProperty("qpdf")[1].GetProperty("obj:7 0 R").GetProperty("stream");
        byte[] data;
        if (mode == "inline") { data = Convert.FromBase64String(Assert.IsType<string>(stream.GetProperty("data").GetString())); }
        else
        {
            Assert.True(host.TryGetOutput(QpdfPath.From(Assert.IsType<string>(stream.GetProperty("datafile").GetString())), out data));
        }
        Assert.Equal("BT (Page) Tj ET", Encoding.ASCII.GetString(data));
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(null, QpdfDiagnosticCode.PasswordRequired)]
    [InlineData("", QpdfDiagnosticCode.InvalidPassword)]
    [InlineData("private-wrong-password", QpdfDiagnosticCode.InvalidPassword)]
    public async Task FailedPasswordsPublishNeitherJsonNorSidecars(string? password, QpdfDiagnosticCode code)
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-r6-ascii.pdf");
        Dictionary<string, object> fields = new() { ["inputFile"] = "security-r6-ascii.pdf", ["json"] = "2",
            ["outputFile"] = "out.json", ["jsonStreamData"] = "file", ["jsonStreamPrefix"] = "side" };
        if (password is not null) { fields["password"] = password; }
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
        Assert.Null(result.OutputText); Assert.False(host.TryGetOutput(QpdfPath.From("out.json"), out _));
        Assert.False(host.TryGetOutput(QpdfPath.From("side-7"), out _)); Assert.Equal(0, host.OpenLeaseCount);
        Assert.DoesNotContain("private-wrong-password", result.Diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawCredentialsAndOutputLimitsPreservePriorPublication()
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-r4-pdfdoc.pdf");
        byte[] password = Convert.FromHexString("636166E980");
        QpdfJsonOptions options = new(2, ["encrypt"]) { Password = QpdfPassword.FromBytes(password) };
        QpdfPath output = QpdfPath.From("out.json");
        QpdfJsonResult first = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("security-r4-pdfdoc.pdf"), output, options, CancellationToken.None);
        Assert.True(host.TryGetOutput(output, out byte[] prior)); Assert.Equal(first.Json, Encoding.UTF8.GetString(prior));
        await Assert.ThrowsAsync<QpdfLimitException>(() => Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("security-r4-pdfdoc.pdf"), output,
            options, QpdfLimits.Default with { MaxOutputBytes = 16L }, CancellationToken.None).AsTask());
        Assert.True(host.TryGetOutput(output, out byte[] retained)); Assert.Equal(prior, retained);
        Assert.Equal(Convert.FromHexString("636166E980"), password); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task EveryJsonAdmissionRefusalRefundsStorageBeforePublication()
    {
        RecordingMemoryBudget baseline = new(1048576);
        await Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++)
        { await Run(new(1048576) { RefuseOnGrowth = growth }, true); }

        static async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost storage = new(); Add(storage, "security-r6-ascii.pdf");
            using IMemoryScope caller = budget.CreateScope();
            MemoryBudgetHost host = new(storage, caller);
            Task<QpdfJsonResult> Export() => Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("security-r6-ascii.pdf"), QpdfPath.From("out.json"),
                new QpdfJsonOptions(2, ["encrypt", "qpdf"], "inline", null, [], false)
                { Password = QpdfPassword.FromText("reader") }, CancellationToken.None).AsTask();
            if (refuses)
            {
                await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Export);
                Assert.False(storage.TryGetOutput(QpdfPath.From("out.json"), out _));
            }
            else { _ = await Export(); }
            Assert.Equal(0, storage.OpenLeaseCount); caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }



    private static void Add(InMemoryQpdfHost host, string file)
        => host.AddInput(QpdfPath.From(file), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Transfer);
}
