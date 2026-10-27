using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentTreeReferenceTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output");

    public static IEnumerable<object[]> Cases()
    {
        foreach (string variant in new[] { "direct", "names", "kids", "key", "all" })
        foreach (string operation in new[] { "list", "extract", "json1", "json2", "copy", "rewrite" })
        foreach (string route in new[] { "typed", "argv", "job" })
        {
            if (operation == "copy" && route != "typed" || operation == "rewrite" && route == "typed") { continue; }
            yield return [variant, operation, route];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EquivalentTreesWorkAcrossPublicOperations(string variant, string operation, string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture(variant, "(note.txt)", 0), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("base"), Fixture("empty", "(note.txt)", 0), QpdfInputOwnership.Transfer);
        string? json = null;
        if (route == "typed")
        {
            if (operation == "list")
            {
                QpdfAttachmentInfo info = Assert.Single(await Qpdf.ListAttachmentsAsync(host, Input, CancellationToken.None));
                Assert.Equal("note.txt", info.Key); Assert.Equal("note.txt", info.FileName);
            }
            else if (operation == "extract")
            {
                QpdfAttachmentExtraction result = await Qpdf.ExtractAttachmentAsync(host, Input, "note.txt", Output, CancellationToken.None);
                Assert.Equal(7L, result.ByteCount);
            }
            else if (operation == "copy")
            {
                QpdfAttachmentEditResult result = await Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("base"), Output,
                    [new QpdfAttachmentCopy(Input, null)], CancellationToken.None);
                Assert.Equal(1, result.AttachmentCount);
            }
            else
            {
                QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, Input, Output,
                    new QpdfJsonOptions(operation == "json1" ? 1 : 2, ["attachments"]), CancellationToken.None);
                json = result.Json;
            }
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(operation switch
                {
                    "list" => ["input", "--list-attachments"],
                    "extract" => ["input", "--show-attachment=note.txt", "--", "output"],
                    "rewrite" => ["input", "output"],
                    _ => ["input", operation == "json1" ? "--json=1" : "--json=2", "--json-key=attachments"],
                }), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, Job(operation), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            if (operation == "list") { Assert.Contains(result.Diagnostics, item => item.Message.Contains("note.txt", StringComparison.Ordinal)); }
            if (operation.StartsWith("json", StringComparison.Ordinal)) { json = Assert.IsType<string>(result.OutputText); }
        }
        if (json is not null)
        {
            using JsonDocument parsed = JsonDocument.Parse(json);
            Assert.Single(parsed.RootElement.GetProperty("attachments").EnumerateObject());
            Assert.True(parsed.RootElement.GetProperty("attachments").TryGetProperty("note.txt", out _));
        }
        if (operation is "copy" or "rewrite")
        {
            Assert.True(host.TryGetOutput(Output, out byte[] bytes));
            QpdfPath readback = QpdfPath.From("readback"); host.AddInput(readback, bytes, QpdfInputOwnership.Transfer);
            Assert.Equal("note.txt", Assert.Single(await Qpdf.ListAttachmentsAsync(host, readback, CancellationToken.None)).Key);
            await Qpdf.ExtractAttachmentAsync(host, readback, "note.txt", Output, CancellationToken.None);
        }
        if (operation is "extract" or "copy" or "rewrite")
        {
            Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("payload"u8.ToArray(), bytes);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static string Job(string operation)
    {
        Dictionary<string, object> job = new() { ["inputFile"] = "input" };
        if (operation == "list") { job["listAttachments"] = ""; }
        else if (operation == "extract") { job["showAttachment"] = "note.txt"; job["outputFile"] = "output"; }
        else if (operation == "rewrite") { job["outputFile"] = "output"; }
        else { job["json"] = operation == "json1" ? "1" : "2"; job["jsonKey"] = new[] { "attachments" }; }
        return JsonSerializer.Serialize(job);
    }

    [Theory]
    [InlineData("names")]
    [InlineData("kids")]
    [InlineData("key")]
    [InlineData("all")]
    public async Task LookupUsesTheEffectiveNonzeroGenerations(string variant)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture(variant, "(note.txt)", 2), QpdfInputOwnership.Transfer);
        Assert.Equal("note.txt", Assert.Single(await Qpdf.ListAttachmentsAsync(host, Input, CancellationToken.None)).Key);
        await Qpdf.ExtractAttachmentAsync(host, Input, "note.txt", Output, CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("payload"u8.ToArray(), bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("direct-null")]
    public async Task AbsentAndDirectNullTreeArraysRemainOptional(string variant)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture(variant, "(note.txt)", 0), QpdfInputOwnership.Transfer);
        Assert.Empty(await Qpdf.ListAttachmentsAsync(host, Input, CancellationToken.None));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> MalformedCases()
    {
        foreach (string variant in new[] { "names-type", "names-null", "names-odd", "names-missing", "names-stale",
            "kids-type", "kids-null", "kids-missing", "kids-stale", "kids-cycle",
            "key-type", "key-null", "key-missing", "key-stale", "deep" })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [variant, route]; }
    }

    [Theory]
    [MemberData(nameof(MalformedCases))]
    public async Task InvalidTreesKeepFailureClassificationAndPriorOutput(string variant, string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture(variant, "(note.txt)", variant.EndsWith("stale", StringComparison.Ordinal) ? 2 : 0), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = variant == "deep" ? 2 : 256 };
        string message;
        if (route == "typed")
        {
            QpdfParseException failure = await Assert.ThrowsAsync<QpdfParseException>(async () =>
                await Qpdf.ExtractAttachmentAsync(host, Input, "note.txt", Output, limits, CancellationToken.None));
            message = failure.Message;
        }
        else
        {
            QpdfExecutionOptions options = new(limits);
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--show-attachment=note.txt", "--", "output"]), options, CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, Job("extract"), options, CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            message = Assert.Single(result.Diagnostics).Message;
        }
        if (variant == "kids-cycle") { Assert.Contains("cycle", message, StringComparison.Ordinal); }
        if (variant == "deep") { Assert.Contains("Embedded-files name tree exceeds nesting budget", message, StringComparison.Ordinal); }
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    internal static byte[] Fixture(string variant, string key, int generation)
    {
        string Reference(int number) => $"{number} {generation} R";
        string shape = variant == "deep" ? "kids" : variant.Split('-')[0];
        string pair = $"[{(shape is "key" or "all" ? Reference(10) : "(note.txt)")} 5 0 R]";
        string names = shape == "all" ? Reference(9) : "[(note.txt) 5 0 R]";
        string node = shape switch
        {
            "empty" => "<< >>",
            "names" => $"<< /Names {Reference(9)} >>",
            "kids" or "all" => $"<< /Kids {Reference(7)} >>",
            _ => $"<< /Names {pair} >>",
        };
        List<(int, string, byte[]?)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 4 0 R >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (4, node, null),
            (5, "<< /Type /Filespec /F (note.txt) /UF (note.txt) /EF << /F 6 0 R >> >>", null),
            (6, "<< /Type /EmbeddedFile /Length 7 >>", "payload"u8.ToArray()),
            (7, $"[{Reference(8)}]", null),
            (8, $"<< /Limits [(note.txt) (note.txt)] /Names {names} >>", null),
            (9, pair, null),
            (10, key, null),
        ];
        switch (variant)
        {
            case "direct-null": objects[3] = (4, "<< /Names null /Kids null >>", null); break;
            case "names-type": objects[8] = (9, "0", null); break;
            case "names-null": objects[8] = (9, "null", null); break;
            case "names-odd": objects[8] = (9, "[(note.txt)]", null); break;
            case "names-missing": objects[3] = (4, "<< /Names 99 0 R >>", null); break;
            case "names-stale": objects[3] = (4, "<< /Names 9 1 R >>", null); break;
            case "kids-type": objects[6] = (7, "0", null); break;
            case "kids-null": objects[6] = (7, "null", null); break;
            case "kids-missing": objects[3] = (4, "<< /Kids 99 0 R >>", null); break;
            case "kids-stale": objects[3] = (4, "<< /Kids 7 1 R >>", null); break;
            case "kids-cycle": objects[6] = (7, "[4 0 R]", null); break;
            case "key-type": objects[9] = (10, "0", null); break;
            case "key-null": objects[9] = (10, "null", null); break;
            case "key-missing": objects[3] = (4, "<< /Names [99 0 R 5 0 R] >>", null); break;
            case "key-stale": objects[3] = (4, "<< /Names [10 1 R 5 0 R] >>", null); break;
            case "deep":
                objects[7] = (8, "<< /Kids [9 0 R] >>", null);
                objects[8] = (9, "<< /Kids [10 0 R] >>", null);
                objects[9] = (10, "<< /Names [(note.txt) 5 0 R] >>", null); break;
        }
        Dictionary<int, int> generations = new() { [7] = generation, [8] = generation, [9] = generation, [10] = generation };
        return CheckTests.Assemble(objects, 1, generations);
    }
}
