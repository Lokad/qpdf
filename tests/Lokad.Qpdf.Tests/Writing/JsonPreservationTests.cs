using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonPreservationTests
{
    private static readonly QpdfPath Declaration = QpdfPath.From("graph.json");
    private static readonly QpdfPath Source = QpdfPath.From("source.pdf");
    private static readonly QpdfPath Output = QpdfPath.From("part.pdf");

    private const string Graph = """
        {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
          "trailer":{"value":{"/Root":"1 0 R"}},
          "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Anchor":"20 0 R"}},
          "obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["3 0 R","5 0 R"],"/Count":2}},
          "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,100,100],"/Contents":"4 0 R","/Resources":{}}},
          "obj:4 0 R":{"stream":{"dict":{},"data":"JSVGSVJTVAo="}},
          "obj:5 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,100,100],"/Contents":"6 0 R","/Resources":{}}},
          "obj:6 0 R":{"stream":{"dict":{},"data":"JSVTRUNPTkQK"}},
          "obj:20 0 R":{"value":{"/Live":"u:AlwaysLive"}},
          "obj:21 0 R":{"value":{"/Orphan":"u:AuthoredOrphan","/Body":"23 0 R","/First":"3 0 R","/Last":"5 0 R"}},
          "obj:22 0 R":{"value":{"/Other":"u:OtherOrphan","/Body":"23 0 R"}},
          "obj:23 0 R":{"stream":{"dict":{},"data":"AP9kYXRh"}},
          "obj:24 0 R":{"stream":{"dict":{"/Type":"/XRef","/PrivateFrame":true},"data":"UHJpdmF0ZQ=="}}
        }]}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandCreationSelectsDetachedRootPolicy(bool preserve)
    {
        using InMemoryQpdfHost host = CreateHost();
        string[] flags = preserve ? ["--preserve-unreferenced"] : [];
        await Run(host, [Declaration.Value, "--json-input", .. flags, Output.Value]);
        await using QpdfDocument document = await OpenOutput(host, Output);
        Assert.Equal(2, document.PageCount);
        Assert.NotNull(FindDictionary(document, "Live"));
        if (preserve) { AssertSharedBody(document); }
        else
        {
            Assert.Null(FindDictionary(document, "Orphan"));
            Assert.Null(FindDictionary(document, "Other"));
        }
    }

    [Fact]
    public async Task TypedCreationRetainsDetachedDeclarations()
    {
        using InMemoryQpdfHost host = CreateHost();
        await Qpdf.CreatePdfFromJsonAsync(host, Declaration, Output, CancellationToken.None);
        await using QpdfDocument document = await OpenOutput(host, Output);
        AssertSharedBody(document);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyRetainedPackingRaisesTheHeaderVersion(bool preserve)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Declaration, """
            {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
              "trailer":{"value":{"/Root":"1 0 R"}},
              "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":{"/Type":"/Pages","/Kids":[],"/Count":0}}},
              "obj:2 0 R":{"value":{"/Orphan":true}}
            }]}
            """u8.ToArray(), QpdfInputOwnership.Transfer);
        string[] flags = preserve ? ["--preserve-unreferenced"] : [];
        await Run(host, [Declaration.Value, "--json-input", "--object-streams=generate", .. flags, Output.Value]);
        await using QpdfDocument document = await OpenOutput(host, Output);
        Assert.Equal(0, document.PageCount);
        Assert.Equal(preserve ? 5 : 4, document.HeaderVersionParts.Minor);
        Assert.Equal(preserve, document.Engine.XRefEntries.Values.Any(entry => entry.Kind == XrefEntryKind.Compressed));
        Assert.Equal(preserve, FindDictionary(document, "Orphan") is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationSplitOnlySharesDetachedRootsWhenRequested(bool preserve)
    {
        using InMemoryQpdfHost host = CreateHost();
        if (preserve)
        {
            QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """
                {"inputFile":"graph.json","jsonInput":"","splitPages":"1","preserveUnreferenced":"","outputFile":"part.pdf"}
                """, CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        else { await Run(host, [Declaration.Value, "--json-input", "--split-pages=1", Output.Value]); }
        for (int page = 1; page <= 2; page++)
        {
            await using QpdfDocument document = await OpenOutput(host, QpdfPath.From($"part-{page}.pdf"));
            Assert.Equal(1, document.PageCount);
            if (preserve)
            {
                AssertSharedBody(document);
                PdfDictionary marker = Assert.IsType<PdfDictionary>(FindDictionary(document, "Orphan"));
                Assert.IsType<PdfReference>(Entry(marker, page == 1 ? "First" : "Last"));
                Assert.IsType<PdfNull>(Entry(marker, page == 1 ? "Last" : "First"));
            }
            else { Assert.Null(FindDictionary(document, "Orphan")); }
            string program = Assert.Single(document.Engine.XRefEntries.Where(entry => entry.Value.Kind != XrefEntryKind.Free)
                .Select(entry => document.GetObject(entry.Key)).OfType<PdfStream>()
                .Select(stream => Encoding.ASCII.GetString(StreamObservation.Decode(stream))),
                text => text.StartsWith("%%", StringComparison.Ordinal));
            Assert.Equal(page == 1 ? "%%FIRST\n" : "%%SECOND\n", program);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UpdateRetiresSourceRolesButPreservesPrivateAliases(bool preserve, bool aliasOldTree)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Source, PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /OldCatalog true >>", null),
            (2, "<< /Type /Pages /Kids 7 0 R /Count 1 /OldPages true >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /OldLeaf true >>", null),
            (4, "<< /Type /Pages /Parent 2 0 R /Kids [] /Count 0 /OldEmpty true >>", null),
            (5, "<< /SourceOrphan true >>", null),
            (6, "<< /Type /Pages /Kids [] /Count 0 /PrivatePages true >>", null),
            (7, "[3 0 R 4 0 R]", null)
        ], 1), QpdfInputOwnership.Transfer);
        string patch = """
            {"qpdf":[{"jsonversion":2},{
              "trailer":{"value":{"/Root":"10 0 R"}},
              "obj:10 0 R":{"value":{"/Type":"/Catalog","/Pages":"11 0 R" ALIAS}},
              "obj:11 0 R":{"value":{"/Type":"/Pages","/Kids":["12 0 R"],"/Count":1}},
              "obj:12 0 R":{"value":{"/Type":"/Page","/Parent":"11 0 R","/MediaBox":[0,0,100,100]}},
              "obj:13 0 R":{"value":{"/PatchOrphan":true}}
            }]}
            """.Replace(" ALIAS", aliasOldTree ? ",\"/Archive\":\"2 0 R\"" : "", StringComparison.Ordinal);
        host.AddInput(Declaration, Encoding.UTF8.GetBytes(patch), QpdfInputOwnership.Transfer);
        string[] flags = preserve ? ["--preserve-unreferenced"] : [];
        await Run(host, [Source.Value, "--update-from-json=graph.json", .. flags, Output.Value]);
        await using QpdfDocument document = await OpenOutput(host, Output);
        Assert.Equal(1, document.PageCount);
        Assert.Null(FindDictionary(document, "OldCatalog"));
        Assert.Equal(aliasOldTree, FindDictionary(document, "OldPages") is not null);
        Assert.Equal(aliasOldTree, FindDictionary(document, "OldEmpty") is not null);
        Assert.Equal(aliasOldTree, FindDictionary(document, "OldLeaf") is not null);
        Assert.Equal(preserve, FindDictionary(document, "SourceOrphan") is not null);
        Assert.Equal(preserve, FindDictionary(document, "PatchOrphan") is not null);
        Assert.Equal(preserve, FindDictionary(document, "PrivatePages") is not null);
        int live = document.Engine.XRefEntries.Values.Count(entry => entry.Kind != XrefEntryKind.Free);
        Assert.Equal(live + 1L, Assert.IsType<PdfInteger>(Entry(document.Engine.Trailer, "Size")).Value);
    }

    [Fact]
    public async Task DecryptedUpdateRetiresPhysicalFramesButRetainsPrivateXrefStream()
    {
        using InMemoryQpdfHost host = CreateHost();
        await Run(host, [Declaration.Value, "--json-input", "--preserve-unreferenced", "--object-streams=generate",
            "--encrypt", "reader", "owner", "256", "--", Source.Value]);
        Assert.True(host.TryGetOutput(Source, out byte[] source));
        host.AddInput(Source, source, QpdfInputOwnership.Transfer);
        QpdfPassword password = QpdfPassword.FromText("reader", false);
        await using (QpdfDocument original = await QpdfDocument.OpenAsync(host, Source, password, QpdfLimits.Default, CancellationToken.None))
        {
            Assert.Contains(original.Engine.XRefEntries.Keys, original.Engine.IsCrossReferenceStream);
            Assert.Contains(original.Engine.XRefEntries.Values, entry => entry.Kind == XrefEntryKind.Compressed);
            Assert.IsType<PdfReference>(Entry(original.Engine.Trailer, "Encrypt"));
        }

        host.AddInput(QpdfPath.From("empty-patch.json"), """{"qpdf":[{"jsonversion":2},{}]}"""u8.ToArray(), QpdfInputOwnership.Transfer);
        await Run(host, [Source.Value, "--password=reader", "--update-from-json=empty-patch.json",
            "--preserve-unreferenced", "--decrypt", "--object-streams=disable", Output.Value]);
        await using QpdfDocument result = await OpenOutput(host, Output);
        Assert.Equal(2, result.PageCount);
        Assert.False(result.Engine.Trailer.TryGet(PdfName.FromAscii("Encrypt"), out _));
        QpdfObject[] values = result.Engine.XRefEntries.Where(entry => entry.Value.Kind != XrefEntryKind.Free)
            .Select(entry => result.GetObject(entry.Key)).ToArray();
        Assert.DoesNotContain(values.OfType<PdfDictionary>(), value => HasName(value, "Filter", "Standard"));
        Assert.DoesNotContain(values.OfType<PdfStream>(), value => HasName(value.Dictionary, "Type", "ObjStm"));
        PdfStream xref = Assert.Single(values.OfType<PdfStream>(), value => HasName(value.Dictionary, "Type", "XRef"));
        Assert.IsType<PdfBoolean>(Entry(xref.Dictionary, "PrivateFrame"));
        Assert.Equal("Private"u8.ToArray(), StreamObservation.Decode(xref));
        AssertSharedBody(result);
    }

    private static InMemoryQpdfHost CreateHost()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(Declaration, Encoding.UTF8.GetBytes(Graph), QpdfInputOwnership.Transfer);
        return host;
    }

    private static async Task Run(InMemoryQpdfHost host, string[] argv)
    {
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join("\n", result.Diagnostics.Select(item => item.Message)));
    }

    private static async Task<QpdfDocument> OpenOutput(InMemoryQpdfHost host, QpdfPath path)
    {
        Assert.True(host.TryGetOutput(path, out byte[] bytes));
        QpdfPath read = QpdfPath.From("read-" + path.Value);
        host.AddInput(read, bytes, QpdfInputOwnership.Transfer);
        return await QpdfDocument.OpenAsync(host, read, CancellationToken.None);
    }

    private static PdfDictionary? FindDictionary(QpdfDocument document, string key)
        => document.Engine.XRefEntries.Where(entry => entry.Value.Kind != XrefEntryKind.Free)
            .Select(entry => document.GetObject(entry.Key)).OfType<PdfDictionary>()
            .SingleOrDefault(value => value.TryGet(PdfName.FromAscii(key), out _));

    private static QpdfObject Entry(PdfDictionary value, string key)
    {
        Assert.True(value.TryGet(PdfName.FromAscii(key), out QpdfObject entry));
        return entry;
    }

    private static bool HasName(PdfDictionary value, string key, string name)
        => value.TryGet(PdfName.FromAscii(key), out QpdfObject raw) && raw is PdfName actual && actual.DisplayText == name;

    private static void AssertSharedBody(QpdfDocument document)
    {
        PdfDictionary orphan = Assert.IsType<PdfDictionary>(FindDictionary(document, "Orphan"));
        PdfDictionary other = Assert.IsType<PdfDictionary>(FindDictionary(document, "Other"));
        PdfReference body = Assert.IsType<PdfReference>(Entry(orphan, "Body"));
        Assert.Equal(body, Entry(other, "Body"));
        Assert.Equal(new byte[] { 0, 255, 100, 97, 116, 97 },
            StreamObservation.Decode(Assert.IsType<PdfStream>(document.GetObject(body))));
    }
}
