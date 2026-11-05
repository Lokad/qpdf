using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class SourceActionIdentityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GenuineActionAliasesShareWhileOtherEventKeysStayOpaque(bool widget, bool job)
    {
        using InMemoryQpdfHost host = new();
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /URI << /Base (https://example.test/base/) >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /" + (widget ? "Widget /FT /Tx /T (field)" : "Link /Border [0 0 0]") + " /Rect [20 20 40 40] /A 5 0 R /AA << /E 5 0 R /K 5 0 R /URI (relative.pdf) >> >>", null),
            (5, "<< /S /URI /URI (relative.pdf) /Next 5 0 R >>", null)], 1);
        host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, "{\"empty\":true,\"outputFile\":\"output\",\"pages\":[{\"file\":\"input\",\"range\":\"1\"}]}", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["--empty", "--pages", "input", "1", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfArray annotations = Assert.IsType<PdfArray>(Value(document, document.PageReferences[0].Page, "Annots"));
        PdfDictionary annotation = Assert.IsType<PdfDictionary>(Resolve(document, annotations[0]));
        PdfDictionary action = Assert.IsType<PdfDictionary>(Value(document, annotation, "A"));
        PdfDictionary events = Assert.IsType<PdfDictionary>(Value(document, annotation, "AA"));
        Assert.Same(action, Value(document, events, "E"));
        Assert.Same(action, Value(document, action, "Next"));
        Assert.Equal("https://example.test/base/relative.pdf", Text(document, action, "URI"));
        Assert.Equal("relative.pdf", Text(document, events, "URI"));
        PdfDictionary key = Assert.IsType<PdfDictionary>(Value(document, events, "K"));
        if (widget) { Assert.Same(action, key); }
        else { Assert.NotSame(action, key); Assert.Equal("relative.pdf", Text(document, key, "URI")); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectActionBindingAllocationScalesLinearly(bool absolute)
    {
        _ = await BindingAllocation(16, absolute);
        long smaller = await BindingAllocation(256, absolute), larger = await BindingAllocation(512, absolute);
        Assert.True(larger < smaller * 3L + 131072L, $"Direct action lookup regressed: {smaller} versus {larger} bytes.");
    }

    private static async Task<long> BindingAllocation(int count, bool absolute)
    {
        string prefix = absolute ? "https://example.test/base/" : "";
        string actions = string.Join(" ", Enumerable.Range(0, count).Select(i => "<< /S /URI /URI (" + prefix + "relative-" + i + ".pdf) >>"));
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /URI << /Base (https://example.test/base/) >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Link /Rect [0 0 20 20] /A << /S /URI /URI (root.pdf) /Next [" + actions + "] >> >>", null)], 1), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, QpdfLimits.Default, 1, owner);
        var page = document.PageReferences[0];
        copier.RegisterPageRoot(page.Source?.ObjectNumber);
        var copied = copier.CopyPageRoot(page.Page, page.Source?.ObjectNumber, new Dictionary<PdfName, QpdfObject>(), false, null);
        try
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            SourceActions.BindDocument(document.Engine, copier, null, null, null, false, null, governor, owner, CancellationToken.None);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            PdfDictionary mapped = Assert.IsType<PdfDictionary>(copier.CopiedObjects[copied.Id]);
            Assert.True(mapped.TryGet(PdfName.FromAscii("Annots"), out QpdfObject annotationRaw));
            PdfArray annotations = Assert.IsType<PdfArray>(annotationRaw);
            QpdfObject raw = annotations[0];
            PdfDictionary annotation = Assert.IsType<PdfDictionary>(raw is PdfReference reference ? copier.CopiedObjects[reference.ObjectNumber] : raw);
            Assert.True(annotation.TryGet(PdfName.FromAscii("A"), out QpdfObject actionRaw));
            PdfDictionary action = Assert.IsType<PdfDictionary>(actionRaw is PdfReference link ? copier.CopiedObjects[link.ObjectNumber] : actionRaw);
            Assert.True(action.TryGet(PdfName.FromAscii("URI"), out QpdfObject uri));
            Assert.Equal("https://example.test/base/root.pdf", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(uri).ValueSpan));
            return allocated;
        }
        finally { governor.CompletePrivateStorage(); }
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string identity in new[] { "direct", "separate", "shared" })
        foreach (bool widget in new[] { false, true })
        foreach (bool reverse in new[] { false, true })
        foreach (bool cycle in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [identity, widget, reverse, cycle, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task BindingUsesIdentityAndKeepsDifferentDictionaryRolesSeparate(string identity, bool widget, bool reverse, bool cycle, bool job)
    {
        RecordingMemoryBudget budget = new(16L * 1024 * 1024);
        using (IMemoryScope owner = budget.CreateScope())
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
            host.AddInput(QpdfPath.From("input"), Source(identity, widget, reverse, cycle), QpdfInputOwnership.Transfer);
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { empty = true, outputFile = "output", pages = new[] { new { file = "input", range = "1" } } }), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["--empty", "--pages", "input", "1", "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
            {
                PdfArray annotations = Assert.IsType<PdfArray>(Value(document, document.PageReferences[0].Page, "Annots"));
                PdfDictionary annotation = Assert.IsType<PdfDictionary>(Resolve(document, annotations[0]));
                PdfDictionary action = Assert.IsType<PdfDictionary>(Value(document, annotation, "A"));
                PdfDictionary additional = Assert.IsType<PdfDictionary>(Value(document, annotation, "AA"));
                Assert.Equal("https://example.test/base/relative.pdf", Text(document, action, "URI"));
                Assert.Equal("relative.pdf", Text(document, additional, "URI"));
                if (cycle)
                {
                    PdfDictionary next = Assert.IsType<PdfDictionary>(Value(document, action, "Next"));
                    Assert.Equal("https://example.test/base/relative.pdf", Text(document, next, "URI"));
                    Assert.Same(next, Value(document, next, "Next"));
                    PdfDictionary privateNext = Assert.IsType<PdfDictionary>(Value(document, additional, "Next"));
                    Assert.Equal("relative.pdf", Text(document, privateNext, "URI"));
                    Assert.Same(privateNext, Value(document, privateNext, "Next"));
                }
                if (widget)
                {
                    PdfDictionary catalog = Assert.IsType<PdfDictionary>(Value(document, document.Engine.Trailer, "Root"));
                    PdfDictionary form = Assert.IsType<PdfDictionary>(Value(document, catalog, "AcroForm"));
                    PdfArray fields = Assert.IsType<PdfArray>(Value(document, form, "Fields"));
                    PdfArray order = Assert.IsType<PdfArray>(Value(document, form, "CO"));
                    Assert.Equal(annotations[0], fields[0]); Assert.Equal(fields[0], order[0]);
                    Assert.Same(annotation, Resolve(document, fields[0]));
                }
            }
            Assert.Equal(0, host.OpenLeaseCount);
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    private static byte[] Source(string identity, bool widget, bool reverse, bool cycle)
    {
        string action = "<< /S /URI /URI (relative.pdf)" + (cycle ? " /Next 5 0 R" : "") + " >>";
        string active = identity == "direct" ? action : "5 0 R";
        string additional = identity == "direct" ? action : identity == "shared" ? "5 0 R" : "6 0 R";
        string roles = reverse ? "/AA " + additional + " /A " + active : "/A " + active + " /AA " + additional;
        string form = widget ? "/AcroForm << /Fields [4 0 R] /CO [4 0 R] /DR << /Font << /F1 7 0 R >> >> /DA (/F1 10 Tf) >>" : "";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /URI << /Base (https://example.test/base/) >> " + form + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /" + (widget ? "Widget /FT /Tx /T (field) /V (unchanged)" : "Link /Border [0 0 0]") + " /F 4 /Rect [20 20 40 40] " + roles + " >>", null),
            (5, action, null), (6, action, null),
            (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", null)], 1);
    }
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value) => value is PdfReference link ? document.GetObject(link.ObjectNumber) : value;
    private static QpdfObject Value(QpdfDocument document, PdfDictionary dictionary, string key)
        => dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value) ? Resolve(document, value) : PdfNull.Instance;
    private static string Text(QpdfDocument document, PdfDictionary dictionary, string key)
        => Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Value(document, dictionary, key)).ValueSpan);
}
