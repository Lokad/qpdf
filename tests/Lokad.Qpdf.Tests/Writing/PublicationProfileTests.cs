using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class PublicationProfileTests
{
    public static IEnumerable<object[]> ImportCases()
    {
        foreach (string kind in new[] { "signature", "indirect", "nested", "registered", "widget", "inherited", "loose", "blank", "private", "ordinary" })
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (bool update in new[] { false, true })
        { yield return [kind, route, update]; }
    }

    [Theory]
    [MemberData(nameof(ImportCases))]
    public async Task JsonPublicationUsesTheCopyProfile(string kind, string route, bool update)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("json"), JsonFixture(kind), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("input"), PdfFixture("ordinary", false), QpdfInputOwnership.Transfer);
        await Seed(host);
        if (route == "typed")
        {
            async Task Run()
            {
                if (update) { await Qpdf.UpdatePdfFromJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("json"), QpdfPath.From("output"), CancellationToken.None); }
                else { await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("json"), QpdfPath.From("output"), CancellationToken.None); }
            }
            await Run();
        }
        else
        {
            QpdfExecutionResult result;
            if (route == "job")
            {
                result = await Qpdf.ExecuteJobAsync(host, update
                    ? JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", updateFromJson = "json" })
                    : JsonSerializer.Serialize(new { inputFile = "json", outputFile = "output", jsonInput = true }), CancellationToken.None);
            }
            else
            {
                string[] args = update ? ["input", "--update-from-json=json", "--", "output"] : ["json", "--json-input", "--", "output"];
                result = await Qpdf.ExecuteAsync(host, new(args), CancellationToken.None);
            }
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(0, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        Assert.True(bytes.AsSpan().StartsWith("%PDF-"u8));
    }

    public static IEnumerable<object[]> LooseCases()
    {
        foreach (string kind in new[] { "signature", "indirect", "nested", "widget", "blank", "private", "ordinary", "cycle" })
        foreach (bool preserve in new[] { false, true })
        foreach (bool reachable in new[] { false, true })
        foreach (bool job in new[] { false, true })
        { yield return [kind, preserve, reachable, job]; }
    }

    [Theory]
    [MemberData(nameof(LooseCases))]
    public async Task ExplicitLoosePreservationKeepsInactiveSignatureData(string kind, bool preserve, bool reachable, bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), PdfFixture(kind, reachable), QpdfInputOwnership.Transfer);
        await Seed(host);
        Dictionary<string, object> fields = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
        List<string> args = ["input"];
        if (preserve) { args.Add("--preserve-unreferenced"); fields["preserveUnreferenced"] = true; }
        args.Add("output");
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(args), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        Assert.True(bytes.AsSpan().StartsWith("%PDF-"u8));
    }

    private static async Task Seed(InMemoryQpdfHost host)
    {
        await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
    }

    private static byte[] JsonFixture(string kind)
    {
        Dictionary<string, object?> root = new() { ["/Type"] = "/Catalog", ["/Pages"] = "2 0 R" };
        if (kind == "registered") { root["/AcroForm"] = new Dictionary<string, object> { ["/Fields"] = new[] { "5 0 R" } }; }
        else if (kind != "loose") { root["/Audit"] = "5 0 R"; }
        Dictionary<string, object?> value = new();
        object? auxiliary = null;
        Dictionary<string, object> signature = new() { ["/Contents"] = "b:00", ["/ByteRange"] = new[] { 0, 1, 2, 3 } };
        if (kind is "signature" or "loose" or "indirect")
        { value["/Type"] = kind == "indirect" ? "6 0 R" : "/Sig"; value["/Contents"] = "b:00"; if (kind == "indirect") { auxiliary = "/Sig"; } }
        else if (kind == "nested") { value["/Nested"] = new object[] { new Dictionary<string, object> { ["/Type"] = "/Sig", ["/Contents"] = "b:00" } }; }
        else if (kind is "registered" or "widget" or "inherited" or "blank")
        {
            if (kind != "registered") { value["/Subtype"] = "/Widget"; }
            if (kind == "inherited") { value["/Parent"] = "6 0 R"; auxiliary = new Dictionary<string, object> { ["/FT"] = "/Sig", ["/Kids"] = new[] { "5 0 R" } }; }
            else { value["/FT"] = "/Sig"; }
            value["/V"] = kind == "blank" ? null : signature;
        }
        else if (kind == "private")
        { value["/V"] = signature; value["/Parent"] = "6 0 R"; auxiliary = new Dictionary<string, object> { ["/Parent"] = "5 0 R" }; }
        else { value["/Marker"] = "u:Keep"; }
        Dictionary<string, object> objects = new()
        {
            ["obj:1 0 R"] = new { value = root },
            ["obj:2 0 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Pages", ["/Count"] = 1, ["/Kids"] = new[] { "3 0 R" } } },
            ["obj:3 0 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Page", ["/Parent"] = "2 0 R", ["/MediaBox"] = new[] { 0, 0, 100, 200 }, ["/Resources"] = new Dictionary<string, object>() } },
            ["obj:5 0 R"] = new { value }, ["obj:6 0 R"] = new { value = auxiliary },
            ["trailer"] = new { value = new Dictionary<string, object> { ["/Root"] = "1 0 R" } },
        };
        return JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2, pdfversion = "1.7" }, objects } });
    }

    private static byte[] PdfFixture(string kind, bool reachable)
    {
        string value = kind switch
        {
            "signature" => "<< /Type /Sig /Contents <00> >>",
            "indirect" => "<< /Type 6 0 R /Contents <00> >>",
            "nested" => "<< /Nested [<< /Type /Sig /Contents <00> >>] >>",
            "widget" => "<< /Subtype /Widget /FT /Sig /V << /Contents <00> >> >>",
            "blank" => "<< /Subtype /Widget /FT /Sig /V null >>",
            "private" => "<< /V << /Contents <00> >> /Parent 6 0 R >>",
            "cycle" => "<< /Parent 6 0 R >>",
            _ => "<< /Marker (Keep) >>",
        };
        string auxiliary = kind == "indirect" ? "/Sig" : kind is "private" or "cycle" ? "<< /Parent 5 0 R >>" : "null";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R" + (reachable ? " /Audit 5 0 R" : "") + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Contents 4 0 R >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()), (5, value, null), (6, auxiliary, null)], 1);
    }
}
