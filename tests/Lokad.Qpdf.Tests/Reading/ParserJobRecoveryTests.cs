using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class ParserJobRecoveryTests
{
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task NestedLengthRepairPreservesIndependentValuesThroughRewrite(bool job, int budget)
    {
        byte[] input = TolerantObjectLoadingTests.Pdf(
            ["<< /Length 6 0 R /Nested 6 0 R /Audit [ @ 5 ] >>\nstream\nabc\nendstream", "[ @ 3 ]"], false);
        InMemoryQpdfHost host = Host(input);
        QpdfExecutionResult result = await Rewrite(host, job, budget);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.RepairedObject);
        Assert.True(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] output));
        host.AddInput(QpdfPath.From("reread.pdf"), output, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread.pdf"), CancellationToken.None))
        {
            PdfDictionary page = Assert.IsType<PdfDictionary>(document.GetObject(3));
            Assert.True(page.TryGet(PdfName.FromAscii("Audit"), out QpdfObject audit));
            QpdfObject value = Resolve(document, audit);
            if (budget == 1) { Assert.IsType<PdfNull>(value); }
            else
            {
                PdfStream stream = Assert.IsType<PdfStream>(value);
                Assert.Equal("abc\n"u8.ToArray(), stream.Data);
                Assert.True(stream.Dictionary.TryGet(PdfName.FromAscii("Nested"), out QpdfObject nested));
                AssertRepairedPair(Resolve(document, nested), 3L);
                Assert.True(stream.Dictionary.TryGet(PdfName.FromAscii("Audit"), out QpdfObject outer));
                AssertRepairedPair(outer, 5L);
            }
        }
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task CompressedMemberBudgetsRemainIndependentThroughRewrite(bool job, int budget)
    {
        byte[] input = ReaderAcceptanceTests.ObjectStreamPdf("[ @ 6 ]", "[ @ 7 ]", false, true, null,
            "/Resources << >> /Audit [6 0 R 7 0 R]");
        InMemoryQpdfHost host = Host(input);
        QpdfExecutionResult result = await Rewrite(host, job, budget);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] output));
        host.AddInput(QpdfPath.From("reread.pdf"), output, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread.pdf"), CancellationToken.None))
        {
            PdfDictionary page = Assert.IsType<PdfDictionary>(document.GetObject(3));
            Assert.True(page.TryGet(PdfName.FromAscii("Audit"), out QpdfObject audit));
            PdfArray values = Assert.IsType<PdfArray>(audit);
            Assert.Equal(2, values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                QpdfObject value = Resolve(document, values[i]);
                if (budget == 1) { Assert.IsType<PdfNull>(value); }
                else { AssertRepairedPair(value, i + 6L); }
            }
        }
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortedCompressedMemberDoesNotAbortTheFollowingValidMember(bool job)
    {
        byte[] input = ReaderAcceptanceTests.ObjectStreamPdf("[ @ 6 ]", "(kept)", false, true, null,
            "/Resources << >> /Audit [6 0 R 7 0 R]");
        InMemoryQpdfHost host = Host(input);
        QpdfExecutionResult result = await Rewrite(host, job, 1);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] output));
        host.AddInput(QpdfPath.From("reread.pdf"), output, QpdfInputOwnership.Copy);
        PdfByteString kept;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread.pdf"), CancellationToken.None))
        {
            PdfDictionary page = Assert.IsType<PdfDictionary>(document.GetObject(3));
            Assert.True(page.TryGet(PdfName.FromAscii("Audit"), out QpdfObject audit));
            PdfArray values = Assert.IsType<PdfArray>(audit);
            Assert.Equal(2, values.Count);
            Assert.IsType<PdfNull>(Resolve(document, values[0]));
            kept = Assert.IsType<PdfByteString>(Resolve(document, values[1]));
        }
        Assert.Equal("kept"u8.ToArray(), kept.Value);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AbortedClassicObjectDoesNotCorruptFollowingNestedStreamLoad(bool job, bool brokenXref)
    {
        byte[] input = TolerantObjectLoadingTests.Pdf(
            ["[6 0 R 7 0 R 9 0 R]", "[ @ 6 ]",
             "<< /Length 8 0 R /Nested 8 0 R >>\nstream\nabc\nendstream", "00003", "(kept)"], brokenXref);
        InMemoryQpdfHost host = Host(input);
        QpdfExecutionResult result = await Rewrite(host, job, 1);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] output));
        host.AddInput(QpdfPath.From("reread.pdf"), output, QpdfInputOwnership.Copy);
        PdfStream stream;
        PdfByteString kept;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread.pdf"), CancellationToken.None))
        {
            PdfDictionary page = Assert.IsType<PdfDictionary>(document.GetObject(3));
            Assert.True(page.TryGet(PdfName.FromAscii("Audit"), out QpdfObject audit));
            PdfArray values = Assert.IsType<PdfArray>(Resolve(document, audit));
            Assert.Equal(3, values.Count);
            Assert.IsType<PdfNull>(Resolve(document, values[0]));
            stream = Assert.IsType<PdfStream>(Resolve(document, values[1]));
            Assert.True(stream.Dictionary.TryGet(PdfName.FromAscii("Nested"), out QpdfObject nested));
            Assert.Equal(3L, Assert.IsType<PdfInteger>(Resolve(document, nested)).Value);
            kept = Assert.IsType<PdfByteString>(Resolve(document, values[2]));
        }
        Assert.Equal("abc"u8.ToArray(), stream.Data);
        Assert.Equal("kept"u8.ToArray(), kept.Value);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompressedDictionaryDoesNotStartFileStreamRecovery(bool job)
    {
        byte[] input = ReaderAcceptanceTests.ObjectStreamPdf("<< /Length -1 >>\nstream\nabc", "42", false, true, null,
            "/Resources << >> /Audit [6 0 R 7 0 R]");
        InMemoryQpdfHost host = Host(input);
        await using (QpdfDocument strict = await QpdfDocument.OpenAsync(host, QpdfPath.From("a.pdf"), CancellationToken.None))
        {
            Assert.Throws<QpdfParseException>(() => strict.GetObject(6));
        }
        QpdfExecutionResult result = await Rewrite(host, job, 2);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] output));
        host.AddInput(QpdfPath.From("reread.pdf"), output, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread.pdf"), CancellationToken.None))
        {
            PdfDictionary page = Assert.IsType<PdfDictionary>(document.GetObject(3));
            Assert.True(page.TryGet(PdfName.FromAscii("Audit"), out QpdfObject audit));
            PdfArray values = Assert.IsType<PdfArray>(audit);
            PdfDictionary dictionary = Assert.IsType<PdfDictionary>(Resolve(document, values[0]));
            Assert.True(dictionary.TryGet(PdfName.FromAscii("Length"), out QpdfObject length));
            Assert.Equal(-1L, Assert.IsType<PdfInteger>(length).Value);
            Assert.Equal(42L, Assert.IsType<PdfInteger>(Resolve(document, values[1])).Value);
        }
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/Length -1")]
    [InlineData("/Length 0")]
    [InlineData("/Length 10000000")]
    [InlineData("/Length 99 0 R")]
    public async Task InvalidStreamLengthsRecoverBeforeWriting(string lengthEntry)
    {
        byte[] input = TolerantObjectLoadingTests.Pdf(["<< " + lengthEntry + " >>\nstream\nabc\nendstream"], false);
        InMemoryQpdfHost host = Host(input);
        QpdfExecutionResult result = await Rewrite(host, false, 2);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] output));
        host.AddInput(QpdfPath.From("reread.pdf"), output, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread.pdf"), CancellationToken.None))
        {
            PdfDictionary page = Assert.IsType<PdfDictionary>(document.GetObject(3));
            Assert.True(page.TryGet(PdfName.FromAscii("Audit"), out QpdfObject audit));
            Assert.Equal("abc\n"u8.ToArray(), Assert.IsType<PdfStream>(Resolve(document, audit)).Data);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryHonorsMarkerDelimitersAndPhysicalObjectBoundaries(bool missingEnd)
    {
        byte[] input = TolerantObjectLoadingTests.Pdf(
            ["<< /Length -1 /Next 6 0 R >>\nstream\n" + (missingEnd ? "abc\n" : "abcendstream!def\nendstream"), "42"], false, missingEnd);
        InMemoryQpdfHost host = Host(input);
        QpdfExecutionResult result = await Rewrite(host, false, 2);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] output));
        host.AddInput(QpdfPath.From("reread.pdf"), output, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread.pdf"), CancellationToken.None))
        {
            PdfDictionary page = Assert.IsType<PdfDictionary>(document.GetObject(3));
            Assert.True(page.TryGet(PdfName.FromAscii("Audit"), out QpdfObject audit));
            PdfStream stream = Assert.IsType<PdfStream>(Resolve(document, audit));
            Assert.Equal(missingEnd ? [] : "abcendstream!def\n"u8.ToArray(), stream.Data);
            Assert.True(stream.Dictionary.TryGet(PdfName.FromAscii("Next"), out QpdfObject next));
            Assert.Equal(42L, Assert.IsType<PdfInteger>(Resolve(document, next)).Value);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task StrictReadingRejectsNestedAndCompressedDamage()
    {
        byte[][] inputs =
        [
            TolerantObjectLoadingTests.Pdf(
                ["<< /Length 6 0 R /Audit [ @ 5 ] >>\nstream\nabc\nendstream", "[ @ 3 ]"], false),
            ReaderAcceptanceTests.ObjectStreamPdf("[ @ 6 ]", "[ @ 7 ]", false, true, null),
        ];
        for (int i = 0; i < inputs.Length; i++)
        {
            InMemoryQpdfHost host = Host(inputs[i]);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("a.pdf"), CancellationToken.None))
            {
                Assert.Throws<QpdfParseException>(() => document.GetObject(i == 0 ? 5 : 6));
                Assert.Throws<QpdfParseException>(() => document.GetObject(i == 0 ? 6 : 7));
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
    }

    private static InMemoryQpdfHost Host(byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("a.pdf"), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static ValueTask<QpdfExecutionResult> Rewrite(InMemoryQpdfHost host, bool job, int budget)
        => job
            ? Qpdf.ExecuteJobAsync(host,
                "{\"inputFile\":\"a.pdf\",\"outputFile\":\"out.pdf\",\"streamData\":\"uncompress\",\"global\":{\"parserMaxErrors\":" + budget + "}}",
                CancellationToken.None)
            : Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["--global", "--parser-max-errors=" + budget, "--", "a.pdf", "--stream-data=uncompress", "out.pdf"]),
                CancellationToken.None);

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    private static void AssertRepairedPair(QpdfObject value, long number)
    {
        PdfArray array = Assert.IsType<PdfArray>(value);
        Assert.Equal(2, array.Count);
        Assert.Equal("@"u8.ToArray(), Assert.IsType<PdfByteString>(array[0]).Value);
        Assert.Equal(number, Assert.IsType<PdfInteger>(array[1]).Value);
    }
}
