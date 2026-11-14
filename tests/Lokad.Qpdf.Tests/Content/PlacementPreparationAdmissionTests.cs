using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Content;

public sealed class PlacementPreparationAdmissionTests
{
    private static readonly QpdfPath Output = QpdfPath.From("output");

    private static async Task<InMemoryQpdfHost> Storage(string mode)
    {
        string placement = mode.Replace("-job", "", StringComparison.Ordinal);
        byte[] input = placement switch
        {
            "overlay" => FlattenResourceStorageTests.Fixture(64, false),
            "overlay-clip" => OverlayAnnotationClippingTests.Target(),
            "overlay-form" or "overlay-form-indirect" or "overlay-generate" => ContentSafetyTests.Fixture(
                Encoding.ASCII.GetBytes(new string(' ', 524288) + "BI /W 1 /H 1 /BPC 8 /CS /G ID X EI"), "", false, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        byte[] overlay = placement switch
        {
            "overlay" => FlattenResourceStorageTests.Fixture(4, false),
            "overlay-clip" => OverlayAnnotationClippingTests.StateSource(64),
            "overlay-generate" => OverlayGenerationContextTests.Source(false, false, 90, 2, true),
            "overlay-form" => OverlayFormStorageTests.Fixture(),
            "overlay-form-indirect" => OverlayDefaultTests.Fixture("/DA 9 0 R /Q 10 0 R /NeedAppearances 11 0 R", "(/F1 18 Tf 0.5 g)", "2", "true", false, "LIVE"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        InMemoryQpdfHost storage = await PreparationAdmissionChecks.StorageAsync(input, Output);
        storage.AddInput(QpdfPath.From("overlay"), overlay, QpdfInputOwnership.Transfer);
        return storage;
    }

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, string mode, CancellationToken token)
    {
        if (mode == "overlay-generate-job")
        { return Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","generateAppearances":true,"overlay":{"file":"overlay"}}""", token); }
        if (mode.EndsWith("-job", StringComparison.Ordinal) || mode == "overlay-form-indirect")
        { return Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","overlay":{"file":"overlay"}}""", token); }
        return Qpdf.ExecuteAsync(host, new(mode == "overlay-generate"
            ? ["input", "--overlay", "overlay", "--", "--generate-appearances", "output"]
            : ["input", "--overlay", "overlay", "--", "output"]), token);
    }

    [Theory]
    [InlineData("overlay")]
    [InlineData("overlay-form")]
    [InlineData("overlay-form-job")]
    [InlineData("overlay-form-indirect")]
    [InlineData("overlay-generate")]
    [InlineData("overlay-generate-job")]
    [InlineData("overlay-clip")]
    [InlineData("overlay-clip-job")]
    public Task PreparedPlacementsRefundOwnersAndRespectPublication(string mode)
        => PreparationAdmissionChecks.VerifyAsync(() => Storage(mode), (host, token) => Run(host, mode, token), Output, 64,
            mode.EndsWith("-job", StringComparison.Ordinal) ? [] : mode == "overlay-form"
                ? ["create", "write", "commit", "cancel", "close"] : ["write"]);
}
