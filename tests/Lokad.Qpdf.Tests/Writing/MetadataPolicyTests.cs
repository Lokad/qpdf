using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class MetadataPolicyTests
{
    [Theory]
    [InlineData("absent", false)]
    [InlineData("direct-null", false)]
    [InlineData("indirect-null", false)]
    [InlineData("missing", false)]
    [InlineData("stale", false)]
    [InlineData("date", true)]
    [InlineData("indirect-date", true)]
    public Task ResolvedDatePresenceControlsReducedInfo(string date, bool keep)
        => CheckDate(date, keep, false);

    [Theory]
    [InlineData("indirect-null", false)]
    [InlineData("missing", false)]
    [InlineData("indirect-date", true)]
    public Task JobProjectsTheSameMetadataPolicy(string date, bool keep)
        => CheckDate(date, keep, true);

    private static async Task CheckDate(string date, bool keep, bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(date, false), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, job, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        Assert.Equal(keep, document.Engine.Trailer.TryGet(PdfName.FromAscii("Info"), out QpdfObject raw));
        if (keep) { AssertDate(document, raw); }
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        Assert.False(catalog.TryGet(PdfName.FromAscii("Metadata"), out _));
        Assert.DoesNotContain("TITLE", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("<x/>", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("99 0 R", false)]
    [InlineData("42", false)]
    [InlineData("<< /ModDate 5 0 R >>", true)]
    public async Task InfoContainersResolveBeforeDateSelection(string info, bool keep)
    {
        string input = Encoding.Latin1.GetString(Fixture("indirect-date", false))
            .Replace("/Info 4 0 R", "/Info " + info, StringComparison.Ordinal);
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Encoding.Latin1.GetBytes(input), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, false, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        Assert.Equal(keep, document.Engine.Trailer.TryGet(PdfName.FromAscii("Info"), out QpdfObject raw));
        if (keep) { AssertDate(document, raw); }
    }

    [Fact]
    public async Task PreservesIndependentInfoXmlAndLiveDateAliases()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("indirect-date", true), QpdfInputOwnership.Transfer);
        Assert.Equal(QpdfExitCode.Success, (await Run(host, false, CancellationToken.None)).ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        Assert.False(catalog.TryGet(PdfName.FromAscii("Metadata"), out _));
        Assert.True(document.Engine.Trailer.TryGet(PdfName.FromAscii("Info"), out QpdfObject rawInfo));
        PdfDictionary reduced = AssertDate(document, rawInfo);
        Assert.True(catalog.TryGet(PdfName.FromAscii("AuditInfo"), out QpdfObject originalInfo));
        Assert.NotEqual(originalInfo, rawInfo);
        PdfDictionary original = Assert.IsType<PdfDictionary>(document.GetObject(Assert.IsType<PdfReference>(originalInfo)));
        Assert.True(original.TryGet(PdfName.FromAscii("Title"), out QpdfObject title));
        Assert.Equal("TITLE"u8.ToArray(), Assert.IsType<PdfByteString>(title).Value);
        Assert.True(original.TryGet(PdfName.FromAscii("ModDate"), out QpdfObject originalDate));
        Assert.True(reduced.TryGet(PdfName.FromAscii("ModDate"), out QpdfObject reducedDate));
        Assert.True(catalog.TryGet(PdfName.FromAscii("AuditDate"), out QpdfObject independentDate));
        Assert.Equal(originalDate, reducedDate); Assert.Equal(independentDate, reducedDate);
        Assert.True(catalog.TryGet(PdfName.FromAscii("AuditXml"), out QpdfObject xml));
        Assert.Equal("<x/>"u8.ToArray(), StreamObservation.Decode(Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(xml)))));
    }

    [Fact]
    public Task PreparedMetadataRefundsAndPreservesPriorTargets()
        => PreparationAdmissionChecks.VerifyAsync(
            () => PreparationAdmissionChecks.StorageAsync(Fixture("indirect-date", true), QpdfPath.From("output")),
            (host, token) => Run(host, false, token), QpdfPath.From("output"), 64, ["write", "cancel"]);

    private static PdfDictionary AssertDate(QpdfDocument document, QpdfObject raw)
    {
        PdfDictionary info = Assert.IsType<PdfDictionary>(raw is PdfReference reference ? document.GetObject(reference) : raw);
        Assert.Equal(1, info.Count); Assert.True(info.TryGet(PdfName.FromAscii("ModDate"), out QpdfObject date));
        PdfByteString value = Assert.IsType<PdfByteString>(date is PdfReference dateReference ? document.GetObject(dateReference) : date);
        Assert.Equal("D:20240102030405Z"u8.ToArray(), value.Value); return info;
    }

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, bool job, CancellationToken token)
        => job ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","removeInfo":""}""", token)
            : Qpdf.ExecuteAsync(host, new(["input", "output", "--remove-info"]), token);

    internal static byte[] Fixture(string date, bool aliases)
    {
        string value = date switch
        {
            "absent" => "", "direct-null" => "/ModDate null", "indirect-null" => "/ModDate 6 0 R",
            "missing" => "/ModDate 99 0 R", "stale" => "/ModDate 5 1 R",
            "date" => "/ModDate (D:20240102030405Z)", "indirect-date" => "/ModDate 5 0 R",
            _ => throw new ArgumentOutOfRangeException(nameof(date)),
        };
        return PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Metadata 7 0 R" + (aliases ? " /AuditInfo 4 0 R /AuditDate 5 0 R /AuditXml 7 0 R" : "") + " >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (4, "<< /Title (TITLE) " + value + " >>", null),
            (5, "(D:20240102030405Z)", null), (6, "null", null),
            (7, "<< /Type /Metadata /Subtype /XML /Length 4 >>", "<x/>"u8.ToArray())], 1, null, "1.4", "/Info 4 0 R");
    }
}
