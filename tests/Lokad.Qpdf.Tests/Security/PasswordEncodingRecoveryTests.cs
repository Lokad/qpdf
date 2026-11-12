using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class PasswordEncodingRecoveryTests
{
    public static IEnumerable<object[]> Vectors()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "security-recovery-vectors.json")));
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            yield return [item.GetProperty("file").GetString() ?? throw new InvalidOperationException(),
                item.GetProperty("user").GetString() ?? throw new InvalidOperationException(),
                item.GetProperty("owner").GetString() ?? throw new InvalidOperationException(),
                item.GetProperty("userHex").GetString() ?? throw new InvalidOperationException(),
                item.GetProperty("ownerHex").GetString() ?? throw new InvalidOperationException(), item.GetProperty("revision").GetInt32()];
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task TextAndExactBytesOpenRecordedSingleBytePasswords(string file, string user, string owner,
        string userHex, string ownerHex, int revision)
    {
        using InMemoryQpdfHost host = new(); Add(host, file);
        foreach ((string text, string hex, bool isOwner) in new[] { (user, userHex, false), (owner, ownerHex, true) })
        {
            byte[] raw = Convert.FromHexString(hex);
            foreach (QpdfPassword password in new[] { QpdfPassword.FromText(text), QpdfPassword.FromBytes(raw) })
            {
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input.pdf"), password,
                    QpdfLimits.Default, CancellationToken.None);
                Assert.Equal(1, document.PageCount); Assert.Equal(revision, document.Encryption?.Revision);
                Assert.Equal(isOwner, document.Encryption?.OwnerPasswordMatched);
                Assert.True(document.Engine.Trailer.TryGet(PdfName.FromAscii("Info"), out QpdfObject info));
                PdfDictionary dictionary = Assert.IsType<PdfDictionary>(info is PdfReference reference ? document.GetObject(reference.ObjectNumber) : info);
                Assert.True(dictionary.TryGet(PdfName.FromAscii("Title"), out QpdfObject title));
                Assert.Equal("TitleText"u8.ToArray(), Assert.IsType<PdfByteString>(title).Value);
            }
            Assert.Equal(Convert.FromHexString(hex), raw);
            QpdfSecurityException error = await Assert.ThrowsAsync<QpdfSecurityException>(() => QpdfDocument.OpenAsync(host,
                QpdfPath.From("input.pdf"), QpdfPassword.FromBytes(Encoding.UTF8.GetBytes(text)), QpdfLimits.Default, CancellationToken.None).AsTask());
            Assert.Equal(QpdfSecurityFailure.InvalidPassword, error.Reason);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task CommandCredentialsRecoverAndSuppressionKeepsExactBytes(string file, string user, string owner,
        string userHex, string ownerHex, int revision)
    {
        Assert.NotEmpty(owner); Assert.NotEmpty(userHex); Assert.NotEmpty(ownerHex); Assert.InRange(revision, 4, 6);
        foreach (string route in new[] { "argv", "job", "file", "hex" })
        foreach (bool suppress in new[] { false, true })
        {
            using InMemoryQpdfHost host = new(); Add(host, file);
            QpdfExecutionResult result;
            if (route == "job")
            {
                Dictionary<string, object> fields = new() { ["inputFile"] = "input.pdf", ["showNpages"] = "", ["password"] = user };
                if (suppress) { fields["suppressPasswordRecovery"] = ""; }
                result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None);
            }
            else
            {
                List<string> argv = ["input.pdf", "--show-npages"];
                if (route == "file")
                {
                    host.AddInput(QpdfPath.From("password"), Encoding.UTF8.GetBytes(user + "\n"), QpdfInputOwnership.Transfer);
                    argv.Add("--password-file=password");
                }
                else if (route == "hex") { argv.AddRange(["--password-mode=hex-bytes", "--password=" + Convert.ToHexString(Encoding.UTF8.GetBytes(user))]); }
                else { argv.Add("--password=" + user); }
                if (suppress) { argv.Add("--suppress-password-recovery"); }
                result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
            }
            Assert.True(result.Status == (suppress ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed),
                route + ": " + string.Join("; ", result.Diagnostics.Select(item => item.Message)));
            if (suppress) { Assert.Equal(QpdfDiagnosticCode.InvalidPassword, Assert.Single(result.Diagnostics).Code); }
            Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryRecoveryAdmissionRefusalRefundsOwnedStorage(bool reverse)
    {
        RecordingMemoryBudget baseline = new(4194304); await Run(baseline, false, reverse);
        Assert.True(baseline.GrowthCalls > 0);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++)
        { await Run(new(4194304) { RefuseOnGrowth = growth }, true, reverse); }

        static async Task Run(RecordingMemoryBudget budget, bool refuses, bool reverse)
        {
            using InMemoryQpdfHost storage = new(); Add(storage, reverse ? "security-recovery-r6-utf8.pdf" : "security-recovery-r6-macroman.pdf");
            using IMemoryScope caller = budget.CreateScope(); BudgetHost host = new(storage, caller);
            async Task Open()
            {
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input.pdf"),
                    reverse ? QpdfPassword.FromHexText("6361668EA5", true) : QpdfPassword.FromText("café•"), QpdfLimits.Default, CancellationToken.None);
                Assert.Equal(1, document.PageCount);
            }
            if (refuses) { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Open); }
            else { await Open(); }
            Assert.Equal(0, storage.OpenLeaseCount); caller.Dispose(); Assert.Equal(0L, budget.ReservedBytes);
        }
    }

    private sealed class BudgetHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }

    private static void Add(InMemoryQpdfHost host, string file)
        => host.AddInput(QpdfPath.From("input.pdf"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Transfer);
}
