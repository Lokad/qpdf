using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentPageModeTests
{
    private static InMemoryQpdfHost Host(string shape)
    {
        string value = shape switch { "name" => "/UseOutlines", "raw" or "direct-raw" => "/Use#80#ff", "opaque" => "[(KEEP) 5 0 R]", "bad" => "[5 0 R 1 0 R]", _ => "null" };
        string mode = shape == "absent" ? "" : " /PageMode " + (shape == "null" ? "null" : shape == "direct-raw" ? value : "4 0 R");
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R" + mode + (shape == "opaque" ? " /Audit 4 0 R" : "") + " >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (4, value, null), (5, "<< /Tag (KEPT) >>", null)], 1);
        InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("added"), "A"u8.ToArray(), QpdfInputOwnership.Transfer);
        return host;
    }

    private static async Task<QpdfDocument> Reopen(InMemoryQpdfHost host, string path)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From(path), out byte[] bytes));
        host.AddInput(QpdfPath.From("again"), bytes, QpdfInputOwnership.Transfer);
        return await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
    }

    public static IEnumerable<object[]> InsertionCases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (string shape in new[] { "absent", "null", "indirect-null", "name", "raw", "direct-raw", "opaque", "bad" })
        { yield return [route, shape]; }
    }

    [Theory]
    [MemberData(nameof(InsertionCases))]
    public async Task InsertionUsesTheResolvedHintOrTheAttachmentDefault(string route, string shape)
    {
        using InMemoryQpdfHost host = Host(shape);
        if (route == "typed")
        { await Qpdf.AddAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), [new("added", "K", null, null, null, null, null, false)], CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","addAttachment":[{"file":"added","key":"K"}]}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--add-attachment", "added", "--key=K", "--", "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        await using (QpdfDocument document = await Reopen(host, "output"))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            Assert.True(catalog.TryGet(PdfName.FromAscii("PageMode"), out QpdfObject hint));
            if (shape == "opaque")
            {
                Assert.True(catalog.TryGet(PdfName.FromAscii("Audit"), out QpdfObject audit)); Assert.Equal(audit, hint);
                PdfReference link = Assert.IsType<PdfReference>(hint);
                PdfArray opaque = Assert.IsType<PdfArray>(document.GetObject(link.ObjectNumber));
                Assert.Equal("KEEP"u8.ToArray(), Assert.IsType<PdfByteString>(opaque[0]).Value);
                Assert.IsType<PdfDictionary>(document.GetObject(Assert.IsType<PdfReference>(opaque[1]).ObjectNumber));
            }
            else
            {
                byte[] expected = shape == "name" ? "UseOutlines"u8.ToArray() : shape is "raw" or "direct-raw" ? [85, 115, 101, 128, 255] : "UseAttachments"u8.ToArray();
                Assert.Equal(expected, Assert.IsType<PdfName>(hint).RawBytes);
            }
            Assert.Equal(1, document.PageCount);
        }
        await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("again"), "K", QpdfPath.From("payload"), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("payload"), out byte[] payload)); Assert.Equal("A"u8.ToArray(), payload);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TypedSelectionResolvesNamesWithoutEnablingOpaqueCatalogPreservation(bool multi, bool opaque)
    {
        using InMemoryQpdfHost host = Host(opaque ? "opaque" : "raw");
        if (multi) { await Qpdf.SelectPagesAsync(host, [new(QpdfPath.From("input"), new PdfPageSelectionOptions(1))], QpdfPath.From("output"), CancellationToken.None); }
        else { await Qpdf.SelectPageAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new PdfPageSelectionOptions(1), CancellationToken.None); }
        await using (QpdfDocument document = await Reopen(host, "output"))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            Assert.Equal(!opaque, catalog.TryGet(PdfName.FromAscii("PageMode"), out QpdfObject hint));
            if (!opaque) { Assert.Equal(new byte[] { 85, 115, 101, 128, 255 }, Assert.IsType<PdfName>(hint).RawBytes); }
            Assert.False(catalog.TryGet(PdfName.FromAscii("Audit"), out _));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv", "name")]
    [InlineData("job", "name")]
    [InlineData("argv", "indirect-null")]
    [InlineData("job", "indirect-null")]
    [InlineData("argv", "opaque")]
    [InlineData("job", "opaque")]
    public async Task SplitKeepsNamedHintsAndDropsOpaqueExtras(string route, string shape)
    {
        using InMemoryQpdfHost host = Host(shape);
        QpdfExecutionResult result = route == "job"
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output.pdf", splitPages = "1", addAttachment = new[] { new { file = "added", key = "K" } } }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--split-pages=1", "--add-attachment", "added", "--key=K", "--", "--", "output.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using (QpdfDocument document = await Reopen(host, "output-1.pdf"))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            Assert.True(catalog.TryGet(PdfName.FromAscii("PageMode"), out QpdfObject hint));
            Assert.Equal(shape == "name" ? "UseOutlines" : "UseAttachments", Assert.IsType<PdfName>(hint).DisplayText);
            Assert.False(catalog.TryGet(PdfName.FromAscii("Audit"), out _));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
