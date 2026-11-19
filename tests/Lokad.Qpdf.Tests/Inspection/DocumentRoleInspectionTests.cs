using System.Text.RegularExpressions;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class DocumentRoleInspectionTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input");

    private static byte[] Fixture() => PdfTestDocument.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> /Metadata 6 0 R /PageLabels 8 0 R /Audit 9 0 R >>", null),
        (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
        (4, "<< /Fields [] >>", null),
        (5, "<< /Type /StructTreeRoot /K [] >>", null),
        (6, "<< /Type /Metadata /Subtype /XML /Length 7 >>", "<test/>"u8.ToArray()),
        (7, "<< /ModDate (D:20240101000000Z) /Title (original) >>", null),
        (8, "<< /Nums [0 << /S /D /P (old-) >>] >>", null),
        (9, "<< /PrivateForm 4 0 R /PrivateInfo 7 0 R /PrivateLabels 8 0 R /PrivateMetadata 6 0 R /PrivateStructure 5 0 R >>", null),
    ], 1, null, "1.4", " /Info 7 0 R");

    private static RecordingJobHost Host()
    {
        RecordingJobHost host = new(null, null, "success", true);
        host.Inner.AddInput(Input, Fixture(), QpdfInputOwnership.Transfer);
        return host;
    }

    private static async Task<string> ShowAsync(RecordingJobHost host, string selector, params string[] flags)
    {
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new([Input.Value, "--show-object=" + selector, .. flags]), CancellationToken.None);
        return Listing(result);
    }

    private static string Listing(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Null(result.OutputBytes);
        return Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.ObjectListing).Message;
    }

    [Fact]
    public async Task CatalogRemovalsApplyBeforeCheckedObjectListing()
    {
        using RecordingJobHost host = Host();
        string original = await ShowAsync(host, "1");
        string effective = await ShowAsync(host, "1", "--check", "--remove-acroform", "--remove-structure", "--remove-metadata");
        foreach (string role in new[] { "/AcroForm", "/StructTreeRoot", "/MarkInfo", "/Metadata" })
        {
            Assert.Contains(role, original, StringComparison.Ordinal);
            Assert.DoesNotContain(role, effective, StringComparison.Ordinal);
        }
        Assert.Contains("/Audit 9 0 R", effective, StringComparison.Ordinal);
        string privateEdges = await ShowAsync(host, "9", "--remove-acroform", "--remove-structure", "--remove-metadata");
        Assert.Equal("<< /PrivateForm 4 0 R /PrivateInfo 7 0 R /PrivateLabels 8 0 R /PrivateMetadata 6 0 R /PrivateStructure 5 0 R >>", privateEdges);
        Assert.Equal(original, await ShowAsync(host, "1"));
        Assert.Empty(host.CreatedPaths);
    }

    [Fact]
    public async Task InfoRemovalPublishesOnlyTheLiveModificationDateInTheActiveRole()
    {
        using RecordingJobHost host = Host();
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input","check":"","removeInfo":"","showObject":"trailer"}""", CancellationToken.None);
        string trailer = Listing(result);
        Match info = Regex.Match(trailer, @"/Info (\d+) 0 R");
        Assert.True(info.Success, trailer);
        Assert.NotEqual("7", info.Groups[1].Value);
        Assert.Equal("<< /ModDate (D:20240101000000Z) >>",
            await ShowAsync(host, info.Groups[1].Value, "--remove-info"));
        Assert.Contains("/Title (original)", await ShowAsync(host, "7", "--remove-info"), StringComparison.Ordinal);
        Assert.DoesNotContain("/Metadata", await ShowAsync(host, "1", "--remove-info"), StringComparison.Ordinal);
        Assert.Empty(host.CreatedPaths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LabelRemovalAndReplacementFollowEffectivePageOrder(bool author)
    {
        using RecordingJobHost host = Host();
        string[] modifiers = author
            ? ["--remove-page-labels", "--set-page-labels", "1:a/5/ch-", "--"]
            : ["--remove-page-labels"];
        string effective = await ShowAsync(host, "1", modifiers);
        if (author)
        {
            Assert.Contains("/PageLabels << /Nums [ 0 << /P (ch-) /S /a /St 5 >> ] >>", effective, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("/PageLabels", effective, StringComparison.Ordinal);
        }
        Assert.Equal("<< /Nums [ 0 << /P (old-) /S /D >> ] >>", await ShowAsync(host, "8", modifiers));
        Assert.Empty(host.CreatedPaths);
    }
}
