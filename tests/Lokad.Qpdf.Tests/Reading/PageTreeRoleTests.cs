using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class PageTreeRoleTests
{
    [Fact]
    public async Task CaptureFollowsKidsThroughNestedAndEmptyNodes()
    {
        using InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("input.pdf");
        host.AddInput(path, PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Private 5 0 R >>", null),
            (2, "<< /Type /Pages /Kids 8 0 R /Count 1 >>", null),
            (3, "<< /Type /Page /MediaBox [0 0 100 100] >>", null),
            (4, "<< /Type /Pages /Kids 7 0 R /Count 0 >>", null),
            (5, "<< /Type /Pages /Kids [] /Count 0 >>", null),
            (6, "null", null),
            (7, "[]", null),
            (8, "[3 0 R 4 0 R]", null)
        ], 1), QpdfInputOwnership.Transfer);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope storage = ParserMemoryScope.Create(governor);
        HashSet<int> roles = ParserContainers.CreateSet<int>(storage);

        await using QpdfDocument document = await QpdfDocument.OpenInspectionCore(host, path, null, QpdfLimits.Default,
            governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict, CancellationToken.None,
            false, false, new PageTreeRoleSet(roles, storage));

        Assert.Equal(1, document.PageCount);
        Assert.Equal([1, 2, 3, 4, 7, 8], roles.Order());
    }
}
