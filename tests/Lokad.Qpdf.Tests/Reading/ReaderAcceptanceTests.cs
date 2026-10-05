using System.IO.Compression;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class ReaderAcceptanceTests
{
    private static byte[] BadHeader(string[] bodies, bool generation)
    {
        string pdf = Encoding.ASCII.GetString(TolerantObjectLoadingTests.Pdf(bodies, false));
        string replacement = generation ? "5 1 obj\n" : "9 0 obj\n";
        return Encoding.ASCII.GetBytes(pdf.Replace("5 0 obj\n", replacement, StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> WrongHeaders()
    {
        string literal = "(" + new string('A', 4096) + ")";
        foreach (string body in new[] { literal, "<" + string.Concat(Enumerable.Repeat("41", 4096)) + ">", "/" + new string('A', 4096),
            new string('0', 4096) + "7", "0." + new string('0', 4096) + "1", "6 0 R", "[ " + literal + " /Name 0007 ]", "<< /A " + literal + " /A null /B [ /Name 7 ] >>" })
        foreach (bool generation in new[] { false, true })
        { yield return [body, generation]; }
    }

    [Theory]
    [MemberData(nameof(WrongHeaders))]
    public async Task RepeatedRejectedHeadersReleaseOnlyTheirValues(string body, bool generation)
    {
        byte[] input = BadHeader([body], generation);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), limits, CancellationToken.None))
        {
            Assert.Equal(1, document.PageCount);
            long baseline = document.Counters.LiveMemoryBytes;
            long work = document.Counters.TotalWork;
            for (int i = 0; i < 50; i++)
            {
                Assert.Contains("header contradicts", Assert.Throws<QpdfParseException>(() => document.GetObject(5)).Message, StringComparison.Ordinal);
                Assert.Equal(baseline, document.Counters.LiveMemoryBytes);
            }
            Assert.True(document.Counters.TotalWork > work);
            Assert.InRange(document.Counters.PeakMemoryBytes, baseline + 1L, limits.MaxMemoryBytes);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task RejectedHeaderPreservesFreshNestedLengthCache()
    {
        string length = new string('0', 4096) + "3";
        string stream = "<< /Length 6 0 R /A (" + new string('A', 4096) + ") >>\nstream\nabc\nendstream";
        byte[] input = BadHeader([stream, length], false);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        await using (QpdfDocument control = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), limits, CancellationToken.None))
        await using (QpdfDocument rejected = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), limits, CancellationToken.None))
        {
            Assert.Equal(3L, Assert.IsType<PdfInteger>(control.GetObject(6)).Value);
            Assert.Throws<QpdfParseException>(() => rejected.GetObject(5));
            PdfInteger cached = Assert.IsType<PdfInteger>(rejected.GetObject(6));
            Assert.Equal(3L, cached.Value);
            for (int i = 0; i < 20; i++)
            {
                Assert.Throws<QpdfParseException>(() => rejected.GetObject(5));
                Assert.Equal(control.Counters.LiveMemoryBytes, rejected.Counters.LiveMemoryBytes);
                Assert.Same(cached, rejected.GetObject(6));
            }
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task AcceptedValueAfterRejectedHeaderKeepsItsAlias()
    {
        byte[] input = BadHeader(["(" + new string('A', 4096) + ")", "(kept)"], false);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None))
        {
            Assert.Throws<QpdfParseException>(() => document.GetObject(5));
            PdfByteString kept = Assert.IsType<PdfByteString>(document.GetObject(6));
            long baseline = document.Counters.LiveMemoryBytes;
            for (int i = 0; i < 20; i++)
            {
                Assert.Throws<QpdfParseException>(() => document.GetObject(5));
                Assert.Equal(baseline, document.Counters.LiveMemoryBytes);
                Assert.Same(kept, document.GetObject(6));
            }
            Assert.Equal("kept"u8.ToArray(), kept.Value);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SyntaxFailureStillPrecedesHeaderIdentityValidation()
    {
        byte[] input = BadHeader(["(" + new string('A', 4096) + ") null"], false);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None))
        {
            long baseline = document.Counters.LiveMemoryBytes;
            Assert.Contains("endobj", Assert.Throws<QpdfParseException>(() => document.GetObject(5)).Message, StringComparison.Ordinal);
            Assert.Equal(baseline, document.Counters.LiveMemoryBytes);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PublicHeaderFailurePreservesTargetsAndObservedWork(bool job, bool generation)
    {
        byte[] input = BadHeader(["(" + new string('A', 4096) + ")"], generation);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        QpdfPath priorPath = QpdfPath.From("outputs/prior.pdf");
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(priorPath, CancellationToken.None))
        {
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxMemoryBytes = 100000L });
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","showObject":"5"}""", options, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=5"]), options, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains("header contradicts", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
        Assert.True(result.Counters.TotalWork > 0L);
        Assert.True(result.Counters.PeakMemoryBytes > input.Length + 4096L);
        Assert.Equal(0L, result.Counters.TotalOutputBytes);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(priorPath, out byte[] priorBytes));
        Assert.Equal("prior"u8.ToArray(), priorBytes);
    }

    internal static byte[] ObjectStreamPdf(string firstBody, string secondBody, bool flate, bool matched, string? headerOverride)
    {
        string header = headerOverride ?? $"6 0 7 {Encoding.ASCII.GetByteCount(firstBody) + 1} ";
        byte[] payload = Encoding.ASCII.GetBytes(header + firstBody + "\n" + secondBody);
        byte[] encoded = payload;
        if (flate)
        {
            using MemoryStream compressed = new();
            using (ZLibStream zlib = new(compressed, CompressionLevel.SmallestSize, true)) { zlib.Write(payload); }
            encoded = compressed.ToArray();
        }
        using MemoryStream pdf = new();
        void Text(string value) { pdf.Write(Encoding.ASCII.GetBytes(value)); }
        long[] offsets = new long[9];
        void Object(int number, string body) { offsets[number] = pdf.Length; Text($"{number} 0 obj\n{body}\nendobj\n"); }
        Text("%PDF-1.5\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>");
        Object(4, "<< /Length 0 >>\nstream\n\nendstream");
        offsets[5] = pdf.Length;
        Text($"5 0 obj\n<< /Type /ObjStm /N 2 /First {header.Length} /Length {encoded.Length}" + (flate ? " /Filter /FlateDecode" : "") + " >>\nstream\n");
        pdf.Write(encoded);
        Text("\nendstream\nendobj\n");
        offsets[8] = pdf.Length;
        using MemoryStream xref = new();
        void Entry(int type, long first, int second)
        {
            xref.WriteByte((byte)type);
            for (int shift = 24; shift >= 0; shift -= 8) { xref.WriteByte((byte)(first >> shift)); }
            xref.WriteByte((byte)(second >> 8)); xref.WriteByte((byte)second);
        }
        Entry(0, 0, 65535);
        for (int i = 1; i <= 5; i++) { Entry(1, offsets[i], 0); }
        Entry(2, 5, 0);
        Entry(matched ? 2 : 0, matched ? 5 : 0, matched ? 1 : 0);
        Entry(1, offsets[8], 0);
        Text($"8 0 obj\n<< /Type /XRef /Size 9 /Root 1 0 R /W [1 4 2] /Length {xref.Length} >>\nstream\n");
        pdf.Write(xref.ToArray());
        Text($"\nendstream\nendobj\nstartxref\n{offsets[8]}\n%%EOF\n");
        return pdf.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedCompressedMembersReleaseValuesAndKeepPriorCache(bool flate)
    {
        string kept = "(" + new string('K', 4096) + ")";
        byte[] input = ObjectStreamPdf(kept, "(" + new string('A', 4096) + ") null", flate, true, null);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = flate ? 1048576L : 100000L };
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), limits, CancellationToken.None))
        {
            Assert.Contains("trailing bytes", Assert.Throws<QpdfParseException>(() => document.GetObject(7)).Message, StringComparison.Ordinal);
            PdfByteString cached = Assert.IsType<PdfByteString>(document.GetObject(6));
            PdfStream raw = Assert.IsType<PdfStream>(document.GetObject(5));
            long baseline = document.Counters.LiveMemoryBytes;
            long work = document.Counters.TotalWork;
            for (int i = 0; i < 50; i++)
            {
                Assert.Contains("trailing bytes", Assert.Throws<QpdfParseException>(() => document.GetObject(7)).Message, StringComparison.Ordinal);
                Assert.Equal(baseline, document.Counters.LiveMemoryBytes);
                Assert.Same(cached, document.GetObject(6));
                Assert.Same(raw, document.GetObject(5));
            }
            Assert.True(document.Counters.TotalWork > work);
            Assert.Equal(Encoding.ASCII.GetBytes(new string('K', 4096)), cached.Value);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnmatchedMembersDoNotRetainParsedCopies(bool flate)
    {
        string discarded = "(" + new string('A', 4096) + ")";
        string padding = "null %" + new string('A', discarded.Length - 6);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), ObjectStreamPdf("(kept)", discarded, flate, false, null), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("inputs/control.pdf"), ObjectStreamPdf("(kept)", padding, flate, false, null), QpdfInputOwnership.Copy);
        await using (QpdfDocument control = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/control.pdf"), CancellationToken.None))
        await using (QpdfDocument discardedDocument = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None))
        {
            // Encoded sizes may differ; baseline after raw loading isolates the member work.
            control.GetObject(5); discardedDocument.GetObject(5);
            long controlBase = control.Counters.LiveMemoryBytes;
            long discardedBase = discardedDocument.Counters.LiveMemoryBytes;
            Assert.Equal("kept"u8.ToArray(), Assert.IsType<PdfByteString>(control.GetObject(6)).Value);
            Assert.Equal("kept"u8.ToArray(), Assert.IsType<PdfByteString>(discardedDocument.GetObject(6)).Value);
            Assert.Same(PdfNull.Instance, discardedDocument.GetObject(7));
            Assert.Equal(control.Counters.LiveMemoryBytes - controlBase, discardedDocument.Counters.LiveMemoryBytes - discardedBase);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateMembersWithinOnePassKeepTheExistingLastWinsPolicy(bool flate)
    {
        byte[] input = ObjectStreamPdf("(first)", "(last)", flate, true, "6 0 6 8 ");
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None))
        {
            PdfByteString value = Assert.IsType<PdfByteString>(document.GetObject(6));
            Assert.Equal("last"u8.ToArray(), value.Value);
            Assert.Same(value, document.GetObject(6));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, "null 0 7 8 ")]
    [InlineData(true, "null 0 7 8 ")]
    [InlineData(false, "6 0 7 0 ")]
    [InlineData(true, "6 0 7 0 ")]
    [InlineData(false, "6 0 5 8 ")]
    [InlineData(true, "6 0 5 8 ")]
    [InlineData(false, "6 0 7 99999 ")]
    [InlineData(true, "6 0 7 99999 ")]
    public async Task InvalidObjectStreamHeadersReleaseTheirScratch(bool flate, string header)
    {
        byte[] input = ObjectStreamPdf("(first)", "(last)", flate, true, header);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None))
        {
            PdfStream raw = Assert.IsType<PdfStream>(document.GetObject(5));
            long baseline = document.Counters.LiveMemoryBytes;
            for (int i = 0; i < 20; i++)
            {
                Assert.Throws<QpdfParseException>(() => document.GetObject(7));
                Assert.Equal(baseline, document.Counters.LiveMemoryBytes);
                Assert.Same(raw, document.GetObject(5));
            }
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CancellationDuringHeaderCandidatePreservesEarlierCache()
    {
        byte[] input = BadHeader(["[ (" + new string('A', 4096) + ") (" + new string('B', 16 * 1024 * 1024) + ") ]", "(kept)"], false);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = 1L };
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), limits, cancellation.Token))
        {
            PdfByteString kept = Assert.IsType<PdfByteString>(document.GetObject(6));
            long baseline = document.Counters.LiveMemoryBytes;
            Task<QpdfObject> loading = Task.Factory.StartNew(() => document.GetObject(5), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            bool observed = SpinWait.SpinUntil(() => document.Counters.LiveMemoryBytes >= baseline + 32L * 1024L * 1024L || loading.IsCompleted, TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.True(observed);
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await loading.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(document.Counters.PeakMemoryBytes >= baseline + 32L * 1024L * 1024L);
            Assert.Equal(baseline, document.Counters.LiveMemoryBytes);
            Assert.Equal("kept"u8.ToArray(), kept.Value);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringCompressedRetryKeepsPriorAcceptedCache(bool flate)
    {
        byte[] input = ObjectStreamPdf("(kept)", "(" + new string('A', 16 * 1024 * 1024) + ") null", flate, true, null);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = 1L };
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), limits, cancellation.Token))
        {
            Assert.Throws<QpdfParseException>(() => document.GetObject(7));
            PdfByteString kept = Assert.IsType<PdfByteString>(document.GetObject(6));
            long baseline = document.Counters.LiveMemoryBytes;
            Task<QpdfObject> loading = Task.Factory.StartNew(() => document.GetObject(7), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            bool observed = SpinWait.SpinUntil(() => document.Counters.LiveMemoryBytes >= baseline + 32L * 1024L * 1024L || loading.IsCompleted, TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.True(observed);
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await loading.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(document.Counters.PeakMemoryBytes >= baseline + 32L * 1024L * 1024L);
            Assert.Equal(baseline, document.Counters.LiveMemoryBytes);
            Assert.Equal("kept"u8.ToArray(), kept.Value);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
