using System.Reflection;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonPageContainerPreparationTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Json = QpdfPath.From("json"), Output = QpdfPath.From("output");
    private const string Attributes = "/MediaBox [0 0 100 200] /Resources << >>";

    private static byte[] Source(string shape)
    {
        List<(int, string, byte[]?)> objects = [(1, "<< /Type /Catalog /Pages 2 0 R >>", null)];
        if (shape == "kids")
        {
            objects.AddRange([
                (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>", null),
                (3, "<< /Type /Pages /Parent 2 0 R /Kids 5 0 R /Count 1 " + Attributes + " >>", null),
                (4, "<< /Type /Pages /Parent 2 0 R /Kids 5 0 R /Count 1 " + Attributes + " >>", null),
                (5, "[6 0 R]", null), (6, "<< /Type /Page /Parent 3 0 R /Tag (old) >>", null)]);
        }
        else
        {
            bool empty = shape == "empty";
            objects.AddRange([
                (2, "<< /Type /Pages /Kids [3 0 R 3 0 R] /Count " + (empty ? "0" : "2") + " >>", null),
                (3, "<< /Type /Pages /Parent 2 0 R /Kids " + (empty ? "[] /Count 0" : "[4 0 R] /Count 1") + " " + Attributes + " >>", null)]);
            if (!empty) { objects.Add((4, "<< /Type /Page /Parent 3 0 R /Tag (old) >>", null)); }
        }
        return PdfTestDocument.Assemble(objects, 1);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string shape in new[] { "branch", "kids", "empty" })
        foreach (bool prepare in new[] { false, true }) { yield return [shape, prepare, false]; }
        yield return ["branch", true, true];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task DeclaredPreparationRejectsRepeatedContainersBeforePublication(string shape, bool prepare, bool pushed)
    {
        using InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(Source(shape), Output);
        string metadata = "{\"qpdf\":[{\"jsonversion\":2,\"" + (pushed ? "pushedinheritedpageresources" : "calledgetallpages")
            + "\":" + (prepare ? "true" : "false") + "},{}]}";
        host.AddInput(Json, Encoding.UTF8.GetBytes(metadata), QpdfInputOwnership.Transfer);
        if (prepare)
        {
            QpdfParseException error = await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, CancellationToken.None).AsTask());
            Assert.Contains("repeated page-tree container", error.Message, StringComparison.Ordinal);
            Assert.True(host.TryGetOutput(Output, out byte[] prior)); Assert.Equal(new byte[] { 17, 23, 41 }, prior);
        }
        else
        {
            QpdfJsonImportResult result = await Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, CancellationToken.None);
            Assert.Equal(shape == "empty" ? 0 : 2, result.PageCount);
            Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.True(bytes.AsSpan().StartsWith("%PDF-"u8));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task EmptyDagPreparationStopsAfterFiniteDistinctContainerWorkAndRefunds()
    {
        const int depth = 64;
        List<(int, string, byte[]?)> objects = [(1, "<< /Type /Catalog /Pages 2 0 R >>", null)];
        for (int number = 2; number <= depth + 1; number++)
        {
            string children = number == depth + 1 ? "[]" : "[" + (number + 1) + " 0 R " + (number + 1) + " 0 R]";
            objects.Add((number, "<< /Type /Pages /Kids " + children + " /Count 0 >>", null));
        }
        using InMemoryQpdfHost host = new(); host.AddInput(Input, PdfTestDocument.Assemble(objects, 1), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None);
        Assert.Equal(0, document.PageCount);
        QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = long.MaxValue };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        FieldInfo work = typeof(ResourceGovernor).GetField("_operationsSinceCheck", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing finite work observation.");
        using (ParserMemoryScope values = ParserMemoryScope.Create(governor))
        using (ParserMemoryScope temporary = ParserMemoryScope.Create(governor))
        {
            Assert.Throws<QpdfParseException>(() => PageTreePreparation.Prepare(document.Engine, false, limits,
                values, temporary, governor, CancellationToken.None));
            long observed = Convert.ToInt64(work.GetValue(governor));
            Assert.InRange(observed, depth, depth * 128L);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
