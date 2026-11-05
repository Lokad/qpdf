using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class RotationDestinationContextTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PlacementDestinationsStayPrunedWhileTargetNavigationAndFieldsFollowBaking(bool underlay, bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Source("explicit"), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("placed"), Source("string"), QpdfInputOwnership.Transfer);
        Dictionary<string, object> request = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["flattenRotation"] = true,
            [underlay ? "underlay" : "overlay"] = new { file = "placed" } };
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(request), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", underlay ? "--underlay" : "--overlay", "placed", "--", "--flatten-rotation", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary catalog = RotationDestinationTests.Dict(document, document.Engine.Trailer, "Root");
        PdfDictionary form = RotationDestinationTests.Dict(document, catalog, "AcroForm");
        PdfArray fields = Assert.IsType<PdfArray>(RotationDestinationTests.Value(document, form, "Fields"));
        PdfArray order = Assert.IsType<PdfArray>(RotationDestinationTests.Value(document, form, "CO"));
        int placed = 0, primary = 0;
        foreach (var page in document.PageReferences)
        {
            PdfArray annotations = Assert.IsType<PdfArray>(RotationDestinationTests.Value(document, page.Page, "Annots"));
            for (int i = 0; i < annotations.Count; i++)
            {
                PdfDictionary widget = Assert.IsType<PdfDictionary>(RotationDestinationTests.Resolve(document, annotations[i]));
                Assert.Contains(Enumerable.Range(0, fields.Count), n => Equals(fields[n], annotations[i]));
                PdfArray original = Assert.IsType<PdfArray>(RotationDestinationTests.Value(document, widget, "Private"));
                RotationDestinationTests.AssertDestination(document, original, "XYZ", [25, 60, 1]);
                PdfDictionary action = RotationDestinationTests.Dict(document, widget, "A");
                PdfArray destination = Destination(document, catalog, RotationDestinationTests.Value(document, action, "D"));
                if (RotationDestinationTests.Resolve(document, destination[0]) is PdfNull)
                {
                    // Placement imports fields, while calculation order stays
                    // with the primary document, matching native qpdf.
                    Assert.DoesNotContain(Enumerable.Range(0, order.Count), n => Equals(order[n], annotations[i]));
                    placed++; Assert.IsType<PdfNull>(destination[0]);
                    RotationDestinationTests.AssertDestination(document, destination, "XYZ", [25, 60, 1]);
                }
                else
                {
                    Assert.Contains(Enumerable.Range(0, order.Count), n => Equals(order[n], annotations[i]));
                    primary++; Assert.Equal(page.Source, destination[0]);
                    double[] expected = RotationDestinationTests.Expected("XYZ", Marker(document, widget) == "First" ? 90 : 270); expected[2] = 1;
                    RotationDestinationTests.AssertDestination(document, destination, "XYZ", expected);
                }
            }
        }
        Assert.Equal(2, primary); Assert.Equal(2, placed);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string mode in new[] { "whole", "reorder", "repeat", "merge", "split", "merge-split", "packed", "linearized", "qdf" })
        foreach (string naming in new[] { "explicit", "legacy", "string" })
        foreach (bool job in new[] { false, true }) { yield return [mode, naming, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FieldRegistrationsAndCanonicalTargetsSurviveAssemblyAndSplit(string mode, string naming, bool job)
    {
        RecordingMemoryBudget budget = new(32L << 20);
        using (IMemoryScope owner = budget.CreateScope())
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
            host.AddInput(QpdfPath.From("input"), Source(naming), QpdfInputOwnership.Transfer);
            host.AddInput(QpdfPath.From("other"), Source(naming), QpdfInputOwnership.Transfer);
            host.AddInput(QpdfPath.From("target"), CheckTests.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null)], 1), QpdfInputOwnership.Transfer);
            List<string> arguments = [mode == "merge" ? "target" : "input", "--flatten-rotation"];
            Dictionary<string, object> request = new() { ["inputFile"] = arguments[0], ["outputFile"] = "out.pdf", ["flattenRotation"] = true };
            if (mode == "merge")
            { arguments.AddRange(["--pages", ".", "1", "input", "1-2", "--"]); request["pages"] = new[] { new { file = ".", range = "1" }, new { file = "input", range = "1-2" } }; }
            if (mode is "reorder" or "repeat")
            {
                string range = mode == "reorder" ? "2,1" : "1,1,2";
                arguments.AddRange(["--pages", ".", range, "--"]); request["pages"] = new[] { new { file = ".", range } };
                if (mode == "repeat") { arguments.Add("--rotate=+90:2"); request["rotate"] = new[] { "+90:2" }; }
            }
            if (mode == "split") { arguments.Add("--split-pages=1"); request["splitPages"] = "1"; }
            if (mode == "merge-split")
            {
                arguments.AddRange(["--pages", ".", "1", "other", "1-2", "--", "--split-pages=1"]);
                request["pages"] = new[] { new { file = ".", range = "1" }, new { file = "other", range = "1-2" } }; request["splitPages"] = "1";
            }
            if (mode == "packed") { arguments.Add("--object-streams=generate"); request["objectStreams"] = "generate"; }
            if (mode == "linearized") { arguments.Add("--linearize"); request["linearize"] = true; }
            if (mode == "qdf") { arguments.Add("--qdf"); request["qdf"] = true; }
            arguments.Add("out.pdf");
            QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(request), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            QpdfObject? escaped = null; double escapedValue = 0;
            foreach (string path in mode == "merge-split" ? new[] { "out-1.pdf", "out-2.pdf", "out-3.pdf" }
                : mode == "split" ? new[] { "out-1.pdf", "out-2.pdf" } : new[] { "out.pdf" })
            {
                Assert.True(host.TryGetOutput(QpdfPath.From(path), out byte[] output));
                host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
                PdfDictionary catalog = RotationDestinationTests.Dict(document, document.Engine.Trailer, "Root");
                PdfDictionary form = RotationDestinationTests.Dict(document, catalog, "AcroForm");
                PdfArray fields = Assert.IsType<PdfArray>(RotationDestinationTests.Value(document, form, "Fields"));
                PdfArray order = Assert.IsType<PdfArray>(RotationDestinationTests.Value(document, form, "CO"));
                Dictionary<string, PdfReference> canonical = new(StringComparer.Ordinal);
                foreach (var page in document.PageReferences)
                {
                    if (!page.Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject raw)) { continue; }
                    PdfArray annotations = Assert.IsType<PdfArray>(RotationDestinationTests.Resolve(document, raw));
                    PdfDictionary widget = Assert.IsType<PdfDictionary>(RotationDestinationTests.Resolve(document, annotations[0]));
                    string marker = Marker(document, widget);
                    canonical.TryAdd(marker, page.Source ?? throw new InvalidDataException("Missing output page identity."));
                }
                foreach (var page in document.PageReferences)
                {
                    if (!page.Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject raw)) { continue; }
                    PdfArray annotations = Assert.IsType<PdfArray>(RotationDestinationTests.Resolve(document, raw));
                    PdfDictionary widget = Assert.IsType<PdfDictionary>(RotationDestinationTests.Resolve(document, annotations[0]));
                    string marker = Marker(document, widget); int angle = marker == "First" ? 90 : 270;
                    Assert.Contains(Enumerable.Range(0, fields.Count), i => Equals(fields[i], annotations[0]));
                    Assert.Contains(Enumerable.Range(0, order.Count), i => Equals(order[i], annotations[0]));
                    PdfDictionary action = RotationDestinationTests.Dict(document, widget, "A");
                    Assert.Same(action, RotationDestinationTests.Value(document, RotationDestinationTests.Dict(document, widget, "AA"), "K"));
                    Assert.Same(action, RotationDestinationTests.Value(document, action, "Next"));
                    PdfArray destination = Destination(document, catalog, RotationDestinationTests.Value(document, action, "D"));
                    Assert.Equal(canonical[marker], destination[0]);
                    double[] expected = RotationDestinationTests.Expected("XYZ", angle); expected[2] = 1;
                    RotationDestinationTests.AssertDestination(document, destination, "XYZ", expected);
                    RotationDestinationTests.AssertDestination(document, RotationDestinationTests.Value(document, widget, "Private"), "XYZ", [25, 60, 1]);
                    escaped = RotationDestinationTests.Resolve(document, destination[2]); escapedValue = expected[0];
                }
            }
            Assert.Equal(0L, host.OpenLeaseCount);
            host.Dispose(); Assert.NotNull(escaped); Assert.True(budget.ReservedBytes > 0);
            Assert.Equal(escapedValue, escaped is PdfInteger integer ? integer.Value : Assert.IsType<PdfReal>(escaped).Value);
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    private static string Marker(QpdfDocument document, PdfDictionary widget)
        => Encoding.ASCII.GetString(Assert.IsType<PdfName>(RotationDestinationTests.Value(document, widget, "Marker")).RawSpan);

    private static PdfArray Destination(QpdfDocument document, PdfDictionary catalog, QpdfObject raw)
    {
        QpdfObject value = RotationDestinationTests.Resolve(document, raw);
        if (value is PdfName name)
        { value = RotationDestinationTests.Value(document, RotationDestinationTests.Dict(document, catalog, "Dests"), Encoding.ASCII.GetString(name.RawSpan)); }
        else if (value is PdfByteString text)
        {
            PdfDictionary tree = RotationDestinationTests.Dict(document, RotationDestinationTests.Dict(document, catalog, "Names"), "Dests");
            PdfArray names = Assert.IsType<PdfArray>(RotationDestinationTests.Value(document, tree, "Names"));
            value = names[Enumerable.Range(0, names.Count / 2).Single(i => Assert.IsType<PdfByteString>(RotationDestinationTests.Resolve(document, names[i * 2])).ValueSpan.SequenceEqual(text.ValueSpan)) * 2 + 1];
            value = RotationDestinationTests.Resolve(document, value);
        }
        if (value is PdfDictionary wrapper) { value = RotationDestinationTests.Value(document, wrapper, "D"); }
        return Assert.IsType<PdfArray>(value);
    }

    internal static byte[] Source(string naming)
    {
        string first = naming == "legacy" ? "/mark" : naming == "string" ? "(landing)" : "9 0 R";
        string second = naming == "legacy" ? "/other" : naming == "string" ? "(second)" : "10 0 R";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Private 4 0 R /AcroForm << /Fields [4 0 R 7 0 R] /CO [4 0 R 7 0 R] >> /Dests << /mark 9 0 R /other 10 0 R >> /Names << /Dests << /Names [(landing) 9 0 R (second) 10 0 R] >> >> >>", null),
            (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 5 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [10 20 110 220] /Resources << >> /Rotate 90 /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (first) /Marker /First /Rect [25 60 75 120] /P 3 0 R /A 6 0 R /AA << /K 6 0 R >> /Private 9 0 R >>", null),
            (5, "<< /Type /Page /Parent 2 0 R /MediaBox [10 20 110 220] /Resources << >> /Rotate 270 /Annots [7 0 R] >>", null),
            (6, "<< /S /GoTo /D " + first + " /Next 6 0 R >>", null),
            (7, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (second) /Marker /Second /Rect [25 60 75 120] /P 5 0 R /A 8 0 R /AA << /K 8 0 R >> /Private 10 0 R >>", null),
            (8, "<< /S /GoTo /D " + second + " /Next 8 0 R >>", null),
            (9, "[3 0 R /XYZ 25 60 1]", null), (10, "[5 0 R /XYZ 25 60 1]", null)], 1);
    }
}
