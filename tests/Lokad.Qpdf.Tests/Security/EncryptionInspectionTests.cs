using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class EncryptionInspectionTests
{
    [Theory]
    [InlineData("minimal-1page.pdf", null, 0, 0)]
    [InlineData("security-r4-ascii.pdf", null, 4, 0)]
    [InlineData("security-r6-ascii.pdf", "wrong-secret", 6, 0)]
    [InlineData("security-r4-ascii.pdf", "reader", 4, 1)]
    [InlineData("security-r6-ascii.pdf", "fixture-owner", 6, 2)]
    [InlineData("security-r6-empty.pdf", null, 6, 1)]
    public async Task DeclaredFactsSurviveCleanupWithoutGrantingUnauthenticatedAccess(string file, string? password, int revision, int match)
    {
        using InMemoryQpdfHost host = new(); QpdfPath path = Add(host, file);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        EncryptionInspection result = await QpdfDocument.InspectSecurityAsync(host, path,
            password is null ? null : QpdfPassword.FromText(password), QpdfLimits.Default, governor,
            new(governor), QpdfRecoveryMode.Strict, CancellationToken.None);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0, host.OpenLeaseCount);
        Assert.Equal(match, (int)result.Authentication);
        if (revision == 0) { Assert.Null(result.Parameters); }
        else
        {
            EncryptionParameters parameters = Assert.IsType<EncryptionParameters>(result.Parameters);
            Assert.Equal(revision, parameters.Revision); Assert.Equal(-3388, parameters.Permissions);
            Assert.Equal(revision == 6 ? 256 : 128, parameters.KeyLengthBits);
            Assert.Equal(revision == 6 ? PdfCipher.Aes256 : PdfCipher.Aes128, parameters.Streams);
            Assert.DoesNotContain("wrong-secret", result.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("reader", false)]
    [InlineData("wrong-secret", false)]
    [InlineData("reader", true)]
    [InlineData("wrong-secret", true)]
    public async Task EveryProbeAdmissionFailureIsTerminalAndRefunded(string password, bool needsDocument)
    {
        RecordingMemoryBudget baseline = new(1048576);
        await Run(baseline, false);
        Assert.True(baseline.GrowthCalls > 1);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++)
        {
            RecordingMemoryBudget refusing = new(1048576) { RefuseOnGrowth = growth };
            await Run(refusing, true); Assert.Equal(growth, refusing.GrowthCalls);
        }

        async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost source = new(); QpdfPath path = Add(source, "security-r6-ascii.pdf");
            ProbeHost host = new(source, budget);
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            async Task Inspect()
            {
                (QpdfDocument? document, _) = await QpdfDocument.OpenSecurityInspectionCore(host, path,
                    QpdfPassword.FromText(password), QpdfLimits.Default, governor, new(governor),
                    QpdfRecoveryMode.Strict, CancellationToken.None, needsDocument);
                await using (document)
                {
                    Assert.Equal(needsDocument && password == "reader", document is not null);
                    if (document is not null) { Assert.Equal(1, document.PageCount); }
                }
            }
            if (refuses) { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Inspect); }
            else { await Inspect(); }
            Assert.Equal(0, source.OpenLeaseCount); Assert.Equal(0L, budget.ReservedBytes);
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
    }

    private sealed class ProbeHost(InMemoryQpdfHost inner, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => inner.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => throw new InvalidOperationException("Security inspection must not create output.");
    }

    private static QpdfPath Add(InMemoryQpdfHost host, string file)
    {
        QpdfPath path = QpdfPath.From(file);
        host.AddInput(path, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Transfer);
        return path;
    }
}
