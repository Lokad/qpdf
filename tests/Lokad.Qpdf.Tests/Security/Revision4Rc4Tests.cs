using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class Revision4Rc4Tests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf");

    [Fact]
    public async Task NativeCheckedMixedFiltersDecryptAndNormalizeThroughTheCommonWriter()
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r4-rc4-mixed.pdf");
        foreach (string password in new[] { "reader", "fixture-owner" })
        {
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(1, document.PageCount);
            Assert.Equal("<x:xmpmeta>Meta</x:xmpmeta>"u8.ToArray(), Assert.IsType<PdfStream>(document.GetObject(3)).Data);
            Assert.Equal("embedded payload"u8.ToArray(), Assert.IsType<PdfStream>(document.GetObject(8)).Data);
            Assert.Equal(PdfCipher.Rc4, document.Engine.Security?.Parameters.Streams);
            Assert.Equal(PdfCipher.Aes128, document.Engine.Security?.Parameters.Strings);
        }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--password=reader", "--linearize", "--object-streams=generate"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); await VerifyOutput(host, 4, ["reader", "fixture-owner"]);
    }

    [Theory]
    [InlineData(null, QpdfSecurityFailure.PasswordRequired)]
    [InlineData("wrong-private", QpdfSecurityFailure.InvalidPassword)]
    public async Task MissingAndWrongPasswordsFailBeforeObjectTraversal(string? password, QpdfSecurityFailure expected)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r4-rc4-ascii.pdf");
        QpdfSecurityException failure = await Assert.ThrowsAsync<QpdfSecurityException>(() => QpdfDocument.OpenAsync(host, Input,
            password is null ? null : QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None).AsTask());
        Assert.Equal(expected, failure.Reason); Assert.DoesNotContain("wrong-private", failure.Message, StringComparison.Ordinal); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ReportsDescribeRc4ButForcedOlderOutputVersionsCannotDiscardProtection()
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r4-rc4-ascii.pdf");
        QpdfExecutionResult report = await Qpdf.ExecuteAsync(host, new([Input.Value, "--show-encryption", "--password=reader"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, report.Status);
        Assert.Contains(report.Diagnostics, item => item.Message == "stream encryption method: RC4");
        QpdfExecutionResult failed = await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--password=reader", "--force-version=1.5"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, failed.Status); Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("/CFM /V2 /Length 16", "/CFM /V2 /Length 5")]
    [InlineData("/Length 128", "/Length 40")]
    public void InvalidRevision4KeyLengthsNeverProduceAContext(string original, string replacement)
    {
        object[] vector = Revision4EncryptionTests.Rc4Vectors().First(); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfDictionary dictionary = Assert.IsType<PdfDictionary>(new PdfParser(QpdfLimits.Default, governor, new(governor), null)
            .ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(((string)vector[1]).Replace(original, replacement, StringComparison.Ordinal))), 0, memory));
        long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfParseException>(() => DocumentSecurity.TryCreate(dictionary, Convert.FromHexString((string)vector[5]), "reader"u8, governor, out _));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, "classic")]
    [InlineData(true, "classic")]
    [InlineData(false, "objects")]
    [InlineData(true, "objects")]
    [InlineData(false, "qdf")]
    [InlineData(true, "qdf")]
    [InlineData(false, "linearized")]
    [InlineData(true, "linearized")]
    public async Task CommandsPreservePasswordsAndNormalizeRevision4ToAes(bool job, string mode)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r4-rc4-ascii.pdf");
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value, ["password"] = "reader" };
        List<string> argv = [Input.Value, Output.Value, "--password=reader"];
        fields["objectStreams"] = mode is "objects" or "linearized" ? "generate" : "disable"; argv.Add("--object-streams=" + fields["objectStreams"]);
        if (mode == "qdf") { fields["qdf"] = ""; argv.Add("--qdf"); }
        if (mode == "linearized") { fields["linearize"] = ""; argv.Add("--linearize"); }
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        await VerifyOutput(host, 4, ["reader", "fixture-owner"]);
    }

    [Theory]
    [InlineData("preserve")]
    [InlineData("remove")]
    [InlineData("new")]
    [InlineData("template")]
    public async Task TypedPoliciesShareTheAuthenticatedRc4Reader(string policy)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r4-rc4-ascii.pdf");
        QpdfPath template = QpdfPath.From("template.pdf"); Add(host, template, "security-r4-rc4-empty.pdf");
        QpdfOutputEncryption encryption = policy switch
        {
            "remove" => QpdfOutputEncryption.Remove,
            "new" => QpdfOutputEncryption.CreateAes256(QpdfPassword.FromText("new-reader"), QpdfPassword.FromText("new-owner"), -4, false),
            "template" => QpdfOutputEncryption.CopyFrom(template, null), _ => QpdfOutputEncryption.Preserve,
        };
        _ = await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), QpdfPassword.FromText("reader"), encryption, CancellationToken.None);
        await VerifyOutput(host, policy == "remove" ? null : policy == "new" ? 6 : 4,
            policy == "remove" ? [""] : policy == "new" ? ["new-reader", "new-owner"] : policy == "template" ? ["", "fixture-owner"] : ["reader", "fixture-owner"]);
    }

    [Fact]
    public async Task AttachmentPayloadsAndFullJsonUpdatesUseTheSharedReader()
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r4-rc4-attachment.pdf");
        QpdfPassword password = QpdfPassword.FromText("café•");
        _ = await Qpdf.ExtractAttachmentAsync(host, Input, "private.bin", Output, password, QpdfLimits.Default, CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] attachment)); Assert.Equal("Private attachment payload.\n"u8.ToArray(), attachment);
        QpdfPath patch = QpdfPath.From("patch.json");
        QpdfJsonResult json = await Qpdf.GetDocumentJsonAsync(host, Input, patch,
            new QpdfJsonOptions(2, ["qpdf"], "inline", null, [], true) { Password = password }, CancellationToken.None);
        host.AddInput(patch, Encoding.UTF8.GetBytes(json.Json), QpdfInputOwnership.Copy);
        _ = await Qpdf.UpdatePdfFromJsonAsync(host, Input, patch, Output, password, QpdfOutputEncryption.Preserve, CancellationToken.None);
        await VerifyOutput(host, 4, ["café•", "owneré‚"]);
    }

    [Theory]
    [InlineData("Rc4", "Aes", "Identity", "")]
    [InlineData("Aes", "Rc4", "Aes", "")]
    [InlineData("Identity", "Aes", "Rc4", "")]
    [InlineData("Aes", "Aes", "Identity", "Rc4")]
    [InlineData("Rc4", "Rc4", "Aes", "Aes")]
    [InlineData("Rc4", "Aes", "Aes", "Identity")]
    public void StringStreamAndFileFiltersUseTheirOwnObjectKeys(string strings, string streams, string files, string explicitFilter)
    {
        object[] vector = Revision4EncryptionTests.Rc4Vectors().First();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope storage = ParserMemoryScope.Create(governor))
        {
            string dictionary = ((string)vector[1]).Replace("/CF <<", "/CF << /Aes << /CFM /AESV2 /Length 16 >> /Rc4 << /CFM /V2 /Length 16 >>", StringComparison.Ordinal)
                .Replace("/StrF /StdCF", "/StrF /" + strings, StringComparison.Ordinal).Replace("/StmF /StdCF", $"/StmF /{streams} /EFF /{files}", StringComparison.Ordinal);
            Assert.Equal(PasswordAuthentication.User, DocumentSecurity.TryCreate(Parse(dictionary), Convert.FromHexString((string)vector[5]), "reader"u8, governor, out DocumentSecurity? context));
            Assert.NotNull(context);
            using (context)
            {
                foreach (bool embedded in new[] { false, true })
                {
                    string filter = explicitFilter.Length == 0 ? embedded ? files : streams : explicitFilter;
                    string entries = embedded ? "/Type /EmbeddedFile " : "";
                    if (explicitFilter.Length != 0) { entries += $"/Filter /Crypt /DecodeParms << /Name /{explicitFilter} >> "; }
                    PdfDictionary header = Parse($"<< {entries}/Private <{Convert.ToHexString(Encrypt("private name"u8, strings))}> >>");
                    byte[] encoded = Encrypt("payload"u8, filter);
                    PdfStream result = Assert.IsType<PdfStream>(context.Decrypt(new PdfStream(header, encoded), 66051, 513, false, new NoResolver(), storage));
                    Assert.Equal("payload"u8.ToArray(), result.Data);
                    Assert.True(result.Dictionary.TryGet(PdfName.FromAscii("Private"), out QpdfObject name));
                    Assert.Equal("private name"u8.ToArray(), Assert.IsType<PdfByteString>(name).Value);
                    Assert.False(result.Dictionary.TryGet(PdfName.FromAscii("Filter"), out _));
                }
            }
            PdfDictionary Parse(string text) => Assert.IsType<PdfDictionary>(new PdfParser(QpdfLimits.Default, governor, new(governor), null)
                .ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(text)), 0, storage));
            byte[] Encrypt(ReadOnlySpan<byte> plain, string filter)
            {
                if (filter == "Identity") { return plain.ToArray(); }
                byte[] material = [.. Convert.FromHexString((string)vector[4]), 3, 2, 1, 1, 2];
                byte[] key = MD5.HashData(filter == "Aes" ? [.. material, .. "sAlT"u8] : material);
                if (filter == "Rc4") { byte[] result = plain.ToArray(); PdfRc4Cipher.Transform(key, result, governor); return result; }
                using Aes aes = Aes.Create(); aes.SetKey(key); byte[] iv = new byte[16];
                return [.. iv, .. aes.EncryptCbc(plain, iv, PaddingMode.PKCS7)];
            }
        }
        Assert.Equal(0, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("preserve")]
    public async Task EveryAdmissionFailureRefundsAndNeverPublishes(string operation)
    {
        RecordingMemoryBudget baseline = new(4194304); await Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++) { await Run(new(4194304) { RefuseOnGrowth = growth }, true); }
        async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost storage = new(); Add(storage, Input, "security-r4-rc4-ascii.pdf");
            using IMemoryScope caller = budget.CreateScope(); BudgetHost host = new(storage, caller);
            async Task Operate()
            {
                if (operation == "open")
                { await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, QpdfPassword.FromText("reader"), QpdfLimits.Default, CancellationToken.None); Assert.Equal(1, document.PageCount); }
                else { _ = await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), QpdfPassword.FromText("reader"), CancellationToken.None); }
            }
            if (refuses) { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Operate); Assert.False(storage.TryGetOutput(Output, out _)); }
            else { await Operate(); }
            Assert.Equal(0, storage.OpenLeaseCount); caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }

    private static async Task VerifyOutput(InMemoryQpdfHost host, int? revision, string[] passwords)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); QpdfPath copy = QpdfPath.From("copy.pdf"); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
        foreach (string password in passwords)
        {
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(1, document.PageCount); Assert.Equal(revision, document.Encryption?.Revision);
            if (revision == 4) { Assert.Equal(PdfCipher.Aes128, document.Engine.Security?.Parameters.Strings); Assert.Equal(PdfCipher.Aes128, document.Engine.Security?.Parameters.Streams); }
            Assert.True(document.Engine.Trailer.TryGet(PdfName.FromAscii("Info"), out QpdfObject raw));
            PdfDictionary info = Assert.IsType<PdfDictionary>(document.GetObject(Assert.IsType<PdfReference>(raw).ObjectNumber));
            Assert.True(info.TryGet(PdfName.FromAscii("Title"), out QpdfObject title)); Assert.Equal("TitleText"u8.ToArray(), Assert.IsType<PdfByteString>(title).Value);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }
    private static void Add(InMemoryQpdfHost host, QpdfPath path, string fixture)
        => host.AddInput(path, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)), QpdfInputOwnership.Copy);
    private sealed class NoResolver : IIndirectResolver
    { public bool TryResolve(PdfReference reference, out QpdfObject? value) { value = null; return false; } }
    private sealed class BudgetHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }
}
