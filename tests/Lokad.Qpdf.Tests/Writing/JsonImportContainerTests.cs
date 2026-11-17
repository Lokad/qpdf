using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonImportContainerTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Json = QpdfPath.From("json"), Output = QpdfPath.From("output");
    private static readonly QpdfLimits Limits = QpdfLimits.Default with { MaxContainerSize = 16 };

    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget? budget) : IQpdfHost, IQpdfMemoryHost
    {
        internal int Created;
        public IMemoryBudget? MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Created++; return storage.CreateOutputAsync(path, token); }
    }

    private static string Value(string shape, int size) => shape == "array"
        ? "[" + string.Join(',', Enumerable.Range(0, size)) + "]"
        : "{" + string.Join(',', Enumerable.Range(0, size).Select(i => "\"/K" + i + "\":" + i)) + "}";

    private static byte[] Fixture(string value, string placement, string extra)
    {
        string target = placement == "nested" ? "{\"/Nested\":" + value + "}" : value;
        string object4 = placement == "trailer" ? "{\"value\":0}" : placement == "stream"
            ? "{\"stream\":{\"dict\":" + (value[0] == '{' ? value : "{\"/Audit\":" + value + "}") + ",\"data\":\"cSBRCg==\"" + extra + "}" + extra + "}"
            : "{\"value\":" + target + extra + "}";
        string trailer = "{\"/Root\":\"1 0 R\""
            + (placement == "trailer" ? ",\"/Info\":" + (value[0] == '{' ? value : "{\"/Audit\":" + value + "}") : "") + "}";
        return Encoding.UTF8.GetBytes("{\"qpdf\":[{\"jsonversion\":2,\"pdfversion\":\"1.4\"" + extra + "},{"
            + "\"obj:1 0 R\":{\"value\":{\"/Type\":\"/Catalog\",\"/Pages\":\"2 0 R\",\"/Audit\":\"4 0 R\"}},"
            + "\"obj:2 0 R\":{\"value\":{\"/Type\":\"/Pages\",\"/Count\":1,\"/Kids\":[\"3 0 R\"]}},"
            + "\"obj:3 0 R\":{\"value\":{\"/Type\":\"/Page\",\"/Parent\":\"2 0 R\",\"/MediaBox\":[0,0,80,40],\"/Resources\":{}}},"
            + "\"obj:4 0 R\":" + object4 + ",\"trailer\":{\"value\":" + trailer + "}}]" + extra + "}");
    }

    private static async Task Seed(InMemoryQpdfHost storage, byte[] json)
    {
        storage.AddInput(Input, PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] >>", null)], 1), QpdfInputOwnership.Transfer);
        storage.AddInput(Json, json, QpdfInputOwnership.Copy);
        await using IQpdfOutputTransaction prior = await storage.CreateOutputAsync(Output, CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
    }

    private static async Task Run(Host host, bool update, string route, string policy, QpdfLimits limits, bool valid)
    {
        if (route == "typed")
        {
            Task<QpdfJsonImportResult> operation = update
                ? Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, limits, CancellationToken.None).AsTask()
                : Qpdf.CreatePdfFromJsonAsync(host, Json, Output, limits, CancellationToken.None).AsTask();
            if (valid) { Assert.Equal(1, (await operation).PageCount); }
            else
            {
                QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() => operation);
                Assert.Equal(nameof(QpdfLimits.MaxContainerSize), failure.LimitName);
                Assert.Equal(16L, failure.Allowed); Assert.True(failure.Requested > failure.Allowed);
            }
            return;
        }
        QpdfExecutionResult result;
        if (route == "argv")
        {
            List<string> argv = [];
            if (policy != "host")
            {
                argv.Add("--global"); argv.Add(policy == "tighten" ? "--parser-max-container-size=16" : "--no-default-limits"); argv.Add("--");
            }
            argv.AddRange(update ? ["input", "--update-from-json=json", "output"] : ["json", "--json-input", "output"]);
            result = await Qpdf.ExecuteAsync(host, new(argv), new QpdfExecutionOptions(limits), CancellationToken.None);
        }
        else
        {
            Dictionary<string, object> job = new() { ["inputFile"] = update ? "input" : "json", ["outputFile"] = "output" };
            job[update ? "updateFromJson" : "jsonInput"] = update ? "json" : true;
            if (policy != "host") { job["global"] = policy == "tighten" ? new { parserMaxContainerSize = 16 } : (object)new { noDefaultLimits = true }; }
            result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), new QpdfExecutionOptions(limits), CancellationToken.None);
        }
        Assert.Equal(valid ? QpdfExecutionStatus.Completed : QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(0L, result.Counters.OpenHandles);
        if (!valid) { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded && d.Message.Contains(nameof(QpdfLimits.MaxContainerSize), StringComparison.Ordinal)); }
    }

    public static IEnumerable<object[]> Containers()
    {
        foreach (bool update in new[] { false, true })
        foreach (string shape in new[] { "array", "dictionary" })
        foreach (string placement in new[] { "value", "nested", "stream", "trailer" })
        foreach (int size in new[] { 16, 17 }) { yield return [update, shape, placement, size]; }
    }

    [Theory]
    [MemberData(nameof(Containers))]
    public async Task ImportedContainersKeepTheExactBoundaryAndRejectOverflowBeforePublication(bool update, string shape, string placement, int size)
    {
        byte[] source = Fixture(Value(shape, size), placement, ""), original = source.ToArray();
        using InMemoryQpdfHost storage = new(); await Seed(storage, source); Host host = new(storage, null);
        await Run(host, update, "typed", "host", Limits, size == 16);
        Assert.Equal(size == 16 ? 1 : 0, host.Created); Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(original, source);
        Assert.True(storage.TryGetOutput(Output, out byte[] output));
        if (size > 16) { Assert.Equal("prior"u8.ToArray(), output); return; }
        storage.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(storage, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        QpdfObject Get(PdfDictionary node, string key) { Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject raw)); return Resolve(raw); }
        QpdfObject target = placement == "trailer" ? Get(document.Engine.Trailer, "Info") : document.GetObject(4);
        if (placement == "nested") { target = Get(Assert.IsType<PdfDictionary>(target), "Nested"); }
        if (target is PdfStream stream) { Assert.Equal("q Q\n"u8.ToArray(), stream.Data); target = stream.Dictionary; }
        if (shape == "array")
        {
            if (target is PdfDictionary wrapper) { target = Get(wrapper, "Audit"); }
            PdfArray array = Assert.IsType<PdfArray>(target); Assert.Equal(16, array.Count); Assert.Equal(15L, Assert.IsType<PdfInteger>(array[15]).Value);
        }
        else
        {
            PdfDictionary dictionary = Assert.IsType<PdfDictionary>(target);
            Assert.Equal(16, dictionary.Keys.Count(k => k.RawSpan.StartsWith("K"u8)));
            Assert.Equal(15L, Assert.IsType<PdfInteger>(Get(dictionary, "K15")).Value);
        }
    }

    [Theory]
    [InlineData(false, "argv", "array", "host", 16)]
    [InlineData(false, "argv", "array", "host", 17)]
    [InlineData(true, "job", "dictionary", "host", 16)]
    [InlineData(true, "job", "dictionary", "host", 17)]
    [InlineData(false, "job", "array", "tighten", 16)]
    [InlineData(false, "job", "array", "tighten", 17)]
    [InlineData(true, "argv", "dictionary", "tighten", 16)]
    [InlineData(true, "argv", "dictionary", "tighten", 17)]
    [InlineData(false, "argv", "dictionary", "no-defaults", 16)]
    [InlineData(false, "argv", "dictionary", "no-defaults", 17)]
    [InlineData(true, "job", "array", "no-defaults", 16)]
    [InlineData(true, "job", "array", "no-defaults", 17)]
    public async Task CommandsEnforceEffectiveContainerLimits(bool update, string route, string shape, string policy, int size)
    {
        using InMemoryQpdfHost storage = new(); await Seed(storage, Fixture(Value(shape, size), "stream", "")); Host host = new(storage, null);
        await Run(host, update, route, policy, policy == "tighten" ? Limits with { MaxContainerSize = 32 } : Limits, size == 16);
        Assert.Equal(size == 16 ? 1 : 0, host.Created); Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.True(storage.TryGetOutput(Output, out byte[] output));
        if (size > 16) { Assert.Equal("prior"u8.ToArray(), output); }
    }

    [Theory]
    [InlineData("array")]
    [InlineData("dictionary")]
    public async Task ContainerChecksPrecedeDecodingInvalidOverflowValues(string shape)
    {
        string value = shape == "array" ? "[\"invalid\"," + string.Join(',', Enumerable.Repeat("0", 16)) + "]"
            : Value("dictionary", 16)[..^1] + ",\"/Overflow\":\"invalid\"}";
        using InMemoryQpdfHost storage = new(); await Seed(storage, Fixture(value, "value", "")); Host host = new(storage, null);
        await Run(host, false, "typed", "host", Limits, false); Assert.Equal(0, host.Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownEnvelopeMembersDoNotBecomePdfContainerLimits(bool update)
    {
        string extra = ",\"future\":" + Value("dictionary", 40);
        using InMemoryQpdfHost storage = new(); await Seed(storage, Fixture("{}", "stream", extra)); Host host = new(storage, null);
        await Run(host, update, "typed", "host", Limits, true); Assert.Equal(1, host.Created); Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Theory]
    [InlineData("duplicates", true)]
    [InlineData("escaped", true)]
    [InlineData("full-duplicate", false)]
    [InlineData("nulls", false)]
    public async Task DecodedDictionaryNamesFollowPdfParserCounting(string kind, bool valid)
    {
        List<(string Name, string Value)> fields = kind == "duplicates"
            ? Enumerable.Range(0, 100).Select(i => ("/K0", i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList()
            : [("/K0", "0")];
        if (kind == "escaped") { fields.Add(("n:/K#30", "99")); }
        fields.AddRange(Enumerable.Range(1, 15).Select(i => ("/K" + i, kind == "nulls" ? "null" : i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        if (kind is "full-duplicate" or "nulls") { fields.Add(("/K0", "123")); }
        string value = "{" + string.Join(',', fields.Select(f => JsonSerializer.Serialize(f.Name) + ":" + f.Value)) + "}";
        using InMemoryQpdfHost storage = new(); await Seed(storage, Fixture(value, "value", "")); Host host = new(storage, null);
        await Run(host, false, "typed", "host", Limits, valid); Assert.Equal(valid ? 1 : 0, host.Created);
        string syntax = "<< " + string.Join(' ', fields.Select(f => (f.Name.StartsWith("n:", StringComparison.Ordinal) ? f.Name[2..] : f.Name) + " " + f.Value)) + " >>";
        ResourceGovernor governor = new(Limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfParser parser = new(Limits, governor, new(governor), null);
        QpdfObject Parse() => parser.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(syntax)), 0, memory);
        if (valid) { Assert.Equal(16, Assert.IsType<PdfDictionary>(Parse()).Count); }
        else { Assert.Equal(nameof(QpdfLimits.MaxContainerSize), Assert.Throws<QpdfLimitException>(Parse).LimitName); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContainerFailureRefundsItsOwnerAndAllowsRetry(bool update)
    {
        using RecordingMemoryBudget budget = new(16L * 1024 * 1024);
        using IMemoryScope earlier = budget.CreateScope();
        Assert.True(earlier.TryReserve(79L, out IMemoryReservation? reservation));
        using IMemoryReservation retained = reservation ?? throw new InvalidOperationException("Missing earlier reservation.");
        using InMemoryQpdfHost storage = new(); await Seed(storage, Fixture(Value("dictionary", 17), "nested", ""));
        using (IMemoryScope owner = budget.CreateScope())
        {
            Host host = new(storage, owner);
            await Run(host, update, "typed", "host", Limits, false);
            Assert.Equal(79L, budget.ReservedBytes); Assert.Equal(0, host.Created); Assert.Equal(0L, storage.OpenLeaseCount);
            storage.AddInput(Json, Fixture(Value("dictionary", 16), "nested", ""), QpdfInputOwnership.Transfer);
            await Run(host, update, "typed", "host", Limits, true); Assert.Equal(1, host.Created);
        }
        Assert.Equal(79L, budget.ReservedBytes); retained.Dispose(); Assert.Equal(0L, budget.ReservedBytes);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    public async Task TrailerDictionaryItselfHasTheSameCeiling(int size)
    {
        string value = "{\"/Root\":\"1 0 R\"," + string.Join(',', Enumerable.Range(1, size - 1).Select(i => "\"/K" + i + "\":0")) + "}";
        string source = Encoding.UTF8.GetString(Fixture("0", "value", "")).Replace("\"trailer\":{\"value\":{\"/Root\":\"1 0 R\"}}", "\"trailer\":{\"value\":" + value + "}", StringComparison.Ordinal);
        using InMemoryQpdfHost storage = new(); await Seed(storage, Encoding.UTF8.GetBytes(source)); Host host = new(storage, null);
        await Run(host, false, "typed", "host", Limits, size == 16); Assert.Equal(size == 16 ? 1 : 0, host.Created);
    }
}
