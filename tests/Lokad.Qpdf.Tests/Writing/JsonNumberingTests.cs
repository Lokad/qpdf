using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonNumberingTests
{
    private static readonly QpdfPath Input = QpdfPath.From("graph.json"), Output = QpdfPath.From("output.pdf");
    private static readonly byte[] Content = "% retained page program\nq Q\n"u8.ToArray();
    private static readonly byte[] Metadata = "<x>clear-metadata-marker</x>"u8.ToArray();

    [Theory]
    [InlineData("disable")]
    [InlineData("generate")]
    public async Task DiscardedDeclarationsDoNotAffectXrefSizeOrPublishedEdges(string objectStreams)
    {
        string[] flags = ["--object-streams=" + objectStreams, "--static-id"];
        byte[] baseline = await Publish(0, false, flags);
        foreach (int count in new[] { 32, 512 })
        {
            Assert.Equal(baseline, await Publish(count, false, flags));
            Assert.Equal(baseline, await Publish(count, true, flags));
        }

        using InMemoryQpdfHost host = new();
        await using QpdfDocument output = await Read(host, baseline, null);
        Assert.Equal(objectStreams == "generate",
            output.Engine.XRefEntries.Values.Any(entry => entry.Kind == XrefEntryKind.Compressed));
        AssertCompact(output);
        AssertPublicationRoles(output);
    }

    [Fact]
    public async Task QdfCommentsRetainTheDeclaredPairsAfterCompaction()
    {
        byte[] bytes = await Publish(32, true, ["--qdf", "--normalize-content=n", "--object-streams=disable"]);
        string text = Encoding.ASCII.GetString(bytes);
        Assert.Contains("%% Original object ID: 3 7\n", text, StringComparison.Ordinal);
        Assert.Contains("%% Original object ID: 13 2\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("%% Original object ID: 1000 4\n", text, StringComparison.Ordinal);
        using InMemoryQpdfHost host = new();
        await using QpdfDocument output = await Read(host, bytes, null);
        AssertPublicationRoles(output);
    }

    [Fact]
    public async Task CompactionUsesFinalEncryptionAndClearMetadataIdentities()
    {
        byte[] bytes = await Publish(32, true, ["--object-streams=generate", "--encrypt", "reader", "owner", "256",
            "--cleartext-metadata", "--"]);
        Assert.True(bytes.AsSpan().IndexOf(Metadata) >= 0);
        Assert.True(bytes.AsSpan().IndexOf(Content) < 0);
        using InMemoryQpdfHost host = new();
        await using QpdfDocument output = await Read(host, bytes, QpdfPassword.FromText("reader", false));
        Assert.False(output.Encryption?.EncryptMetadata);
        AssertCompact(output);
        AssertPublicationRoles(output);
    }

    [Fact]
    public async Task UnpublishedDeclarationsStillCountAgainstTheObjectLimit()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Declaration(32, true), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Input.Value, "--json-input", Output.Value]),
            new(QpdfLimits.Default with { MaxContainerSize = 16 }), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.False(host.TryGetOutput(Output, out _));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static async Task<byte[]> Publish(int count, bool before, string[] flags)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Declaration(count, before), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new([Input.Value, "--json-input", "--stream-data=preserve", .. flags, Output.Value]), CancellationToken.None);
        Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join("\n", result.Diagnostics.Select(item => item.Message)));
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        return bytes;
    }

    private static byte[] Declaration(int count, bool before)
    {
        var members = new List<string> { """ "trailer":{"value":{"/Root":"1 0 R","/Info":"13 2 R"}} """ };
        string[] detached = Enumerable.Range(1000, count)
            .Select(number => FormattableString.Invariant($"\"obj:{number} 4 R\":{{\"value\":{{\"/Detached\":true}}}}")).ToArray();
        if (before) { members.AddRange(detached); }
        members.AddRange([
            """ "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Metadata":"7 0 R","/Private":"3 7 R"}} """,
            """ "obj:2 0 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["3 7 R"]}} """,
            """ "obj:3 7 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,100,100],"/Resources":{},"/Contents":"5 0 R"}} """,
            $$$""" "obj:5 0 R":{"stream":{"dict":{},"data":"{{{Convert.ToBase64String(Content)}}}"}} """,
            $$$""" "obj:7 0 R":{"stream":{"dict":{"/Type":"/Metadata","/Subtype":"/XML"},"data":"{{{Convert.ToBase64String(Metadata)}}}"}} """,
            """ "obj:13 2 R":{"value":{"/Title":"u:kept-title"}} """
        ]);
        if (!before) { members.AddRange(detached); }
        return Encoding.UTF8.GetBytes("""{"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{""" + string.Join(',', members) + "}]}");
    }

    private static ValueTask<QpdfDocument> Read(InMemoryQpdfHost host, byte[] bytes, QpdfPassword? password)
    {
        host.AddInput(Output, bytes, QpdfInputOwnership.Transfer);
        return QpdfDocument.OpenAsync(host, Output, password, QpdfLimits.Default, CancellationToken.None);
    }

    private static void AssertCompact(QpdfDocument document)
    {
        int live = document.Engine.XRefEntries.Values.Count(entry => entry.Kind != XrefEntryKind.Free);
        Assert.Equal(live + 1L, Assert.IsType<PdfInteger>(Entry(document.Engine.Trailer, "Size")).Value);
    }

    private static void AssertPublicationRoles(QpdfDocument document)
    {
        Assert.Equal(1, document.PageCount);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(document, document.Engine.Trailer, "Root"));
        Assert.Equal(document.PageReferences[0].Source, Entry(catalog, "Private"));
        PdfDictionary page = document.PageReferences[0].Page;
        Assert.Equal(Content, StreamObservation.Decode(Assert.IsType<PdfStream>(Resolve(document, page, "Contents"))));
        Assert.Equal(Metadata, StreamObservation.Decode(Assert.IsType<PdfStream>(Resolve(document, catalog, "Metadata"))));
        PdfDictionary info = Assert.IsType<PdfDictionary>(Resolve(document, document.Engine.Trailer, "Info"));
        Assert.Equal("kept-title"u8.ToArray(), Assert.IsType<PdfByteString>(Entry(info, "Title")).Value);
    }

    private static QpdfObject Resolve(QpdfDocument document, PdfDictionary dictionary, string name)
        => document.GetObject(Assert.IsType<PdfReference>(Entry(dictionary, name)));

    private static QpdfObject Entry(PdfDictionary dictionary, string name)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value));
        return value;
    }
}
