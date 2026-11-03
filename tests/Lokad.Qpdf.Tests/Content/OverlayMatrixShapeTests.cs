using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayMatrixShapeTests
{
    private static byte[] Source(string value, bool indirect)
    {
        const string appearance = "1 0 0 rg 0 0 10 10 re f";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [6 0 R 8 0 R] >>", null),
            (4, "null", null),
            (5, "<< /Subtype /Form /BBox [0 0 10 10] /Resources << >> /Matrix " + (indirect ? "7 0 R" : value) + " /Length " + appearance.Length + " >>", System.Text.Encoding.ASCII.GetBytes(appearance)),
            (6, "<< /Type /Annot /Subtype /Stamp /Rect [10 10 30 30] /F 4 /AP << /N 5 0 R /R 5 0 R /D << /On 5 0 R >> >> >>", null),
            (7, value, null),
            (8, "<< /Type /Annot /Subtype /Stamp /Rect [40 40 60 60] /F 4 /AP << /N 5 0 R >> >>", null)], 1);
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    private static QpdfObject Get(QpdfDocument document, PdfDictionary node, string key)
    { Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(document, value); }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string value in new[] { "42", "/Opaque", "<< /Private (data) >>" })
        foreach (bool indirect in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [value, indirect, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NonArrayMatrixStaysOpaqueAndSharedAcrossAppearanceSlots(string value, bool indirect, bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("stamp"), Source(value, indirect), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("input"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> >>", null)], 1), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","overlay":{"file":"stamp"}}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--overlay", "stamp", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Single(result.Diagnostics, d => d.Message.Contains("non-array", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
        {
            PdfArray annotations = Assert.IsType<PdfArray>(Get(document, document.PageReferences[0].Page, "Annots")); Assert.Equal(2, annotations.Count);
            PdfDictionary first = Assert.IsType<PdfDictionary>(Resolve(document, annotations[0])); PdfDictionary second = Assert.IsType<PdfDictionary>(Resolve(document, annotations[1]));
            PdfDictionary slots = Assert.IsType<PdfDictionary>(Get(document, first, "AP"));
            PdfStream normal = Assert.IsType<PdfStream>(Get(document, slots, "N"));
            Assert.Same(normal, Get(document, slots, "R")); Assert.Same(normal, Get(document, Assert.IsType<PdfDictionary>(Get(document, slots, "D")), "On"));
            Assert.Same(normal, Get(document, Assert.IsType<PdfDictionary>(Get(document, second, "AP")), "N"));
            QpdfObject matrix = Get(document, normal.Dictionary, "Matrix");
            if (value == "42") { Assert.Equal(42L, Assert.IsType<PdfInteger>(matrix).Value); }
            else if (value == "/Opaque") { Assert.Equal(PdfName.FromAscii("Opaque"), Assert.IsType<PdfName>(matrix)); }
            else { Assert.True(Assert.IsType<PdfByteString>(Get(document, Assert.IsType<PdfDictionary>(matrix), "Private")).ValueSpan.SequenceEqual("data"u8)); }
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticOverflowPreservesTheTargetAndSuppressedWarningsConsumeNoQuota(bool suppress)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Source("42", true), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), Source("/Opaque", false), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        // Separate placements create separate copied appearance identities.
        List<string> args = ["input", "--overlay", "stamp", "--", "--overlay", "stamp", "--"];
        if (suppress) { args.AddRange(["--no-warn", "--warning-exit-0"]); }
        args.Add("output");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(args), new(QpdfLimits.Default with { MaxDiagnostics = 1 }), CancellationToken.None);
        Assert.Equal(suppress ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        if (suppress) { Assert.Empty(result.Diagnostics); Assert.False(output.AsSpan().SequenceEqual("prior"u8)); }
        else { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded); Assert.Equal("prior"u8.ToArray(), output); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
