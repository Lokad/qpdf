using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Tests.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class FormPreparationAdmissionTests
{
    private static readonly QpdfPath Output = QpdfPath.From("output");

    private static async Task<InMemoryQpdfHost> Storage(string mode)
    {
        string form = mode.Replace("-job", "", StringComparison.Ordinal);
        byte[] input = form switch
        {
            "signature" => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "signature-incremental.pdf")),
            "catalog" => OrdinaryFormStorageTests.Fixture(512),
            "flatten" => FlattenResourceStorageTests.Fixture(64, false),
            "merge" => FormMergeStorageTests.Fixture(new string('F', 32768), false),
            "form-indirect-generate" or "form-indirect-both" => FormNeedAppearancesTests.Fixture("9 3 R", "true", 3),
            "generate" => AppearanceGenerationRegressionTests.CheckboxFixture(64),
            "generate-parent" => AppearanceParentScopeTests.Fixture("value", "info", false),
            "generate-array" => AnnotationArrayIsolationTests.Fixture("button", "attachment", true, 3, 1),
            "generate-text" => AppearanceProgramTests.Fixture(new string(' ', 32768) + "/Tx BMC\n0 0 10 10 re f EMC", "", "/DA (/Helv 17 Tf)", ""),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        InMemoryQpdfHost storage = await PreparationAdmissionChecks.StorageAsync(input, Output);
        if (form == "catalog") { storage.AddInput(QpdfPath.From("payload"), "abc"u8.ToArray(), QpdfInputOwnership.Transfer); }
        if (form == "merge")
        { storage.AddInput(QpdfPath.From("overlay"), FormMergeStorageTests.Fixture(new string('F', 32768), true), QpdfInputOwnership.Transfer); }
        return storage;
    }

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, string mode, CancellationToken token)
    {
        if (mode == "catalog-job")
        { return Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1"}],"addAttachment":[{"file":"payload"}]}""", token); }
        if (mode == "form-indirect-both")
        { return Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","generateAppearances":true,"flattenAnnotations":"all"}""", token); }
        if (mode.EndsWith("-job", StringComparison.Ordinal) || mode == "form-indirect-generate")
        { return Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","generateAppearances":true}""", token); }
        string[] arguments = mode switch
        {
            "signature" => ["input", "--pages", ".", "3,1,3", "--", "output"],
            "catalog" => ["input", "--pages", ".", "1", "--", "--add-attachment", "payload", "--", "output"],
            "flatten" => ["input", "--flatten-annotations=all", "--ii-min-bytes=0", "--", "output"],
            "merge" => ["input", "--pages", ".", "1", "overlay", "1", "--", "output"],
            "generate" or "generate-text" or "generate-array" or "generate-parent" => ["input", "--generate-appearances", "--ii-min-bytes=0", "--", "output"],
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        return Qpdf.ExecuteAsync(host, new(arguments), token);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("catalog")]
    [InlineData("catalog-job")]
    [InlineData("flatten")]
    [InlineData("merge")]
    [InlineData("form-indirect-generate")]
    [InlineData("form-indirect-both")]
    [InlineData("generate")]
    [InlineData("generate-job")]
    [InlineData("generate-text")]
    [InlineData("generate-text-job")]
    [InlineData("generate-array")]
    [InlineData("generate-array-job")]
    [InlineData("generate-parent")]
    [InlineData("generate-parent-job")]
    public Task PreparedFormsRefundOwnersAndRespectPublication(string mode)
        => PreparationAdmissionChecks.VerifyAsync(() => Storage(mode), (host, token) => Run(host, mode, token), Output, 64,
            mode.EndsWith("-job", StringComparison.Ordinal) ? [] : mode == "generate"
                ? ["create", "write", "commit", "cancel", "close"] : ["write"]);
}
