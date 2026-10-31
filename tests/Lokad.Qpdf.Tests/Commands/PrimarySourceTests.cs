using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Tests.Writing;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class PrimarySourceTests
{
    internal static byte[] Fixture(string name)
    {
        byte[] content = Encoding.ASCII.GetBytes("q\n% " + name + "\nQ\n");
        byte[] xml = Encoding.ASCII.GetBytes("<" + name + "/>");
        byte[] attachment = Encoding.ASCII.GetBytes("attached-" + name);
        byte[] pdf = CheckTests.Assemble(
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /Metadata 6 0 R /ViewerPreferences << /DisplayDocTitle true >> /Names << /EmbeddedFiles << /Names [(attached) 7 0 R] >> >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> /Contents 4 0 R >>", null),
            (4, "<< /Length " + content.Length + " >>", content),
            (5, "<< /Title (" + name + ") >>", null),
            (6, "<< /Type /Metadata /Subtype /XML /Length " + xml.Length + " >>", xml),
            (7, "<< /Type /Filespec /F (" + name + ".txt) /EF << /F 8 0 R >> >>", null),
            (8, "<< /Type /EmbeddedFile /Length " + attachment.Length + " >>", attachment),
        ], 1);
        return Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(pdf).Replace("/Root 1 0 R",
            "/Root 1 0 R /Info 5 0 R /ID [(" + name + "-original)(second-id)]", StringComparison.Ordinal));
    }

    internal static void Register(InMemoryQpdfHost host, string primary)
    {
        if (primary != "missing")
        { host.AddInput(QpdfPath.From("primary"), primary == "bad" ? "not a PDF"u8.ToArray() : Fixture("primary"), QpdfInputOwnership.Copy); }
        host.AddInput(QpdfPath.From("secondary"), Fixture("secondary"), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("blob"), "new attachment"u8.ToArray(), QpdfInputOwnership.Copy);
    }

    internal static async Task SeedOutputs(InMemoryQpdfHost host)
    {
        foreach (string path in new[] { "primary", "secondary", "output" })
        {
            await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From(path), CancellationToken.None);
            await prior.WriteAsync(Encoding.ASCII.GetBytes("prior-" + path), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
    }

    internal static async Task<QpdfExecutionResult> Run(IQpdfHost host, InMemoryQpdfHost storage, string route,
        string range, bool replace, bool includePrimary, string output, CancellationToken token)
    {
        List<string> argv = ["primary", "--compress-streams=n", "--decode-level=none", "--object-streams=disable", "--static-id"];
        if (replace) { argv.Add("--replace-input"); }
        argv.AddRange(["--pages", "secondary", range]);
        if (includePrimary) { argv.AddRange([".", "1"]); }
        argv.Add("--");
        // Attachment edits activate the existing retention policy for the primary.
        argv.AddRange(["--add-attachment", "blob", "--key=added", "--"]);
        if (!replace) { argv.Add(output); }
        if (route == "mixed")
        {
            storage.AddInput(QpdfPath.From("settings"), "{}"u8.ToArray(), QpdfInputOwnership.Copy);
            argv.Insert(0, "--job-json-file=settings");
        }
        if (route == "response")
        {
            storage.AddInput(QpdfPath.From("arguments"), Encoding.UTF8.GetBytes(string.Join('\n', argv)), QpdfInputOwnership.Copy);
            argv = ["@arguments"];
        }
        if (route != "job") { return await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), token); }
        List<object> pages = [new { file = "secondary", range }];
        if (includePrimary) { pages.Add(new { file = ".", range = "1" }); }
        Dictionary<string, object> job = new() { ["inputFile"] = "primary", ["pages"] = pages,
            ["compressStreams"] = "n", ["decodeLevel"] = "none", ["objectStreams"] = "disable", ["staticId"] = true,
            ["addAttachment"] = new[] { new { file = "blob", key = "added" } } };
        if (replace) { job["replaceInput"] = true; } else { job["outputFile"] = output; }
        return await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), token);
    }

    public static IEnumerable<object[]> ExternalSelections()
    {
        foreach (string route in new[] { "argv", "response", "mixed", "job" })
        foreach (string range in new[] { "1", "1-1" })
        foreach (bool replace in new[] { false, true })
        foreach (bool includePrimary in new[] { false, true })
        { yield return [route, range, replace, includePrimary]; }
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    private static QpdfObject Entry(QpdfDocument document, PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return Resolve(document, value);
    }

    [Theory]
    [MemberData(nameof(ExternalSelections))]
    public async Task PrimarySuppliesCatalogAndReplacementTargetIndependentlyOfPages(string route, string range, bool replace, bool includePrimary)
    {
        PdfStream escapedMetadata;
        PdfStream escapedAttachment;
        using (InMemoryQpdfHost host = new())
        {
            Register(host, "valid"); await SeedOutputs(host);
            QpdfExecutionResult result = await Run(host, host, route, range, replace, includePrimary, "output", CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
            string target = replace ? "primary" : "output";
            Assert.True(host.TryGetOutput(QpdfPath.From(target), out byte[] output));
            foreach (string path in new[] { "primary", "secondary", "output" }.Where(path => path != target))
            { Assert.True(host.TryGetOutput(QpdfPath.From(path), out byte[] prior)); Assert.Equal(Encoding.ASCII.GetBytes("prior-" + path), prior); }
            Assert.Equal(Convert.ToHexString("primary-original"u8), WriterControlsTests.IdsOf(output).Id1);
            host.AddInput(QpdfPath.From("result"), output, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("result"), CancellationToken.None);
            Assert.Equal(includePrimary ? 2 : 1, document.PageCount);
            Assert.Equal("q\n% secondary\nQ\n", Encoding.ASCII.GetString(Assert.IsType<PdfStream>(Entry(document, document.PageReferences[0].Page, "Contents")).Data));
            if (includePrimary)
            { Assert.Equal("q\n% primary\nQ\n", Encoding.ASCII.GetString(Assert.IsType<PdfStream>(Entry(document, document.PageReferences[1].Page, "Contents")).Data)); }
            PdfDictionary info = Assert.IsType<PdfDictionary>(Entry(document, document.Engine.Trailer, "Info"));
            Assert.Equal("primary"u8.ToArray(), Assert.IsType<PdfByteString>(Entry(document, info, "Title")).Value);
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            Assert.True(Assert.IsType<PdfBoolean>(Entry(document, Assert.IsType<PdfDictionary>(Entry(document, catalog, "ViewerPreferences")), "DisplayDocTitle")).Value);
            escapedMetadata = Assert.IsType<PdfStream>(Entry(document, catalog, "Metadata"));
            PdfDictionary names = Assert.IsType<PdfDictionary>(Entry(document, catalog, "Names"));
            PdfDictionary embedded = Assert.IsType<PdfDictionary>(Entry(document, names, "EmbeddedFiles"));
            PdfArray nameArray = Assert.IsType<PdfArray>(Entry(document, embedded, "Names"));
            Assert.Equal("attached"u8.ToArray(), Assert.IsType<PdfByteString>(nameArray[2]).Value);
            PdfDictionary spec = Assert.IsType<PdfDictionary>(Resolve(document, nameArray[3]));
            escapedAttachment = Assert.IsType<PdfStream>(Entry(document, Assert.IsType<PdfDictionary>(Entry(document, spec, "EF")), "F"));
        }
        Assert.Equal("<primary/>"u8.ToArray(), escapedMetadata.Data);
        Assert.Equal("attached-primary"u8.ToArray(), escapedAttachment.Data);
    }

    public static IEnumerable<object[]> InvalidPrimaries()
    {
        foreach (string route in new[] { "argv", "response", "mixed", "job" })
        foreach (string primary in new[] { "missing", "bad" })
        foreach (bool replace in new[] { false, true })
        { yield return [route, primary, replace]; }
    }

    [Theory]
    [MemberData(nameof(InvalidPrimaries))]
    public async Task InvalidUnselectedPrimaryPreservesAllPriorOutputs(string route, string primary, bool replace)
    {
        using InMemoryQpdfHost host = new(); Register(host, primary); await SeedOutputs(host);
        QpdfExecutionResult result = await Run(host, host, route, "1-1", replace, false, "output", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.NotEmpty(result.Diagnostics);
        Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        foreach (string path in new[] { "primary", "secondary", "output" })
        { Assert.True(host.TryGetOutput(QpdfPath.From(path), out byte[] prior)); Assert.Equal(Encoding.ASCII.GetBytes("prior-" + path), prior); }
    }

    [Theory]
    [InlineData("argv", "primary")]
    [InlineData("argv", "secondary")]
    [InlineData("job", "primary")]
    [InlineData("job", "secondary")]
    public async Task OrdinaryOutputCannotAliasPrimaryOrPageProvider(string route, string target)
    {
        using InMemoryQpdfHost host = new(); Register(host, "valid"); await SeedOutputs(host);
        QpdfExecutionResult result = await Run(host, host, route, "1-1", false, false, target, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From(target), out byte[] prior)); Assert.Equal(Encoding.ASCII.GetBytes("prior-" + target), prior);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv", false)]
    [InlineData("argv", true)]
    [InlineData("job", false)]
    [InlineData("job", true)]
    public async Task CatalogOnlyPrimaryAddsNoCollationGroup(string route, bool replace)
    {
        using InMemoryQpdfHost host = new(); Register(host, "valid");
        host.AddInput(QpdfPath.From("third"), Fixture("third"), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = route == "argv"
            ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(replace
                ? ["primary", "--replace-input", "--compress-streams=n", "--decode-level=none", "--collate=1,2", "--pages", "secondary", "1,1", "third", "1,1,1", "--"]
                : ["primary", "--compress-streams=n", "--decode-level=none", "--collate=1,2", "--pages", "secondary", "1,1", "third", "1,1,1", "--", "output"]), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new Dictionary<string, object>
                { ["inputFile"] = "primary", [replace ? "replaceInput" : "outputFile"] = replace ? true : "output",
                    ["compressStreams"] = "n", ["decodeLevel"] = "none", ["collate"] = "1,2",
                    ["pages"] = new[] { new { file = "secondary", range = "1,1" }, new { file = "third", range = "1,1,1" } } }), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From(replace ? "primary" : "output"), out byte[] output));
        host.AddInput(QpdfPath.From("result"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("result"), CancellationToken.None);
        string[] expected = ["secondary", "third", "third", "secondary", "third"];
        Assert.Equal(expected.Length, document.PageCount);
        for (int index = 0; index < expected.Length; index++)
        { Assert.Equal("q\n% " + expected[index] + "\nQ\n", Encoding.ASCII.GetString(Assert.IsType<PdfStream>(Entry(document, document.PageReferences[index].Page, "Contents")).Data)); }
    }
}
