using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonCatalogVersionTests
{
    private static readonly QpdfPath Json = QpdfPath.From("input.json");
    private static readonly QpdfPath Source = QpdfPath.From("source.pdf");
    private static readonly QpdfPath Output = QpdfPath.From("output.pdf");

    private sealed class Host(string fault, IMemoryBudget? budget, CancellationTokenSource? cancel)
        : IQpdfHost, IQpdfMemoryHost, IDisposable
    {
        internal readonly InMemoryQpdfHost Inner = new();
        internal int Created;
        public IMemoryBudget? MemoryBudget => budget;
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            IQpdfInput input = await Inner.OpenInputAsync(path, token);
            if (fault == "read-cancel") { cancel?.Cancel(); }
            return input;
        }
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            Created++;
            return new Transaction(await Inner.CreateOutputAsync(path, token), fault, cancel);
        }
        public void Dispose() => Inner.Dispose();

        private sealed class Transaction(IQpdfOutputTransaction inner, string fault, CancellationTokenSource? cancel)
            : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            {
                if (fault == "write") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); }
                if (fault == "cancel") { cancel?.Cancel(); token.ThrowIfCancellationRequested(); }
                return inner.WriteAsync(bytes, token);
            }
            public ValueTask CommitAsync(CancellationToken token)
                => fault == "commit" ? throw new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed) : inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private static byte[] Fixture(object? version, bool present, bool indirect, bool directRoot)
    {
        Dictionary<string, object?> catalog = new() { ["/Type"] = "/Catalog", ["/Pages"] = "2 0 R" };
        if (present) { catalog["/Version"] = indirect ? "4 0 R" : version; }
        return JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] {
            new { jsonversion = 2, pdfversion = "1.3" }, new Dictionary<string, object> {
                ["obj:1 0 R"] = new { value = catalog },
                ["obj:2 0 R"] = new { value = new Dictionary<string, object> {
                    ["/Type"] = "/Pages", ["/Kids"] = new[] { "3 0 R" }, ["/Count"] = 1 } },
                ["obj:3 0 R"] = new { value = new Dictionary<string, object> {
                    ["/Type"] = "/Page", ["/Parent"] = "2 0 R", ["/MediaBox"] = new[] { 0, 0, 10, 20 } } },
                ["obj:4 0 R"] = new { value = indirect ? version : null },
                ["trailer"] = new { value = new Dictionary<string, object> {
                    ["/Root"] = directRoot ? catalog : "1 0 R" } }
            } } });
    }

    private static async Task Seed(Host host, byte[] json)
    {
        host.Inner.AddInput(Json, json, QpdfInputOwnership.Copy);
        host.Inner.AddInput(Source, CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 20] >>", null)], 1), QpdfInputOwnership.Copy);
        await using IQpdfOutputTransaction prior = await host.Inner.CreateOutputAsync(Output, CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
        await prior.CommitAsync(CancellationToken.None);
    }

    private static async Task<QpdfExecutionResult?> Run(Host host, string route, bool update, string setting, CancellationToken token)
    {
        if (route == "typed")
        {
            if (update) { await Qpdf.UpdatePdfFromJsonAsync(host, Source, Json, Output, token); }
            else { await Qpdf.CreatePdfFromJsonAsync(host, Json, Output, token); }
            return null;
        }
        if (route == "job")
        {
            Dictionary<string, object> job = new() { ["inputFile"] = update ? "source.pdf" : "input.json", ["outputFile"] = "output.pdf" };
            job[update ? "updateFromJson" : "jsonInput"] = update ? "input.json" : "";
            if (setting != "none") { job[setting == "force" ? "forceVersion" : "minVersion"] = setting == "force" ? "1.4" : "1.6"; }
            return await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), token);
        }
        List<string> argv = update ? ["source.pdf", "--update-from-json=input.json"] : ["input.json", "--json-input"];
        if (setting != "none") { argv.Add(setting == "force" ? "--force-version=1.4" : "--min-version=1.6"); }
        argv.AddRange(["--", "output.pdf"]);
        return await Qpdf.ExecuteAsync(host, new(argv), token);
    }

    private static void AssertUnpublished(Host host)
    {
        Assert.Equal(0, host.Inner.OpenLeaseCount);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] bytes));
        Assert.Equal("prior"u8.ToArray(), bytes);
    }

    public static IEnumerable<object[]> InvalidCases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (bool update in new[] { false, true })
        foreach (bool directRoot in new[] { false, true })
        foreach (bool indirect in new[] { false, true })
        foreach (string kind in new[] { "string", "integer", "array", "dictionary", "malformed", "unsupported" })
        { yield return [route, update, directRoot, indirect, kind]; }
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public async Task InvalidVersionsFailBeforeOpeningOutput(string route, bool update, bool directRoot, bool indirect, string kind)
    {
        object value = kind switch { "string" => "u:bad", "integer" => 17, "array" => new[] { 1 },
            "dictionary" => new { }, "malformed" => "/1.70", _ => "/9.9" };
        using Host host = new("", null, null);
        await Seed(host, Fixture(value, true, indirect, directRoot));
        string expected = kind switch { "malformed" => "Malformed catalog /Version /1.70.",
            "unsupported" => "Unsupported PDF version 9.9.", _ => "Catalog /Version must be a name." };
        if (route == "typed")
        {
            QpdfParseException error = await Assert.ThrowsAsync<QpdfParseException>(() => Run(host, route, update, "none", CancellationToken.None));
            Assert.Contains(expected, error.Message);
        }
        else
        {
            QpdfExecutionResult result = Assert.IsType<QpdfExecutionResult>(await Run(host, route, update, "none", CancellationToken.None));
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains(expected, StringComparison.Ordinal));
            Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0, host.Created);
        AssertUnpublished(host);
    }

    public static IEnumerable<object[]> ValidCases()
    {
        foreach (string version in new[] { "1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "1.7", "2.0" })
        foreach (bool update in new[] { false, true })
        foreach (bool directRoot in new[] { false, true })
        foreach (bool indirect in new[] { false, true })
        { yield return [version, update, directRoot, indirect]; }
    }

    private static async Task AssertReadable(Host host, string header, string effective, string? version)
    {
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] bytes));
        QpdfPath readback = QpdfPath.From("readback");
        host.Inner.AddInput(readback, bytes, QpdfInputOwnership.Copy);
        QpdfDocument document = await QpdfDocument.OpenAsync(host.Inner, readback, CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        PdfName? escaped = null;
        if (version is not null)
        {
            Assert.True(catalog.TryGet(PdfName.FromAscii("Version"), out QpdfObject raw));
            escaped = Assert.IsType<PdfName>(raw is PdfReference reference ? document.GetObject(reference.ObjectNumber) : raw);
        }
        await document.DisposeAsync();
        Assert.Equal(header, document.HeaderVersion.ToString());
        Assert.Equal(effective, document.Version.ToString());
        if (escaped is not null) { Assert.Equal(version, escaped.DisplayText); }
        Assert.True(catalog.TryGet(PdfName.FromAscii("Pages"), out _));
        Assert.Equal(0, host.Inner.OpenLeaseCount);
    }

    [Theory]
    [MemberData(nameof(ValidCases))]
    public async Task SupportedNamesKeepTheirHeaderAndEscapedValues(string version, bool update, bool directRoot, bool indirect)
    {
        using Host host = new("", null, null);
        await Seed(host, Fixture("/" + version, true, indirect, directRoot));
        await Run(host, "typed", update, "none", CancellationToken.None);
        await AssertReadable(host, "1.3", string.CompareOrdinal(version, "1.3") > 0 ? version : "1.3", version);
    }

    public static IEnumerable<object[]> ReferenceCases()
    {
        foreach (bool update in new[] { false, true })
        foreach (bool directRoot in new[] { false, true })
        foreach (string kind in new[] { "absent", "null", "indirect-null", "missing" })
        { yield return [update, directRoot, kind]; }
    }

    [Theory]
    [MemberData(nameof(ReferenceCases))]
    public async Task NormalizedNullsRemainValid(bool update, bool directRoot, string kind)
    {
        object? value = kind == "missing" ? "99 0 R" : null;
        using Host host = new("", null, null);
        await Seed(host, Fixture(value, kind != "absent", kind == "indirect-null", directRoot));
        QpdfExecutionResult result = Assert.IsType<QpdfExecutionResult>(await Run(host, "argv", update, "none", CancellationToken.None));
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await AssertReadable(host, "1.3", "1.3", null);
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("99 0 R", false)]
    [InlineData("4 0 R", true)]
    [InlineData("5 0 R", true)]
    public async Task ExistingVersionTargetsNormalizeDanglingReferencesAndRejectCycles(string target, bool cycle)
    {
        using Host host = new("", null, null);
        await Seed(host, System.Text.Encoding.UTF8.GetBytes("""
            {"qpdf":[{"jsonversion":2,"pdfversion":"1.3"},{
              "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Version":"4 0 R"}}}]}
            """));
        host.Inner.AddInput(Source, CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 20] >>", null),
            (4, target, null), (5, "4 0 R", null)], 1), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = Assert.IsType<QpdfExecutionResult>(await Run(host, "argv", true, "none", CancellationToken.None));
        if (cycle)
        {
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("Catalog /Version must be a name.", StringComparison.Ordinal));
            Assert.Equal(0, host.Created); AssertUnpublished(host);
        }
        else
        {
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            await AssertReadable(host, "1.3", "1.3", null);
        }
    }

    public static IEnumerable<object[]> HeaderCases()
    {
        foreach (string route in new[] { "argv", "job" })
        foreach (bool update in new[] { false, true })
        foreach (bool directRoot in new[] { false, true })
        foreach (bool indirect in new[] { false, true })
        foreach (string setting in new[] { "none", "force", "min" })
        { yield return [route, update, directRoot, indirect, setting]; }
    }

    [Theory]
    [MemberData(nameof(HeaderCases))]
    public async Task CatalogValidationPreservesJsonHeaderNegotiation(string route, bool update, bool directRoot, bool indirect, string setting)
    {
        using Host host = new("", null, null);
        await Seed(host, Fixture("/1.7", true, indirect, directRoot));
        QpdfExecutionResult result = Assert.IsType<QpdfExecutionResult>(await Run(host, route, update, setting, CancellationToken.None));
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await AssertReadable(host, setting == "force" ? "1.4" : setting == "min" ? "1.6" : "1.3", "1.7", "1.7");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryAdmissionRefusalPreservesOutputAndRefundsPrivateStorage(bool update)
    {
        int growths;
        RecordingMemoryBudget baseline = new(16777216L);
        using (IMemoryScope owner = baseline.CreateScope())
        using (Host host = new("", owner, null))
        {
            await Seed(host, Fixture("/1.7", true, true, true));
            QpdfExecutionResult result = Assert.IsType<QpdfExecutionResult>(await Run(host, "argv", update, "none", CancellationToken.None));
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            growths = baseline.GrowthCalls;
            Assert.True(growths > 0);
        }
        Assert.Equal(0L, baseline.ReservedBytes);
        for (int refused = 1; refused <= growths; refused++)
        {
            RecordingMemoryBudget budget = new(16777216L) { RefuseOnGrowth = refused };
            using (IMemoryScope owner = budget.CreateScope())
            using (Host host = new("", owner, null))
            {
                await Seed(host, Fixture("/1.7", true, true, true));
                QpdfExecutionResult result = Assert.IsType<QpdfExecutionResult>(await Run(host, "argv", update, "none", CancellationToken.None));
                Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
                AssertUnpublished(host);
            }
            Assert.Equal(0L, budget.ReservedBytes);
        }
    }

    [Theory]
    [InlineData(false, "write")]
    [InlineData(true, "write")]
    [InlineData(false, "commit")]
    [InlineData(true, "commit")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "read-cancel")]
    [InlineData(true, "read-cancel")]
    public async Task CancellationAndOutputFailuresPreservePriorBytes(bool update, string fault)
    {
        using CancellationTokenSource cancel = new();
        using Host host = new(fault, null, cancel);
        await Seed(host, Fixture("/1.7", true, true, true));
        QpdfExecutionResult result = Assert.IsType<QpdfExecutionResult>(await Run(host, "argv", update, "none", cancel.Token));
        Assert.Equal(fault.Contains("cancel", StringComparison.Ordinal) ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(fault == "read-cancel" ? 0 : 1, host.Created);
        Assert.Equal(0L, result.Counters.OpenHandles);
        AssertUnpublished(host);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongMalformedNamesHaveBoundedDiagnosticsInJsonAndBinaryReaders(bool update)
    {
        string name = new('x', 100000);
        using Host host = new("", null, null);
        await Seed(host, Fixture("/" + name, true, true, false));
        QpdfParseException jsonError = await Assert.ThrowsAsync<QpdfParseException>(() => Run(host, "typed", update, "none", CancellationToken.None));
        Assert.Contains("Malformed catalog /Version", jsonError.Message);
        Assert.True(jsonError.Message.Length < 256);
        Assert.Equal(0, host.Created); AssertUnpublished(host);
        byte[] pdf = CheckTests.Assemble([(1, "<< /Type /Catalog /Version /" + name + " >>", null)], 1);
        host.Inner.AddInput(QpdfPath.From("long.pdf"), pdf, QpdfInputOwnership.Copy);
        QpdfParseException readError = await Assert.ThrowsAsync<QpdfParseException>(async () =>
        { await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("long.pdf"), CancellationToken.None); });
        Assert.Contains("Malformed catalog /Version", readError.Message);
        Assert.True(readError.Message.Length < 256);
        Assert.Equal(0, host.Inner.OpenLeaseCount);
    }
}
