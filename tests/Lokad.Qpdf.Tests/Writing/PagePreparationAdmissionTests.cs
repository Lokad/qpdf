using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Content;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class PagePreparationAdmissionTests
{
    private static bool Split(string mode) => mode.StartsWith("split-", StringComparison.Ordinal)
        || mode.StartsWith("labels-split", StringComparison.Ordinal) || mode == "preserve-split";

    private static Task<InMemoryQpdfHost> Storage(string mode)
    {
        string operation = mode.Replace("-job", "", StringComparison.Ordinal);
        byte[] input = operation switch
        {
            "source-groups" or "source-groups-linear" or "zero-collation" => SourceGroupIdentityTests.Fixture("A"),
            "catalog-extras" or "split-max" => CatalogStorageTests.Fixture(512),
            "preserve-many" => PreservationAdmissionTests.Scalars(128),
            "preserve-rollback" or "preserve-split" => PreservationGraphTests.RollbackFixture(),
            "navigation" => RotationDestinationContextTests.Source("explicit"),
            "rotation" => RotationAppearanceTests.SharedFixture(270),
            "labels-split" => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "labels-split-start.pdf")),
            "split-membership" => SplitPageMembershipTests.Fixture("shared", true),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        return PreparationAdmissionChecks.StorageAsync(input, QpdfPath.From(Split(mode) ? "output-1" : "output"));
    }

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, string mode, CancellationToken token)
    {
        if (mode.StartsWith("rotation", StringComparison.Ordinal))
        { return RotationAppearanceTests.Run(host, mode == "rotation-job" ? "job" : "argv", token); }
        if (mode.EndsWith("-job", StringComparison.Ordinal))
        {
            string json = mode switch
            {
                "source-groups-job" => """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1"},{"file":"input","range":"2,3"}]}""",
                "zero-collation-job" => """{"inputFile":"input","outputFile":"output","collate":"1,0","pages":[{"file":".","range":"1,2"},{"file":".","range":"3"}]}""",
                "split-membership-job" => """{"inputFile":"input","outputFile":"output","splitPages":"1","pages":[{"file":".","range":"1"},{"file":".","range":"2,3"}]}""",
                "catalog-extras-job" => """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1,1"}],"rotate":["+90:2"]}""",
                "split-max-job" => """{"inputFile":"input","outputFile":"output","splitPages":"2147483647"}""",
                "navigation-job" => """{"inputFile":"input","outputFile":"output","flattenRotation":true}""",
                "labels-split-job" => """{"inputFile":"input","outputFile":"output","splitPages":"1"}""",
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
            return Qpdf.ExecuteJobAsync(host, json, token);
        }
        string[] arguments = mode switch
        {
            "source-groups" => ["input", "--pages", ".", "1", "input", "2,3", "--", "output"],
            "source-groups-linear" => ["input", "--pages", ".", "1", "input", "2,3", "--", "--linearize", "--object-streams=generate", "output"],
            "zero-collation" => ["input", "--collate=1,0", "--pages", ".", "1,2", ".", "3", "--", "output"],
            "split-membership" => ["input", "--split-pages=1", "--pages", ".", "1", ".", "2,3", "--", "output"],
            "catalog-extras" => ["input", "--pages", ".", "1,1", "--", "--rotate=+90:2", "output"],
            "split-max" => ["input", "--split-pages=2147483647", "output"],
            "preserve-many" or "preserve-rollback" => ["input", "--preserve-unreferenced", "output"],
            "preserve-split" => ["input", "--preserve-unreferenced", "--split-pages=1", "output"],
            "navigation" => ["input", "--flatten-rotation", "output"],
            "labels-split" => ["input", "--split-pages=1", "output"],
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        return Qpdf.ExecuteAsync(host, new(arguments), token);
    }

    [Theory]
    [InlineData("source-groups")]
    [InlineData("source-groups-job")]
    [InlineData("source-groups-linear")]
    [InlineData("catalog-extras")]
    [InlineData("catalog-extras-job")]
    [InlineData("split-max")]
    [InlineData("split-max-job")]
    [InlineData("preserve-many")]
    [InlineData("preserve-rollback")]
    [InlineData("preserve-split")]
    [InlineData("navigation")]
    [InlineData("navigation-job")]
    [InlineData("rotation")]
    [InlineData("rotation-job")]
    [InlineData("labels-split")]
    [InlineData("labels-split-job")]
    [InlineData("split-membership")]
    [InlineData("split-membership-job")]
    [InlineData("zero-collation")]
    [InlineData("zero-collation-job")]
    public Task PreparedPageGraphsRefundOwnersAndRespectPublication(string mode)
        => PreparationAdmissionChecks.VerifyAsync(() => Storage(mode), (host, token) => Run(host, mode, token),
            QpdfPath.From(Split(mode) ? "output-1" : "output"), mode.StartsWith("labels-split", StringComparison.Ordinal) ? 128 : 64,
            mode.EndsWith("-job", StringComparison.Ordinal) || mode == "source-groups-linear" ? [] : ["write"]);
}
