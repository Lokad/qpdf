using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class SharedResourceAutoModeTests
{
    private static byte[] Input(string shape)
    {
        string branch = shape == "branch" ? " /Resources 5 0 R" : string.Empty;
        string leaf = shape == "shared-leaves" ? " /Resources 5 0 R" : shape == "direct-leaves" ? " /Resources << /Font << /F1 8 0 R /F2 9 0 R >> >>" : string.Empty;
        byte[] content = "BT /F1 12 Tf 0 0 Td (x) Tj ET\n"u8.ToArray();
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2" + branch + " >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 6 0 R" + leaf + " >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 7 0 R" + leaf + " >>", null),
            (5, "<< /Font << /F1 8 0 R /F2 9 0 R >> >>", null), (6, "<< /Length " + content.Length + " >>", content), (7, "<< /Length " + content.Length + " >>", content),
            (8, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null), (9, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", null)
        ], 1);
    }

    private static async Task<byte[]> Rewrite(byte[] input, string route, bool explicitAuto)
    {
        InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        string modeJson = explicitAuto ? ",\"removeUnreferencedResources\":\"auto\"" : string.Empty;
        string settings = "\"pages\":[{\"file\":\".\",\"range\":\"1\"}],\"deterministicId\":\"\",\"objectStreams\":\"disable\"" + modeJson;
        string json = route == "job" ? "{\"inputFile\":\"inputs/a.pdf\",\"outputFile\":\"outputs/a.pdf\"," + settings + "}" : "{" + settings + "}";
        host.AddInput(QpdfPath.From("inputs/job.json"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);
        QpdfExecutionResult result;
        if (route == "job") { result = await Qpdf.ExecuteJobAsync(host, json, CancellationToken.None); }
        else
        {
            List<string> args = ["inputs/a.pdf"];
            if (route == "mixed") { args.Add("--job-json-file=inputs/job.json"); args.AddRange(["--", "outputs/a.pdf"]); }
            else
            {
                if (explicitAuto) { args.Add("--remove-unreferenced-resources=auto"); }
                args.AddRange(["--deterministic-id", "--object-streams=disable", "--pages", ".", "1", "--", "outputs/a.pdf"]);
            }
            result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(args), CancellationToken.None);
        }
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] output)); return output;
    }

    [Theory]
    [InlineData("branch", "argv", false)]
    [InlineData("branch", "job", false)]
    [InlineData("branch", "mixed", false)]
    [InlineData("shared-leaves", "argv", false)]
    [InlineData("shared-leaves", "job", false)]
    [InlineData("shared-leaves", "mixed", false)]
    [InlineData("direct-leaves", "argv", true)]
    [InlineData("direct-leaves", "job", true)]
    [InlineData("direct-leaves", "mixed", true)]
    public async Task ExplicitAutoMatchesDefaultDecisionAndKeepsOnlyTheExpectedResources(string shape, string route, bool keepUnused)
    {
        byte[] input = Input(shape); byte[] implicitOutput = await Rewrite(input, route, false), autoOutput = await Rewrite(input, route, true);
        Assert.Equal(implicitOutput, autoOutput);
        InMemoryQpdfHost reader = new(); reader.AddInput(QpdfPath.From("result.pdf"), autoOutput, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(reader, QpdfPath.From("result.pdf"), CancellationToken.None);
        Assert.Equal(1, document.PageCount); PdfDictionary page = document.PageReferences[0].Page;
        Assert.True(page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject resourceValue));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(Resolve(document, resourceValue));
        Assert.True(resources.TryGet(PdfName.FromAscii("Font"), out QpdfObject fontValue));
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(Resolve(document, fontValue));
        Assert.True(fonts.TryGet(PdfName.FromAscii("F1"), out _)); Assert.Equal(keepUnused, fonts.TryGet(PdfName.FromAscii("F2"), out _));
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
    {
        if (value is not PdfReference reference) { return value; }
        Assert.Equal(0, reference.Generation); return document.GetObject(reference.ObjectNumber);
    }
}
