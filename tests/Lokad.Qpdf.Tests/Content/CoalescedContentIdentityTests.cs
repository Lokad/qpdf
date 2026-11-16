using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;

namespace Lokad.Qpdf.Tests.Content;

public sealed class CoalescedContentIdentityTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string alias in new[] { "none", "page", "catalog", "both" })
        {
            foreach (string layout in new[] { "classic", "packed", "qdf", "linear" })
            { yield return [alias, layout, false, false, false]; }
            yield return [alias, "classic", true, false, false];
            yield return [alias, "classic", false, true, false];
        }
        foreach ((string alias, string layout, bool preserve, bool duplicate) in new[]
        {
            ("none", "classic", false, false), ("page", "packed", false, false),
            ("catalog", "qdf", false, false), ("both", "linear", false, false),
            ("both", "packed", true, false), ("both", "qdf", false, true)
        }) { yield return [alias, layout, preserve, duplicate, true]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CoalescingReplacesThePageEntryAndPreservesSharedArrayValues(string alias, string layout, bool preserve, bool duplicate, bool job)
    {
        bool catalogAlias = alias is "catalog" or "both";
        bool pageAlias = alias is "page" or "both";
        byte[] fixture = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R " + (catalogAlias ? "/Audit 5 0 R" : string.Empty) + " >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 5 0 R " + (pageAlias ? "/Audit 5 0 R" : string.Empty) + " >>", null),
            (4, "<< /Length 1 >>", "q"u8.ToArray()),
            (5, "[4 0 R 6 0 R]", null),
            (6, "<< /Length 1 >>", "Q"u8.ToArray())], 1);
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), fixture, QpdfInputOwnership.Transfer);
        List<string> arguments = ["input", "--coalesce-contents", "--normalize-content=n", "--stream-data=uncompress"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["coalesceContents"] = true, ["normalizeContent"] = "n", ["streamData"] = "uncompress" };
        if (layout == "packed") { arguments.Add("--object-streams=generate"); settings["objectStreams"] = "generate"; }
        if (layout == "qdf") { arguments.Add("--qdf"); settings["qdf"] = true; }
        if (layout == "linear") { arguments.Add("--linearize"); settings["linearize"] = true; }
        if (preserve) { arguments.Add("--preserve-unreferenced"); settings["preserveUnreferenced"] = true; }
        if (duplicate)
        {
            arguments.AddRange(["--pages", ".", "1,1", "--"]);
            settings["pages"] = new[] { new { file = ".", range = "1,1" } };
        }
        arguments.Add("output");
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
        Assert.Equal(duplicate ? 2 : 1, document.PageCount);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(Get(document.Engine.Trailer, "Root")));
        QpdfObject? firstContents = null;
        foreach (var location in document.PageReferences)
        {
            QpdfObject contents = Get(location.Page, "Contents");
            if (duplicate && firstContents is not null) { Assert.Equal(firstContents, contents); }
            firstContents = contents;
            PdfStream joined = Assert.IsType<PdfStream>(Resolve(contents));
            Assert.Equal("q\nQ"u8.ToArray(), joined.Data);
            foreach (PdfDictionary owner in new[] { catalog, location.Page })
            {
                if (!owner.TryGet(PdfName.FromAscii("Audit"), out QpdfObject preserved)) { continue; }
                Assert.NotEqual(contents, preserved);
                PdfArray original = Assert.IsType<PdfArray>(Resolve(preserved));
                Assert.Equal(2, original.Count);
                Assert.Equal("q"u8.ToArray(), Assert.IsType<PdfStream>(Resolve(original[0])).Data);
                Assert.Equal("Q"u8.ToArray(), Assert.IsType<PdfStream>(Resolve(original[1])).Data);
            }
            if (catalogAlias && pageAlias) { Assert.Equal(Get(catalog, "Audit"), Get(location.Page, "Audit")); }
        }
        if (alias == "none")
        {
            PdfStream[] streams = document.Engine.XRefEntries.Where(e => e.Key > 0 && e.Value.Kind != XrefEntryKind.Free)
                .Select(e => document.GetObject(e.Key)).OfType<PdfStream>().ToArray();
            if (preserve)
            {
                Assert.Contains(streams, stream => stream.RawMemory.Span.SequenceEqual("q"u8));
                Assert.Contains(streams, stream => stream.RawMemory.Span.SequenceEqual("Q"u8));
            }
            else { Assert.DoesNotContain(streams, stream => stream.RawMemory.Span.SequenceEqual("q"u8) || stream.RawMemory.Span.SequenceEqual("Q"u8)); }
        }
    }

    private static QpdfObject Get(PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return value;
    }
}
