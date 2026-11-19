using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Content;

public sealed partial class ArticleThreadTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf");

    [Theory]
    [InlineData("1", "1:1,1:2", "2,1")]
    [InlineData("2", "", "")]
    [InlineData("3,1", "2:1,2:2,1:3", "3/2,1")]
    [InlineData("1,1,3", "1:1,2:1,1:2,2:2,3:3", "2,1/2,1/3")]
    public async Task SelectionProjectsReciprocalArticleLinksAndKeepsPrivateOriginals(string pages, string article, string registrations)
    {
        using InMemoryQpdfHost host = Host();
        QpdfExecutionResult result = await Run(host, "input.pdf", "--pages", ".", pages, "--", "output.pdf");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await Open(host, "output.pdf");
        AssertArticle(output, article, registrations);
        AssertPrivate(output, article.Length > 0);
    }

    [Fact]
    public async Task UnchangedRewriteKeepsArticleAndRegistrationAliases()
    {
        using InMemoryQpdfHost host = Host();
        QpdfExecutionResult result = await Run(host, "input.pdf", "output.pdf");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await Open(host, "output.pdf");
        AssertArticle(output, "1:1,1:2,3:3", "2,1//3");
        PdfDictionary catalog = Catalog(output);
        Assert.Equal(Value(catalog, "PrivateThread"), Array(output, Value(catalog, "Threads"))[0]);
        PdfDictionary first = output.PageReferences[0].Page;
        Assert.Equal(Value(first, "PrivateBeads"), Value(first, "B"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SplitPublishesArticleRingsLocalToEachPart(bool bound)
    {
        using InMemoryQpdfHost host = Host();
        host.AddInput(QpdfPath.From("patch.json"),
            """{"qpdf":[{"jsonversion":2},{"obj:4 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,100,102],"/Resources":{}}}}]}"""u8.ToArray(),
            QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = bound
            ? await Qpdf.ExecuteJobAsync(host,
                """{"inputFile":"input.pdf","updateFromJson":"patch.json","splitPages":"1","outputFile":"part.pdf"}""",
                CancellationToken.None)
            : await Run(host, "input.pdf", "--split-pages=1", "part.pdf");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument first = await Open(host, "part-1.pdf");
        await using QpdfDocument second = await Open(host, "part-2.pdf");
        await using QpdfDocument third = await Open(host, "part-3.pdf");
        AssertArticle(first, "1:1,1:2", "2,1");
        AssertArticle(second, "", "");
        AssertArticle(third, "1:3", "3");
        if (bound) { AssertPrivate(first, true); }
    }

    [Fact]
    public async Task BoundSplitDistinguishesRepeatedOccurrencesOfOnePage()
    {
        using InMemoryQpdfHost host = Host();
        host.AddInput(QpdfPath.From("patch.json"),
            """{"qpdf":[{"jsonversion":2},{"obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["3 0 R","3 0 R","5 0 R"],"/Count":3}}}]}"""u8.ToArray(),
            QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, "input.pdf", "--update-from-json=patch.json", "--split-pages=3", "part.pdf");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await Open(host, "part-1-3.pdf");
        AssertArticle(output, "1:1,2:1,1:2,2:2,3:3", "2,1/2,1/3");
        AssertPrivate(output, true);
    }

    [Fact]
    public async Task AssembledJsonRecreatesProjectedArticlesAndTheirPrivateAliases()
    {
        using InMemoryQpdfHost host = Host(false, 2);
        QpdfExecutionResult report = await Run(host, "input.pdf", "--pages", ".", "1,1,3", "--", "--json-output");
        Assert.Equal(QpdfExitCode.Success, report.ExitCode);
        host.AddInput(QpdfPath.From("report.json"), System.Text.Encoding.UTF8.GetBytes(Assert.IsType<string>(report.OutputText)),
            QpdfInputOwnership.Transfer);
        QpdfExecutionResult recreated = await Run(host, "--json-input", "report.json", "recreated.pdf");
        Assert.Equal(QpdfExitCode.Success, recreated.ExitCode);
        await using QpdfDocument output = await Open(host, "recreated.pdf");
        AssertArticle(output, "1:1,2:1,1:2,2:2,3:3", "2,1/2,1/3");
        AssertPrivate(output, true);
    }

    [Fact]
    public async Task RotationChangesOnlyTheActiveBeadRectangle()
    {
        using InMemoryQpdfHost host = Host();
        QpdfExecutionResult result = await Run(host, "input.pdf", "--rotate=90:1", "--flatten-rotation", "output.pdf");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await Open(host, "output.pdf");
        AssertArticle(output, "1:1,1:2,3:3", "2,1//3");
        AssertPrivate(output, true);
        PdfDictionary active = Dict(output, Array(output, Value(Catalog(output), "Threads"))[0]);
        PdfDictionary first = Dict(output, Value(active, "F"));
        Assert.Equal([10d, 80d, 30d, 90d], Numbers(output, Value(first, "R")));
    }

    [Fact]
    public async Task BoundRotationProjectsAnArticleAcrossRepeatedOccurrences()
    {
        using InMemoryQpdfHost host = Host();
        host.AddInput(QpdfPath.From("patch.json"),
            """{"qpdf":[{"jsonversion":2},{"obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["3 0 R","3 0 R","5 0 R"],"/Count":3}}}]}"""u8.ToArray(),
            QpdfInputOwnership.Transfer);
        QpdfExecutionResult report = await Run(host, "input.pdf", "--update-from-json=patch.json", "--rotate=90:2",
            "--flatten-rotation", "--json-output");
        Assert.Equal(QpdfExitCode.Warning, report.ExitCode);
        Assert.Contains(report.Diagnostics, item => item.Code == QpdfDiagnosticCode.RecoveryEngaged);
        host.AddInput(QpdfPath.From("report.json"), System.Text.Encoding.UTF8.GetBytes(Assert.IsType<string>(report.OutputText)),
            QpdfInputOwnership.Transfer);
        Assert.Equal(QpdfExitCode.Success, (await Run(host, "--json-input", "report.json", "recreated.pdf")).ExitCode);
        await using QpdfDocument output = await Open(host, "recreated.pdf");
        AssertArticle(output, "1:1,2:1,1:2,2:2,3:3", "2,1/2,1/3");
        AssertPrivate(output, true);
        PdfDictionary active = Dict(output, Array(output, Value(Catalog(output), "Threads"))[0]);
        PdfDictionary first = Dict(output, Value(active, "F"));
        PdfDictionary second = Dict(output, Value(first, "N"));
        Assert.Equal([10d, 10d, 20d, 30d], Numbers(output, Value(first, "R")));
        Assert.Equal([10d, 80d, 30d, 90d], Numbers(output, Value(second, "R")));
    }

    [Fact]
    public async Task BrokenReciprocalChainOmitsBothActiveRoles()
    {
        using InMemoryQpdfHost host = Host(true);
        QpdfExecutionResult result = await Run(host, "input.pdf", "--pages", ".", "1", "--", "output.pdf");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await Open(host, "output.pdf");
        AssertArticle(output, "", "");
        AssertPrivate(output, false);
    }

    private static InMemoryQpdfHost Host() => Host(false);

    private static InMemoryQpdfHost Host(bool broken) => Host(broken, 0);

    private static InMemoryQpdfHost Host(bool broken, int generation)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(Input, PdfTestDocument.Assemble(
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /Threads [6 0 R] /PrivateThread 6 0 R >>", null),
            (2, $"<< /Type /Pages /Kids [3 {generation} R 4 0 R 5 0 R] /Count 3 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 101] /Resources << >> /B 11 0 R /PrivateBeads 11 0 R >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 102] /Resources << >> >>", null),
            (5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 103] /Resources << >> /B [10 0 R] >>", null),
            (6, "<< /Type /Thread /F 8 0 R /I 7 0 R >>", null),
            (7, "<< /Title (Article) >>", null),
            (8, $"<< /Type /Bead /T 6 0 R /N 9 0 R /V 10 0 R /P 3 {generation} R /R 12 0 R /Marker 1 >>", null),
            (9, $"<< /Type /Bead /N 10 0 R /V {(broken ? 10 : 8)} 0 R /P 3 {generation} R /R [20 30 30 40] /Marker 2 >>", null),
            (10, "<< /Type /Bead /N 8 0 R /V 9 0 R /P 5 0 R /R [30 60 50 80] /Marker 3 >>", null),
            (11, "[9 0 R 8 0 R]", null),
            (12, "[10 10 20 30]", null),
        ], 1, new Dictionary<int, int> { [3] = generation }), QpdfInputOwnership.Transfer);
        return host;
    }

    private static async ValueTask<QpdfDocument> Open(InMemoryQpdfHost host, string name)
    {
        QpdfPath path = QpdfPath.From(name);
        Assert.True(host.TryGetOutput(path, out byte[] data));
        host.AddInput(path, data, QpdfInputOwnership.Transfer);
        return await QpdfDocument.OpenAsync(host, path, CancellationToken.None);
    }

    private static ValueTask<QpdfExecutionResult> Run(InMemoryQpdfHost host, params string[] argv)
        => Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);

    private static void AssertArticle(QpdfDocument output, string expectedRing, string expectedPageLists)
    {
        PdfDictionary catalog = Catalog(output);
        HashSet<PdfReference> registered = [];
        List<string> pageLists = [];
        for (int index = 0; index < output.PageCount; index++)
        {
            PdfDictionary header = output.PageReferences[index].Page;
            List<int> markers = [];
            if (header.TryGet(PdfName.FromAscii("B"), out QpdfObject raw))
            {
                foreach (QpdfObject value in Array(output, raw).BorrowedItems)
                {
                    PdfReference reference = Assert.IsType<PdfReference>(value);
                    Assert.True(registered.Add(reference));
                    PdfDictionary bead = Dict(output, value);
                    Assert.Equal(output.PageReferences[index].Source, Value(bead, "P"));
                    markers.Add(Marker(bead));
                }
            }
            pageLists.Add(string.Join(",", markers));
        }
        Assert.Equal(expectedPageLists, string.Join("/", pageLists));
        if (expectedRing.Length == 0)
        {
            Assert.False(catalog.TryGet(PdfName.FromAscii("Threads"), out _));
            Assert.Empty(registered);
            return;
        }

        PdfReference threadLink = Assert.IsType<PdfReference>(Assert.Single(Array(output, Value(catalog, "Threads")).BorrowedItems));
        PdfDictionary thread = Dict(output, threadLink);
        Assert.Equal("Article"u8.ToArray(), Assert.IsType<PdfByteString>(Value(Dict(output, Value(thread, "I")), "Title")).Value);
        PdfReference first = Assert.IsType<PdfReference>(Value(thread, "F"));
        PdfReference current = first;
        List<string> ring = [];
        do
        {
            Assert.True(registered.Remove(current));
            PdfDictionary bead = Dict(output, current);
            PdfReference page = Assert.IsType<PdfReference>(Value(bead, "P"));
            int position = output.PageReferences.ToList().FindIndex(candidate => page.Equals(candidate.Source)) + 1;
            ring.Add(FormattableString.Invariant($"{position}:{Marker(bead)}"));
            if (current.Equals(first)) { Assert.Equal(threadLink, Value(bead, "T")); }
            PdfReference next = Assert.IsType<PdfReference>(Value(bead, "N"));
            Assert.Equal(current, Value(Dict(output, next), "V"));
            current = next;
        }
        while (!current.Equals(first));
        Assert.Empty(registered);
        Assert.Equal(expectedRing, string.Join(",", ring));
    }

    private static void AssertPrivate(QpdfDocument output, bool articleExists)
    {
        PdfDictionary catalog = Catalog(output);
        QpdfObject alias = Value(catalog, "PrivateThread");
        PdfDictionary first = Dict(output, Value(Dict(output, alias), "F"));
        Assert.Equal(alias, Value(first, "T"));
        Assert.Equal([10d, 10d, 20d, 30d], Numbers(output, Value(first, "R")));
        if (articleExists) { Assert.NotEqual(alias, Array(output, Value(catalog, "Threads"))[0]); }
        foreach (var page in output.PageReferences)
        {
            if (!page.Page.TryGet(PdfName.FromAscii("PrivateBeads"), out QpdfObject registrations)) { continue; }
            PdfArray original = Array(output, registrations);
            Assert.Equal([2, 1], original.BorrowedItems.Select(value => Marker(Dict(output, value))));
            if (page.Page.TryGet(PdfName.FromAscii("B"), out QpdfObject active)) { Assert.NotEqual(registrations, active); }
        }
    }

    private static double[] Numbers(QpdfDocument output, QpdfObject value)
        => Array(output, value).BorrowedItems.Select(item => Resolve(output, item) is PdfInteger integral
            ? integral.Value : Assert.IsType<PdfReal>(Resolve(output, item)).Value).ToArray();

    private static int Marker(PdfDictionary dictionary) => (int)Assert.IsType<PdfInteger>(Value(dictionary, "Marker")).Value;

    private static PdfDictionary Catalog(QpdfDocument output) => Dict(output, Value(output.Engine.Trailer, "Root"));

    private static PdfDictionary Dict(QpdfDocument output, QpdfObject value) => Assert.IsType<PdfDictionary>(Resolve(output, value));

    private static PdfArray Array(QpdfDocument output, QpdfObject value) => Assert.IsType<PdfArray>(Resolve(output, value));

    private static QpdfObject Value(PdfDictionary dictionary, string name)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value), name);
        return value;
    }

    private static QpdfObject Resolve(QpdfDocument output, QpdfObject value) => value is PdfReference link ? output.GetObject(link) : value;
}
