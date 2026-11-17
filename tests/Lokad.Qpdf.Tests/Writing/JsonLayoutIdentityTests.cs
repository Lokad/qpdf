using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonLayoutIdentityTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Json = QpdfPath.From("json"), Output = QpdfPath.From("output");
    private static byte[] Source() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "json-layout-sparse.pdf"));
    private static byte[] Patch() => """
        {"qpdf":[{"jsonversion":2},{
          "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Probe":"6 0 R","/Decoy":"7 0 R","/Twin":"6 1 R","/Marker":"u:patched"}},
          "obj:6 1 R":{"value":{"/Private":"u:new-generation"}}
        }]}
        """u8.ToArray();

    [Theory]
    [InlineData("preserve", false)]
    [InlineData("preserve", true)]
    [InlineData("generate", false)]
    [InlineData("generate", true)]
    [InlineData("disable", false)]
    [InlineData("disable", true)]
    public async Task SourceMembershipFollowsFullPairsAcrossNormalizedAndFinalIds(string mode, bool linearize)
    {
        byte[] source = Source(), patch = Patch(), originalSource = (byte[])source.Clone(), originalPatch = (byte[])patch.Clone();
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, source, QpdfInputOwnership.Copy); host.AddInput(Json, patch, QpdfInputOwnership.Copy);
        await using (QpdfDocument input = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None))
        {
            Assert.Equal(XrefEntryKind.Compressed, input.Engine.XRefEntries[6].Kind);
            Assert.Equal(XrefEntryKind.Uncompressed, input.Engine.XRefEntries[7].Kind);
        }
        QpdfJsonImportResult result = await Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output,
            new QpdfJsonImportOptions(mode, linearize), null, QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(1, result.PageCount); Assert.Empty(result.Warnings); Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.TryGetOutput(Output, out byte[] output)); host.AddInput(Output, output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Output, CancellationToken.None))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(Assert.IsType<PdfReference>(Value(document.Engine.Trailer, "Root"))));
            Assert.Equal("patched", Text(Value(catalog, "Marker")));
            PdfReference probe = Assert.IsType<PdfReference>(Value(catalog, "Probe")), twin = Assert.IsType<PdfReference>(Value(catalog, "Twin"));
            Assert.NotEqual(probe, twin);
            foreach ((string role, string value, bool compressed) in new[]
            {
                ("Probe", "kept", mode != "disable"), ("Decoy", "decoy", mode == "generate"),
                ("Twin", "new-generation", mode == "generate"),
            })
            {
                PdfReference reference = Assert.IsType<PdfReference>(Value(catalog, role));
                Assert.Equal(value, Text(Value(Assert.IsType<PdfDictionary>(document.GetObject(reference)), "Private")));
                Assert.Equal(compressed, document.Engine.XRefEntries[reference.ObjectNumber].Kind == XrefEntryKind.Compressed);
            }
        }
        QpdfCheckResult check = await Qpdf.CheckAsync(host, Output, new QpdfCheckOptions(), QpdfLimits.Default, CancellationToken.None);
        Assert.True(check.Passed);
        if (linearize) { Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(check.Checks, c => c.Name == "linearization").Status); }
        using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(1, independent.NumberOfPages); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(originalSource, source); Assert.Equal(originalPatch, patch);
    }

    private static QpdfObject Value(PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
    private static string Text(QpdfObject value) => Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(value).Value);

    [Fact]
    public Task PreservedMembershipRetiresOnEveryAdmissionAndPublicationFailure()
        => PreparationAdmissionChecks.VerifyAsync(async () =>
        {
            InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(Source(), Output);
            host.AddInput(Json, Patch(), QpdfInputOwnership.Transfer); return host;
        }, (host, token) => Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation([Input.Value, "--suppress-recovery", "--update-from-json=" + Json.Value, "--object-streams=preserve", Output.Value]), token),
            Output, 256, ["create", "write", "commit", "cancel", "close"]);
}
