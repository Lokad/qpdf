using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;
using UglyToad.PdfPig;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class SelectPageTests
{
    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    private static async Task<(InMemoryQpdfHost Host, byte[] Bytes)> LoadFixture(string file)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), file));
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/" + file), bytes, QpdfInputOwnership.Copy);
        return (host, bytes);
    }

    private static async Task<QpdfDocument> Reopen(InMemoryQpdfHost host, string outputName)
    {
        QpdfPath outputPath = QpdfPath.From(outputName);
        if (!host.TryGetOutput(outputPath, out byte[] published))
        {
            throw new Xunit.Sdk.XunitException($"Output was not published: {outputName}.");
        }

        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, published, QpdfInputOwnership.Copy);
        return await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
    }

    [Fact]
    public async Task SelectsSinglePageWithPreservedStreams()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("minimal-1page.pdf");
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/minimal-1page.pdf"),
            QpdfPath.From("outputs/page.pdf"),
            new PdfPageSelectionOptions(1),
            CancellationToken.None);
        Assert.Equal([1], result.SourcePages);
        Assert.Equal(1, result.PageCount);
        Assert.True(result.Counters.TotalOutputBytes > 0L);

        await using QpdfDocument output = await Reopen(host, "outputs/page.pdf");
        Assert.Equal(1, output.PageCount);
        await using QpdfDocument source = await QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/minimal-1page.pdf"), CancellationToken.None);
        PdfStream sourceStream = Assert.IsType<PdfStream>(source.GetObject(4));
        PdfStream outputStream = Assert.IsType<PdfStream>(output.GetObject(4));
        Assert.Equal(sourceStream.Data, outputStream.Data);

        bool found = host.TryGetOutput(QpdfPath.From("outputs/page.pdf"), out byte[] published);
        Assert.True(found);
        using MemoryStream buffer = new(published);
        using PdfDocument reread = PdfDocument.Open(buffer);
        Assert.Equal(1, reread.NumberOfPages);
    }

    [Fact]
    public async Task SelectsSecondPageOfTwo()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("minimal-2page.pdf");
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/minimal-2page.pdf"),
            QpdfPath.From("outputs/second.pdf"),
            new PdfPageSelectionOptions(2),
            CancellationToken.None);
        Assert.Equal([2], result.SourcePages);

        await using QpdfDocument output = await Reopen(host, "outputs/second.pdf");
        Assert.Equal(1, output.PageCount);
        bool hasSecond = false;
        foreach (int number in new[] { 3, 4, 5, 6, 7 })
        {
            try
            {
                if (output.GetObject(number) is PdfStream stream
                    && Encoding.ASCII.GetString(stream.Data).Contains("Second", StringComparison.Ordinal))
                {
                    hasSecond = true;
                }
            }
            catch (QpdfParseException)
            {
            }
        }

        Assert.True(hasSecond);
    }

    [Fact]
    public async Task MaterializesInheritedAttributes()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("inherited-attrs.pdf");
        await Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/inherited-attrs.pdf"),
            QpdfPath.From("outputs/inherited.pdf"),
            new PdfPageSelectionOptions(1),
            CancellationToken.None);

        await using QpdfDocument output = await Reopen(host, "outputs/inherited.pdf");
        Assert.Equal(1, output.PageCount);
        PdfDictionary? page = null;
        foreach (int number in new[] { 3, 4, 5, 6 })
        {
            try
            {
                if (output.GetObject(number) is PdfDictionary candidate
                    && candidate.TryGet(new PdfName(Encoding.ASCII.GetBytes("Type")), out QpdfObject type)
                    && type is PdfName name && name.DisplayText == "Page")
                {
                    page = candidate;
                }
            }
            catch (QpdfParseException)
            {
            }
        }

        Assert.NotNull(page);
        Assert.True(page.TryGet(new PdfName(Encoding.ASCII.GetBytes("MediaBox")), out QpdfObject box));
        PdfArray mediaBox = Assert.IsType<PdfArray>(box);
        Assert.Equal(new PdfInteger("612", 612L), mediaBox[2]);
        Assert.True(page.TryGet(new PdfName(Encoding.ASCII.GetBytes("Resources")), out QpdfObject resources));
        Assert.IsType<PdfDictionary>(resources);
    }

    [Theory]
    [InlineData("typed", "null")]
    [InlineData("argv", "7 0 R")]
    [InlineData("job", "7 0 R")]
    public async Task InheritsNearestNonNullAttributesWithoutReplacingLeafValues(string route, string resources)
    {
        List<(int, string, byte[]?)> objects = CheckTests.SinglePage("<< /Length 4 >>", "q Q\n"u8.ToArray());
        objects[1] = (2, "<< /Type /Pages /Kids [6 0 R] /Count 1 /MediaBox [0 0 612 792]"
            + " /CropBox [0 0 600 780] /Rotate 180 /Resources << /Marker /Root >> >>", null);
        objects[2] = (3, "<< /Type /Page /Parent 6 0 R /Contents 4 0 R /MediaBox 7 0 R"
            + " /CropBox 7 0 R /Rotate 270 /Resources " + resources + " >>", null);
        objects.Add((6, "<< /Type /Pages /Parent 2 0 R /Kids [3 0 R] /Count 1"
            + " /Resources 7 0 R /CropBox [10 20 500 700] /Rotate 90 >>", null));
        objects.Add((7, "null", null));
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("in.pdf"), CheckTests.Assemble(objects, 1), QpdfInputOwnership.Copy);
        if (route == "typed")
        {
            await Qpdf.SelectPageAsync(host, QpdfPath.From("in.pdf"), QpdfPath.From("out.pdf"),
                new PdfPageSelectionOptions(1), CancellationToken.None);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["in.pdf", "--pages", ".", "1", "--", "out.pdf"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "in.pdf", outputFile = "out.pdf",
                    pages = new[] { new { file = ".", range = "1" } } }), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        await using QpdfDocument output = await Reopen(host, "out.pdf");
        PdfDictionary page = Assert.Single(output.PageReferences).Page;
        Assert.True(page.TryGet(PdfName.FromAscii("MediaBox"), out QpdfObject media));
        Assert.Equal(612L, Assert.IsType<PdfInteger>(Assert.IsType<PdfArray>(media)[2]).Value);
        Assert.True(page.TryGet(PdfName.FromAscii("CropBox"), out QpdfObject crop));
        Assert.Equal(10L, Assert.IsType<PdfInteger>(Assert.IsType<PdfArray>(crop)[0]).Value);
        Assert.True(page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject rotate));
        Assert.Equal(270L, Assert.IsType<PdfInteger>(rotate).Value);
        Assert.True(page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject inherited));
        Assert.True(Assert.IsType<PdfDictionary>(inherited).TryGet(PdfName.FromAscii("Marker"), out QpdfObject marker));
        Assert.Equal(PdfName.FromAscii("Root"), marker);
    }

    [Theory]
    [InlineData("typed", "self")]
    [InlineData("argv", "repeated-parent")]
    [InlineData("job", "direct-prefix")]
    public async Task RejectsAncestryCyclesWithoutPublishingOrLeakingLeases(string route, string shape)
    {
        List<(int, string, byte[]?)> objects = CheckTests.SinglePage("<< /Length 4 >>", "q Q\n"u8.ToArray());
        string parent = shape == "self" ? "3 0 R" : shape == "direct-prefix" ? "<< /Parent 6 0 R >>" : "6 0 R";
        objects[2] = (3, "<< /Type /Page /Parent " + parent
            + " /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>", null);
        objects.Add((6, shape == "direct-prefix" ? "<< /Parent 3 0 R >>" : "<< /Parent 6 0 R >>", null));
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("in.pdf"), CheckTests.Assemble(objects, 1), QpdfInputOwnership.Copy);
        byte[] prior = "prior output"u8.ToArray();
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("out.pdf"), CancellationToken.None))
        {
            await seed.WriteAsync(prior, CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }
        if (route == "typed")
        {
            QpdfParseException failure = await Assert.ThrowsAsync<QpdfParseException>(async () =>
                await Qpdf.SelectPageAsync(host, QpdfPath.From("in.pdf"), QpdfPath.From("out.pdf"),
                    new PdfPageSelectionOptions(1), CancellationToken.None));
            Assert.Contains("cycle", failure.Message, StringComparison.Ordinal);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["in.pdf", "--pages", ".", "1", "--", "out.pdf"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "in.pdf", outputFile = "out.pdf",
                    pages = new[] { new { file = ".", range = "1" } } }), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("cycle", StringComparison.Ordinal));
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] unchanged));
        Assert.Equal(prior, unchanged);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task PreservesStreamsWithIndirectLengths()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("indirect-length.pdf");
        await Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/indirect-length.pdf"),
            QpdfPath.From("outputs/indirect.pdf"),
            new PdfPageSelectionOptions(1),
            CancellationToken.None);

        await using QpdfDocument output = await Reopen(host, "outputs/indirect.pdf");
        Assert.Equal(1, output.PageCount);
        await using QpdfDocument source = await QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/indirect-length.pdf"), CancellationToken.None);
        PdfStream sourceStream = Assert.IsType<PdfStream>(source.GetObject(4));
        bool preserved = false;
        foreach (int number in new[] { 3, 4, 5, 6, 7 })
        {
            try
            {
                if (output.GetObject(number) is PdfStream candidate
                    && candidate.Data.AsSpan().SequenceEqual(sourceStream.Data))
                {
                    preserved = true;
                }
            }
            catch (QpdfParseException)
            {
            }
        }

        Assert.True(preserved);
    }

    [Fact]
    public async Task PreservesStreamsWithUnknownFiltersUndecoded()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("bogus-filter.pdf");
        await Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/bogus-filter.pdf"),
            QpdfPath.From("outputs/bogus.pdf"),
            new PdfPageSelectionOptions(1),
            CancellationToken.None);

        await using QpdfDocument output = await Reopen(host, "outputs/bogus.pdf");
        Assert.Equal(1, output.PageCount);
        await using QpdfDocument source = await QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/bogus-filter.pdf"), CancellationToken.None);
        PdfStream sourceStream = Assert.IsType<PdfStream>(source.GetObject(4));
        bool preserved = false;
        foreach (int number in new[] { 3, 4, 5, 6 })
        {
            try
            {
                if (output.GetObject(number) is PdfStream candidate
                    && candidate.Data.AsSpan().SequenceEqual(sourceStream.Data))
                {
                    preserved = true;
                }
            }
            catch (QpdfParseException)
            {
            }
        }

        Assert.True(preserved);
    }

    [Fact]
    public async Task RepeatedSelectionsAreByteIdentical()
    {
        (InMemoryQpdfHost first, byte[] _) = await LoadFixture("minimal-2page.pdf");
        (InMemoryQpdfHost second, byte[] _) = await LoadFixture("minimal-2page.pdf");
        await Qpdf.SelectPageAsync(
            first,
            QpdfPath.From("inputs/minimal-2page.pdf"),
            QpdfPath.From("outputs/a.pdf"),
            new PdfPageSelectionOptions(1),
            CancellationToken.None);
        await Qpdf.SelectPageAsync(
            second,
            QpdfPath.From("inputs/minimal-2page.pdf"),
            QpdfPath.From("outputs/a.pdf"),
            new PdfPageSelectionOptions(1),
            CancellationToken.None);
        Assert.True(first.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));
        Assert.True(second.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task InvalidSelectionsFailExplicitly()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("minimal-2page.pdf");
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfPageSelectionOptions(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/minimal-2page.pdf"),
            QpdfPath.From("outputs/x.pdf"),
            new PdfPageSelectionOptions(3),
            CancellationToken.None).AsTask());
        bool published = host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _);
        Assert.False(published);
    }

    [Fact]
    public async Task ConflictingOutputLeavesTargetUntouched()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("minimal-1page.pdf");
        QpdfPath input = QpdfPath.From("inputs/minimal-1page.pdf");
        await using QpdfDocument guard = await QpdfDocument.OpenAsync(host, input, CancellationToken.None);
        await Assert.ThrowsAsync<QpdfHostException>(() => Qpdf.SelectPageAsync(
            host,
            input,
            input,
            new PdfPageSelectionOptions(1),
            CancellationToken.None).AsTask());
        bool published = host.TryGetOutput(input, out byte[] _);
        Assert.False(published);
    }

    [Fact]
    public async Task OutputBudgetIsEnforcedBeforeStaging()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("minimal-1page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        await Assert.ThrowsAsync<QpdfLimitException>(() => Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/minimal-1page.pdf"),
            QpdfPath.From("outputs/x.pdf"),
            new PdfPageSelectionOptions(1),
            limits,
            CancellationToken.None).AsTask());
        bool published = host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _);
        Assert.False(published);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CancellationAbortsSelection()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("minimal-1page.pdf");
        using CancellationTokenSource source = new();
        source.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/minimal-1page.pdf"),
            QpdfPath.From("outputs/x.pdf"),
            new PdfPageSelectionOptions(1),
            source.Token).AsTask());
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
