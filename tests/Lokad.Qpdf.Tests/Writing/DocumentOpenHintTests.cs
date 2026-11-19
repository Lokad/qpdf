using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using UglyToad.PdfPig;

namespace Lokad.Qpdf.Tests.Writing;

/// <summary>Native document-open assignments and catalog graph preservation.</summary>
public sealed class DocumentOpenHintTests
{
    private static readonly JsonObject Corpus = Load();
    private static readonly JsonObject Fixtures = Assert.IsType<JsonObject>(Corpus["Fixtures"]);

    private readonly record struct Run(JsonObject Fixture, string Mode, bool Compress, bool Job)
    {
        internal static Run FromId(string id)
        {
            int route = id.LastIndexOf('-');
            int compression = id.LastIndexOf('-', route - 1);
            int packing = id.LastIndexOf('-', compression - 1);
            return new(Assert.IsType<JsonObject>(Fixtures[id[..packing]]), id[(packing + 1)..compression],
                bool.Parse(id.AsSpan(compression + 1, route - compression - 1)), id.AsSpan(route + 1).SequenceEqual("job"));
        }
    }

    private static JsonObject Load()
    {
        using Stream stream = typeof(DocumentOpenHintTests).Assembly.GetManifestResourceStream(
            "Lokad.Qpdf.Tests.Writing.DocumentOpenHintCases.json") ?? throw new InvalidOperationException();
        return Assert.IsType<JsonObject>(JsonNode.Parse(stream));
    }

    private static string Text(JsonNode? node) => node?.GetValue<string>() ?? throw new InvalidOperationException();

    public static IEnumerable<object[]> Cases()
    {
        foreach ((string fixture, JsonNode? _) in Fixtures)
        {
            foreach (string mode in new[] { "disable", "generate" })
            {
                yield return [$"{fixture}-{mode}-False-argv"];
                yield return [$"{fixture}-{mode}-True-argv"];
            }
        }
        yield return ["minimal-2page.pdf-disable-False-job"];
        yield return ["minimal-2page.pdf-generate-True-job"];
    }

