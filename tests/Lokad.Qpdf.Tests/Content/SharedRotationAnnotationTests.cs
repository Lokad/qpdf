using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class SharedRotationAnnotationTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string kind in new[] { "stamp", "merged", "separate" })
        foreach (string rotations in new[] { "first", "second", "different", "same" })
        foreach (bool job in new[] { false, true })
        { yield return [kind, rotations, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EachOccurrenceOwnsItsGeometryRelationshipsAndRegisteredField(string kind, string rotations, bool job)
    {
        string form = kind == "stamp" ? string.Empty : "/AcroForm << /Fields [" + (kind == "merged" ? "4 0 R" : "9 0 R") + "] >>";
        string annot = kind == "stamp" ? "/Subtype /Stamp" : "/Subtype /Widget "
            + (kind == "merged" ? "/FT /Tx /T (Field) /V (Value)" : "/Parent 9 0 R");
        byte[] fixture = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R " + form + " >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Annots 10 0 R >>", null),
            (4, "<< " + annot + " /Rect [10 20 30 40] /P 3 0 R /AP << /N 8 0 R >> /Popup 5 0 R >>", null),
            (5, "<< /Subtype /Popup /Rect [40 60 60 80] /P 3 0 R /Parent 4 0 R >>", null),
            (6, "<< /Subtype /Stamp /Rect [20 30 50 60] /P 3 0 R /IRT 4 0 R /AP << /N 8 0 R >> >>", null),
            (7, "null", null),
            (8, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length 4 >>", "q Q\n"u8.ToArray()),
            (9, "<< /FT /Tx /T (Field) /V (Value) /Kids [4 0 R] >>", null),
            (10, "[4 0 R 5 0 R 6 0 R]", null)], 1);
        string[] values = rotations switch { "first" => ["90:1"], "second" => ["90:2"], "different" => ["90:1", "270:2"], _ => ["90:1-2"] };
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), fixture, QpdfInputOwnership.Transfer);
        List<string> argv = ["input", "--flatten-rotation", "--stream-data=uncompress"];
        foreach (string value in values) { argv.Add("--rotate=" + value); }
        argv.AddRange(["--pages", ".", "1,1", "--", "output"]);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", flattenRotation = true,
                streamData = "uncompress", rotate = values, pages = new[] { new { file = ".", range = "1,1" } } }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
        PdfStream escaped;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None))
        {
            QpdfObject Resolve(QpdfObject value) => value is PdfReference link ? document.GetObject(link.ObjectNumber) : value;
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(Get(document.Engine.Trailer, "Root")));
            PdfArray pageRefs = Assert.IsType<PdfArray>(Get(Assert.IsType<PdfDictionary>(Resolve(Get(catalog, "Pages"))), "Kids"));
            PdfArray? fields = kind == "stamp" ? null : Assert.IsType<PdfArray>(Resolve(Get(Assert.IsType<PdfDictionary>(Resolve(Get(catalog, "AcroForm"))), "Fields")));
            if (fields is not null) { Assert.Equal(2, fields.Count); }
            PdfReference? previous = null;
            PdfArray firstAnnotations = Assert.IsType<PdfArray>(Resolve(Get(document.PageReferences[0].Page, "Annots")));
            PdfDictionary firstAnnotation = Assert.IsType<PdfDictionary>(Resolve(firstAnnotations[0]));
            PdfDictionary firstAppearance = Assert.IsType<PdfDictionary>(Resolve(Get(firstAnnotation, "AP")));
            escaped = Assert.IsType<PdfStream>(Resolve(Get(firstAppearance, "N")));
            for (int index = 0; index < 2; index++)
            {
                PdfArray annotations = Assert.IsType<PdfArray>(Resolve(Get(document.PageReferences[index].Page, "Annots")));
                Assert.Equal(3, annotations.Count);
                PdfReference current = Assert.IsType<PdfReference>(annotations[0]);
                Assert.NotEqual(previous, current); previous = current;
                PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(current));
                int angle = index == 0 ? (rotations == "second" ? 0 : 90) : rotations switch { "first" => 0, "different" => 270, _ => 90 };
                double[] expected = angle switch { 90 => [20, 70, 40, 90], 270 => [160, 10, 180, 30], _ => [10, 20, 30, 40] };
                PdfArray rectangle = Assert.IsType<PdfArray>(Resolve(Get(widget, "Rect")));
                Assert.Equal(expected, rectangle.Items.Select(value => value is PdfInteger integer ? integer.Value : Assert.IsType<PdfReal>(value).Value));
                Assert.Equal(pageRefs[index], Get(widget, "P"));
                Assert.Equal(annotations[1], Get(widget, "Popup"));
                Assert.Equal(current, Get(Assert.IsType<PdfDictionary>(Resolve(annotations[1])), "Parent"));
                Assert.Equal(current, Get(Assert.IsType<PdfDictionary>(Resolve(annotations[2])), "IRT"));
                if (fields is not null)
                {
                    QpdfObject root = kind == "merged" ? current : Get(widget, "Parent");
                    Assert.Contains(root, fields.Items);
                    if (kind == "separate") { Assert.Contains(current, Assert.IsType<PdfArray>(Resolve(Get(Assert.IsType<PdfDictionary>(Resolve(root)), "Kids"))).Items); }
                }
            }
        }
        host.Dispose();
        Assert.Equal("q Q\n"u8.ToArray(), escaped.Data);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NestedFieldCopiesRetainOtherPagesAndCalculationOrder(bool job, bool linear)
    {
        byte[] fixture = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [14 0 R] /CO [9 0 R 13 0 R] >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 11 0 R] /Count 2 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Subtype /Widget /Parent 9 0 R /Rect [10 20 30 40] /P 3 0 R /AP << /N 8 0 R >> >>", null),
            (5, "null", null), (6, "null", null), (7, "null", null),
            (8, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length 4 >>", "q Q\n"u8.ToArray()),
            (9, "<< /Parent 14 0 R /T (Child) /V (Value) /Kids [4 0 R] >>", null),
            (10, "null", null),
            (11, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Annots [12 0 R] >>", null),
            (12, "<< /Subtype /Widget /Parent 13 0 R /Rect [10 20 30 40] /P 11 0 R /AP << /N 8 0 R >> >>", null),
            (13, "<< /Parent 14 0 R /T (Other) /V (OtherValue) /Kids [12 0 R] >>", null),
            (14, "<< /FT /Tx /T (Root) /Kids [9 0 R 13 0 R] >>", null)], 1);
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), fixture, QpdfInputOwnership.Transfer);
        List<string> argv = ["input", "--flatten-rotation", "--rotate=90:1", "--rotate=270:2", "--stream-data=uncompress"];
        if (linear) { argv.Add("--linearize"); }
        argv.AddRange(["--pages", ".", "1,1,2", "--", "output"]);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", flattenRotation = true,
                linearize = linear, streamData = "uncompress", rotate = new[] { "90:1", "270:2" }, pages = new[] { new { file = ".", range = "1,1,2" } } }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None))
        {
            QpdfObject Resolve(QpdfObject value) => value is PdfReference link ? document.GetObject(link.ObjectNumber) : value;
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(Get(document.Engine.Trailer, "Root")));
            PdfDictionary form = Assert.IsType<PdfDictionary>(Resolve(Get(catalog, "AcroForm")));
            PdfArray fields = Assert.IsType<PdfArray>(Resolve(Get(form, "Fields")));
            Assert.Equal(2, fields.Count);
            PdfArray order = Assert.IsType<PdfArray>(Resolve(Get(form, "CO")));
            Assert.Equal(3, order.Count);
            List<QpdfObject> roots = [];
            for (int index = 0; index < 3; index++)
            {
                PdfArray annots = Assert.IsType<PdfArray>(Resolve(Get(document.PageReferences[index].Page, "Annots")));
                PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(annots[0]));
                QpdfObject branchRef = Get(widget, "Parent");
                Assert.Contains(branchRef, order.Items);
                PdfDictionary branch = Assert.IsType<PdfDictionary>(Resolve(branchRef));
                Assert.Contains(annots[0], Assert.IsType<PdfArray>(Resolve(Get(branch, "Kids"))).Items);
                QpdfObject rootRef = Get(branch, "Parent"); roots.Add(rootRef);
                Assert.Contains(rootRef, fields.Items);
                PdfDictionary root = Assert.IsType<PdfDictionary>(Resolve(rootRef));
                PdfArray kids = Assert.IsType<PdfArray>(Resolve(Get(root, "Kids")));
                Assert.Contains(branchRef, kids.Items);
                Assert.Equal(index == 1 ? 1 : 2, kids.Count);
                Assert.Equal(index == 2 ? "OtherValue"u8.ToArray() : "Value"u8.ToArray(), Assert.IsType<PdfByteString>(Get(branch, "V")).Value);
            }
            Assert.Equal(roots[0], roots[2]); Assert.NotEqual(roots[0], roots[1]);
            PdfDictionary clone = Assert.IsType<PdfDictionary>(Resolve(roots[1]));
            Assert.Equal("Root+1"u8.ToArray(), Assert.IsType<PdfByteString>(Get(clone, "T")).Value);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedPopupsOutsideAnnotsStayWithTheirOccurrence(bool job)
    {
        byte[] fixture = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Annots [4 0 R] >>", null),
            (4, "<< /Subtype /Text /Rect [10 20 30 40] /P 3 0 R /Popup 5 0 R >>", null),
            (5, "<< /Subtype /Popup /Rect [40 60 60 80] /P 3 0 R /Parent 4 0 R >>", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), fixture, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","flattenRotation":true,"rotate":["90:1"],"pages":[{"file":".","range":"1,1"}]}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--flatten-rotation", "--rotate=90:1", "--pages", ".", "1,1", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference link ? document.GetObject(link.ObjectNumber) : value;
        QpdfObject? previousPopup = null;
        for (int index = 0; index < 2; index++)
        {
            PdfArray annots = Assert.IsType<PdfArray>(Resolve(Get(document.PageReferences[index].Page, "Annots")));
            PdfDictionary annotation = Assert.IsType<PdfDictionary>(Resolve(annots[0]));
            QpdfObject popupRef = Get(annotation, "Popup"); Assert.NotEqual(previousPopup, popupRef); previousPopup = popupRef;
            PdfDictionary popup = Assert.IsType<PdfDictionary>(Resolve(popupRef));
            Assert.Equal(annots[0], Get(popup, "Parent"));
            Assert.Equal(Get(annotation, "P"), Get(popup, "P"));
            PdfArray rectangle = Assert.IsType<PdfArray>(Get(popup, "Rect"));
            Assert.Equal(index == 0 ? new double[] { 60, 40, 80, 60 } : [40, 60, 60, 80],
                rectangle.Items.Select(value => value is PdfInteger integer ? integer.Value : Assert.IsType<PdfReal>(value).Value));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IndependentRepliesFollowTheirLocalSharedAnnotation(bool job, bool direct)
    {
        const string first = "<< /Subtype /Stamp /Rect [40 60 60 80] /P 3 0 R /IRT 5 0 R >>";
        const string second = "<< /Subtype /Stamp /Rect [40 60 60 80] /P 4 0 R /IRT 5 0 R >>";
        byte[] fixture = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Annots [5 0 R " + (direct ? first : "7 0 R") + "] >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Annots [5 0 R " + (direct ? second : "8 0 R") + "] >>", null),
            (5, "<< /Subtype /Text /Rect [10 20 30 40] /P 3 0 R >>", null),
            (6, "null", null), (7, first, null), (8, second, null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), fixture, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","flattenRotation":true,"rotate":["90:1"]}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--flatten-rotation", "--rotate=90:1", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference link ? document.GetObject(link.ObjectNumber) : value;
        for (int index = 0; index < 2; index++)
        {
            PdfArray annots = Assert.IsType<PdfArray>(Resolve(Get(document.PageReferences[index].Page, "Annots")));
            Assert.Equal(annots[0], Get(Assert.IsType<PdfDictionary>(Resolve(annots[1])), "IRT"));
            Assert.Equal(direct, annots[1] is PdfDictionary);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UnregisteredWidgetBranchesStayCoherentWithoutNewRegistrations(bool job, bool removeForm)
    {
        byte[] fixture = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields " + (removeForm ? "[5 0 R]" : "[]") + " >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Annots [4 0 R] >>", null),
            (4, "<< /Subtype /Widget /Rect [10 20 30 40] /P 3 0 R /Parent 5 0 R >>", null),
            (5, "<< /FT /Tx /T (Unregistered) /Kids [4 0 R] >>", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), fixture, QpdfInputOwnership.Transfer);
        List<string> argv = ["input", "--flatten-rotation", "--rotate=90:1"];
        if (removeForm) { argv.Add("--remove-acroform"); }
        argv.AddRange(["--pages", ".", "1,1", "--", "output"]);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", flattenRotation = true,
                removeAcroform = removeForm, rotate = new[] { "90:1" }, pages = new[] { new { file = ".", range = "1,1" } } }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference link ? document.GetObject(link.ObjectNumber) : value;
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(Get(document.Engine.Trailer, "Root")));
        Assert.False(catalog.TryGet(PdfName.FromAscii("AcroForm"), out _));
        for (int index = 0; index < 2; index++)
        {
            PdfArray annots = Assert.IsType<PdfArray>(Resolve(Get(document.PageReferences[index].Page, "Annots")));
            PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(annots[0]));
            PdfDictionary parent = Assert.IsType<PdfDictionary>(Resolve(Get(widget, "Parent")));
            PdfArray kids = Assert.IsType<PdfArray>(Resolve(Get(parent, "Kids")));
            Assert.Equal(annots[0], Assert.Single(kids.Items));
        }
    }

    [Theory]
    [InlineData(false, "memory")]
    [InlineData(true, "memory")]
    [InlineData(false, "output")]
    [InlineData(true, "output")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "commit")]
    [InlineData(true, "commit")]
    public async Task FailureKeepsPriorOutputAndClosesLeases(bool job, string failure)
    {
        byte[] fixture = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Annots [4 0 R] >>", null),
            (4, "<< /Subtype /Stamp /Rect [10 20 30 40] /P 3 0 R >>", null)], 1);
        using InMemoryQpdfHost memory = new(); memory.AddInput(QpdfPath.From("input"), fixture, QpdfInputOwnership.Transfer);
        byte[] prior = "prior output"u8.ToArray();
        await using (IQpdfOutputTransaction transaction = await memory.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await transaction.WriteAsync(prior, CancellationToken.None); await transaction.CommitAsync(CancellationToken.None); }
        IQpdfHost host = failure == "commit" ? new CommitFailureHost(memory) : memory;
        QpdfLimits limits = failure switch
        {
            "memory" => QpdfLimits.Default with { MaxMemoryBytes = 32768L },
            "output" => QpdfLimits.Default with { MaxOutputBytes = 1L },
            _ => QpdfLimits.Default,
        };
        using CancellationTokenSource cancellation = new(); if (failure == "cancel") { cancellation.Cancel(); }
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","flattenRotation":true,"rotate":["90:1"],"pages":[{"file":".","range":"1,1"}]}""", new QpdfExecutionOptions(limits), cancellation.Token)
            : await Qpdf.ExecuteAsync(host, new(["input", "--flatten-rotation", "--rotate=90:1", "--pages", ".", "1,1", "--", "output"]), new QpdfExecutionOptions(limits), cancellation.Token);
        Assert.Equal(failure == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        Assert.True(memory.TryGetOutput(QpdfPath.From("output"), out byte[] actual)); Assert.Equal(prior, actual);
        Assert.Equal(0L, memory.OpenLeaseCount);
    }

    private sealed class CommitFailureHost(InMemoryQpdfHost memory) : IQpdfHost
    {
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
            => memory.OpenInputAsync(path, cancellationToken);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<IQpdfOutputTransaction>(new FailingCommitTransaction(path)); }
    }

    private static QpdfObject Get(PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return value;
    }
}
