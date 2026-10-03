using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AcroFormPreservationTests
{
    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    private static async Task<InMemoryQpdfHost> HostWithFixture(string logical, string fixture)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), fixture));
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(logical), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static async Task<QpdfDocument> RereadAsync(InMemoryQpdfHost host, string output)
    {
        return await RereadAsync(host, output, "inputs/reread.pdf");
    }

    private static async Task<QpdfDocument> RereadAsync(InMemoryQpdfHost host, string output, string alias)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From(output), out byte[] pdf));
        host.AddInput(QpdfPath.From(alias), pdf, QpdfInputOwnership.Copy);
        return await QpdfDocument.OpenAsync(host, QpdfPath.From(alias), CancellationToken.None);
    }

    private static PdfDictionary RequireCatalog(QpdfDocument output)
    {
        return Assert.IsType<PdfDictionary>(output.GetObject(1));
    }

    private static PdfDictionary RequireForm(PdfDictionary catalog)
    {
        Assert.True(catalog.TryGet(PdfName.FromAscii("AcroForm"), out QpdfObject? raw));
        return Assert.IsType<PdfDictionary>(raw);
    }

    private static List<PdfDictionary> ResolveFields(QpdfDocument output, PdfDictionary form)
    {
        Assert.True(form.TryGet(PdfName.FromAscii("Fields"), out QpdfObject? raw));
        PdfArray fields = Assert.IsType<PdfArray>(raw);
        List<PdfDictionary> resolved = new(fields.Count);
        for (int i = 0; i < fields.Count; i++)
        {
            PdfReference reference = Assert.IsType<PdfReference>(fields[i]);
            resolved.Add(Assert.IsType<PdfDictionary>(output.GetObject(reference.ObjectNumber)));
        }

        return resolved;
    }

    private static string FieldText(PdfDictionary field, string key)
    {
        Assert.True(field.TryGet(PdfName.FromAscii(key), out QpdfObject? raw));
        return Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(raw).Value);
    }

    private static byte[] BuildFormPdf(
        string catalogExtra,
        string pageAnnots,
        byte[] contents,
        List<(string Dict, byte[]? StreamData)> extras)
    {
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R" + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /Helv 8 0 R >> >>" + pageAnnots + " >>",
            "<< /Length " + contents.Length.ToString() + " >>",
        };
        foreach ((string dict, byte[]? _) in extras)
        {
            bodies.Add(dict);
        }

        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        List<byte[]> parts = new() { header };
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[]? streamData = number == 4 ? contents : number > 4 ? extras[number - 5].StreamData : null;
            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " 0 obj\n" + bodies[i] + "\n");
            if (streamData is null)
            {
                byte[] close = Encoding.ASCII.GetBytes("endobj\n");
                offsets.Add(position);
                position += open.Length + close.Length;
                byte[] plain = new byte[open.Length + close.Length];
                Buffer.BlockCopy(open, 0, plain, 0, open.Length);
                Buffer.BlockCopy(close, 0, plain, open.Length, close.Length);
                parts.Add(plain);
                continue;
            }

            byte[] streamOpen = Encoding.ASCII.GetBytes("stream\n");
            byte[] streamClose = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
            offsets.Add(position);
            position += open.Length + streamOpen.Length + streamData.Length + streamClose.Length;
            byte[] payload = new byte[open.Length + streamOpen.Length + streamData.Length + streamClose.Length];
            int at = 0;
            Buffer.BlockCopy(open, 0, payload, at, open.Length);
            at += open.Length;
            Buffer.BlockCopy(streamOpen, 0, payload, at, streamOpen.Length);
            at += streamOpen.Length;
            Buffer.BlockCopy(streamData, 0, payload, at, streamData.Length);
            at += streamData.Length;
            Buffer.BlockCopy(streamClose, 0, payload, at, streamClose.Length);
            parts.Add(payload);
        }

        int size = bodies.Count + 1;
        StringBuilder xref = new();
        xref.Append("xref\n0 " + size.ToString() + "\n");
        xref.Append("0000000000 65535 f \n");
        foreach (long offset in offsets)
        {
            xref.Append(offset.ToString("D10") + " 00000 n \n");
        }

        xref.Append("trailer\n<< /Size " + size.ToString() + " /Root 1 0 R >>\n");
        xref.Append("startxref\n" + position.ToString() + "\n%%EOF\n");
        byte[] tail = Encoding.ASCII.GetBytes(xref.ToString());
        parts.Add(tail);

        int total = 0;
        foreach (byte[] part in parts)
        {
            total += part.Length;
        }

        byte[] output = new byte[total];
        int cursor = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, output, cursor, part.Length);
            cursor += part.Length;
        }

        return output;
    }

    [Fact]
    public async Task TypedSelectionPreservesFieldRegistrationAndValue()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.pdf"),
            PdfPageSelectionOptions.FromPages([1]), CancellationToken.None);
        Assert.Equal([1], result.SourcePages);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf");
        Assert.Equal(1, output.PageCount);
        PdfDictionary form = RequireForm(RequireCatalog(output));
        List<PdfDictionary> fields = ResolveFields(output, form);
        PdfDictionary field = Assert.Single(fields);
        Assert.Equal("Name", FieldText(field, "T"));
        Assert.Equal("Hello", FieldText(field, "V"));
        Assert.True(form.TryGet(PdfName.FromAscii("NeedAppearances"), out QpdfObject? needRaw));
        Assert.True(Assert.IsType<PdfBoolean>(needRaw).Value);
        Assert.True(output.PageReferences[0].Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject _));
    }

    [Fact]
    public async Task ArgvSelectionPreservesFieldRegistrationAndValue()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf");
        PdfDictionary field = Assert.Single(ResolveFields(output, RequireForm(RequireCatalog(output))));
        Assert.Equal("Name", FieldText(field, "T"));
        Assert.Equal("Hello", FieldText(field, "V"));
    }

    [Fact]
    public async Task JobSelectionPreservesFieldRegistrationAndValue()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
        string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}]}""";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf");
        PdfDictionary field = Assert.Single(ResolveFields(output, RequireForm(RequireCatalog(output))));
        Assert.Equal("Name", FieldText(field, "T"));
        Assert.Equal("Hello", FieldText(field, "V"));
    }

    [Fact]
    public async Task WholeFileRewritePreservesForm()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
        string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "coalesceContents": true}""";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf");
        PdfDictionary field = Assert.Single(ResolveFields(output, RequireForm(RequireCatalog(output))));
        Assert.Equal("Hello", FieldText(field, "V"));
    }

    [Theory]
    [InlineData(1, "First", "AAA")]
    [InlineData(2, "Second", "BBB")]
    public async Task SubsetSelectionPrunesUnselectedField(int page, string name, string value)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-2page.pdf");
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.pdf"),
            PdfPageSelectionOptions.FromPages([page]), CancellationToken.None);
        Assert.Equal([page], result.SourcePages);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf");
        Assert.Equal(1, output.PageCount);
        PdfDictionary field = Assert.Single(ResolveFields(output, RequireForm(RequireCatalog(output))));
        Assert.Equal(name, FieldText(field, "T"));
        Assert.Equal(value, FieldText(field, "V"));
    }

    [Fact]
    public async Task FullSelectionKeepsBothFieldsAndDefaultResources()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-2page.pdf");
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.pdf"),
            PdfPageSelectionOptions.FromPages([1, 2]), CancellationToken.None);
        Assert.Equal(2, result.PageCount);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf");
        PdfDictionary form = RequireForm(RequireCatalog(output));
        List<PdfDictionary> fields = ResolveFields(output, form);
        Assert.Equal(2, fields.Count);
        Assert.Equal("First", FieldText(fields[0], "T"));
        Assert.Equal("AAA", FieldText(fields[0], "V"));
        Assert.Equal("Second", FieldText(fields[1], "T"));
        Assert.Equal("BBB", FieldText(fields[1], "V"));
        Assert.True(form.TryGet(PdfName.FromAscii("DR"), out QpdfObject? drRaw));
        PdfDictionary dr = Assert.IsType<PdfDictionary>(drRaw);
        Assert.True(dr.TryGet(PdfName.FromAscii("Font"), out QpdfObject? fontsRaw));
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(fontsRaw);
        Assert.True(fonts.TryGet(PdfName.FromAscii("Helv"), out QpdfObject? helvRaw));
        PdfReference helv = Assert.IsType<PdfReference>(helvRaw);
        PdfDictionary font = Assert.IsType<PdfDictionary>(output.GetObject(helv.ObjectNumber));
        Assert.True(font.TryGet(PdfName.FromAscii("BaseFont"), out QpdfObject? baseRaw));
        Assert.Equal("Helvetica", Assert.IsType<PdfName>(baseRaw).DisplayText);
    }

    [Fact]
    public async Task ParentRelationshipsAndDefaultResourcesPreserved()
    {
        byte[] appearance = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 10 10 Td (A) Tj ET");
        byte[] contents = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 100 700 Td (P) Tj ET");
        byte[] fixture = BuildFormPdf(
            " /AcroForm << /Fields [5 0 R] /NeedAppearances true /DR << /Font << /Helv 8 0 R >> >> >>",
            " /Annots [6 0 R]",
            contents,
            new List<(string Dict, byte[]? StreamData)>()
            {
                ("<< /FT /Tx /T (Parent1) /Kids [6 0 R] /DA (/Helv 12 Tf) >>", null),
                ("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /Parent 5 0 R /V (AAA) /AP << /N 7 0 R >> >>", null),
                ("<< /Length " + appearance.Length.ToString() + " >>", appearance),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            });
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), fixture, QpdfInputOwnership.Copy);
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.pdf"),
            PdfPageSelectionOptions.FromPages([1]), CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf");
        PdfDictionary form = RequireForm(RequireCatalog(output));
        PdfDictionary field = Assert.Single(ResolveFields(output, form));
        Assert.Equal("Parent1", FieldText(field, "T"));
        Assert.True(field.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw));
        PdfArray kids = Assert.IsType<PdfArray>(kidsRaw);
        Assert.Equal(1, kids.Count);
        PdfReference kid = Assert.IsType<PdfReference>(kids[0]);
        PdfDictionary widget = Assert.IsType<PdfDictionary>(output.GetObject(kid.ObjectNumber));
        Assert.Equal("AAA", FieldText(widget, "V"));
        Assert.True(widget.TryGet(PdfName.FromAscii("Parent"), out QpdfObject? parentRaw));
        PdfReference parent = Assert.IsType<PdfReference>(parentRaw);
        PdfDictionary parentField = Assert.IsType<PdfDictionary>(output.GetObject(parent.ObjectNumber));
        Assert.Equal("Parent1", FieldText(parentField, "T"));
        Assert.True(form.TryGet(PdfName.FromAscii("DR"), out QpdfObject? drRaw));
        Assert.IsType<PdfDictionary>(drRaw);
    }

    [Fact]
    public async Task MergeKeepsBothRegistrations()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-2page.pdf");
        host.AddInput(QpdfPath.From("inputs/b.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "forms-1page.pdf")), QpdfInputOwnership.Copy);
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/a.pdf"), new PdfPageSelectionOptions(1)),
             new PdfPageSource(QpdfPath.From("inputs/b.pdf"), new PdfPageSelectionOptions(1))],
            QpdfPath.From("outputs/merged.pdf"),
            CancellationToken.None);
        Assert.Equal(2, result.PageCount);
        await using QpdfDocument output = await RereadAsync(host, "outputs/merged.pdf");
        Assert.Equal(2, output.PageCount);
        List<PdfDictionary> fields = ResolveFields(output, RequireForm(RequireCatalog(output)));
        Assert.Equal(2, fields.Count);
        Assert.Equal("First", FieldText(fields[0], "T"));
        Assert.Equal("AAA", FieldText(fields[0], "V"));
        Assert.Equal("Name", FieldText(fields[1], "T"));
        Assert.Equal("Hello", FieldText(fields[1], "V"));
    }

    [Fact]
    public async Task TypedSelfMergeRenamesCollision()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/a.pdf"), new PdfPageSelectionOptions(1)),
             new PdfPageSource(QpdfPath.From("inputs/a.pdf"), new PdfPageSelectionOptions(1))],
            QpdfPath.From("outputs/merged.pdf"),
            CancellationToken.None);
        Assert.Equal(2, result.PageCount);
        await using QpdfDocument output = await RereadAsync(host, "outputs/merged.pdf");
        List<PdfDictionary> fields = ResolveFields(output, RequireForm(RequireCatalog(output)));
        Assert.Equal(2, fields.Count);
        Assert.Equal("Name", FieldText(fields[0], "T"));
        Assert.Equal("Hello", FieldText(fields[0], "V"));
        Assert.Equal("Name+1", FieldText(fields[1], "T"));
        Assert.Equal("Hello", FieldText(fields[1], "V"));
    }

    [Fact]
    public async Task ArgvThreeWayCollisionSuffixesInOrder()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--empty", "--pages", "inputs/a.pdf", "1", "inputs/a.pdf", "1", "inputs/a.pdf", "1", "--", "outputs/m.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/m.pdf");
        List<PdfDictionary> fields = ResolveFields(output, RequireForm(RequireCatalog(output)));
        Assert.Equal(3, fields.Count);
        Assert.Equal("Name", FieldText(fields[0], "T"));
        Assert.Equal("Name+1", FieldText(fields[1], "T"));
        Assert.Equal("Name+2", FieldText(fields[2], "T"));
    }

    private static byte[] BuildTwoFieldPdf(string firstName, string firstValue, string secondName, string secondValue)
    {
        byte[] contents = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 100 700 Td (P) Tj ET");
        byte[] firstAppearance = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 10 10 Td (" + firstValue + ") Tj ET");
        byte[] secondAppearance = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 10 10 Td (" + secondValue + ") Tj ET");
        return BuildFormPdf(
            " /AcroForm << /Fields [5 0 R 6 0 R] /NeedAppearances true >>",
            " /Annots [5 0 R 6 0 R]",
            contents,
            new List<(string Dict, byte[]? StreamData)>()
            {
                ("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /T (" + firstName + ") /V (" + firstValue + ") /DA (/Helv 12 Tf) /AP << /N 7 0 R >> >>", null),
                ("<< /Type /Annot /Subtype /Widget /Rect [100 500 300 520] /FT /Tx /T (" + secondName + ") /V (" + secondValue + ") /DA (/Helv 12 Tf) /AP << /N 9 0 R >> >>", null),
                ("<< /Length " + firstAppearance.Length.ToString() + " >>", firstAppearance),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
                ("<< /Length " + secondAppearance.Length.ToString() + " >>", secondAppearance),
            });
    }

    [Fact]
    public async Task MergePreexistingSuffixAdvances()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), BuildTwoFieldPdf("Name", "Hello", "Name+1", "World"), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("inputs/b.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "forms-1page.pdf")), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--empty", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--", "outputs/m.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/m.pdf");
        List<PdfDictionary> fields = ResolveFields(output, RequireForm(RequireCatalog(output)));
        Assert.Equal(3, fields.Count);
        Assert.Equal("Name", FieldText(fields[0], "T"));
        Assert.Equal("Hello", FieldText(fields[0], "V"));
        Assert.Equal("Name+1", FieldText(fields[1], "T"));
        Assert.Equal("World", FieldText(fields[1], "V"));
        Assert.Equal("Name+2", FieldText(fields[2], "T"));
        Assert.Equal("Hello", FieldText(fields[2], "V"));
    }

    [Fact]
    public async Task MergePreexistingSuffixKeepsOrderFill()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
        host.AddInput(QpdfPath.From("inputs/b.pdf"), BuildTwoFieldPdf("Name", "Hello", "Name+1", "World"), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--empty", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--", "outputs/m.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/m.pdf");
        List<PdfDictionary> fields = ResolveFields(output, RequireForm(RequireCatalog(output)));
        Assert.Equal(3, fields.Count);
        Assert.Equal("Name", FieldText(fields[0], "T"));
        Assert.Equal("Hello", FieldText(fields[0], "V"));
        Assert.Equal("Name+1", FieldText(fields[1], "T"));
        Assert.Equal("Hello", FieldText(fields[1], "V"));
        Assert.Equal("Name+1", FieldText(fields[2], "T"));
        Assert.Equal("World", FieldText(fields[2], "V"));
    }

    [Fact]
    public async Task SingleSelectionKeepsIntraDocumentDuplicates()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), BuildTwoFieldPdf("Same", "A", "Same", "B"), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf");
        List<PdfDictionary> fields = ResolveFields(output, RequireForm(RequireCatalog(output)));
        Assert.Equal(2, fields.Count);
        Assert.Equal("Same", FieldText(fields[0], "T"));
        Assert.Equal("A", FieldText(fields[0], "V"));
        Assert.Equal("Same", FieldText(fields[1], "T"));
        Assert.Equal("B", FieldText(fields[1], "V"));
    }

    [Fact]
    public async Task HierarchicalParentRenamesLikeReference()
    {
        byte[] appearance = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 10 10 Td (Hi) Tj ET");
        byte[] contents = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 100 700 Td (P) Tj ET");
        byte[] hierarchical = BuildFormPdf(
            " /AcroForm << /Fields [5 0 R] /NeedAppearances true >>",
            " /Annots [6 0 R]",
            contents,
            new List<(string Dict, byte[]? StreamData)>()
            {
                ("<< /FT /Tx /T (P) /Kids [6 0 R] /DA (/Helv 12 Tf) >>", null),
                ("<< /Type /Annot /Subtype /Widget /Parent 5 0 R /Rect [100 600 300 620] /T (K) /V (Hi) /AP << /N 7 0 R >> >>", null),
                ("<< /Length " + appearance.Length.ToString() + " >>", appearance),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            });
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), hierarchical, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("inputs/b.pdf"), hierarchical, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--empty", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--", "outputs/m.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/m.pdf");
        List<PdfDictionary> fields = ResolveFields(output, RequireForm(RequireCatalog(output)));
        Assert.Equal(2, fields.Count);
        Assert.Equal("P", FieldText(fields[0], "T"));
        Assert.Equal("P+1", FieldText(fields[1], "T"));
    }

    [Fact]
    public async Task UnnamedContainerKidRenames()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), BuildTwoFieldPdf("K", "v", "Z", "z"), QpdfInputOwnership.Copy);
        byte[] appearance = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 10 10 Td (Hi) Tj ET");
        byte[] contents = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 100 700 Td (U) Tj ET");
        host.AddInput(QpdfPath.From("inputs/b.pdf"), BuildFormPdf(
            " /AcroForm << /Fields [5 0 R] /NeedAppearances true >>",
            " /Annots [6 0 R]",
            contents,
            new List<(string Dict, byte[]? StreamData)>()
            {
                ("<< /FT /Tx /Kids [6 0 R] /DA (/Helv 12 Tf) >>", null),
                ("<< /Type /Annot /Subtype /Widget /Parent 5 0 R /Rect [100 600 300 620] /T (K) /V (Hi) /AP << /N 7 0 R >> >>", null),
                ("<< /Length " + appearance.Length.ToString() + " >>", appearance),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            }), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--empty", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--", "outputs/m.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/m.pdf");
        List<PdfDictionary> fields = ResolveFields(output, RequireForm(RequireCatalog(output)));
        Assert.Equal(3, fields.Count);
        Assert.Equal("K", FieldText(fields[0], "T"));
        Assert.Equal("Z", FieldText(fields[1], "T"));
        Assert.True(fields[2].TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw));
        PdfReference kidRef = Assert.IsType<PdfReference>(Assert.IsType<PdfArray>(kidsRaw)[0]);
        Assert.Equal("K+1", FieldText(Assert.IsType<PdfDictionary>(output.GetObject(kidRef.ObjectNumber)), "T"));
    }

    [Fact]
    public async Task ReverseOrderRenamesFlatCounterpart()
    {
        byte[] appearance = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 10 10 Td (Hi) Tj ET");
        byte[] contents = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 100 700 Td (U) Tj ET");
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), BuildFormPdf(
            " /AcroForm << /Fields [5 0 R] /NeedAppearances true >>",
            " /Annots [6 0 R]",
            contents,
            new List<(string Dict, byte[]? StreamData)>()
            {
                ("<< /FT /Tx /Kids [6 0 R] /DA (/Helv 12 Tf) >>", null),
                ("<< /Type /Annot /Subtype /Widget /Parent 5 0 R /Rect [100 600 300 620] /T (K) /V (Hi) /AP << /N 7 0 R >> >>", null),
                ("<< /Length " + appearance.Length.ToString() + " >>", appearance),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            }), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("inputs/b.pdf"), BuildTwoFieldPdf("K", "v", "Z", "z"), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--empty", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--", "outputs/m.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/m.pdf");
        List<PdfDictionary> fields = ResolveFields(output, RequireForm(RequireCatalog(output)));
        Assert.Equal(3, fields.Count);
        Assert.True(fields[0].TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw));
        PdfReference kidRef = Assert.IsType<PdfReference>(Assert.IsType<PdfArray>(kidsRaw)[0]);
        Assert.Equal("K", FieldText(Assert.IsType<PdfDictionary>(output.GetObject(kidRef.ObjectNumber)), "T"));
        Assert.Equal("K+1", FieldText(fields[1], "T"));
        Assert.Equal("Z", FieldText(fields[2], "T"));
    }

    [Fact]
    public async Task DirectKidCollisionRenamesLikeReference()
    {
        byte[] contents = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 100 700 Td (U) Tj ET");
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), BuildFormPdf(
            " /AcroForm << /Fields [5 0 R] /NeedAppearances true >>",
            " /Annots [5 0 R]",
            contents,
            new List<(string Dict, byte[]? StreamData)>()
            {
                ("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /T (D) /V (a) /AP << /N 6 0 R >> >>", null),
                ("<< /Length 13 >>", Encoding.ASCII.GetBytes("BT (Hi) Tj ET")),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            }), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("inputs/b.pdf"), BuildFormPdf(
            " /AcroForm << /Fields [5 0 R] /NeedAppearances true >>",
            " /Annots [6 0 R]",
            contents,
            new List<(string Dict, byte[]? StreamData)>()
            {
                ("<< /Kids [<< /T (D) /FT /Tx /V (b) >> 6 0 R] >>", null),
                ("<< /Type /Annot /Subtype /Widget /Parent 5 0 R /Rect [100 500 300 520] /FT /Tx /T (W2) /V (w2) /AP << /N 7 0 R >> >>", null),
                ("<< /Length 13 >>", Encoding.ASCII.GetBytes("BT (Hi) Tj ET")),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            }), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--empty", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--", "outputs/m.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/m.pdf");
        List<PdfDictionary> fields = ResolveFields(output, RequireForm(RequireCatalog(output)));
        Assert.Equal(2, fields.Count);
        Assert.Equal("D", FieldText(fields[0], "T"));
        Assert.Equal("a", FieldText(fields[0], "V"));
        Assert.True(fields[1].TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw));
        PdfArray kids = Assert.IsType<PdfArray>(kidsRaw);
        Assert.Equal(2, kids.Count);
        PdfDictionary directKid = Assert.IsType<PdfDictionary>(kids[0]);
        Assert.Equal("D+1", FieldText(directKid, "T"));
        Assert.Equal("b", FieldText(directKid, "V"));
        PdfReference w2Ref = Assert.IsType<PdfReference>(kids[1]);
        Assert.Equal("W2", FieldText(Assert.IsType<PdfDictionary>(output.GetObject(w2Ref.ObjectNumber)), "T"));
    }

    [Fact]
    public async Task DirectKidUnderRenamedParentKeepsName()
    {
        byte[] contents = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 100 700 Td (U) Tj ET");
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), BuildFormPdf(
            " /AcroForm << /Fields [5 0 R] /NeedAppearances true >>",
            " /Annots [6 0 R]",
            contents,
            new List<(string Dict, byte[]? StreamData)>()
            {
                ("<< /T (P) /Kids [<< /T (D) /FT /Tx /V (a) >> 6 0 R] >>", null),
                ("<< /Type /Annot /Subtype /Widget /Parent 5 0 R /Rect [100 600 300 620] /FT /Tx /T (W) /V (w) /AP << /N 7 0 R >> >>", null),
                ("<< /Length 13 >>", Encoding.ASCII.GetBytes("BT (Hi) Tj ET")),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            }), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("inputs/b.pdf"), BuildFormPdf(
            " /AcroForm << /Fields [5 0 R] /NeedAppearances true >>",
            " /Annots [6 0 R]",
            contents,
            new List<(string Dict, byte[]? StreamData)>()
            {
                ("<< /T (P) /Kids [<< /T (D) /FT /Tx /V (b) >> 6 0 R] >>", null),
                ("<< /Type /Annot /Subtype /Widget /Parent 5 0 R /Rect [100 600 300 620] /FT /Tx /T (W) /V (w) /AP << /N 7 0 R >> >>", null),
                ("<< /Length 13 >>", Encoding.ASCII.GetBytes("BT (Hi) Tj ET")),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            }), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--empty", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--", "outputs/m.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/m.pdf");
        List<PdfDictionary> fields = ResolveFields(output, RequireForm(RequireCatalog(output)));
        Assert.Equal(2, fields.Count);
        string[] expectedParents = ["P", "P+1"];
        string[] expectedValues = ["a", "b"];
        for (int f = 0; f < 2; f++)
        {
            Assert.Equal(expectedParents[f], FieldText(fields[f], "T"));
            Assert.True(fields[f].TryGet(PdfName.FromAscii("Kids"), out QpdfObject? fieldKidsRaw));
            PdfArray fieldKids = Assert.IsType<PdfArray>(fieldKidsRaw);
            Assert.Equal(2, fieldKids.Count);
            PdfDictionary keptKid = Assert.IsType<PdfDictionary>(fieldKids[0]);
            Assert.Equal("D", FieldText(keptKid, "T"));
            Assert.Equal(expectedValues[f], FieldText(keptKid, "V"));
            PdfReference keptWidgetRef = Assert.IsType<PdfReference>(fieldKids[1]);
            Assert.Equal("W", FieldText(Assert.IsType<PdfDictionary>(output.GetObject(keptWidgetRef.ObjectNumber)), "T"));
        }
    }

    [Fact]
    public async Task ThreeLevelTopAncestorRenames()
    {
        byte[] appearance = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 10 10 Td (Hi) Tj ET");
        byte[] contents = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 100 700 Td (P) Tj ET");
        byte[] tree = BuildFormPdf(
            " /AcroForm << /Fields [5 0 R] /NeedAppearances true >>",
            " /Annots [7 0 R]",
            contents,
            new List<(string Dict, byte[]? StreamData)>()
            {
                ("<< /FT /Tx /T (P) /Kids [6 0 R] /DA (/Helv 12 Tf) >>", null),
                ("<< /FT /Tx /Parent 5 0 R /T (Q) /Kids [7 0 R] >>", null),
                ("<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [100 600 300 620] /T (K) /V (Hi) /AP << /N 9 0 R >> >>", null),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
                ("<< /Length " + appearance.Length.ToString() + " >>", appearance),
            });
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), tree, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("inputs/b.pdf"), tree, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--empty", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--", "outputs/m.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/m.pdf");
        List<PdfDictionary> fields = ResolveFields(output, RequireForm(RequireCatalog(output)));
        Assert.Equal(2, fields.Count);
        Assert.Equal("P", FieldText(fields[0], "T"));
        Assert.Equal("P+1", FieldText(fields[1], "T"));
        Assert.True(fields[1].TryGet(PdfName.FromAscii("Kids"), out QpdfObject? midKidsRaw));
        PdfReference midRef = Assert.IsType<PdfReference>(Assert.IsType<PdfArray>(midKidsRaw)[0]);
        PdfDictionary middle = Assert.IsType<PdfDictionary>(output.GetObject(midRef.ObjectNumber));
        Assert.Equal("Q", FieldText(middle, "T"));
        Assert.True(middle.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? leafKidsRaw));
        PdfReference leafRef = Assert.IsType<PdfReference>(Assert.IsType<PdfArray>(leafKidsRaw)[0]);
        Assert.Equal("K", FieldText(Assert.IsType<PdfDictionary>(output.GetObject(leafRef.ObjectNumber)), "T"));
    }

    [Fact]
    public async Task JobMergeRenamesCollision()
    {
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost, """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/job.pdf", "pages": [{"file": ".", "range": "1"}, {"file": ".", "range": "1"}]}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/job.pdf"), out byte[] j));
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            argvHost, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", ".", "1", "--", "outputs/argv.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argv.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/argv.pdf"), out byte[] b));
        Assert.Equal(b, j);
        await using QpdfDocument output = await RereadAsync(jobHost, "outputs/job.pdf");
        List<PdfDictionary> fields = ResolveFields(output, RequireForm(RequireCatalog(output)));
        Assert.Equal("Name", FieldText(fields[0], "T"));
        Assert.Equal("Name+1", FieldText(fields[1], "T"));
    }

    [Fact]
    public async Task SplitGroupsKeepTheirOwnFields()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", ".", "2", "--split-pages", "--", "outputs/split.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument first = await RereadAsync(host, "outputs/split-1.pdf", "inputs/first.pdf");
        await using QpdfDocument second = await RereadAsync(host, "outputs/split-2.pdf", "inputs/second.pdf");
        Assert.Equal(1, first.PageCount);
        Assert.Equal(1, second.PageCount);
        PdfDictionary firstField = Assert.Single(ResolveFields(first, RequireForm(RequireCatalog(first))));
        Assert.Equal("First", FieldText(firstField, "T"));
        Assert.Equal("AAA", FieldText(firstField, "V"));
        PdfDictionary secondField = Assert.Single(ResolveFields(second, RequireForm(RequireCatalog(second))));
        Assert.Equal("Second", FieldText(secondField, "T"));
        Assert.Equal("BBB", FieldText(secondField, "V"));
    }

    [Fact]
    public async Task RemoveAcroformDropsPreservedFormButKeepsWidgets()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--remove-acroform", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf");
        PdfDictionary catalog = RequireCatalog(output);
        Assert.False(catalog.TryGet(PdfName.FromAscii("AcroForm"), out QpdfObject _));
        Assert.True(output.PageReferences[0].Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject _));
    }

    [Fact]
    public async Task FlattenAllDropsFormWithAnnotations()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--generate-appearances", "--flatten-annotations=all", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf");
        PdfDictionary catalog = RequireCatalog(output);
        Assert.False(catalog.TryGet(PdfName.FromAscii("AcroForm"), out QpdfObject _));
        Assert.False(output.PageReferences[0].Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject _));
    }

    [Fact]
    public async Task ArgvSelectionPreservesParentAndDefaultResources()
    {
        byte[] appearance = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 10 10 Td (A) Tj ET");
        byte[] contents = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 100 700 Td (P) Tj ET");
        byte[] fixture = BuildFormPdf(
            " /AcroForm << /Fields [5 0 R] /NeedAppearances true /DR << /Font << /Helv 8 0 R >> >> >>",
            " /Annots [6 0 R]",
            contents,
            new List<(string Dict, byte[]? StreamData)>()
            {
                ("<< /FT /Tx /T (Parent1) /Kids [6 0 R] /DA (/Helv 12 Tf) >>", null),
                ("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /Parent 5 0 R /V (AAA) /AP << /N 7 0 R >> >>", null),
                ("<< /Length " + appearance.Length.ToString() + " >>", appearance),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            });
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), fixture, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf");
        PdfDictionary form = RequireForm(RequireCatalog(output));
        PdfDictionary field = Assert.Single(ResolveFields(output, form));
        Assert.Equal("Parent1", FieldText(field, "T"));
        Assert.True(field.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw));
        PdfArray kids = Assert.IsType<PdfArray>(kidsRaw);
        PdfReference kid = Assert.IsType<PdfReference>(Assert.Single(kids.Items));
        PdfDictionary widget = Assert.IsType<PdfDictionary>(output.GetObject(kid.ObjectNumber));
        Assert.Equal("AAA", FieldText(widget, "V"));
        Assert.True(widget.TryGet(PdfName.FromAscii("Parent"), out QpdfObject? parentRaw));
        PdfReference parent = Assert.IsType<PdfReference>(parentRaw);
        Assert.Equal("Parent1", FieldText(Assert.IsType<PdfDictionary>(output.GetObject(parent.ObjectNumber)), "T"));
        Assert.True(form.TryGet(PdfName.FromAscii("DR"), out QpdfObject? drRaw));
        Assert.IsType<PdfDictionary>(drRaw);
    }

    [Fact]
    public async Task JobSelectionPreservesParentAndDefaultResources()
    {
        byte[] appearance = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 10 10 Td (A) Tj ET");
        byte[] contents = Encoding.ASCII.GetBytes("BT /Helv 12 Tf 100 700 Td (P) Tj ET");
        byte[] fixture = BuildFormPdf(
            " /AcroForm << /Fields [5 0 R] /NeedAppearances true /DR << /Font << /Helv 8 0 R >> >> >>",
            " /Annots [6 0 R]",
            contents,
            new List<(string Dict, byte[]? StreamData)>()
            {
                ("<< /FT /Tx /T (Parent1) /Kids [6 0 R] /DA (/Helv 12 Tf) >>", null),
                ("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /Parent 5 0 R /V (AAA) /AP << /N 7 0 R >> >>", null),
                ("<< /Length " + appearance.Length.ToString() + " >>", appearance),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            });
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), fixture, QpdfInputOwnership.Copy);
        string job = "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}]}";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf");
        PdfDictionary form = RequireForm(RequireCatalog(output));
        PdfDictionary field = Assert.Single(ResolveFields(output, form));
        Assert.Equal("Parent1", FieldText(field, "T"));
        Assert.True(form.TryGet(PdfName.FromAscii("DR"), out QpdfObject? drRaw));
        Assert.IsType<PdfDictionary>(drRaw);
    }

    [Fact]
    public async Task FormCopyYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-2page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
    }
}
