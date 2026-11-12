using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed partial class LegacyDocumentTests
{
    [Theory]
    [InlineData(2, null, QpdfSecurityFailure.PasswordRequired)]
    [InlineData(3, null, QpdfSecurityFailure.PasswordRequired)]
    [InlineData(2, "private-wrong", QpdfSecurityFailure.InvalidPassword)]
    [InlineData(3, "private-wrong", QpdfSecurityFailure.InvalidPassword)]
    public async Task MissingAndWrongCredentialsRemainDistinct(int revision, string? password, QpdfSecurityFailure expected)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, $"security-r{revision}-ascii.pdf");
        QpdfSecurityException error = await Assert.ThrowsAsync<QpdfSecurityException>(() => QpdfDocument.OpenAsync(host, Input,
            password is null ? null : QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None).AsTask());
        Assert.Equal(expected, error.Reason); Assert.DoesNotContain("private-wrong", error.Message, StringComparison.Ordinal); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(2, "1.2")]
    [InlineData(3, "1.3")]
    public async Task IncompatibleForcedVersionsCannotDiscardProtection(int revision, string version)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, $"security-r{revision}-ascii.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--password=reader", "--force-version=" + version]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("/Length 40", "          ")]
    [InlineData("/V 2", "/V 1")]
    public async Task HistoricalFortyBitDeclarationsKeepTheirDefinedKeyLength(string original, string replacement)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r3-key40-short-digest.pdf"));
        string text = Encoding.Latin1.GetString(bytes); Assert.Contains(original, text, StringComparison.Ordinal); Assert.Equal(original.Length, replacement.Length);
        using InMemoryQpdfHost host = new(); host.AddInput(Input, Encoding.Latin1.GetBytes(text.Replace(original, replacement, StringComparison.Ordinal)), QpdfInputOwnership.Copy);
        foreach (string password in new[] { "reader", "fixture-owner" })
        {
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(1, document.PageCount); Assert.Equal(40, document.Encryption?.KeyLengthBits); AssertTitle(document, "LegacyTitle");
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void PreVersionFourIgnoresInactiveCryptFiltersAndMetadataFlags(int revision)
    {
        object[] vector = LegacyEncryptionTests.Vectors().First(value => (string)value[0] == $"security-r{revision}-ascii.pdf");
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        string declaration = ((string)vector[1]).Insert(2, " /CF /Invalid /StmF /Identity /StrF /Identity /EFF /Identity /EncryptMetadata false ");
        if (revision == 2) { declaration = declaration.Replace("/Length 40", "/Length /Invalid", StringComparison.Ordinal); }
        PdfDictionary dictionary = Assert.IsType<PdfDictionary>(new PdfParser(QpdfLimits.Default, governor, new(governor), null).ParseObjectValue(
            new PdfTokenizer(Encoding.ASCII.GetBytes(declaration)), 0, memory));
        long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.Equal(PasswordAuthentication.User, DocumentSecurity.TryCreate(dictionary, Convert.FromHexString((string)vector[5]), "reader"u8, governor, out DocumentSecurity? security, out EncryptionParameters parameters));
        using (security)
        {
            Assert.NotNull(security); Assert.True(parameters.EncryptMetadata);
            Assert.Equal(PdfCipher.Rc4, parameters.Strings); Assert.Equal(PdfCipher.Rc4, parameters.Streams); Assert.Equal(PdfCipher.Rc4, parameters.Files);
            Assert.Equal(revision == 2 ? 40 : 128, parameters.KeyLengthBits);
        }
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("39")]
    [InlineData("41")]
    [InlineData("136")]
    [InlineData("/Invalid")]
    public void InvalidVersionTwoKeyLengthsFailBeforeAuthentication(string length)
    {
        object[] vector = LegacyEncryptionTests.Vectors().First(value => (string)value[0] == "security-r3-ascii.pdf");
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfDictionary dictionary = Assert.IsType<PdfDictionary>(new PdfParser(QpdfLimits.Default, governor, new(governor), null).ParseObjectValue(
            new PdfTokenizer(Encoding.ASCII.GetBytes(((string)vector[1]).Replace("/Length 128", "/Length " + length, StringComparison.Ordinal))), 0, memory));
        long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfParseException>(() => DocumentSecurity.TryCreate(dictionary, Convert.FromHexString((string)vector[5]), "reader"u8, governor, out _));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("security-r2-ascii.pdf", "open")]
    [InlineData("security-r3-key96-short-digest.pdf", "open")]
    [InlineData("security-r2-ascii.pdf", "preserve")]
    [InlineData("security-r3-key96-short-digest.pdf", "preserve")]
    public async Task EveryAdmissionRefusalClosesReadersAndRefundsLegacyKeys(string fixture, string operation)
    {
        RecordingMemoryBudget baseline = new(4194304); await Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++) { await Run(new(4194304) { RefuseOnGrowth = growth }, true); }
        async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost storage = new(); Add(storage, Input, fixture); using IMemoryScope caller = budget.CreateScope(); BudgetHost host = new(storage, caller);
            async Task Operate()
            {
                if (operation == "open")
                { await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, QpdfPassword.FromText("fixture-owner"), QpdfLimits.Default, CancellationToken.None); Assert.Equal(1, document.PageCount); }
                else { _ = await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), QpdfPassword.FromText("fixture-owner"), CancellationToken.None); }
            }
            if (refuses) { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Operate); Assert.False(storage.TryGetOutput(Output, out _)); }
            else { await Operate(); }
            Assert.Equal(0, storage.OpenLeaseCount); caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }

    private sealed class BudgetHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }
}
