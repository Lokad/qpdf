using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ImageInspectionTests
{
    internal static byte[] Fixture(string subtype, string target, string resources, string descriptors)
        => CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << /XObject << " + resources + " >> >> /Contents 4 0 R >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (5, "<< /Type /XObject " + (subtype.Length == 0 ? "" : "/Subtype " + subtype) + " " + descriptors + " /Length 12 >>", new byte[12]),
            (6, target, null)], 1);

    private const string Dimensions = "/Width 2 /Height 2 /BitsPerComponent 8 /ColorSpace /DeviceRGB";

    public static IEnumerable<object[]> Subtypes()
    {
        foreach (var item in new[] {
            ("/Image", "/Image", true), ("6 0 R", "/Image", true),
            ("/Form", "/Image", false), ("6 0 R", "/Form", false),
            ("6 0 R", "null", false), ("6 0 R", "17", false),
            ("6 0 R", "6 0 R", false), ("42 0 R", "/Image", false),
            ("6 1 R", "/Image", false), ("", "/Image", false) })
        foreach (int version in new[] { 1, 2 })
        foreach (string route in new[] { "typed", "argv", "job" })
        { yield return [item.Item1, item.Item2, item.Item3, version, route]; }
    }

    internal static async Task<byte[]> Inspect(InMemoryQpdfHost host, int version, string route)
    {
        if (route == "typed")
        {
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"),
                new(version, ["pages"]), CancellationToken.None);
            Assert.Empty(result.Warnings);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--json=" + version, "--json-key=pages", "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", json = version.ToString(), jsonKey = new[] { "pages" } }), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal(0L, host.OpenLeaseCount);
        return output;
    }

    [Theory]
    [MemberData(nameof(Subtypes))]
    public async Task SemanticSubtypesIncludeOnlyImages(string subtype, string target, bool expected, int version, string route)
    {
        byte[] output;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Fixture(subtype, target, "/Im0 5 0 R", Dimensions), QpdfInputOwnership.Transfer);
            output = await Inspect(host, version, route);
        }
        using JsonDocument json = JsonDocument.Parse(output);
        JsonElement images = json.RootElement.GetProperty("pages")[0].GetProperty("images");
        Assert.Equal(expected ? 1 : 0, images.GetArrayLength());
        if (expected) { Assert.Equal("5 0 R", images[0].GetProperty("object").GetString()); }
    }

    [Theory]
    [InlineData(false, "/Image", true)]
    [InlineData(true, "/Image", true)]
    [InlineData(false, "/Form", false)]
    [InlineData(true, "/Form", false)]
    [InlineData(false, "6 0 R", false)]
    [InlineData(true, "6 0 R", false)]
    public async Task TextListingsUseTheSameSubtypeRule(bool job, string target, bool expected)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("6 0 R", target, "/Im0 5 0 R", Dimensions), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","showPages":true,"withImages":true}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--show-pages", "--with-images"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(expected, result.Diagnostics.Any(item => item.Message == "    /Im0: 5 0 R, 2 x 2"));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private sealed class Resolver(Action observed) : IIndirectResolver
    {
        internal int Calls;
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        { Calls++; observed(); value = PdfName.FromAscii("Image"); return true; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubtypeResolutionChecksBothCancellationRoutesAfterTheCallback(bool caller)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        Resolver resolver = new(cancellation.Cancel);
        PdfDictionary dictionary = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Subtype")] = new PdfReference(6, 0) });
        Assert.ThrowsAny<OperationCanceledException>(() => PdfDictionaryLookup.HasName(dictionary, "Subtype"u8, "Image"u8,
            resolver, governor, caller ? cancellation.Token : CancellationToken.None));
        Assert.Equal(1, resolver.Calls); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
