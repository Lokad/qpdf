using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class IndirectRotationTests
{
    internal static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output");

    internal static byte[] Fixture(string rotation, string target, bool inherited)
    {
        string parent = inherited ? " /Rotate " + rotation : string.Empty;
        string page = inherited ? string.Empty : " /Rotate " + rotation;
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R]" + parent + " >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R /Annots [6 0 R]" + page + " >>", null),
            (4, "<< /Length 4 >>", "q Q\n"u8.ToArray()),
            (5, target, null),
            (6, "<< /Type /Annot /Subtype /Square /Rect [10 20 30 40] /F 20 /AP << /N 7 0 R >> >>", null),
            (7, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Resources << >> /Length 15 >>", "0 0 20 20 re f\n"u8.ToArray()),
        ], 1);
    }

    public static IEnumerable<object[]> RotationCases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (bool inherited in new[] { false, true })
        foreach (bool indirect in new[] { false, true })
        foreach (PdfRotationMode mode in Enum.GetValues<PdfRotationMode>())
        { yield return [route, inherited, indirect, mode]; }
    }

    [Theory]
    [MemberData(nameof(RotationCases))]
    public async Task RotationsFoldOverEquivalentLocalAndInheritedIntegers(
        string route, bool inherited, bool indirect, PdfRotationMode mode)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture(indirect ? "5 0 R" : "90", "90", inherited), QpdfInputOwnership.Copy);
        string specification = mode switch
        {
            PdfRotationMode.Set => "270", PdfRotationMode.Add => "+90", PdfRotationMode.Subtract => "-90",
            _ => throw new ArgumentException(nameof(mode)),
        };
        if (route == "typed")
        {
            _ = await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1),
                [new PdfPageRotation(mode, mode == PdfRotationMode.Set ? 270 : 90, null)], CancellationToken.None);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new([Input.Value, "--pages", ".", "1", "--", "--rotate=" + specification, Output.Value]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"pages\":[{\"file\":\".\",\"range\":\"1\"}],\"rotate\":[\"" + specification + "\"]}", CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.Empty(result.Diagnostics);
        }
        byte[] output = Published(host);
        host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
        long expected = mode switch { PdfRotationMode.Set => 270L, PdfRotationMode.Add => 180L, _ => 0L };
        Assert.Equal(expected, Assert.IsType<PdfInteger>(Value(document.PageReferences[0].Page, "Rotate")).Value);
    }

    [Theory]
    [InlineData("450")]
    [InlineData("-270")]
    [InlineData("4294967490")]
    public async Task IndirectAnglesUseTheExistingLongNormalization(string angle)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture("5 0 R", angle, true), QpdfInputOwnership.Copy);
        _ = await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1),
            [new PdfPageRotation(PdfRotationMode.Add, 90, null)], CancellationToken.None);
        host.AddInput(QpdfPath.From("readback"), Published(host), QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
        Assert.Equal(180L, Assert.IsType<PdfInteger>(Value(document.PageReferences[0].Page, "Rotate")).Value);
    }

    public static IEnumerable<object[]> FlattenCases()
    {
        foreach (bool job in new[] { false, true })
        foreach (bool inherited in new[] { false, true })
        foreach (bool indirect in new[] { false, true })
        foreach (string operation in new[] { "rotation", "annotations" })
        { yield return [job, inherited, indirect, operation]; }
    }

    [Theory]
    [MemberData(nameof(FlattenCases))]
    public async Task FlatteningUsesTheEffectiveRotation(bool job, bool inherited, bool indirect, string operation)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture(indirect ? "5 0 R" : "90", "90", inherited), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Flatten(host, operation, job, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        host.AddInput(QpdfPath.From("readback"), Published(host), QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
        PdfDictionary page = document.PageReferences[0].Page;
        string contents = Contents(document, page);
        if (operation == "rotation")
        {
            Assert.False(page.TryGet(PdfName.FromAscii("Rotate"), out _));
            Assert.Contains("0 -1 1 0 0 100 cm", contents, StringComparison.Ordinal);
        }
        else
        {
            Assert.False(page.TryGet(PdfName.FromAscii("Annots"), out _));
            Assert.Contains("0 1 -1 0 30 40 cm", contents, StringComparison.Ordinal);
        }
    }

    public static IEnumerable<object[]> MalformedCases()
    {
        foreach (string operation in new[] { "add", "rotation", "annotations" })
        foreach ((string rotation, string target, string? failure) in new (string, string, string?)[]
        {
            ("null", "90", null), ("5 0 R", "null", null),
            ("(bad)", "90", operation == "add" ? "invalid /Rotate" : null),
            ("5 0 R", "(bad)", operation == "add" ? "invalid /Rotate" : null),
            ("99 0 R", "90", "Unresolvable reference"),
            ("5 0 R", "5 0 R", "reference chain exceeds the nesting limit"),
        })
        { yield return [operation, rotation, target, failure ?? string.Empty]; }
    }

    [Theory]
    [MemberData(nameof(MalformedCases))]
    public async Task NullAndMalformedPoliciesRemainBounded(string operation, string rotation, string target, string failure)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture(rotation, target, false), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = operation == "add"
            ? await Qpdf.ExecuteAsync(host, new([Input.Value, "--pages", ".", "1", "--", "--rotate=+90", Output.Value]), CancellationToken.None)
            : await Flatten(host, operation, false, CancellationToken.None);
        Assert.Equal(0, host.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
        if (failure.Length > 0)
        {
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
            Assert.Contains(failure, diagnostic.Message, StringComparison.Ordinal);
            Assert.Equal("prior"u8.ToArray(), Published(host));
        }
        else
        {
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            host.AddInput(QpdfPath.From("readback"), Published(host), QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
            PdfDictionary page = document.PageReferences[0].Page;
            if (operation == "add")
            { Assert.Equal(90L, Assert.IsType<PdfInteger>(Value(page, "Rotate")).Value); }
            else if (operation == "annotations")
            { Assert.Contains("1 0 0 1 10 20 cm", Contents(document, page), StringComparison.Ordinal); }
            else
            { Assert.IsType<PdfReference>(Value(page, "Contents")); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UntouchedIndirectValuesAndStreamsOutliveTheirDocument(bool inherited)
    {
        PdfInteger angle; PdfStream stream;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(Input, Fixture("5 0 R", "90", inherited), QpdfInputOwnership.Copy);
            _ = await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), CancellationToken.None);
            host.AddInput(QpdfPath.From("readback"), Published(host), QpdfInputOwnership.Copy);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
            {
                PdfDictionary page = document.PageReferences[0].Page;
                PdfReference reference = Assert.IsType<PdfReference>(Value(page, "Rotate"));
                angle = Assert.IsType<PdfInteger>(document.GetObject(reference.ObjectNumber));
                stream = Assert.IsType<PdfStream>(Resolve(document, Value(page, "Contents")));
            }
            Assert.Equal(0, host.OpenLeaseCount);
        }
        Assert.Equal(90L, angle.Value);
        Assert.Equal("q Q\n"u8.ToArray(), stream.Data);
    }

    internal static ValueTask<QpdfExecutionResult> Flatten(IQpdfHost host, string operation, bool job, CancellationToken token)
        => job
            ? Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\",\""
                + (operation == "rotation" ? "flattenRotation\":\"\"" : "flattenAnnotations\":\"all\"")
                + ",\"streamData\":\"uncompress\"}", token)
            : Qpdf.ExecuteAsync(host, new([Input.Value, operation == "rotation" ? "--flatten-rotation" : "--flatten-annotations=all",
                "--stream-data=uncompress", Output.Value]), token);

    private static byte[] Published(InMemoryQpdfHost host)
    {
        Assert.Equal(0, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(Output, out byte[] output));
        return output;
    }

    private static QpdfObject Value(PdfDictionary dictionary, string name)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return value; }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    private static string Contents(QpdfDocument document, PdfDictionary page)
        => string.Concat(Assert.IsType<PdfArray>(Resolve(document, Value(page, "Contents"))).Items.Select(item =>
            Encoding.ASCII.GetString(Assert.IsType<PdfStream>(Resolve(document, item)).Data)));
}
