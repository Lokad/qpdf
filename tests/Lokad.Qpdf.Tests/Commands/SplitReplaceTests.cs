using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class SplitReplaceTests
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

    private static async Task<QpdfDocument> Reopen(InMemoryQpdfHost host, string output)
    {
        if (!host.TryGetOutput(QpdfPath.From(output), out byte[] published))
        {
            throw new Xunit.Sdk.XunitException("Output was not published: " + output + ".");
        }

        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, published, QpdfInputOwnership.Copy);
        return await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
    }

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        return Assert.Single(result.Diagnostics);
    }

    [Fact]
    public async Task SplitPagesWritesNamedGroups()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", ".", "2", "--split-pages", "--", "outputs/split.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument first = await Reopen(host, "outputs/split-1.pdf");
        Assert.Equal(1, first.PageCount);
        await using QpdfDocument second = await Reopen(host, "outputs/split-2.pdf");
        Assert.Equal(1, second.PageCount);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/split.pdf"), out byte[] _));
    }

    [Fact]
    public async Task SplitGroupsShareNamingRule()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", ".", "2", "--split-pages=2", "--", "outputs/split.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument both = await Reopen(host, "outputs/split-1-2.pdf");
        Assert.Equal(2, both.PageCount);
    }

    [Fact]
    public async Task SplitPercentDPatternApplies()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", ".", "2", "--split-pages", "--", "outputs/split%d.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument first = await Reopen(host, "outputs/split1.pdf");
        Assert.Equal(1, first.PageCount);
    }

    [Fact]
    public async Task SplitPrunesUnsharedObjects()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", ".", "2", "--split-pages", "--", "outputs/split.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/split-1.pdf"), out byte[] first));
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/split-2.pdf"), out byte[] second));
        Assert.DoesNotContain("Second", Encoding.ASCII.GetString(first), StringComparison.Ordinal);
        Assert.DoesNotContain("First", Encoding.ASCII.GetString(second), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SplitOutputAliasingInputFailsCleanly()
    {
        InMemoryQpdfHost host = await HostWithFixture("outputs/m-1.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["outputs/m-1.pdf", "--pages", ".", "1", "--split-pages", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/m-1.pdf"), out byte[] _));
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] _));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ReplaceInputOverwritesInPlace()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "2", "--replace-input"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("inputs/a.pdf"), out byte[] published));
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), published, QpdfInputOwnership.Copy);
        {
            await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
            Assert.Equal(1, output.PageCount);
        }

        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ReplaceRejectsOutputAndSplitAndMultiInput()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        host.AddInput(QpdfPath.From("inputs/b.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        QpdfExecutionResult withOutput = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--replace-input", "--", "outputs/x.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(withOutput).Code);

        QpdfExecutionResult withSplit = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--split-pages", "--replace-input"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(withSplit).Code);

        QpdfExecutionResult multiInput = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "inputs/b.pdf", "1", "--replace-input"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(multiInput).Code);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SplitSingleGroupMatchesDirectBytes()
    {
        InMemoryQpdfHost direct = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult plain = await Qpdf.ExecuteAsync(
            direct,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "2", "--", "outputs/plain.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, plain.ExitCode);
        Assert.True(direct.TryGetOutput(QpdfPath.From("outputs/plain.pdf"), out byte[] expected));

        InMemoryQpdfHost split = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            split,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "2", "--split-pages=9", "--", "outputs/s.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(split.TryGetOutput(QpdfPath.From("outputs/s-1.pdf"), out byte[] actual));
        Assert.Equal(expected, actual);
    }
}
