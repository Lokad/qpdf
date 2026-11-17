using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class OutlineHintOwnershipTests
{
    internal static byte[] Source(bool populated, bool open, bool shared)
        => Source(populated ? 1 : 0, open, shared);

    private static byte[] Source(int count, bool open, bool shared)
    {
        string outlines = "<< /Type /Outlines /Probe (KEEP) "
            + (count > 0 ? $"/First 8 0 R /Last {7 + count} 0 R /Count {count}" : "/Count 0") + " >>";
        byte[] content = Encoding.ASCII.GetBytes(shared ? "BT /F1 12 Tf 1 1 Td (Hi) Tj ET\n" : "q Q\n");
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R /Audit 7 0 R " + (open ? "/PageMode /UseOutlines" : "") + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources "
                + (shared ? "<< /Font << /F1 6 0 R >> >>" : "<< >>") + " >>", null),
            (4, $"<< /Length {content.Length} >>", content), (5, outlines, null),
            (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null), (7, "17", null),
        ];
        for (int index = 0; index < count; index++)
        {
            string links = (index > 0 ? $" /Prev {7 + index} 0 R" : "") + (index + 1 < count ? $" /Next {9 + index} 0 R" : "");
            objects.Add((8 + index, $"<< /Title (Ch{index + 1}) /Dest [3 0 R /Fit] /Parent 5 0 R{links} >>", null));
        }
        return PdfTestDocument.Assemble(objects, 1);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task LinearizedOutlineClosureStaysDirectWhileOtherObjectsPack(bool populated, bool open, bool shared)
    {
        using InMemoryQpdfHost host = new(); QpdfPath input = QpdfPath.From("input"), output = QpdfPath.From("output");
        host.AddInput(input, Source(populated, open, shared), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation([input.Value, "--linearize", "--object-streams=generate", output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.TryGetOutput(output, out byte[] bytes)); host.AddInput(output, bytes, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, output, CancellationToken.None))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(Assert.IsType<PdfReference>(Value(document.Engine.Trailer, "Root"))));
            PdfReference reference = Assert.IsType<PdfReference>(Value(catalog, "Outlines"));
            Assert.Equal(XrefEntryKind.Uncompressed, document.Engine.XRefEntries[reference.ObjectNumber].Kind);
            Assert.Contains(document.Engine.XRefEntries.Values, entry => entry.Kind == XrefEntryKind.Compressed);
            PdfDictionary outlines = Assert.IsType<PdfDictionary>(document.GetObject(reference));
            Assert.Equal("KEEP"u8.ToArray(), Assert.IsType<PdfByteString>(Value(outlines, "Probe")).Value);
            if (populated)
            {
                PdfDictionary item = Assert.IsType<PdfDictionary>(document.GetObject(Assert.IsType<PdfReference>(Value(outlines, "First"))));
                Assert.Equal("Ch1"u8.ToArray(), Assert.IsType<PdfByteString>(Value(item, "Title")).Value);
            }
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(LinearizationStatus.Valid, LinearizationCheck.Inspect(document.Engine, 1, QpdfLimits.Default, governor, CancellationToken.None).Status);
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
        QpdfCheckResult check = await Qpdf.CheckAsync(host, output, CancellationToken.None);
        Assert.True(check.Passed); Assert.Equal(1, check.PageCount); Assert.Equal(0L, host.OpenLeaseCount);
        using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.Equal(shared ? "Hi" : "", independent.GetPage(1).Text);
    }

    private static QpdfObject Value(PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }

    [Fact]
    public async Task LargeOutlineClosureStaysContiguousWhileOtherObjectsPack()
    {
        using InMemoryQpdfHost host = new(); QpdfPath input = QpdfPath.From("input"), output = QpdfPath.From("output");
        host.AddInput(input, Source(110, false, true), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([input.Value, "--linearize", "--object-streams=generate", output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(output, out byte[] bytes)); host.AddInput(output, bytes, QpdfInputOwnership.Transfer);
        QpdfCheckResult checkedOutput = await Qpdf.CheckAsync(host, output, CancellationToken.None);
        Assert.True(checkedOutput.Passed);
        QpdfJsonResult report = await Qpdf.GetDocumentJsonAsync(host, output, new QpdfJsonOptions(2, ["outlines"]), CancellationToken.None);
        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(report.Json);
        Assert.Equal(110, json.RootElement.GetProperty("outlines").GetArrayLength());
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, output, CancellationToken.None);
        Assert.Contains(document.Engine.XRefEntries.Values, entry => entry.Kind == XrefEntryKind.Compressed);
    }

    [Fact]
    public Task OutlinePreparationRetiresOnEveryAdmissionAndPublicationFailure()
        => PreparationAdmissionChecks.VerifyAsync(
            () => PreparationAdmissionChecks.StorageAsync(Source(true, true, true), QpdfPath.From("output")),
            (host, token) => Qpdf.ExecuteAsync(host, new(["input", "--linearize", "--object-streams=generate", "output"]), token),
            QpdfPath.From("output"), 256, ["create", "write", "commit", "cancel", "close"]);

    [Theory]
    [InlineData("linearized-outline-missing-hint.pdf", false)]
    [InlineData("linearized-outline-split-group.pdf", false)]
    [InlineData("linearized-outline-native-omission.pdf", true)]
    public async Task FrozenIndependentControlsDistinguishMissingAndLegitimatelyOmittedHints(string fixture, bool valid)
    {
        using InMemoryQpdfHost host = new(); QpdfPath input = QpdfPath.From("input");
        host.AddInput(input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)), QpdfInputOwnership.Transfer);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, input, CancellationToken.None);
        Assert.Equal(valid, result.Passed);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, input, CancellationToken.None);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(valid ? LinearizationStatus.Valid : LinearizationStatus.Invalid,
            LinearizationCheck.Inspect(document.Engine, 1, QpdfLimits.Default, governor, CancellationToken.None).Status);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
