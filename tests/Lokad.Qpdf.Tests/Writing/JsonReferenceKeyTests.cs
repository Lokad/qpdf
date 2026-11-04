using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonReferenceKeyTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string storage in new[] { "classic-zero", "classic-seven", "packed" })
        foreach (int version in new[] { 1, 2 })
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (string selected in new[] { "all", "page", "trailer" })
        { yield return [storage, version, route, selected]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PageReferencesAddressObjectAndInfoMaps(string storage, int version, string route, string selected)
    {
        using InMemoryQpdfHost host = new();
        byte[] input = Fixture(storage == "classic-seven");
        if (storage == "packed")
        {
            using InMemoryQpdfHost prepare = new();
            prepare.AddInput(QpdfPath.From("source"), input, QpdfInputOwnership.Transfer);
            QpdfExecutionResult packed = await Qpdf.ExecuteAsync(prepare, new(["source", "--object-streams=generate", "packed"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, packed.ExitCode);
            Assert.True(prepare.TryGetOutput(QpdfPath.From("packed"), out input));
        }
        host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        int generation = storage == "classic-seven" ? 7 : 0;
        string selector = selected == "trailer" ? "trailer" : "3," + generation;
        string[] selectors = selected == "all" ? [] : [selector];
        string json;
        if (route == "typed")
        {
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"),
                new QpdfJsonOptions(version, [], "none", null, selectors, false), CancellationToken.None);
            Assert.Empty(result.Warnings); json = result.Json;
        }
        else
        {
            List<string> args = ["input", "--json=" + version];
            Dictionary<string, object> fields = new() { ["inputFile"] = "input", ["json"] = version.ToString() };
            if (selected != "all") { args.Add("--json-object=" + selector); fields["jsonObject"] = selectors; }
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(args), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            json = Assert.IsType<string>(result.OutputText);
        }
        using JsonDocument report = JsonDocument.Parse(json);
        JsonElement root = report.RootElement;
        string reference = Assert.IsType<string>(root.GetProperty("pages")[0].GetProperty("object").GetString());
        Assert.Equal("3 " + generation + " R", reference);
        JsonElement objects = version == 1 ? root.GetProperty("objects") : root.GetProperty("qpdf")[1];
        if (selected == "trailer")
        {
            Assert.Equal("trailer", Assert.Single(objects.EnumerateObject()).Name);
            if (version == 1) { Assert.Empty(root.GetProperty("objectinfo").EnumerateObject()); }
        }
        else
        {
            string key = version == 1 ? reference : "obj:" + reference;
            Assert.True(objects.TryGetProperty(key, out JsonElement page));
            Assert.Equal("/Page", (version == 1 ? page : page.GetProperty("value")).GetProperty("/Type").GetString());
            if (version == 1)
            {
                JsonElement info = root.GetProperty("objectinfo");
                Assert.True(info.TryGetProperty(reference, out _));
                Assert.Equal(objects.EnumerateObject().Select(p => p.Name).Where(n => n != "trailer").ToArray(), info.EnumerateObject().Select(p => p.Name).ToArray());
                Assert.All(info.EnumerateObject(), p => Assert.EndsWith(" R", p.Name));
            }
            if (selected == "page") { Assert.Single(objects.EnumerateObject()); }
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    private static byte[] Fixture(bool nonzero)
    {
        byte[] bytes = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Contents 4 0 R >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray())], 1);
        if (!nonzero) { return bytes; }
        // Equal-width substitutions keep all object/xref offsets intact.
        return Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(bytes)
            .Replace(" 0 obj", " 7 obj", StringComparison.Ordinal)
            .Replace(" 0 R", " 7 R", StringComparison.Ordinal)
            .Replace(" 00000 n ", " 00007 n ", StringComparison.Ordinal));
    }
}
