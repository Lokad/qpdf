using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class CryptoExclusionTests
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

    private static QpdfDiagnostic SingleCryptoError(QpdfExecutionResult result, string output)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, diagnostic.Code);
        return diagnostic;
    }

    [Theory]
    [InlineData("--password=x")]
    [InlineData("--password-file=x")]
    [InlineData("--password-mode=x")]
    [InlineData("--password-is-hex-key")]
    [InlineData("--suppress-password-recovery")]
    [InlineData("--encryption-file-password=x")]
    [InlineData("--user-password=x")]
    [InlineData("--owner-password=x")]
    [InlineData("--bits=128")]
    [InlineData("--allow-weak-crypto")]
    [InlineData("--allow-insecure")]
    [InlineData("--use-aes")]
    [InlineData("--force-V4")]
    [InlineData("--force-R5")]
    [InlineData("--cleartext-metadata")]
    [InlineData("--static-aes-iv")]
    [InlineData("--accessibility=y")]
    [InlineData("--annotate=y")]
    [InlineData("--assemble=y")]
    [InlineData("--extract=y")]
    [InlineData("--form=y")]
    [InlineData("--modify-other=y")]
    [InlineData("--modify=y")]
    [InlineData("--print=y")]
    [InlineData("--show-crypto")]
    [InlineData("--show-encryption")]
    [InlineData("--show-encryption-key")]
    [InlineData("--is-encrypted")]
    [InlineData("--requires-password")]
    [InlineData("--remove-restrictions")]
    [InlineData("--encrypt")]
    [InlineData("--decrypt")]
    [InlineData("--copy-encryption")]
    public async Task ArgvCryptoFlagsFailWithoutOutput(string flag)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf", flag, "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        SingleCryptoError(result, "outputs/a.pdf");
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("passwordFile")]
    [InlineData("passwordMode")]
    [InlineData("passwordIsHexKey")]
    [InlineData("suppressPasswordRecovery")]
    [InlineData("encryptionFilePassword")]
    [InlineData("allowWeakCrypto")]
    [InlineData("copyEncryption")]
    [InlineData("isEncrypted")]
    [InlineData("requiresPassword")]
    [InlineData("showEncryption")]
    [InlineData("showEncryptionKey")]
    [InlineData("staticAesIv")]
    [InlineData("removeRestrictions")]
    [InlineData("encrypt")]
    [InlineData("decrypt")]
    public async Task JobCryptoKeysFailWithoutOutput(string key)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        string job = "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"" + key + "\": \"x\"}";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        SingleCryptoError(result, "outputs/a.pdf");
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task JobEncryptBlockFails()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        string job = "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"encrypt\": {\"userPassword\": \"x\"}}";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        SingleCryptoError(result, "outputs/a.pdf");
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task JobCopyAttachmentsPasswordFails()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        string job = "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"copyAttachmentsFrom\": [{\"file\": \"inputs/a.pdf\", \"password\": \"x\"}]}";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        SingleCryptoError(result, "outputs/a.pdf");
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task ArgvCopyAttachmentsPasswordFails()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--copy-attachments-from", "inputs/a.pdf", "--password=y", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        SingleCryptoError(result, "outputs/a.pdf");
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("pages")]
    [InlineData("overlay")]
    [InlineData("underlay")]
    public async Task JobNestedPasswordsFail(string section)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        string nested = "{\"file\": \"inputs/a.pdf\", \"password\": \"x\"}";
        string job = section == "pages"
            ? "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\", \"password\": \"x\"}]}"
            : "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"" + section + "\": [" + nested + "]}";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        SingleCryptoError(result, "outputs/a.pdf");
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("--pages", ".", "1", "--", "outputs/e.pdf")]
    [InlineData("--check")]
    [InlineData("--json", "--json-key=pages", "--", "outputs/e.json")]
    public async Task EncryptedInputFailsWithCryptoCode(params string[] flags)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/e.pdf", "encrypted-decl.pdf");
        List<string> argv = new() { "inputs/e.pdf" };
        argv.AddRange(flags);

        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        SingleCryptoError(result, "outputs/e.pdf");
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/e.pdf"), out byte[] _));
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/e.json"), out byte[] _));
    }

    [Fact]
    public async Task UnknownTopLevelFlagFailsAsInvalidArguments()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--bogus-flag", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }
}
