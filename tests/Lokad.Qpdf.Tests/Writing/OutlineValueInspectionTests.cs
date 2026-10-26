using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class OutlineValueInspectionTests
{
    private static byte[] Fixture(string title, string count, string destination)
        => CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> >>", null),
            (4, "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>", null),
            (5, "<< " + (title.Length == 0 ? "" : "/Title " + title) + (count.Length == 0 ? "" : " /Count " + count)
                + " " + destination + " /Parent 4 0 R /First 6 0 R /Last 6 0 R >>", null),
            (6, "<< /Title (Child) /Parent 5 0 R /Dest [3 0 R /Fit] >>", null),
            (7, "(Parent)", null), (8, "-1", null),
            (9, "<< /S /GoTo /D [3 0 R /Fit] >>", null), (10, "[3 0 R /Fit]", null),
            (11, "/GoTo", null), (12, "42", null)], 1);

    private static async Task<JsonDocument> Inspect(InMemoryQpdfHost storage, IQpdfHost host, string route, int version)
    {
        if (route == "typed")
        { await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("report"), new(version, ["outlines", "pages"]), CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--json=" + version, "--json-key=outlines", "--json-key=pages", "--", "report"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "report", json = version.ToString(), jsonKey = new[] { "outlines", "pages" } }), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(0L, storage.OpenLeaseCount); Assert.True(storage.TryGetOutput(QpdfPath.From("report"), out byte[] bytes));
        return JsonDocument.Parse(bytes);
    }

    public static IEnumerable<object[]> ScalarCases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (int version in new[] { 1, 2 })
        foreach ((string title, string count, string expected, bool open) in new[]
        {
            ("(Parent)", "-1", "Parent", false), ("7 0 R", "-1", "Parent", false),
            ("(Parent)", "8 0 R", "Parent", false), ("7 0 R", "8 0 R", "Parent", false),
            ("", "", "", true), ("null", "null", "", true), ("99 0 R", "99 0 R", "", true),
            ("8 0 R", "7 0 R", "", true), ("7 1 R", "8 1 R", "", true),
        }) { yield return [route, version, title, count, expected, open]; }
    }

    [Theory]
    [MemberData(nameof(ScalarCases))]
    public async Task OptionalTitleAndCountKeepDirectSemantics(string route, int version, string title, string count, string expected, bool open)
    {
        using InMemoryQpdfHost storage = new(); storage.AddInput(QpdfPath.From("input"), Fixture(title, count, "/Dest [3 0 R /Fit]"), QpdfInputOwnership.Transfer);
        using JsonDocument report = await Inspect(storage, storage, route, version);
        JsonElement parent = report.RootElement.GetProperty("outlines")[0];
        Assert.Equal(expected, parent.GetProperty("title").GetString()); Assert.Equal(open, parent.GetProperty("open").GetBoolean());
        Assert.Equal(1, parent.GetProperty("destpageposfrom1").GetInt32()); Assert.Single(parent.GetProperty("kids").EnumerateArray());
        Assert.Equal(new[] { expected, "Child" }, report.RootElement.GetProperty("pages")[0].GetProperty("outlines").EnumerateArray().Select(entry => entry.GetProperty("title").GetString()));
    }
}
