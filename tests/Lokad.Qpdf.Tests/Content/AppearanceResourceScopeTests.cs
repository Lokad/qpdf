using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceResourceScopeTests
{
    private static readonly byte[] Program = "/Fx0 Do BT /F1 10 Tf 1 5 Td (ABCD) Tj ET"u8.ToArray();

    private static byte[] Fixture(string resources, string shape)
    {
        string scope = resources switch { "absent" => "", "null" => "/Resources null", "reference-null" => "/Resources 16 0 R",
            "local" => "/Resources << /Font << /F1 11 0 R >> /XObject << /Fx0 7 0 R >> >>", _ => "/Resources 17 0 R" };
        string normal = shape switch { "stream" => "5 0 R", "states" => "<< /On 5 0 R >>", _ => "18 0 R" };
        string appearance = "/AP << /N " + normal + " /R 5 0 R /D 5 0 R /Private 15 0 R >> /AS /On";
        string xobjects = "/XObject << /Fx0 7 0 R /Fx1 7 0 R /Fx2 7 0 R /Fx3 7 0 R /Unused 7 0 R >>";
        return PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /OriginalAP 5 0 R /OriginalResources 9 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 9 0 R /Contents 8 0 R /Annots [6 0 R] >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 10 0 R /Contents 8 0 R /Annots [14 0 R] >>", null),
            (5, "<< /Subtype /Form /BBox [0 0 40 20] /Length " + Program.Length + " " + scope + " >>", Program),
            (6, "<< /Type /Annot /Subtype /Stamp /Rect [20 20 80 50] /F 4 " + appearance + " >>", null),
            (7, "<< /Subtype /Form /BBox [0 0 8 8] /Resources << >> /Length 22 >>", "1 0 0 rg 0 0 8 8 re f\n"u8.ToArray()),
            (8, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (9, "<< /Font << /F1 11 0 R /Unused 12 0 R >> " + xobjects + " >>", null),
            (10, "<< /Font << /F1 12 0 R /Unused 11 0 R >> " + xobjects + " >>", null),
            (11, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (12, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", null), (13, "null", null),
            (14, "<< /Type /Annot /Subtype /Stamp /Rect [20 20 80 50] /F 4 " + appearance + " >>", null),
            (15, "<< /Subtype /Form /BBox [0 0 1 1] /Matrix [1 0 0 1 17 23] /Length 3 >>", "q Q"u8.ToArray()),
            (16, "null", null), (17, "<< /Font << /F1 11 0 R >> /XObject << /Fx0 7 0 R >> >>", null),
            (18, "<< /On 5 0 R >>", null)], 1);
    }

    private static QpdfObject Get(QpdfDocument document, PdfDictionary node, string key)
    { Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value; }
    private static PdfDictionary Dict(QpdfDocument document, PdfDictionary node, string key) => Assert.IsType<PdfDictionary>(Get(document, node, key));
    private static PdfStream Appearance(QpdfDocument document, PdfDictionary annotation, string shape)
    {
        PdfDictionary ap = Dict(document, annotation, "AP"); QpdfObject normal = Get(document, ap, "N");
        return shape == "stream" ? Assert.IsType<PdfStream>(normal) : Assert.IsType<PdfStream>(Get(document, Assert.IsType<PdfDictionary>(normal), "On"));
    }

    public static IEnumerable<object[]> RetainedCases()
    {
        foreach (string resources in new[] { "absent", "null", "reference-null", "local", "reference-local" })
        foreach (string shape in new[] { "stream", "states", "reference-states" })
        foreach (string operation in new[] { "prune", "overlay", "underlay", "flatten", "combined" })
        { yield return [resources, shape, operation, false]; }
        yield return ["reference-local", "reference-states", "combined", true];
        yield return ["reference-null", "states", "underlay", true];
        yield return ["local", "stream", "prune", true];
    }

    [Theory]
    [MemberData(nameof(RetainedCases))]
    public async Task RetainedAppearancesKeepTheirOwnPageScopeAndAvoidPlacementNames(string resources, string shape, string operation, bool job)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(resources, shape), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), PdfTestDocument.Assemble(PdfTestDocument.SinglePage("<< /Length 3 >>", "q Q"u8.ToArray()), 1), QpdfInputOwnership.Transfer);
        bool local = resources is "local" or "reference-local", flatten = operation is "flatten" or "combined", prune = operation is "prune" or "combined";
        List<string> argv = ["input", "--compress-streams=n", "--decode-level=none"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["compressStreams"] = "n", ["decodeLevel"] = "none" };
        if (prune)
        {
            argv.AddRange(["--pages", ".", "1-2", "--", "--remove-unreferenced-resources=yes"]);
            settings["pages"] = new[] { new { file = ".", range = "1-2" } }; settings["removeUnreferencedResources"] = "yes";
        }
        if (operation != "prune")
        {
            string placement = operation == "underlay" ? "underlay" : "overlay";
            argv.AddRange(["--" + placement, "stamp", "--repeat=1", "--"]); settings[placement] = new[] { new Dictionary<string, string> { ["file"] = "stamp", ["repeat"] = "1" } };
        }
        if (flatten) { argv.Add("--flatten-annotations=all"); settings["flattenAnnotations"] = "all"; }
        argv.Add("output");
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary catalog = Dict(document, document.Engine.Trailer, "Root"); PdfStream original = Assert.IsType<PdfStream>(Get(document, catalog, "OriginalAP"));
        Assert.Equal(Program, original.RawMemory.ToArray());
        Assert.Equal(local, original.Dictionary.TryGet(PdfName.FromAscii("Resources"), out QpdfObject originalResources)
            && (originalResources is PdfReference link ? document.GetObject(link.ObjectNumber) : originalResources) is PdfDictionary);
        for (int index = 0; index < document.PageReferences.Count; index++)
        {
            PdfDictionary page = document.PageReferences[index].Page, pageResources = Dict(document, page, "Resources");
            if (!local)
            {
                PdfDictionary fonts = Dict(document, pageResources, "Font"); PdfDictionary font = Dict(document, fonts, "F1");
                Assert.Equal(PdfName.FromAscii(index == 0 ? "Helvetica" : "Courier"), Get(document, font, "BaseFont"));
                PdfDictionary objects = Dict(document, pageResources, "XObject"); PdfStream legacy = Assert.IsType<PdfStream>(Get(document, objects, "Fx0"));
                Assert.Equal("1 0 0 rg 0 0 8 8 re f\n"u8.ToArray(), legacy.RawMemory.ToArray());
                if (operation != "prune" && !flatten)
                {
                    PdfStream content = Assert.IsType<PdfStream>(Get(document, page, "Contents"));
                    string program = Encoding.ASCII.GetString(content.RawMemory.Span);
                    for (int reserved = 0; reserved < 4; reserved++)
                    { Assert.DoesNotContain("/Fx" + reserved + " Do", program); }
                }
            }
            if (flatten)
            {
                Assert.False(page.TryGet(PdfName.FromAscii("Annots"), out _));
                AssertFlattenedAppearance(document, page, pageResources, index == 0 || local ? "Helvetica" : "Courier");
                continue;
            }
            PdfArray annotations = Assert.IsType<PdfArray>(Get(document, page, "Annots")); PdfReference annotLink = Assert.IsType<PdfReference>(annotations[0]);
            PdfDictionary annotation = Assert.IsType<PdfDictionary>(document.GetObject(annotLink.ObjectNumber));
            Assert.Same(original, Appearance(document, annotation, shape));
        }
    }

    private static void AssertFlattenedAppearance(QpdfDocument document, PdfDictionary page, PdfDictionary pageResources, string expectedFont)
    {
        QpdfObject contents = Get(document, page, "Contents");
        IEnumerable<PdfStream> streams = contents is PdfArray sequence
            ? sequence.Items.Select(item => Assert.IsType<PdfStream>(item is PdfReference reference ? document.GetObject(reference.ObjectNumber) : item))
            : [Assert.IsType<PdfStream>(contents)];
        string program = string.Join('\n', streams.Select(stream => Encoding.ASCII.GetString(stream.RawMemory.Span)));
        PdfDictionary objects = Dict(document, pageResources, "XObject");
        IEnumerable<PdfStream> painted = Regex.Matches(program, @"/([^\s/]+)\s+Do\b").Select(match =>
            Assert.IsType<PdfStream>(Get(document, objects, match.Groups[1].Value)));
        PdfStream appearance = Assert.Single(painted, form => form.RawMemory.Span.SequenceEqual(Program));
        PdfDictionary scope = pageResources;
        if (appearance.Dictionary.TryGet(PdfName.FromAscii("Resources"), out QpdfObject declared))
        {
            QpdfObject resolved = declared is PdfReference reference ? document.GetObject(reference.ObjectNumber) : declared;
            if (resolved is not PdfNull) { scope = Assert.IsType<PdfDictionary>(resolved); }
        }
        PdfDictionary font = Dict(document, Dict(document, scope, "Font"), "F1");
        Assert.Equal(PdfName.FromAscii(expectedFont), Get(document, font, "BaseFont"));
        PdfStream nested = Assert.IsType<PdfStream>(Get(document, Dict(document, scope, "XObject"), "Fx0"));
        Assert.Equal("1 0 0 rg 0 0 8 8 re f\n"u8.ToArray(), nested.RawMemory.ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PaintAssertionObservesTheInvocationAndTheFormScope(bool missingPaint, bool wrongFont)
    {
        byte[] paint = missingPaint ? "q Q"u8.ToArray() : "/Placed Do"u8.ToArray();
        byte[] square = "1 0 0 rg 0 0 8 8 re f\n"u8.ToArray();
        byte[] input = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Placed 5 0 R >> >> /Contents 4 0 R >>", null),
            (4, $"<< /Length {paint.Length} >>", paint),
            (5, $"<< /Subtype /Form /BBox [0 0 40 20] /Resources << /Font << /F1 6 0 R >> /XObject << /Fx0 7 0 R >> >> /Length {Program.Length} >>", Program),
            (6, $"<< /Type /Font /Subtype /Type1 /BaseFont /{(wrongFont ? "Courier" : "Helvetica")} >>", null),
            (7, $"<< /Subtype /Form /BBox [0 0 8 8] /Resources << >> /Length {square.Length} >>", square)
        ], 1);
        using InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("ap.pdf");
        host.AddInput(path, input, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, path, CancellationToken.None);
        PdfDictionary page = document.PageReferences[0].Page;
        Action observe = () => AssertFlattenedAppearance(document, page, Dict(document, page, "Resources"), "Helvetica");
        if (missingPaint || wrongFont) { Assert.ThrowsAny<Xunit.Sdk.XunitException>(observe); }
        else { observe(); }
    }

    public static IEnumerable<object[]> ImportedCases()
    {
        foreach (string resources in new[] { "absent", "null", "reference-null", "local", "reference-local" })
        foreach (string shape in new[] { "stream", "states", "reference-states" })
        foreach (bool underlay in new[] { false, true })
        { yield return [resources, shape, underlay, false]; }
        yield return ["reference-null", "reference-states", false, true];
        yield return ["local", "stream", true, true];
    }

    [Theory]
    [MemberData(nameof(ImportedCases))]
    public async Task ImportedAppearancesCarryTheSourcePageScopeAndKeepPrivateAPValues(string resources, string shape, bool underlay, bool job)
    {
        PdfStream escaped;
        using (InMemoryQpdfHost host = new())
        {
            byte[] input = PdfTestDocument.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> >>", null),
                (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> >>", null)], 1);
            host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
            host.AddInput(QpdfPath.From("stamp"), Fixture(resources, shape), QpdfInputOwnership.Transfer);
            string placement = underlay ? "underlay" : "overlay";
            Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["compressStreams"] = "n", [placement] = new[] { new { file = "stamp" } } };
            QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--compress-streams=n", "--" + placement, "stamp", "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
            PdfStream? first = null;
            for (int index = 0; index < document.PageReferences.Count; index++)
            {
                PdfDictionary page = document.PageReferences[index].Page;
                PdfArray annotations = Assert.IsType<PdfArray>(Get(document, page, "Annots")); PdfReference link = Assert.IsType<PdfReference>(annotations[0]);
                PdfDictionary annotation = Assert.IsType<PdfDictionary>(document.GetObject(link.ObjectNumber));
                PdfStream appearance = Appearance(document, annotation, shape);
                PdfDictionary own = Dict(document, appearance.Dictionary, "Resources"); PdfDictionary fonts = Dict(document, own, "Font"); PdfDictionary font = Dict(document, fonts, "F1");
                bool local = resources is "local" or "reference-local";
                Assert.Equal(PdfName.FromAscii(index == 0 || local ? "Helvetica" : "Courier"), Get(document, font, "BaseFont"));
                if (first is not null) { Assert.NotSame(first, appearance); } first = appearance;
                PdfDictionary ap = Dict(document, annotation, "AP"); PdfStream privateValue = Assert.IsType<PdfStream>(Get(document, ap, "Private"));
                PdfArray matrix = Assert.IsType<PdfArray>(Get(document, privateValue.Dictionary, "Matrix"));
                Assert.Equal(17L, Assert.IsType<PdfInteger>(matrix[4]).Value); Assert.Equal(23L, Assert.IsType<PdfInteger>(matrix[5]).Value);
                Assert.False(privateValue.Dictionary.TryGet(PdfName.FromAscii("Resources"), out _));
            }
            escaped = Assert.IsType<PdfStream>(first);
        }
        Assert.Equal(Program, escaped.RawMemory.ToArray());
        Assert.True(escaped.Dictionary.TryGet(PdfName.FromAscii("Resources"), out _));
    }

    private sealed class CancelingResolver(CancellationTokenSource cancellation, PdfDictionary annotation) : IIndirectResolver
    {
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        { cancellation.Cancel(); value = annotation; return true; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PageScopeDecisionChecksBothCancellationRoutesAfterResolution(bool caller)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        PdfDictionary page = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Annots")] = new PdfArray([new PdfReference(1, 0)]) });
        PdfDictionary annotation = new(new Dictionary<PdfName, QpdfObject>());
        Assert.ThrowsAny<OperationCanceledException>(() => AppearanceResources.PageNeedsResources(page, new CancelingResolver(cancellation, annotation), governor,
            caller ? cancellation.Token : CancellationToken.None));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private static async Task<InMemoryQpdfHost> ScopeStorage(bool imported, QpdfPath output)
    {
        byte[] blank = PdfTestDocument.Assemble(PdfTestDocument.SinglePage("<< /Length 3 >>", "q Q"u8.ToArray()), 1);
        byte[] withAppearance = Fixture("reference-null", "reference-states");
        InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(imported ? blank : withAppearance, output);
        host.AddInput(QpdfPath.From("stamp"), imported ? withAppearance : blank, QpdfInputOwnership.Transfer);
        return host;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public Task ScopePublicationFailuresPreserveTheTargetAndRefundAdmission(bool imported, bool job)
    {
        QpdfPath target = QpdfPath.From("output");
        return PreparationAdmissionChecks.VerifyAsync(() => ScopeStorage(imported, target),
            (host, token) => job
                ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","overlay":[{"file":"stamp","repeat":"1"}]}""", token)
                : Qpdf.ExecuteAsync(host, new(["input", "--overlay", "stamp", "--repeat=1", "--", "output"]), token),
            target, 96, ["write", "commit", "cancel"]);
    }

    [Fact]
    public async Task ScopePreparationLocalLimitKeepsThePriorOutput()
    {
        QpdfPath target = QpdfPath.From("output");
        using InMemoryQpdfHost host = await ScopeStorage(true, target);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096 };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--overlay", "stamp", "--repeat=1", "--", target.Value]), new(limits), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(host.TryGetOutput(target, out byte[] bytes));
        Assert.Equal(new byte[] { 17, 23, 41 }, bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }
}
