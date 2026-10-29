using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class RecoveryHeaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultidigitHeadersKeepCatalogIdentityOnPublicRecovery(bool job)
    {
        byte[] pdf = TolerantObjectLoadingTests.Pdf(Enumerable.Repeat("null", 50).ToArray(), true);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","showObject":"1"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=1"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing && d.Message == "<< /Pages 2 0 R /Type /Catalog >>");
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public void RecoveryEntriesUseCompleteHeadersRatherThanDigitSuffixes()
    {
        byte[] pdf = TolerantObjectLoadingTests.Pdf(Enumerable.Repeat("null", 50).ToArray(), true);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        XRefTable table = XRefTable.Parse(pdf, QpdfLimits.Default, governor, new DiagnosticBag(governor), QpdfRecoveryMode.BoundedRepair, valuesEscape: true);
        for (int number = 1; number < 55; number++)
        {
            byte[] header = Encoding.ASCII.GetBytes($"\n{number} 0 obj\n");
            int expectedOffset = pdf.AsSpan().IndexOf(header) + 1;
            Assert.True(expectedOffset > 0);
            Assert.Equal(XrefEntryKind.Uncompressed, table.Entries[number].Kind);
            Assert.Equal(expectedOffset, table.Entries[number].First);
            Assert.Equal(0, table.Entries[number].Second);
        }
    }
}
