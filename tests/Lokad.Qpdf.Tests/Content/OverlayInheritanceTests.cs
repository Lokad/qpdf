using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Tests.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayInheritanceTests
{
    [Theory]
    [InlineData(true, true, 3)]
    [InlineData(false, true, 2)]
    [InlineData(false, false, 0)]
    public async Task GeometryIsValidatedBeforeUnusedResources(bool empty, bool oversizedResources, int exit)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Primary(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), Source(empty, oversizedResources), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--overlay", "stamp", "--", "output"]),
            new(QpdfLimits.Default with { MaxContainerSize = 16 }), CancellationToken.None);
        Assert.Equal((QpdfExitCode)exit, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        if (exit == 2)
        {
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal("prior"u8.ToArray(), bytes);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded); return;
        }
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        if (empty)
        {
            Assert.Equal("Overlay page has an empty media intersection; skipping it.", Assert.Single(result.Diagnostics).Message);
            Assert.Equal(0L, result.Counters.TotalDecodedBytes);
        }
        else { Assert.Empty(result.Diagnostics); }
        await CheckSourceForm(host, bytes, empty ? 0 : 1);
    }

    [Fact]
    public async Task JobUnderlayPlacesTheInheritedSource()
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Primary(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), Source(false, false), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input","outputFile":"output","underlay":{"file":"stamp"}}""", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); await CheckSourceForm(host, bytes, 1);
    }

    private static async Task CheckSourceForm(InMemoryQpdfHost host, byte[] bytes, int expected)
    {
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        Assert.Equal(1, document.PageCount); int sourceForms = 0;
        foreach ((int number, XrefEntry entry) in document.Engine.XRefEntries)
        {
            if (entry.Kind != XrefEntryKind.Free && document.GetObject(number) is PdfStream stream
                && stream.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject raw)
                && raw is PdfName name && name.DisplayText == "Form"
                && Encoding.ASCII.GetString(StreamObservation.Decode(stream)).Contains("inheritance-source", StringComparison.Ordinal)) { sourceForms++; }
        }
        Assert.Equal(expected, sourceForms);
    }

    private static byte[] Primary() => PdfTestDocument.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null)], 1);

    private static byte[] Source(bool empty, bool oversizedResources)
    {
        byte[] content = "% inheritance-source\nq Q\n"u8.ToArray();
        return PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] /MediaBox [0 0 " + (empty ? "0" : "100") + " 100] /Resources 4 0 R /Rotate 0 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>", null),
            (4, oversizedResources ? "[" + string.Join(" ", Enumerable.Range(0, 17)) + "]" : "<< >>", null),
            (5, "<< /Length " + content.Length + " >>", content)], 1);
    }
}
