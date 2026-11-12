using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Security;

public sealed class PasswordReverseRecoveryTests
{
    public static IEnumerable<object[]> Vectors()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "security-reverse-vectors.json")));
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            yield return [item.GetProperty("file").GetString() ?? throw new InvalidOperationException(),
                item.GetProperty("name").GetString() ?? throw new InvalidOperationException(),
                item.GetProperty("suppliedHex").GetString() ?? throw new InvalidOperationException(),
                item.GetProperty("exactHex").GetString() ?? throw new InvalidOperationException(), item.GetProperty("owner").GetBoolean()];
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task CommandsRecoverRecordedRepresentationsWhileTypedBytesStayExact(string file, string name,
        string suppliedHex, string exactHex, bool owner)
    {
        Assert.NotEmpty(name);
        using InMemoryQpdfHost host = new(); Add(host, file);
        byte[] supplied = Convert.FromHexString(suppliedHex);
        await Assert.ThrowsAsync<QpdfSecurityException>(() => QpdfDocument.OpenAsync(host, QpdfPath.From("input.pdf"),
            QpdfPassword.FromBytes(supplied), QpdfLimits.Default, CancellationToken.None).AsTask());
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input.pdf"),
            QpdfPassword.FromBytes(Convert.FromHexString(exactHex)), QpdfLimits.Default, CancellationToken.None))
        { Assert.Equal(owner, document.Encryption?.OwnerPasswordMatched); Assert.Equal(1, document.PageCount); }
        foreach (bool job in new[] { false, true })
        foreach (bool suppress in new[] { false, true })
        {
            QpdfExecutionResult result;
            if (job)
            {
                Dictionary<string, object> fields = new() { ["inputFile"] = "input.pdf", ["json"] = "2", ["jsonKey"] = new[] { "encrypt" },
                    ["passwordMode"] = "hex-bytes", ["password"] = suppliedHex };
                if (suppress) { fields["suppressPasswordRecovery"] = ""; }
                result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None);
            }
            else
            {
                List<string> argv = ["input.pdf", "--json", "--json-key=encrypt", "--password-mode=hex-bytes", "--password=" + suppliedHex];
                if (suppress) { argv.Add("--suppress-password-recovery"); }
                result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
            }
            Assert.True(result.Status == (suppress ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed),
                name + ": " + string.Join("; ", result.Diagnostics.Select(item => item.Message)));
            if (suppress) { Assert.Equal(QpdfDiagnosticCode.InvalidPassword, Assert.Single(result.Diagnostics).Code); }
            else
            {
                using JsonDocument json = JsonDocument.Parse(result.OutputText ?? throw new InvalidOperationException());
                Assert.Equal(owner, json.RootElement.GetProperty("encrypt").GetProperty("ownerpasswordmatched").GetBoolean());
            }
        }
        Assert.Equal(Convert.FromHexString(suppliedHex), supplied); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("security-recovery-r6-utf8.pdf", "6361668EA5")]
    [InlineData("security-recovery-r6-ligatures.pdf", "FEFF0152")]
    [InlineData("security-recovery-r6-ligatures.pdf", "FFFE5201")]
    public async Task HostFileBytesUseTheSameRecoveryPolicy(string file, string suppliedHex)
    {
        using InMemoryQpdfHost host = new(); Add(host, file);
        byte[] supplied = Convert.FromHexString(suppliedHex);
        host.AddInput(QpdfPath.From("password"), [.. supplied, 10], QpdfInputOwnership.Copy);
        foreach (bool suppress in new[] { false, true })
        {
            List<string> argv = ["input.pdf", "--show-npages", "--password-file=password"];
            if (suppress) { argv.Add("--suppress-password-recovery"); }
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
            Assert.Equal(suppress ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
            if (suppress) { Assert.Equal(QpdfDiagnosticCode.InvalidPassword, Assert.Single(result.Diagnostics).Code); }
        }
        Assert.Equal(Convert.FromHexString(suppliedHex), supplied); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("FEFFD8010152")]
    [InlineData("FEFF0152FF")]
    public async Task MalformedUtf16NeverRecoversByDiscardingBytes(string suppliedHex)
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-recovery-r6-ligatures.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input.pdf", "output.pdf", "--password-mode=hex-bytes", "--password=" + suppliedHex]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.InvalidPassword, Assert.Single(result.Diagnostics).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("output.pdf"), out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    private static void Add(InMemoryQpdfHost host, string file)
        => host.AddInput(QpdfPath.From("input.pdf"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Transfer);
}
