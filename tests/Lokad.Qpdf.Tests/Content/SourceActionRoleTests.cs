using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class SourceActionRoleTests
{
    internal static byte[] Fixture(bool indirect, string basis)
    {
        string uri = "<< /S /URI /URI (item.pdf)" + (indirect ? " /Next 11 0 R" : "") + " >>";
        string go = "<< /S /GoTo /D (Here) >>";
        string action = indirect ? "8 0 R" : uri, destination = indirect ? "10 0 R" : go;
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /URI << /Base " + basis + " >> /Dests 13 0 R /Names << /Dests 14 0 R >> /AcroForm << /Fields [5 0 R] /CO [6 0 R 7 0 R] >> /Audit << /Field 5 0 R /Widget 6 0 R /OtherWidget 7 0 R /Action 8 0 R /GoTo 10 0 R /Private 12 0 R >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [6 0 R 9 0 R 18 0 R] /AA << /O " + destination + " /Private 8 0 R >> >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [7 0 R] >>", null),
            (5, "<< /FT /Tx /T (Group) /Kids [6 0 R 7 0 R] /AA << /C << /S /JavaScript /JS () /Next " + action + " >> /Private 8 0 R >> /A 12 0 R >>", null),
            (6, "<< /Type /Annot /Subtype /Widget /Rect [10 10 30 30] /P 3 0 R /Parent 5 0 R /T (First) /A " + action + " /AA << /U " + destination + " >> >>", null),
            (7, "<< /Type /Annot /Subtype /Widget /Rect [40 40 60 60] /P 4 0 R /Parent 5 0 R /T (Second) /A " + action + " >>", null),
            (8, uri, null),
            (9, "<< /Type /Annot /Subtype /Link /Rect [50 50 80 80] /Dest /Here >>", null),
            (10, go, null), (11, "[8 0 R 10 0 R]", null),
            (12, "<< /S /URI /URI (private.pdf) /D (Here) /A 8 0 R >>", null),
            (13, "<< /Here << /D [4 0 R /Fit] >> >>", null),
            (14, "<< /Kids [16 0 R] >>", null),
            (15, "[(Here) << /D [3 0 R /FitH 90] >>]", null),
            (16, "<< /Limits [17 0 R 17 0 R] /Names 15 0 R >>", null),
            (17, "(Here)", null),
            (18, "<< /Type /Annot /Subtype /Link /Rect [60 60 80 80] /Dest [3 0 R /Fit] >>", null)], 1);
    }

    private static byte[] Target() => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /URI << /Base (https://primary.test/) >> /Dests << /Here [3 0 R /Fit] >> >>", null),
        (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null)], 1);

    private static async Task Run(InMemoryQpdfHost host, string route, string mode)
    {
        if (route == "typed")
        {
            if (mode == "merge")
            { await Qpdf.SelectPagesAsync(host, [new(QpdfPath.From("target"), new(1)), new(QpdfPath.From("input"), PdfPageSelectionOptions.FromPages([1, 2]))], QpdfPath.From("output"), new PdfPageMergeOptions(), CancellationToken.None); }
            else { await Qpdf.SelectPageAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), PdfPageSelectionOptions.FromPages(mode == "repeat" ? [1, 1, 2] : [1, 2]), CancellationToken.None); }
            return;
        }
        string primary = mode == "merge" ? "target" : "input";
        List<string> argv = [primary]; Dictionary<string, object> job = new() { ["inputFile"] = primary, ["outputFile"] = "output" };
        if (mode == "merge")
        { argv.AddRange(["--pages", ".", "1", "input", "1-2", "--"]); job["pages"] = new[] { new { file = ".", range = "1" }, new { file = "input", range = "1-2" } }; }
        else if (mode == "repeat")
        { argv.AddRange(["--pages", ".", "1,1,2", "--"]); job["pages"] = new[] { new { file = ".", range = "1,1,2" } }; }
        argv.Add("output");
        QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
    }

    public static IEnumerable<object[]> Roles()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (string mode in new[] { "whole", "merge", "repeat", "repair" })
        foreach (bool indirect in new[] { false, true }) { yield return [route, mode, indirect]; }
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task FieldAndAnnotationRolesKeepMeaningSharingAndPrivateAliases(string route, string mode, bool indirect)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(indirect, "(https://source.test/base/)" + (mode == "repair" ? " /Source 1 0 R" : "")), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("target"), SourceActionRoleTests.Target(), QpdfInputOwnership.Transfer);
        await Run(host, route, mode); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        QpdfObject Read(PdfDictionary dictionary, string key)
        { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
        PdfDictionary Dict(QpdfObject value) => Assert.IsType<PdfDictionary>(Resolve(value));
        PdfArray Array(QpdfObject value) => Assert.IsType<PdfArray>(Resolve(value));
        string Text(QpdfObject value) => Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Resolve(value)).ValueSpan);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        string UriMeaning(PdfDictionary action)
        {
            string text = Text(Read(action, "URI"));
            if (Uri.TryCreate(text, UriKind.Absolute, out Uri? absolute)) { return absolute.AbsoluteUri; }
            return new Uri(new Uri(Text(Read(Dict(Read(catalog, "URI")), "Base"))), text).AbsoluteUri;
        }
        QpdfObject Target(QpdfObject raw)
        {
            QpdfObject value = Resolve(raw);
            if (value is PdfName name)
            { Assert.Equal("Here", name.DisplayText); value = Read(Dict(Read(catalog, "Dests")), "Here"); }
            else if (value is PdfByteString text)
            {
                Assert.Equal("Here", Encoding.ASCII.GetString(text.ValueSpan));
                PdfDictionary tree = Dict(Read(Dict(Read(catalog, "Names")), "Dests"));
                tree = Dict(Assert.Single(Array(Read(tree, "Kids")).Items));
                value = Array(Read(tree, "Names"))[1];
            }
            value = Resolve(value);
            if (value is PdfDictionary wrapper) { value = Read(wrapper, "D"); }
            return Array(value)[0];
        }
        int first = mode == "merge" ? 1 : 0, last = document.PageReferences.Count - 1;
        PdfDictionary firstPage = document.PageReferences[first].Page;
        PdfArray annotations = Array(Read(firstPage, "Annots"));
        PdfDictionary widget = Dict(annotations[0]), second = Dict(Array(Read(document.PageReferences[last].Page, "Annots"))[0]);
        PdfDictionary action = Dict(Read(widget, "A"));
        Assert.Equal("https://source.test/base/item.pdf", UriMeaning(action));
        Assert.Equal("https://source.test/base/item.pdf", UriMeaning(Dict(Read(second, "A"))));
        PdfDictionary field = Dict(Read(widget, "Parent"));
        Assert.Equal(Read(widget, "Parent"), Read(second, "Parent"));
        PdfArray members = Array(Read(field, "Kids")); Assert.Contains(annotations[0], members.Items);
        Assert.Contains(Array(Read(document.PageReferences[last].Page, "Annots"))[0], members.Items);
        PdfDictionary form = Dict(Read(catalog, "AcroForm"));
        PdfReference top = Assert.IsType<PdfReference>(Read(widget, "Parent"));
        Assert.Contains(top, Array(Read(form, "Fields")).Items);
        PdfArray order = Array(Read(form, "CO")); Assert.Contains(annotations[0], order.Items);
        PdfDictionary link = Dict(annotations[1]);
        Assert.Equal(document.PageReferences[last].Source, Target(Read(link, "Dest")));
        Assert.Equal(document.PageReferences[first].Source, Target(Read(Dict(annotations[2]), "Dest")));
        PdfDictionary eventAction = Dict(Read(Dict(Read(widget, "AA")), "U"));
        Assert.Equal(document.PageReferences[first].Source, Target(Read(eventAction, "D")));
        Assert.Equal(document.PageReferences[first].Source, Target(Read(Dict(Read(Dict(Read(firstPage, "AA")), "O")), "D")));
        if (route == "typed" || mode is "merge" or "repair")
        {
            Assert.Equal("https://source.test/base/item.pdf", Text(Read(action, "URI")));
            Assert.IsType<PdfArray>(Resolve(Read(eventAction, "D")));
            Assert.IsType<PdfArray>(Resolve(Read(link, "Dest")));
        }
        Assert.Equal("private.pdf", Text(Read(Dict(Read(field, "A")), "URI")));
        Assert.Equal("item.pdf", Text(Read(Dict(Read(Dict(Read(firstPage, "AA")), "Private")), "URI")));
        if (indirect && mode != "repeat")
        {
            Assert.Equal(Read(widget, "A"), Read(second, "A"));
            PdfArray next = Array(Read(action, "Next")); Assert.Equal(Read(widget, "A"), next[0]);
            Assert.Equal(document.PageReferences[first].Source, Target(Read(Dict(next[1]), "D")));
        }
        if (route != "typed" && mode != "merge")
        {
            PdfDictionary audit = Dict(Read(catalog, "Audit"));
            if (mode == "repair")
            { Assert.NotEqual(Read(audit, "Widget"), annotations[0]); Assert.NotEqual(Read(audit, "Field"), top); }
            else
            { Assert.Equal(Read(audit, "Widget"), annotations[0]); Assert.Equal(Read(audit, "Field"), top); }
            Assert.Equal("item.pdf", Text(Read(Dict(Read(audit, "Action")), "URI")));
            Assert.IsType<PdfByteString>(Resolve(Read(Dict(Read(audit, "GoTo")), "D")));
            Assert.Equal(Read(audit, "Field"), Read(Dict(Read(audit, "Widget")), "Parent"));
        }
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task SplitBindsNamedActionsBeforeDiscardingTheirCatalogContext(string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(true, "(https://source.test/base/)"), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = route == "argv"
            ? await Qpdf.ExecuteAsync(host, new(["input", "--split-pages=1", "output"]), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"splitPages\":\"1\"}", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        foreach (int index in new[] { 1, 2 })
        {
            Assert.True(host.TryGetOutput(QpdfPath.From("output-" + index), out byte[] output));
            QpdfPath path = QpdfPath.From("read-" + index); host.AddInput(path, output, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, path, CancellationToken.None);
            QpdfObject Resolve(QpdfObject raw) => raw is PdfReference reference ? document.GetObject(reference.ObjectNumber) : raw;
            QpdfObject Read(PdfDictionary owner, string key) { Assert.True(owner.TryGet(PdfName.FromAscii(key), out QpdfObject raw)); return Resolve(raw); }
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1)); Assert.False(catalog.TryGet(PdfName.FromAscii("URI"), out _));
            PdfDictionary page = Assert.Single(document.PageReferences).Page;
            PdfArray annotations = Assert.IsType<PdfArray>(Read(page, "Annots"));
            PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(annotations[0]));
            PdfDictionary action = Assert.IsType<PdfDictionary>(Read(widget, "A"));
            Assert.Equal("https://source.test/base/item.pdf", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Read(action, "URI")).ValueSpan));
            PdfArray chain = Assert.IsType<PdfArray>(Read(action, "Next"));
            PdfArray destination = Assert.IsType<PdfArray>(Read(Assert.IsType<PdfDictionary>(Resolve(chain[1])), "D"));
            if (index == 1) { Assert.Equal(Assert.Single(document.PageReferences).Source, destination[0]); }
            else { Assert.IsType<PdfNull>(destination[0]); }
        }
    }

    [Theory]
    [InlineData("argv", "overlay")]
    [InlineData("job", "overlay")]
    [InlineData("argv", "underlay")]
    [InlineData("job", "underlay")]
    public async Task PlacementBindsFieldsWithoutResurrectingSourcePages(string route, string placement)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Target(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("source"), Fixture(true, "(https://source.test/base/)"), QpdfInputOwnership.Transfer);
        string[] argv = ["input", "--" + placement, "source", "--", "output"];
        string job = JsonSerializer.Serialize(new Dictionary<string, object> { ["inputFile"] = "input", ["outputFile"] = "output", [placement] = new { file = "source" } });
        QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        QpdfObject Read(PdfDictionary dictionary, string key)
        { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(value); }
        PdfDictionary page = Assert.Single(document.PageReferences).Page;
        PdfArray annotations = Assert.IsType<PdfArray>(Read(page, "Annots"));
        PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(annotations[0]));
        PdfDictionary action = Assert.IsType<PdfDictionary>(Read(widget, "A"));
        Assert.Equal("https://source.test/base/item.pdf", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Read(action, "URI")).ValueSpan));
        PdfDictionary go = Assert.IsType<PdfDictionary>(Read(Assert.IsType<PdfDictionary>(Read(widget, "AA")), "U"));
        Assert.IsType<PdfNull>(Assert.IsType<PdfArray>(Read(go, "D"))[0]);
        PdfDictionary field = Assert.IsType<PdfDictionary>(Read(widget, "Parent"));
        Assert.Contains(annotations[0], Assert.IsType<PdfArray>(Read(field, "Kids")).Items);
    }

    [Theory]
    [InlineData("typed", "Popup")]
    [InlineData("argv", "Popup")]
    [InlineData("job", "Popup")]
    [InlineData("typed", "Movie")]
    [InlineData("argv", "Movie")]
    [InlineData("job", "Movie")]
    public async Task ActivationDataAndPopupExtrasDoNotBecomeActions(string route, string subtype)
    {
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /URI << /Base (https://source.test/base/) /Source 1 0 R >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R 5 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /" + subtype + " /Rect [0 0 20 20] /A 6 0 R /Parent 5 0 R >>", null),
            (5, "<< /Type /Annot /Subtype /Link /Rect [30 30 50 50] /A 6 0 R /Popup 4 0 R >>", null),
            (6, "<< /S /URI /URI (item.pdf) >>", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
        if (route == "typed") { await Qpdf.SelectPageAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new(1), CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new(["input", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\"}", CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        QpdfObject Read(PdfDictionary dictionary, string key)
        { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(value); }
        PdfArray annots = Assert.IsType<PdfArray>(Read(document.PageReferences[0].Page, "Annots"));
        PdfDictionary first = Assert.IsType<PdfDictionary>(Resolve(annots[0])), second = Assert.IsType<PdfDictionary>(Resolve(annots[1]));
        PdfByteString untouched = Assert.IsType<PdfByteString>(Read(Assert.IsType<PdfDictionary>(Read(first, "A")), "URI"));
        PdfByteString bound = Assert.IsType<PdfByteString>(Read(Assert.IsType<PdfDictionary>(Read(second, "A")), "URI"));
        Assert.Equal("item.pdf", Encoding.ASCII.GetString(untouched.ValueSpan));
        Assert.Equal("https://source.test/base/item.pdf", Encoding.ASCII.GetString(bound.ValueSpan));
    }

    [Theory]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    public async Task CatalogAndOutlineActionsBindTheirRolesWithoutChangingAliases(string route, bool linear)
    {
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /URI << /Base (https://source.test/base/) /Source 1 0 R >> /Dests 6 0 R /Outlines 4 0 R /OpenAction 7 0 R /AA << /WC << /S /JavaScript /JS () /Next 7 0 R >> /Private 7 0 R >> /Audit << /Action 7 0 R /Outlines 4 0 R >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (4, "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>", null),
            (5, "<< /Title (Navigation) /Parent 4 0 R /Dest /Here >>", null),
            (6, "<< /Here [3 0 R /Fit] >>", null), (7, "<< /S /URI /URI (item.pdf) >>", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
        List<string> argv = ["input"]; if (linear) { argv.Add("--linearize"); } argv.Add("output");
        string job = JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", linearize = linear });
        QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        QpdfObject Read(PdfDictionary dictionary, string key)
        { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(value); }
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.Engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject root) ? Resolve(root) : null);
        PdfDictionary action = Assert.IsType<PdfDictionary>(Read(catalog, "OpenAction"));
        Assert.Equal("https://source.test/base/item.pdf", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Read(action, "URI")).ValueSpan));
        PdfDictionary audit = Assert.IsType<PdfDictionary>(Read(catalog, "Audit"));
        Assert.Equal("item.pdf", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Read(Assert.IsType<PdfDictionary>(Read(audit, "Action")), "URI")).ValueSpan));
        PdfDictionary outline = Assert.IsType<PdfDictionary>(Read(Assert.IsType<PdfDictionary>(Read(catalog, "Outlines")), "First"));
        Assert.Equal(document.PageReferences[0].Source, Assert.IsType<PdfArray>(Read(outline, "Dest"))[0]);
        Assert.IsType<PdfName>(Read(Assert.IsType<PdfDictionary>(Read(Assert.IsType<PdfDictionary>(Read(audit, "Outlines")), "First")), "Dest"));
    }
}
