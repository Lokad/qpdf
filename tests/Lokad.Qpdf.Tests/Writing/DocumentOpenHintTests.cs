using System.Security.Cryptography;
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

    private static JsonObject Load()
    {
        using Stream stream = typeof(DocumentOpenHintTests).Assembly.GetManifestResourceStream(
            "Lokad.Qpdf.Tests.Writing.DocumentOpenHintCases.json") ?? throw new InvalidOperationException();
        return Assert.IsType<JsonObject>(JsonNode.Parse(stream));
    }

    private static string Text(JsonNode? node) => node?.GetValue<string>() ?? throw new InvalidOperationException();
    private static JsonObject Row(string id) => Assert.IsType<JsonArray>(Corpus["Cases"])
        .OfType<JsonObject>().Single(row => Text(row["Id"]) == id);

    public static IEnumerable<object[]> Cases()
    {
        foreach (JsonObject row in Assert.IsType<JsonArray>(Corpus["Cases"]).OfType<JsonObject>())
        { yield return [Text(row["Id"])]; }
    }

    private static async Task<InMemoryQpdfHost> HostAsync(JsonObject row)
    {
        string digest = Text(row["Fixture"]);
        byte[] fixture = Convert.FromBase64String(Text(Corpus["Fixtures"]?[digest]));
        Assert.Equal(digest, Convert.ToHexString(SHA256.HashData(fixture)));
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), fixture, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("inputs/job.json"), System.Text.Encoding.UTF8.GetBytes(
            Assert.IsType<JsonObject>(row["Job"]).ToJsonString()), QpdfInputOwnership.Copy);
        await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("outputs/a.pdf"), CancellationToken.None);
        await prior.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None);
        await prior.CommitAsync(CancellationToken.None);
        return host;
    }

    private static ValueTask<QpdfExecutionResult> ExecuteAsync(InMemoryQpdfHost host, JsonObject row, QpdfLimits limits)
    {
        QpdfExecutionOptions options = new(limits);
        return Text(row["Route"]) == "job"
            ? Qpdf.ExecuteJobAsync(host, Assert.IsType<JsonObject>(row["Job"]).ToJsonString(), options, CancellationToken.None)
            : Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(Assert.IsType<JsonArray>(row["Args"]).Select(Text).ToArray()), options, CancellationToken.None);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NativeAssignmentsAndCatalogGraphsRemainCompatible(string id)
    {
        JsonObject row = Row(id);
        InMemoryQpdfHost host = await HostAsync(row);
        QpdfLimits limits = QpdfLimits.Default;
        QpdfExecutionResult result = await ExecuteAsync(host, row, limits);
        Assert.Equal((QpdfExitCode)(row["NativeExit"]?.GetValue<int>() ?? throw new InvalidOperationException()), result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        Assert.Equal(0L, host.OpenLeaseCount);

        InMemoryQpdfHost readerHost = new();
        readerHost.AddInput(QpdfPath.From("read.pdf"), pdf, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(readerHost, QpdfPath.From("read.pdf"), CancellationToken.None))
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            using LinearizationDataOwner owner = Assert.IsType<LinearizationDataOwner>(LinearizationCheck.TryReadOwnedData(
                document.Engine, 2, QpdfLimits.Default, governor, CancellationToken.None));
            LinearizationData data = owner.Data;
            JsonObject fields = Assert.IsType<JsonObject>(row["NativeFields"]);
            Assert.Equal(Assert.IsType<JsonArray>(fields["ObjectCounts"]).Select(node => node?.GetValue<long>() ?? throw new InvalidOperationException()),
                data.PageTable.Entries.Select(entry => entry.ObjectCount));
            Assert.Equal(Assert.IsType<JsonArray>(fields["SharedCounts"]).Select(node => node?.GetValue<int>() ?? throw new InvalidOperationException()),
                data.PageTable.Entries.Select(entry => entry.SharedIdentifiers.Count));
            Assert.Equal(fields["FirstPageCount"]?.GetValue<int>(), data.SharedTable.FirstPageCount);
            Assert.Equal(fields["TotalCount"]?.GetValue<int>(), data.SharedTable.TotalCount);
            Assert.Equal(0, data.PageTable.NumeratorBits);

            if (!string.IsNullOrEmpty(Text(row["Key"])))
            {
                long firstPageOffset = document.Engine.XRefEntries[checked((int)data.FirstPageObject)].First;
                PdfReference catalogReference = Assert.IsType<PdfReference>(Get(document.Engine.Trailer, "Root"));
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(catalogReference.ObjectNumber));
                QpdfObject root = Get(catalog, Text(row["Key"]));
                if (Text(row["Key"]) == "AcroForm" && Text(row["Shape"]) == "array")
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
                else if (Text(row["Shape"]) == "reference")
                { Assert.Equal("AcroForm", Text(row["Key"])); Assert.IsType<PdfDictionary>(root); }
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
        InMemoryQpdfHost host = await HostAsync(Row("AcroForm-array-disable-False-argv"));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        InMemoryQpdfHost readerHost = new();
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
        foreach (JsonObject row in Assert.IsType<JsonArray>(Corpus["Cases"]).OfType<JsonObject>())
        {
            if (Text(row["Key"]) == "AcroForm" && Text(row["Shape"]) == "dictionary"
                || Text(row["Key"]) == "PageMode" && Text(row["Shape"]) == "reference"
                || Text(row["Id"]) == "AcroForm-array-generate-True-job")
            { yield return [Text(row["Id"])]; }
        }
    }

    [Theory]
    [MemberData(nameof(PublicationCases))]
    public async Task OutputLimitAfterClassificationPreservesPriorTarget(string id)
    {
        JsonObject row = Row(id);
        InMemoryQpdfHost host = await HostAsync(row);
        QpdfExecutionResult result = await ExecuteAsync(host, row, QpdfLimits.Default with { MaxOutputBytes = 1L });
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded
            && diagnostic.Message.Contains(nameof(QpdfLimits.MaxOutputBytes), StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] actual));
        Assert.Equal(new byte[] { 17, 23, 41 }, actual);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
