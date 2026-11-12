using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed partial class LegacyDocumentTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf"), Template = QpdfPath.From("template.pdf");

    [Theory]
    [MemberData(nameof(LegacyEncryptionTests.Vectors), MemberType = typeof(LegacyEncryptionTests))]
    public async Task ReadersAuthenticateBothRolesAndExposeNativePlaintext(string file, string dictionary, string userHex, string ownerHex, string keyHex, string idHex)
    {
        Assert.NotEmpty(dictionary); Assert.NotEmpty(idHex); using InMemoryQpdfHost host = new(); Add(host, Input, file);
        string vectorsFile = file.Contains("-key", StringComparison.Ordinal) ? "security-r3-key-vectors.json" : file.StartsWith("security-r2-", StringComparison.Ordinal) ? "security-r2-vectors.json" : "security-r3-vectors.json";
        using JsonDocument vectors = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", vectorsFile)));
        JsonElement vector = vectors.RootElement.EnumerateArray().Single(item => item.GetProperty("file").GetString() == file);
        foreach ((string hex, bool owner) in new[] { (userHex, false), (ownerHex, true) })
        {
            byte[] bytes = Convert.FromHexString(hex);
            string text = file.EndsWith("-pdfdoc.pdf", StringComparison.Ordinal) ? owner ? "owneré‚" : "café•" : Encoding.UTF8.GetString(bytes);
            foreach (QpdfPassword password in new[] { QpdfPassword.FromBytes(bytes), QpdfPassword.FromText(text) })
            {
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, password, QpdfLimits.Default, CancellationToken.None);
                Assert.Equal(1, document.PageCount); Assert.Equal(file.StartsWith("security-r2-", StringComparison.Ordinal) ? 2 : 3, document.Encryption?.Revision);
                Assert.Equal(keyHex.Length * 4, document.Encryption?.KeyLengthBits); Assert.Equal(owner, document.Encryption?.OwnerPasswordMatched);
                Assert.True(document.Encryption?.EncryptMetadata);
                foreach (JsonElement stream in vector.GetProperty("streams").EnumerateArray())
                { Assert.Equal(Convert.FromBase64String(stream.GetProperty("plainBase64").GetString() ?? throw new InvalidDataException()), Assert.IsType<PdfStream>(document.GetObject(stream.GetProperty("number").GetInt32())).Data); }
                AssertTitle(document, file.Contains("-key", StringComparison.Ordinal) ? "LegacyTitle" : "TitleText");
            }
            Assert.Equal(Convert.FromHexString(hex), bytes);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> WriterCases()
    {
        foreach (int revision in new[] { 2, 3 })
        foreach (bool job in new[] { false, true })
        foreach (string mode in new[] { "classic", "objects", "qdf", "linearized" }) { yield return [revision, job, mode]; }
    }

    [Theory]
    [MemberData(nameof(WriterCases))]
    public async Task SharedWritersPreserveLegacyKeysAndPasswords(int revision, bool job, string mode)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, $"security-r{revision}-ascii.pdf");
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value, ["password"] = "reader" };
        List<string> argv = [Input.Value, Output.Value, "--password=reader"];
        fields["objectStreams"] = mode is "objects" or "linearized" ? "generate" : "disable"; argv.Add("--object-streams=" + fields["objectStreams"]);
        if (mode == "qdf") { fields["qdf"] = ""; argv.Add("--qdf"); }
        if (mode == "linearized") { fields["linearize"] = ""; argv.Add("--linearize"); }
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); await VerifyOutput(host, revision, ["reader", "fixture-owner"]);
        QpdfCheckResult check = await Qpdf.CheckAsync(host, QpdfPath.From("copy.pdf"), new QpdfCheckOptions { Password = QpdfPassword.FromText("reader") }, QpdfLimits.Default, CancellationToken.None);
        Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed);
        if (mode == "linearized") { Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(check.Checks, item => item.Name == "linearization").Status); }
    }

    [Theory]
    [InlineData(2, "remove")]
    [InlineData(3, "remove")]
    [InlineData(2, "new")]
    [InlineData(3, "new")]
    [InlineData(2, "template")]
    [InlineData(3, "template")]
    public async Task TypedPoliciesRemoveReplaceOrCopyLegacyProtection(int revision, string policy)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, $"security-r{revision}-ascii.pdf"); Add(host, Template, $"security-r{revision}-empty.pdf");
        QpdfOutputEncryption encryption = policy switch
        {
            "remove" => QpdfOutputEncryption.Remove,
            "new" => QpdfOutputEncryption.CreateAes256(QpdfPassword.FromText("new-reader"), QpdfPassword.FromText("new-owner"), -4, false),
            _ => QpdfOutputEncryption.CopyFrom(Template, null),
        };
        _ = await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), QpdfPassword.FromText("reader"), encryption, CancellationToken.None);
        await VerifyOutput(host, policy == "remove" ? null : policy == "new" ? 6 : revision,
            policy == "remove" ? [""] : policy == "new" ? ["new-reader", "new-owner"] : ["", "fixture-owner"]);
    }

    public static IEnumerable<object[]> ShortKeyCases() => LegacyEncryptionTests.Vectors().Where(vector => ((string)vector[0]).Contains("-key", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(ShortKeyCases))]
    public async Task TemplateCopyingRetainsEveryHistoricalKeyLength(string file, string dictionary, string userHex, string ownerHex, string keyHex, string idHex)
    {
        Assert.NotEmpty(dictionary); Assert.NotEmpty(userHex); Assert.NotEmpty(idHex);
        using InMemoryQpdfHost host = new(); Add(host, Input, "meta-1page.pdf"); Add(host, Template, file);
        _ = await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), null,
            QpdfOutputEncryption.CopyFrom(Template, QpdfPassword.FromBytes(Convert.FromHexString(ownerHex))), CancellationToken.None);
        await VerifyOutput(host, 3, ["reader", "fixture-owner"]);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy.pdf"), QpdfPassword.FromText("reader"), QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(keyHex.Length * 4, document.Encryption?.KeyLengthBits);
    }

    private static async Task VerifyOutput(InMemoryQpdfHost host, int? revision, string[] passwords)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); QpdfPath copy = QpdfPath.From("copy.pdf"); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
        foreach (string password in passwords)
        {
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(1, document.PageCount); Assert.Equal(revision, document.Encryption?.Revision); AssertTitle(document, "TitleText");
            if (revision is 2 or 3) { Assert.Equal(PdfCipher.Rc4, document.Engine.Security?.Parameters.Strings); Assert.True(document.Encryption?.EncryptMetadata); }
            PdfStream content = Assert.IsType<PdfStream>(document.GetObject(Ref(document.PageReferences[0].Page, "Contents")));
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            using FilterPlan filters = FilterChain.Resolve(content.Dictionary, document.Engine, governor, CancellationToken.None);
            Assert.Equal("BT (Page) Tj ET"u8.ToArray(), FilterChain.Decode(content.Data, filters, QpdfLimits.Default, governor, memory));
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }
    private static void AssertTitle(QpdfDocument document, string expected)
    {
        PdfDictionary info = Assert.IsType<PdfDictionary>(document.GetObject(Ref(document.Engine.Trailer, "Info")));
        Assert.True(info.TryGet(PdfName.FromAscii("Title"), out QpdfObject title)); Assert.Equal(Encoding.ASCII.GetBytes(expected), Assert.IsType<PdfByteString>(title).Value);
    }
    private static int Ref(PdfDictionary dictionary, string name)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return Assert.IsType<PdfReference>(value).ObjectNumber; }
    private static void Add(InMemoryQpdfHost host, QpdfPath path, string fixture)
        => host.AddInput(path, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)), QpdfInputOwnership.Copy);
}
