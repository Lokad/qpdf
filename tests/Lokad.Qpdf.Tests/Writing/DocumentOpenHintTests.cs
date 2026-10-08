using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using UglyToad.PdfPig;

namespace Lokad.Qpdf.Tests.Writing;

/// <summary>Frozen native document-open assignments and retained schema rejections.</summary>
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
    public async Task NativeAssignmentsOrExplicitSchemaRejectionRemainVisible(string id)
    {
        JsonObject row = Row(id);
        InMemoryQpdfHost host = await HostAsync(row);
        QpdfLimits limits = QpdfLimits.Default;
        QpdfExecutionResult result = await ExecuteAsync(host, row, limits);
        Assert.Equal((QpdfExitCode)(row["ManagedExit"]?.GetValue<int>() ?? throw new InvalidOperationException()), result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        Assert.Equal(0L, host.OpenLeaseCount);
        if (result.ExitCode != QpdfExitCode.Success)
        {
            // An array-valued AcroForm is an existing schema rejection. The
            // native success and complete hints remain in the corpus as a gap.
            Assert.Equal("AcroForm", Text(row["Key"]));
            Assert.Equal("array", Text(row["Shape"]));
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("Catalog /AcroForm must be a dictionary.", StringComparison.Ordinal));
            Assert.Equal(new byte[] { 17, 23, 41 }, pdf);
            return;
        }
        Assert.InRange(result.Counters.TotalWork, 1L, limits.MaxWork);

        InMemoryQpdfHost readerHost = new();
        readerHost.AddInput(QpdfPath.From("read.pdf"), pdf, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(readerHost, QpdfPath.From("read.pdf"), CancellationToken.None))
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            LinearizationData data = Assert.IsType<LinearizationData>(LinearizationCheck.TryReadData(
                document.Engine, 2, QpdfLimits.Default, governor, CancellationToken.None));
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

    public static IEnumerable<object[]> PublicationCases()
    {
        foreach (JsonObject row in Assert.IsType<JsonArray>(Corpus["Cases"]).OfType<JsonObject>())
        {
            if (Text(row["Key"]) == "AcroForm" && Text(row["Shape"]) == "dictionary"
                || Text(row["Key"]) == "PageMode" && Text(row["Shape"]) == "reference")
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

    [Theory]
    [InlineData("AcroForm-dictionary-generate-False-job")]
    [InlineData("PageMode-reference-generate-True-job")]
    public async Task WorkCeilingBoundsTraversalAndPreservesAtomicPublication(string id)
    {
        JsonObject row = Row(id);
        InMemoryQpdfHost host = await HostAsync(row);
        const long work = 100000L;
        QpdfExecutionResult result = await ExecuteAsync(host, row, QpdfLimits.Default with { MaxWork = work });
        Assert.InRange(result.Counters.TotalWork, 1L, work);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] actual));
        if (result.ExitCode == QpdfExitCode.Success)
        {
            using PdfDocument independent = PdfDocument.Open(new MemoryStream(actual));
            Assert.Equal(2, independent.NumberOfPages);
        }
        else
        {
            Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded
                && diagnostic.Message.Contains(nameof(QpdfLimits.MaxWork), StringComparison.Ordinal));
            Assert.Equal(new byte[] { 17, 23, 41 }, actual);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