    private static async Task<InMemoryQpdfHost> HostAsync(JsonObject fixture)
    {
        byte[] bytes = Convert.FromBase64String(Text(fixture["Bytes"]));
        Assert.Equal(Text(fixture["SHA256"]), Convert.ToHexString(SHA256.HashData(bytes)));
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), bytes, QpdfInputOwnership.Copy);
        await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("outputs/a.pdf"), CancellationToken.None);
        await prior.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None);
        await prior.CommitAsync(CancellationToken.None);
        return host;
    }

    private static ValueTask<QpdfExecutionResult> ExecuteAsync(InMemoryQpdfHost host, Run run, QpdfLimits limits)
    {
        QpdfExecutionOptions options = new(limits);
        string compression = run.Compress ? "y" : "n";
        return run.Job
            ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "inputs/a.pdf", outputFile = "outputs/a.pdf",
                linearize = "", objectStreams = run.Mode, compressStreams = compression }), options, CancellationToken.None)
            : Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--linearize", "--object-streams=" + run.Mode,
                "--compress-streams=" + compression, "outputs/a.pdf"]), options, CancellationToken.None);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NativeAssignmentsAndCatalogGraphsRemainCompatible(string id)
    {
        Run run = Run.FromId(id);
        JsonObject fixture = run.Fixture;
        using InMemoryQpdfHost host = await HostAsync(fixture);
        QpdfLimits limits = QpdfLimits.Default;
        QpdfExecutionResult result = await ExecuteAsync(host, run, limits);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        Assert.Equal(0L, host.OpenLeaseCount);

        using InMemoryQpdfHost readerHost = new();
        readerHost.AddInput(QpdfPath.From("read.pdf"), pdf, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(readerHost, QpdfPath.From("read.pdf"), CancellationToken.None))
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            using LinearizationDataOwner owner = Assert.IsType<LinearizationDataOwner>(LinearizationCheck.TryReadOwnedData(
                document.Engine, 2, QpdfLimits.Default, governor, CancellationToken.None));
            LinearizationData data = owner.Data;
            JsonObject fields = Assert.IsType<JsonObject>((run.Mode == "generate" ? fixture["GeneratedFields"] : null) ?? fixture["NativeFields"]);
            Assert.Equal(Assert.IsType<JsonArray>(fields["ObjectCounts"]).Select(node => node?.GetValue<long>() ?? throw new InvalidOperationException()),
                data.PageTable.Entries.Select(entry => entry.ObjectCount));
            Assert.Equal(Assert.IsType<JsonArray>(fields["SharedCounts"]).Select(node => node?.GetValue<int>() ?? throw new InvalidOperationException()),
                data.PageTable.Entries.Select(entry => entry.SharedIdentifiers.Count));
            Assert.Equal(fields["FirstPageCount"]?.GetValue<int>(), data.SharedTable.FirstPageCount);
            Assert.Equal(fields["TotalCount"]?.GetValue<int>(), data.SharedTable.TotalCount);
            Assert.Equal(0, data.PageTable.NumeratorBits);

            string key = Text(fixture["Key"]), shape = Text(fixture["Shape"]);
            if (key.Length != 0)
            {
                long firstPageOffset = document.Engine.XRefEntries[checked((int)data.FirstPageObject)].First;
                PdfReference catalogReference = Assert.IsType<PdfReference>(Get(document.Engine.Trailer, "Root"));
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(catalogReference.ObjectNumber));
                QpdfObject root = Get(catalog, key);
                if (key == "AcroForm" && shape == "array")
                {
                    PdfDictionary open = Assert.IsType<PdfDictionary>(Assert.Single(Assert.IsType<PdfArray>(root).Items));
                    Assert.True(Assert.IsType<PdfBoolean>(Get(open, "DocOpenRoot")).Value);
                    PdfReference anchor = Assert.IsType<PdfReference>(Get(open, "Anchor"));
                    PdfDictionary child = Assert.IsType<PdfDictionary>(document.GetObject(anchor.ObjectNumber));
                    Assert.Equal(anchor, Assert.IsType<PdfReference>(Get(child, "Cycle")));
                    Assert.Equal(Get(open, "Resource"), Get(child, "Resource"));
                    Assert.Equal(Assert.IsType<PdfArray>(Get(open, "Destinations"))[1], Get(child, "P"));
                    foreach (QpdfObject destination in Assert.IsType<PdfArray>(Get(open, "Destinations")).Items)
                    {
                        PdfReference page = Assert.IsType<PdfReference>(destination);
                        Assert.Equal("Page", Assert.IsType<PdfName>(Get(Assert.IsType<PdfDictionary>(
                            document.GetObject(page.ObjectNumber)), "Type")).DisplayText);
                    }
                }
                if (root is PdfReference openReference)
                { Assert.True(PhysicalOffset(document, openReference.ObjectNumber) < firstPageOffset); }
                else if (shape == "reference")
                { Assert.Equal("AcroForm", key); Assert.IsType<PdfDictionary>(root); }
                foreach (int number in document.Engine.XRefEntries.Keys.Where(number => number > 0))
                {
                    if (document.GetObject(number) is PdfDictionary dictionary && dictionary.TryGet(PdfName.FromAscii("DocOpenChild"), out _))
                    { Assert.True(PhysicalOffset(document, number) < firstPageOffset); }
                }
            }
        }
        Assert.Equal(0L, readerHost.OpenLeaseCount);
        using PdfDocument independent = PdfDocument.Open(new MemoryStream(pdf));
        Assert.Equal(2, independent.NumberOfPages);
    }

    private static QpdfObject Get(PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return value;
    }

    private static long PhysicalOffset(QpdfDocument document, int number)
    {
        XrefEntry entry = document.Engine.XRefEntries[number];
        if (entry.Kind == XrefEntryKind.Compressed)
        { entry = document.Engine.XRefEntries[checked((int)entry.First)]; }
        Assert.Equal(XrefEntryKind.Uncompressed, entry.Kind);
        return entry.First;
    }

    [Fact]
    public async Task ArrayFormSubsetPrunesOmittedPageLinks()
    {
        using InMemoryQpdfHost host = await HostAsync(Assert.IsType<JsonObject>(Fixtures["AcroForm-array"]));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        using InMemoryQpdfHost readerHost = new();
        readerHost.AddInput(QpdfPath.From("read.pdf"), pdf, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(readerHost, QpdfPath.From("read.pdf"), CancellationToken.None))
        {
            Assert.Equal(1, document.PageCount);
            PdfReference catalogReference = Assert.IsType<PdfReference>(Get(document.Engine.Trailer, "Root"));
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(catalogReference.ObjectNumber));
            PdfDictionary open = Assert.IsType<PdfDictionary>(Assert.Single(Assert.IsType<PdfArray>(Get(catalog, "AcroForm")).Items));
            PdfArray destinations = Assert.IsType<PdfArray>(Get(open, "Destinations"));
            Assert.IsType<PdfReference>(destinations[0]);
            Assert.IsType<PdfNull>(destinations[1]);
            PdfReference anchor = Assert.IsType<PdfReference>(Get(open, "Anchor"));
            PdfDictionary child = Assert.IsType<PdfDictionary>(document.GetObject(anchor.ObjectNumber));
            if (child.TryGet(PdfName.FromAscii("P"), out QpdfObject omittedPage))
            { Assert.IsType<PdfNull>(omittedPage); }
        }
        Assert.Equal(0L, readerHost.OpenLeaseCount);
        using PdfDocument independent = PdfDocument.Open(new MemoryStream(pdf));
        Assert.Equal(1, independent.NumberOfPages);
    }

    public static IEnumerable<object[]> PublicationCases()
    {
        foreach (string fixture in new[] { "AcroForm-dictionary", "PageMode-reference" })
        {
            foreach (string mode in new[] { "disable", "generate" })
            foreach (string route in new[] { "argv", "job" })
            {
                yield return [$"{fixture}-{mode}-False-{route}"];
                yield return [$"{fixture}-{mode}-True-{route}"];
            }
        }
        yield return ["AcroForm-array-generate-True-job"];
    }

    [Theory]
    [MemberData(nameof(PublicationCases))]
    public async Task OutputLimitAfterClassificationPreservesPriorTarget(string id)
    {
        Run run = Run.FromId(id);
        using InMemoryQpdfHost host = await HostAsync(run.Fixture);
        QpdfExecutionResult result = await ExecuteAsync(host, run, QpdfLimits.Default with { MaxOutputBytes = 1L });
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded
            && diagnostic.Message.Contains(nameof(QpdfLimits.MaxOutputBytes), StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] actual));
        Assert.Equal(new byte[] { 17, 23, 41 }, actual);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
