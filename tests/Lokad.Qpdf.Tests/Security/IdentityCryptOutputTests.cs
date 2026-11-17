using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Security;

public sealed class IdentityCryptOutputTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output");
    private static readonly byte[] Plain = "q Q\n"u8.ToArray();

    internal static byte[] Fixture(string kind)
    {
        string fields = kind switch
        {
            "none" => "", "bare" => "/Filter /Crypt", "empty" => "/Filter /Crypt /DecodeParms []",
            "null" => "/Filter /Crypt /DecodeParms << /Name null >>",
            "identity" => "/Filter /Crypt /DecodeParms << /Name /Identity >>",
            "flate" => "/Filter [/Crypt /FlateDecode] /DecodeParms [<< /Name /Identity >> null]",
            "indirect" => "/Filter 7 0 R /DecodeParms 8 0 R /PrivateFilter 7 0 R /PrivateParms 8 0 R",
            "unknown" => "/Filter /Crypt /DecodeParms << /Name /Unknown >>",
            "duplicate" => "/Filter [/Crypt /Crypt] /DecodeParms [<< /Name /Identity >> null]",
            "misordered" => "/Filter [/FlateDecode /Crypt] /DecodeParms [null << /Name /Identity >>]",
            "wrong-name" => "/Filter /Crypt /DecodeParms << /Name (Identity) >>",
            _ => throw new InvalidOperationException("Unknown crypt fixture.")
        };
        byte[] data = kind is "flate" or "indirect" ? Convert.FromHexString("789C2B5408E4020002D400ED") : Plain;
        return PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> /Contents 4 0 R >>", null),
            (4, "<< /Length " + data.Length + " " + fields + " >>", data), (5, "null", null), (6, "null", null),
            (7, "[/Crypt /FlateDecode]", null), (8, "[9 0 R null]", null),
            (9, "<< /Name 10 0 R >>", null), (10, "/Identity", null)], 1, null, "1.7", "");
    }

    private static QpdfOutputEncryption Protect() => QpdfOutputEncryption.CreateAes256(
        QpdfPassword.FromText("reader"), QpdfPassword.FromText("owner"), -4, true);

    private static async Task AssertOutput(InMemoryQpdfHost host, string kind, bool linearized, bool opaque)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        int contentNumber;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), QpdfPassword.FromText("reader"), QpdfLimits.Default, CancellationToken.None))
        {
            Assert.Equal(1, document.PageCount); Assert.Equal(6, Assert.IsType<QpdfEncryptionInfo>(document.Encryption).Revision);
            QpdfObject Resolve(QpdfObject raw) => raw is PdfReference reference ? document.GetObject(reference.ObjectNumber) : raw;
            QpdfObject Get(PdfDictionary node, string key) { Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject raw)); return Resolve(raw); }
            Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject content));
            PdfReference reference = Assert.IsType<PdfReference>(content); contentNumber = reference.ObjectNumber;
            PdfStream stream = Assert.IsType<PdfStream>(Resolve(reference));
            if (opaque) { Assert.Equal(kind is "flate" or "indirect" ? Convert.FromHexString("789C2B5408E4020002D400ED") : Plain, stream.Data); }
            if (stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject filter))
            {
                QpdfObject resolved = Resolve(filter);
                if (resolved is PdfArray array) { Assert.Equal(PdfName.FromAscii("FlateDecode"), Assert.Single(array.Items)); }
                else { Assert.Equal(PdfName.FromAscii("FlateDecode"), Assert.IsType<PdfName>(resolved)); }
            }
            if (kind == "indirect")
            {
                PdfArray originalFilters = Assert.IsType<PdfArray>(Get(stream.Dictionary, "PrivateFilter"));
                Assert.Equal(2, originalFilters.Count); Assert.Equal(PdfName.FromAscii("Crypt"), originalFilters[0]);
                Assert.Equal(2, Assert.IsType<PdfArray>(Get(stream.Dictionary, "PrivateParms")).Count);
            }
        }
        QpdfJsonResult json = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("read"),
            new QpdfJsonOptions(2, ["qpdf"], "inline", null, [], false) { Password = QpdfPassword.FromText("reader") }, CancellationToken.None);
        using (JsonDocument parsed = JsonDocument.Parse(json.Json))
        {
            JsonElement stream = parsed.RootElement.GetProperty("qpdf")[1].GetProperty("obj:" + contentNumber + " 0 R").GetProperty("stream");
            Assert.Equal(Plain, stream.GetProperty("data").GetBytesFromBase64());
        }
        QpdfCheckResult check = await Qpdf.CheckAsync(host, QpdfPath.From("read"), new QpdfCheckOptions { Password = QpdfPassword.FromText("reader") }, QpdfLimits.Default, CancellationToken.None);
        Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed);
        if (linearized) { Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(check.Checks, item => item.Name == "linearization").Status); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("bare")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("identity")]
    [InlineData("flate")]
    [InlineData("indirect")]
    public async Task TypedEncryptionConsumesIdentityAndPreservesEncodedBytesAndPrivateAliases(string kind)
    {
        byte[] input = Fixture(kind), original = input.ToArray();
        using InMemoryQpdfHost host = new(); host.AddInput(Input, input, QpdfInputOwnership.Copy);
        await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), null, Protect(), CancellationToken.None);
        Assert.Equal(original, input); await AssertOutput(host, kind, false, true);
    }

    [Theory]
    [InlineData("packed")]
    [InlineData("qdf")]
    [InlineData("linear")]
    [InlineData("linear-packed")]
    [InlineData("job")]
    [InlineData("merge")]
    public async Task PhysicalWritersAndJobAdapterUseThePreparedHeader(string mode)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(Input, Fixture("indirect"), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result;
        if (mode == "job")
        { result = await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","encrypt":{"userPassword":"reader","ownerPassword":"owner","256bit":{}}}""", CancellationToken.None); }
        else
        {
            List<string> argv = mode == "merge" ? ["--empty", "--pages", "input", "1", "--"] : ["input"];
            argv.AddRange(["--encrypt", "reader", "owner", "256", "--"]);
            if (mode is "packed" or "linear-packed") { argv.Add("--object-streams=generate"); }
            if (mode.StartsWith("linear", StringComparison.Ordinal)) { argv.Add("--linearize"); }
            if (mode == "qdf") { argv.Add("--qdf"); }
            argv.Add("output"); result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        }
        Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        await AssertOutput(host, "indirect", mode.StartsWith("linear", StringComparison.Ordinal), false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JsonCreationAndUpdateNormalizeAuthoredIdentityStages(bool update)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(Input, Fixture("none"), QpdfInputOwnership.Transfer);
        byte[] json = Encoding.UTF8.GetBytes("""{"qpdf":[{"jsonversion":2,"pdfversion":"1.7"},{"obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R"}},"obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["3 0 R"],"/Count":1}},"obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,80,40],"/Resources":{},"/Contents":"4 0 R"}},"obj:4 0 R":{"stream":{"dict":{"/Filter":"/Crypt","/DecodeParms":{"/Name":"/Identity"}},"data":"cSBRCg=="}},"trailer":{"value":{"/Root":"1 0 R","/Size":5}}}]}""");
        QpdfPath path = QpdfPath.From("json"); host.AddInput(path, json, QpdfInputOwnership.Transfer);
        if (update) { await Qpdf.UpdatePdfFromJsonAsync(host, Input, path, Output, null, Protect(), CancellationToken.None); }
        else { await Qpdf.CreatePdfFromJsonAsync(host, path, Output, Protect(), QpdfLimits.Default, CancellationToken.None); }
        await AssertOutput(host, "identity", false, false);
    }

    private sealed class Host(InMemoryQpdfHost storage) : IQpdfHost
    {
        internal int Created;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Created++; return storage.CreateOutputAsync(path, token); }
    }

    private static async Task Seed(InMemoryQpdfHost host, string kind)
    {
        host.AddInput(Input, Fixture(kind), QpdfInputOwnership.Transfer);
        await using IQpdfOutputTransaction previous = await host.CreateOutputAsync(Output, CancellationToken.None);
        await previous.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await previous.CommitAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("unknown", false)]
    [InlineData("duplicate", true)]
    [InlineData("misordered", false)]
    [InlineData("wrong-name", true)]
    public async Task UnknownAndMalformedCryptStagesPreserveThePriorOutput(string kind, bool job)
    {
        using InMemoryQpdfHost storage = new(); await Seed(storage, kind); Host host = new(storage);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","encrypt":{"userPassword":"reader","ownerPassword":"owner","256bit":{}}}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--encrypt", "reader", "owner", "256", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(0, host.Created); Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.True(storage.TryGetOutput(Output, out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
    }
}
