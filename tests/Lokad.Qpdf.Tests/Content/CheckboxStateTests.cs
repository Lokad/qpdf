using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class CheckboxStateTests
{
    private static byte[] Fixture(string states, string value, bool separate, bool shared, bool siblings)
    {
        byte[] red = "1 0 0 rg 0 0 20 20 re f % A\n"u8.ToArray(), green = "0 1 0 rg 0 0 20 20 re f % B\n"u8.ToArray();
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [" + (separate ? "8" : "4") + " 0 R] /NeedAppearances true >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R" + (shared ? " 9 0 R" : "") + "] /Count " + (shared ? "2" : "1") + " >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R" + (siblings ? " 10 0 R" : "") + "] >>", null),
            (4, "<< /Type /Annot /Subtype /Widget /Rect [10 20 30 40] /F 4 " + (separate ? "/Parent 8 0 R" : "/FT /Btn /T (C) /V /" + value)
                + " /AS /Off /AP << /N " + states + " >> >>", null),
            (5, "<< /Subtype /Form /BBox [0 0 20 20] /Resources << >> /Length 0 >>", []),
            (6, "<< /Subtype /Form /BBox [0 0 20 20] /Resources << >> /Length " + red.Length + " >>", red),
            (7, "<< /Subtype /Form /BBox [0 0 20 20] /Resources << >> /Length " + green.Length + " >>", green),
            (8, "<< /FT /Btn /T (C) /V /" + value + " /Kids [4 0 R" + (siblings ? " 10 0 R" : "") + "] >>", null),
            (9, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R] >>", null),
            (10, "<< /Subtype /Widget /Parent 8 0 R /Rect [50 20 70 40] /F 4 /AS /Off /AP << /N << /Off 5 0 R /B 7 0 R >> >> >>", null),
            (11, "null", null)], 1);
    }

    private static IEnumerable<object[]> Inputs()
    {
        yield return ["<< /Off 5 0 R /A 6 0 R /B 7 0 R >>", "B", "B", "B", false];
        yield return ["<< /B 7 0 R /A 6 0 R /Off 5 0 R >>", "A", "A", "A", false];
        yield return ["<< /Off 5 0 R /A null /B 7 0 R >>", "B", "B", "B", false];
        yield return ["<< /Off 5 0 R /A 11 0 R /B 7 0 R >>", "Initial", "B", "B", false];
        yield return ["<< /Off 5 0 R /A 42 /B 7 0 R >>", "Initial", "B", "B", false];
        yield return ["<< /Off 5 0 R /A 6 0 R /B 7 0 R >>", "Initial", "Initial", "Off", true];
        yield return ["<< /B 7 0 R /A 6 0 R /Off 5 0 R >>", "Initial", "Initial", "Off", true];
        yield return ["<< /Off 5 0 R /A null /B 11 0 R >>", "Initial", "Initial", "Off", true];
        yield return ["<< /Off 5 0 R >>", "Initial", "Initial", "Off", true];
        yield return ["6 0 R", "Initial", "Initial", "Off", true];
        yield return ["<< /Off 5 0 R /A 6 0 R /B 7 0 R >>", "Off", "Off", "Off", false];
        yield return ["<< /Off 5 0 R /A#23B 6 0 R /B 7 0 R >>", "A#23B", "A#23B", "A#23B", false];
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (object[] row in Inputs())
        foreach (bool job in new[] { false, true }) { yield return row.Concat(new object[] { job, false, false }).ToArray(); }
        foreach (object[] row in Inputs().Take(6)) { yield return row.Concat(new object[] { true, true, true }).ToArray(); }
    }

    private static QpdfObject Get(QpdfDocument document, PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    }

    private static Task<QpdfExecutionResult> Run(InMemoryQpdfHost host, bool job, bool flatten)
    {
        List<string> argv = ["input", "--generate-appearances", "--compress-streams=n"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["generateAppearances"] = true, ["compressStreams"] = "n" };
        if (flatten) { argv.Add("--flatten-annotations=all"); settings["flattenAnnotations"] = "all"; }
        argv.Add("output");
        return job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None).AsTask()
            : Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None).AsTask();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task StateSelectionUsesValuesAndStreamBackedAppearances(string states, string value, string expectedValue,
        string expectedState, bool skipped, bool job, bool separate, bool shared)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(states, value, separate, shared, false), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, job, false);
        Assert.Equal(skipped ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        if (skipped) { Assert.Contains(result.Diagnostics, d => d.Message.Contains("appearance state", StringComparison.Ordinal)); }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        List<PdfName> escaped = [];
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
        {
            PdfDictionary form = Assert.IsType<PdfDictionary>(Get(document, Assert.IsType<PdfDictionary>(document.GetObject(1)), "AcroForm"));
            bool needs = form.TryGet(PdfName.FromAscii("NeedAppearances"), out QpdfObject raw) && raw is PdfBoolean flag && flag.Value;
            Assert.Equal(skipped, needs);
            foreach (var location in document.PageReferences)
            {
                PdfArray annots = Assert.IsType<PdfArray>(Get(document, location.Page, "Annots"));
                PdfDictionary widget = Assert.IsType<PdfDictionary>(annots[0] is PdfReference reference ? document.GetObject(reference.ObjectNumber) : annots[0]);
                PdfName state = Assert.IsType<PdfName>(Get(document, widget, "AS")); escaped.Add(state);
                PdfDictionary field = separate ? Assert.IsType<PdfDictionary>(Get(document, widget, "Parent")) : widget;
                Assert.Equal(expectedValue, Assert.IsType<PdfName>(Get(document, field, "V")).DisplayText);
            }
        }
        host.Dispose(); Assert.Equal(shared ? 2 : 1, escaped.Count);
        Assert.All(escaped, state => Assert.Equal(expectedState, state.DisplayText));
    }

    [Fact]
    public async Task AmbiguousStatesKeepReadinessAndSurviveLaterFlattening()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("<< /Off 5 0 R /A 6 0 R /B 7 0 R >>", "Initial", false, false, false), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, true, true); Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfArray annots = Assert.IsType<PdfArray>(Get(document, document.PageReferences[0].Page, "Annots")); Assert.Equal(1, annots.Count);
        PdfDictionary widget = Assert.IsType<PdfDictionary>(annots[0] is PdfReference reference ? document.GetObject(reference.ObjectNumber) : annots[0]);
        Assert.Equal("Initial", Assert.IsType<PdfName>(Get(document, widget, "V")).DisplayText);
        Assert.Equal("Off", Assert.IsType<PdfName>(Get(document, widget, "AS")).DisplayText);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SharedFieldValueDoesNotChangeWhileSynchronizingSiblingWidgets(bool job, bool flatten)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("<< /Off 5 0 R /A 6 0 R >>", "B", true, false, true), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, job, flatten);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary page = document.PageReferences[0].Page;
        if (flatten)
        {
            PdfDictionary forms = Assert.IsType<PdfDictionary>(Get(document, Assert.IsType<PdfDictionary>(Get(document, page, "Resources")), "XObject"));
            string[] programs = forms.Keys.Select(name => System.Text.Encoding.ASCII.GetString(Assert.IsType<PdfStream>(Get(document, forms, name.DisplayText)).RawMemory.Span)).ToArray();
            Assert.DoesNotContain(programs, text => text.Contains("% A", StringComparison.Ordinal)); Assert.Contains(programs, text => text.Contains("% B", StringComparison.Ordinal));
        }
        else
        {
            PdfArray annots = Assert.IsType<PdfArray>(Get(document, page, "Annots"));
            string[] states = Enumerable.Range(0, annots.Count).Select(index =>
            {
                PdfDictionary widget = Assert.IsType<PdfDictionary>(annots[index] is PdfReference reference ? document.GetObject(reference.ObjectNumber) : annots[index]);
                Assert.Equal("B", Assert.IsType<PdfName>(Get(document, Assert.IsType<PdfDictionary>(Get(document, widget, "Parent")), "V")).DisplayText);
                return Assert.IsType<PdfName>(Get(document, widget, "AS")).DisplayText;
            }).ToArray();
            Assert.Equal(new[] { "Off", "B" }, states);
        }
    }
}
