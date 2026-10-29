using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonSidecarNameTests
{
    private sealed class Host(InMemoryQpdfHost storage) : IQpdfHost
    {
        internal readonly List<string> Created = [];
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Created.Add(path.Value); return storage.CreateOutputAsync(path, token); }
    }

    private static string Prefix(string kind) => kind switch
    {
        "ascii" => "side", "bmp" => "café", "pair" => "📄",
        "pair-edge" => new string('a', 255) + "📄" + new string('b', 10),
        "high" => "\uD800", "low" => "\uDC00", "broken" => "a\uD800x", "nul" => "s\0x",
        "both-fit" => new('a', 4093), "one-fits" => new('a', 4094),
        "overflow" => new('a', 4095), "max-prefix" => new('a', 4096),
        _ => throw new InvalidOperationException("Unknown prefix case.")
    };

    public static IEnumerable<object[]> Cases()
    {
        foreach (string kind in new[] { "ascii", "bmp", "pair", "pair-edge", "high", "low", "broken", "nul", "both-fit", "one-fits", "overflow", "max-prefix" })
        foreach (string route in new[] { "typed", "argv", "job", "mixed" })
        foreach (bool defaultPrefix in new[] { false, true })
        foreach (bool twoStreams in new[] { false, true })
        {
            if (kind == "nul" && defaultPrefix) { continue; }
            yield return [kind, route, defaultPrefix, twoStreams];
        }
    }

    private static byte[] Fixture(bool twoStreams)
    {
        List<(int, string, byte[]?)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R" + (twoStreams ? " /Extra 10 0 R" : "") + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> /Contents 4 0 R >>", null),
            (4, "<< /Length 4 >>", "q Q\n"u8.ToArray())];
        if (twoStreams)
        {
            for (int number = 5; number < 10; number++) { objects.Add((number, "null", null)); }
            objects.Add((10, "<< /Length 7 >>", "SECOND\n"u8.ToArray()));
        }
        return CheckTests.Assemble(objects, 1);
    }

    private static string Literal(string value) => "\"" + string.Concat(value.Select(unit =>
        "\\u" + ((int)unit).ToString("x4", System.Globalization.CultureInfo.InvariantCulture))) + "\"";

    private static async Task<(QpdfExecutionResult? Command, string? Text)> Export(Host host, string route, string output, string? prefix)
    {
        if (route == "typed")
        {
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From(output),
                new QpdfJsonOptions(2, ["qpdf"], "file", prefix, [], false), CancellationToken.None);
            return (null, result.Json);
        }
        if (route == "job")
        {
            string job = "{\"inputFile\":\"input\",\"outputFile\":" + Literal(output)
                + ",\"json\":\"2\",\"jsonKey\":[\"qpdf\"],\"jsonStreamData\":\"file\""
                + (prefix is null ? "" : ",\"jsonStreamPrefix\":" + Literal(prefix)) + "}";
            return (await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None), null);
        }
        List<string> arguments = ["input"];
        if (route == "mixed") { arguments.Add("--job-json-file=settings"); }
        arguments.AddRange(["--json=2", "--json-key=qpdf", "--json-stream-data=file"]);
        if (prefix is not null) { arguments.Add("--json-stream-prefix=" + prefix); }
        arguments.AddRange(["--", output]);
        return (await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None), null);
    }

    private static async Task Seed(InMemoryQpdfHost storage, string name, byte[] bytes)
    {
        await using IQpdfOutputTransaction prior = await storage.CreateOutputAsync(QpdfPath.From(name), CancellationToken.None);
        await prior.WriteAsync(bytes, CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SidecarNamesStayExactOrFailBeforeAnyPublication(string kind, string route, bool defaultPrefix, bool twoStreams)
    {
        using InMemoryQpdfHost storage = new(); Host host = new(storage);
        storage.AddInput(QpdfPath.From("input"), Fixture(twoStreams), QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("settings"), "{}"u8.ToArray(), QpdfInputOwnership.Transfer);
        string prefix = Prefix(kind), output = defaultPrefix ? prefix : "main";
        int[] numbers = twoStreams ? [4, 10] : [4];
        await Seed(storage, output, "prior-main"u8.ToArray());
        List<string> seeded = [];
        foreach (int number in numbers)
        {
            string name = prefix + "-" + number;
            if (name.Length <= QpdfPath.MaxLength && !name.Contains('\0'))
            { await Seed(storage, name, "prior-side"u8.ToArray()); seeded.Add(name); }
        }
        bool valid = kind is "ascii" or "bmp" or "pair" or "pair-edge" or "both-fit" || (kind == "one-fits" && !twoStreams);
        if (!valid)
        {
            if (route == "typed") { await Assert.ThrowsAsync<QpdfParseException>(() => Export(host, route, output, defaultPrefix ? null : prefix)); }
            else
            {
                var failure = await Export(host, route, output, defaultPrefix ? null : prefix);
                Assert.NotNull(failure.Command); Assert.Equal(QpdfExecutionStatus.Failed, failure.Command.Status);
                Assert.NotEmpty(failure.Command.Diagnostics);
            }
            Assert.Empty(host.Created);
            Assert.True(storage.TryGetOutput(QpdfPath.From(output), out byte[] prior)); Assert.Equal("prior-main"u8.ToArray(), prior);
            foreach (string name in seeded)
            { Assert.True(storage.TryGetOutput(QpdfPath.From(name), out byte[] bytes)); Assert.Equal("prior-side"u8.ToArray(), bytes); }
            Assert.Equal(0L, storage.OpenLeaseCount); return;
        }
        var result = await Export(host, route, output, defaultPrefix ? null : prefix);
        if (result.Command is not null) { Assert.Equal(QpdfExitCode.Success, result.Command.ExitCode); Assert.Empty(result.Command.Diagnostics); }
        Assert.Equal(numbers.Length + 1, host.Created.Count);
        Assert.True(storage.TryGetOutput(QpdfPath.From(output), out byte[] jsonBytes));
        using (JsonDocument json = JsonDocument.Parse(jsonBytes))
        {
            foreach (int number in numbers)
            {
                string expected = prefix + "-" + number;
                Assert.Equal(expected, json.RootElement.GetProperty("qpdf")[1].GetProperty("obj:" + number + " 0 R")
                    .GetProperty("stream").GetProperty("datafile").GetString());
                Assert.Contains(expected, host.Created);
                Assert.True(storage.TryGetOutput(QpdfPath.From(expected), out byte[] payload));
                Assert.Equal(number == 4 ? "q Q\n"u8.ToArray() : "SECOND\n"u8.ToArray(), payload);
                storage.AddInput(QpdfPath.From(expected), payload, QpdfInputOwnership.Transfer);
            }
        }
        storage.AddInput(QpdfPath.From("json"), jsonBytes, QpdfInputOwnership.Transfer);
        await Qpdf.CreatePdfFromJsonAsync(storage, QpdfPath.From("json"), QpdfPath.From("roundtrip"), CancellationToken.None);
        Assert.True(storage.TryGetOutput(QpdfPath.From("roundtrip"), out byte[] pdf));
        storage.AddInput(QpdfPath.From("again"), pdf, QpdfInputOwnership.Transfer);
        QpdfJsonResult inline = await Qpdf.GetDocumentJsonAsync(storage, QpdfPath.From("again"), QpdfPath.From("inline"),
            new QpdfJsonOptions(2, ["qpdf"], "inline", null, [], false), CancellationToken.None);
        Assert.Equal(0L, storage.OpenLeaseCount); storage.Dispose();
        using JsonDocument roundtrip = JsonDocument.Parse(inline.Json);
        foreach (int number in numbers)
        {
            byte[] bytes = roundtrip.RootElement.GetProperty("qpdf")[1].GetProperty("obj:" + number + " 0 R")
                .GetProperty("stream").GetProperty("data").GetBytesFromBase64();
            Assert.Equal(number == 4 ? "q Q\n"u8.ToArray() : "SECOND\n"u8.ToArray(), bytes);
        }
        if (result.Text is not null) { using JsonDocument typed = JsonDocument.Parse(result.Text); }
    }

    [Theory]
    [InlineData("length")]
    [InlineData("unicode")]
    [InlineData("nul")]
    [InlineData("refusal")]
    [InlineData("cancel")]
    public void NameChecksAndAdmissionPrecedeLargePayloadCopies(string mode)
    {
        byte[] payload = new byte[1048576];
        string prefix = mode switch { "length" => new('a', 4086), "unicode" => "\uD800", "nul" => "s\0x", _ => "side" };
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096 };
        ResourceGovernor governor = new(limits, cancellation.Token);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(71L);
        long baseline = governor.Snapshot().LiveMemoryBytes, before = GC.GetAllocatedBytesForCurrentThread();
        using (PreparedJsonOutput output = PreparedJsonOutput.Create(governor))
        {
            if (mode == "cancel") { cancellation.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => output.AddSidecar(prefix, int.MaxValue, payload, limits)); }
            else if (mode == "refusal") { Assert.Throws<QpdfLimitException>(() => output.AddSidecar(prefix, int.MaxValue, payload, limits)); }
            else { Assert.Throws<QpdfParseException>(() => output.AddSidecar(prefix, int.MaxValue, payload, limits)); }
        }
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 131072L);
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void MaximumObjectNumberSuffixCanReachTheExactLogicalPathLimit()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (PreparedJsonOutput output = PreparedJsonOutput.Create(governor))
        {
            JsonPayload payload = output.AddSidecar(new string('a', 4085), int.MaxValue, "X"u8, QpdfLimits.Default);
            Assert.Equal(QpdfPath.MaxLength, payload.Name.Length); Assert.EndsWith("-2147483647", payload.Name);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
