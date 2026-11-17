using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Tests.Writing;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class PageLabelSpecTests
{
    [Theory]
    [InlineData("1:D", PdfPageLabelStyle.Decimal, null, null)]
    [InlineData("1:D/", PdfPageLabelStyle.Decimal, null, null)]
    [InlineData("1:D//", PdfPageLabelStyle.Decimal, null, null)]
    [InlineData("1:D//A-", PdfPageLabelStyle.Decimal, null, "A-")]
    [InlineData("1://Cover", null, null, "Cover")]
    [InlineData("1:/", null, null, null)]
    [InlineData("1://", null, null, null)]
    [InlineData("1:D/1", PdfPageLabelStyle.Decimal, 1, null)]
    [InlineData("1:D/7/", PdfPageLabelStyle.Decimal, 7, null)]
    [InlineData("1:/4/title", null, 4, "title")]
    [InlineData("r1:r//A-/tail", PdfPageLabelStyle.LowerRoman, null, "A-/tail")]
    [InlineData("z:a//章/😀", PdfPageLabelStyle.LowerAlpha, null, "章/😀")]
    [InlineData("1:R/3/P", PdfPageLabelStyle.UpperRoman, 3, "P")]
    [InlineData("1:A//prefix", PdfPageLabelStyle.UpperAlpha, null, "prefix")]
    public void OptionalStartAndPrefixFieldsRemainIndependent(string text, PdfPageLabelStyle? style, int? start, string? prefix)
    {
        PageLabelSpec spec = PageLabelSpec.Parse(text, 17L, null);
        Assert.Equal(text[..text.IndexOf(':')], spec.FirstPage);
        Assert.Equal(style, spec.Style); Assert.Equal(start, spec.Start); Assert.Equal(prefix, spec.Prefix);
    }

    [Theory]
    [InlineData("1:D/0")]
    [InlineData("1:D/-1")]
    [InlineData("1:D/+1")]
    [InlineData("1:D/x")]
    [InlineData("1:D/2147483648")]
    [InlineData("1:D/00000000001")]
    [InlineData("1:X//prefix")]
    [InlineData("0:D/")]
    [InlineData("r0:D/")]
    [InlineData("1:D/ /prefix")]
    public void ExplicitInvalidStartsAndStylesStillReject(string text)
    {
        QpdfParseException failure = Assert.Throws<QpdfParseException>(() => PageLabelSpec.Parse(text, 17L, null));
        Assert.Equal(17L, failure.ByteOffset);
    }

    private static readonly QpdfPath Output = QpdfPath.From("output");
    private static string[] Arguments(string mode)
        => ["input", "--set-page-labels", "1:D//A-", "z://Tail", "--",
            .. mode == "select" ? new[] { "--pages", ".", "3,1,3", "--" } : Array.Empty<string>(),
            .. mode == "split" ? new[] { "--split-pages=2" } : Array.Empty<string>(), Output.Value];

    private static async Task Verify(InMemoryQpdfHost host, string mode)
    {
        foreach (string path in mode == "split" ? new[] { "output-1-2", "output-3" } : new[] { Output.Value })
        {
            Assert.True(host.TryGetOutput(QpdfPath.From(path), out byte[] bytes));
            host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
            PdfArray numbers = LabelIndirectionTests.Numbers(document);
            Assert.Equal(mode == "split" ? 2 : 4, numbers.Count);
            for (int index = 0; index < numbers.Count; index += 2)
            {
                bool tail = path == "output-3" || index == 2;
                Assert.Equal(index == 0 ? 0L : 2L, Assert.IsType<PdfInteger>(numbers[index]).Value);
                PdfDictionary label = Assert.IsType<PdfDictionary>(numbers[index + 1]);
                Assert.False(label.TryGet(PdfName.FromAscii("St"), out _));
                Assert.True(label.TryGet(PdfName.FromAscii("P"), out QpdfObject prefix));
                Assert.Equal(tail ? "Tail"u8.ToArray() : "A-"u8.ToArray(), Assert.IsType<PdfByteString>(prefix).Value);
                Assert.Equal(!tail, label.TryGet(PdfName.FromAscii("S"), out QpdfObject style));
                if (!tail) { Assert.Equal(PdfName.FromAscii("D"), style); }
            }
        }
    }

    [Theory]
    [InlineData("whole")]
    [InlineData("select")]
    [InlineData("split")]
    public async Task DefaultStartsApplyInAssembledOrderBeforeSplitting(string mode)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), LabelTests.BuildDoc(3), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(Arguments(mode)), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, result.Counters.OpenHandles);
        await Verify(host, mode); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task JobLabelsUseTheSameOptionalStartParser()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), LabelTests.BuildDoc(3), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input","outputFile":"output","setPageLabels":["1:D//A-","z://Tail"]}""", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); await Verify(host, "whole");
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public Task DefaultStartLabelPreparationKeepsAdmissionAndPublicationBoundaries()
        => PreparationAdmissionChecks.VerifyAsync(() => PreparationAdmissionChecks.StorageAsync(LabelTests.BuildDoc(3), Output),
            (host, token) => Qpdf.ExecuteAsync(host, new(Arguments("whole")), token), Output, 64, ["write"]);
}
