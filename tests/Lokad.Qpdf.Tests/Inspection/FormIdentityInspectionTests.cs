using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class FormIdentityInspectionTests
{
    private static byte[] Fixture(int generation, string relation, bool indirectSubtype, bool registered)
    {
        int parentNumber = relation == "different-object" ? 6 : 4;
        int parentGeneration = relation == "wrong-generation" ? generation + 1 : parentNumber == 4 ? generation : 0;
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [" + (registered ? "4 " + generation + " R" : "") + "] >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> /Annots [5 0 R] >>", null),
            (4, "<< /FT /Tx /T (Field) /Kids [5 0 R] >>", null),
            (5, "<< /Subtype " + (indirectSubtype ? "7 0 R" : "/Widget") + " /Rect [0 0 20 10] /P 3 0 R /Parent "
                + parentNumber + " " + parentGeneration + " R >>", null),
            (6, "<< /FT /Tx /T (Field) /Kids [5 0 R] >>", null), (7, "/Widget", null),
        ], 1, new Dictionary<int, int> { [4] = generation });
    }

    private static async Task<IReadOnlyList<QpdfDiagnostic>> Check(byte[] input, string route)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        IReadOnlyList<QpdfDiagnostic> diagnostics;
        if (route == "typed")
        { diagnostics = (await Qpdf.CheckAsync(host, QpdfPath.From("input"), CancellationToken.None)).Diagnostics; }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--check"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", check = string.Empty }), CancellationToken.None);
            Assert.NotEqual(QpdfExecutionStatus.Canceled, result.Status);
            if (result.ExitCode == QpdfExitCode.Error)
            { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.FormStructureWarning); }
            diagnostics = result.Diagnostics;
        }
        Assert.Equal(0L, host.OpenLeaseCount); return diagnostics;
    }

    public static IEnumerable<object[]> ParentRelations()
    {
        foreach (int generation in new[] { 0, 2, 17 })
        foreach (string relation in new[] { "correct", "wrong-generation", "different-object" })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [generation, relation, route]; }
    }

    [Theory]
    [MemberData(nameof(ParentRelations))]
    public async Task ParentBacklinksRequireTheLiveParentIdentity(int generation, string relation, string route)
    {
        IReadOnlyList<QpdfDiagnostic> diagnostics = await Check(Fixture(generation, relation, false, true), route);
        const string expected = "Field node 5 has an invalid /Parent entry while traversing /AcroForm.";
        Assert.Equal(relation != "correct", diagnostics.Any(d => d.Message == expected));
    }

    public static IEnumerable<object[]> WidgetRepresentations()
    {
        foreach (bool indirect in new[] { false, true })
        foreach (bool registered in new[] { false, true })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [indirect, registered, route]; }
    }

    [Theory]
    [MemberData(nameof(WidgetRepresentations))]
    public async Task WidgetReachabilityUsesTheResolvedSubtype(bool indirect, bool registered, string route)
    {
        IReadOnlyList<QpdfDiagnostic> diagnostics = await Check(Fixture(0, "correct", indirect, registered), route);
        const string expected = "Widget annotation 5 is not reachable from /AcroForm.";
        Assert.Equal(!registered, diagnostics.Any(d => d.Message == expected));
    }
}
