using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Writing;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class SourceGroupLeaseTests
{
    private sealed class Host(InMemoryQpdfHost original, InMemoryQpdfHost replacement) : IQpdfHost
    {
        internal int Opens;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => (++Opens == 1 ? original : replacement).OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            Assert.Equal(0L, original.OpenLeaseCount); Assert.Equal(0L, replacement.OpenLeaseCount);
            return original.CreateOutputAsync(path, token);
        }
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task OneLogicalSourceUsesOneStableLeaseAcrossGroups(string route)
    {
        using InMemoryQpdfHost original = new(), replacement = new();
        QpdfPath input = QpdfPath.From("input"), output = QpdfPath.From("output");
        original.AddInput(input, SourceGroupIdentityTests.Fixture("A"), QpdfInputOwnership.Transfer);
        replacement.AddInput(input, SourceGroupIdentityTests.Fixture("B"), QpdfInputOwnership.Transfer);
        Host host = new(original, replacement);
        if (route == "typed")
        {
            PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host,
                [new(input, new(1)), new(input, new(2)), new(input, new(3))], output, CancellationToken.None);
            Assert.Equal(3, result.PageCount); Assert.Equal(new[] { 3, 3, 3 }, result.SourcePageCounts);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(SourceGroupIdentityTests.Arguments("split")), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1"},{"file":"input","range":"2"},{"file":".","range":"3"}]}""", CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(1, host.Opens); Assert.Equal(0L, original.OpenLeaseCount); Assert.Equal(0L, replacement.OpenLeaseCount);
        Assert.True(original.TryGetOutput(output, out byte[] bytes));
        original.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument doc = await QpdfDocument.OpenAsync(original, QpdfPath.From("read"), CancellationToken.None);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(doc.PageReferences[i].Page.TryGet(PdfName.FromAscii("Marker"), out QpdfObject marker));
            Assert.Equal("A" + (i + 1), System.Text.Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(marker).ValueSpan));
        }
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task InvalidLaterGroupClosesTheSourceWithoutOpeningOutput(string route)
    {
        using InMemoryQpdfHost original = new(), replacement = new();
        QpdfPath input = QpdfPath.From("input"), output = QpdfPath.From("output");
        original.AddInput(input, SourceGroupIdentityTests.Fixture("A"), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction seed = await original.CreateOutputAsync(output, CancellationToken.None))
        { await seed.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        Host host = new(original, replacement);
        if (route == "typed")
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Qpdf.SelectPagesAsync(host,
                [new(input, new(1)), new(input, new(99))], output, CancellationToken.None).AsTask());
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1", ".", "99", "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1"},{"file":".","range":"99"}]}""", CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        }
        Assert.Equal(1, host.Opens); Assert.Equal(0L, original.OpenLeaseCount); Assert.Equal(0L, replacement.OpenLeaseCount);
        Assert.True(original.TryGetOutput(output, out byte[] prior)); Assert.Equal("prior"u8.ToArray(), prior);
    }
}
