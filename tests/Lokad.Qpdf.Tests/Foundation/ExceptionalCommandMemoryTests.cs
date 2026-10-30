using System.IO.Compression;
using System.Text;
using Lokad.Qpdf.Image;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class ExceptionalCommandMemoryTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output");

    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget, string phase, Exception failure) : IQpdfHost, IQpdfMemoryHost, IQpdfJpegCodecHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public IQpdfJpegCodec JpegCodec => LokadImageJpegCodec.Instance;
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            IQpdfInput input = await storage.OpenInputAsync(path, token);
            return path == Input ? new FaultingInput(input, phase, failure) : input;
        }
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            if (phase == "output") { throw failure; }
            return new Transaction(await storage.CreateOutputAsync(path, token), phase, failure);
        }
        private sealed class FaultingInput(IQpdfInput inner, string phase, Exception failure) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            {
                int result = await inner.ReadAsync(offset, destination, token);
                if (phase == "read") { throw failure; }
                return result;
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
        private sealed class Transaction(IQpdfOutputTransaction inner, string phase, Exception failure) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            { if (phase == "write") { throw failure; } return inner.WriteAsync(bytes, token); }
            public ValueTask CommitAsync(CancellationToken token)
            { if (phase == "commit") { throw failure; } return inner.CommitAsync(token); }
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                if (phase == "close") { throw failure; }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbandoningCommandPreparationRefundsOnceAndPreservesOtherOwners(bool constructed)
    {
        RecordingMemoryBudget root = new(4194304L);
        using IMemoryScope owner = root.CreateScope();
        Assert.True(owner.TryReserve(4096L, out IMemoryReservation? held));
        using (held)
        using (InMemoryQpdfHost storage = new())
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None,
                new Host(storage, owner, "none", new InvalidOperationException()));
            governor.ExpectExecutionResult();
            if (constructed) { governor.PrepareExecutionResult(); }
            using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
            {
                memory.Reserve(1048640L);
                memory.RetainGraph();
            }
            Assert.True(root.ReservedBytes > 1048576L);
            governor.AbandonExecutionResult();
            Assert.Equal(4096L, root.ReservedBytes);
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            governor.AbandonExecutionResult();
            Assert.Equal(4096L, root.ReservedBytes);
            Assert.Equal(4096L, held.Bytes);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    public static IEnumerable<object[]> FailureCases()
    {
        foreach (string route in new[] { "argv", "job", "mixed", "response", "qdf", "compress", "uncompress" })
        foreach (string phase in new[] { "read", "output", "close" })
        foreach (bool oom in new[] { false, true }) { yield return [route, phase, oom]; }
        foreach (string route in new[] { "attachment", "appearances", "image", "inspection" })
        foreach (string phase in new[] { "output", "write", "commit", "close" })
        foreach (bool oom in new[] { false, true }) { yield return [route, phase, oom]; }
    }

    private static async Task<byte[]> PrepareInput(string route)
    {
        if (route == "inspection")
        {
            byte[] encoded = Encoding.ASCII.GetBytes(Convert.ToHexString(new byte[65536]) + ">");
            return CheckTests.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1 1] /Contents 4 0 R >>", null),
                (4, "<< /Filter /ASCIIHexDecode /Length " + encoded.Length + " >>", encoded),
            ], 1);
        }
        if (route == "image")
        {
            byte[] samples = new byte[256 * 256 * 3]; new Random(42).NextBytes(samples);
            return Content.ImageOptimizeTests.BuildImagePage(256, 256, "DeviceRGB", 8, string.Empty, samples);
        }
        if (route == "attachment")
        {
            using MemoryStream packed = new();
            using (ZLibStream compressor = new(packed, CompressionLevel.Fastest, true)) { compressor.Write(new byte[131072]); }
            return CheckTests.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(data) 4 0 R] >> >> >>", null),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1 1] >>", null),
                (4, "<< /Type /Filespec /F (data) /EF << /F 5 0 R >> >>", null),
                (5, "<< /Type /EmbeddedFile /Filter /FlateDecode /Length " + packed.Length + " >>", packed.ToArray()),
            ], 1);
        }
        if (route == "appearances")
        {
            byte[] appearance = Encoding.ASCII.GetBytes("q BT ET Q");
            return CheckTests.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /NeedAppearances true >> >>", null),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [4 0 R] >>", null),
                (4, "<< /Type /Annot /Subtype /Widget /Rect [0 0 100 20] /FT /Tx /V (" + new string('A', 131072) + ") /DA (/F#31 12 Tf) /AP << /N 5 0 R >> >>", null),
                (5, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> /Length " + appearance.Length + " >>", appearance),
            ], 1);
        }
        if (route is "compress" or "uncompress")
        {
            byte[] raw = Enumerable.Repeat((byte)'A', 131072).ToArray();
            if (route == "compress") { return raw; }
            using MemoryStream result = new();
            using (ZLibStream encoded = new(result, CompressionLevel.Fastest, true)) { encoded.Write(raw); }
            return result.ToArray();
        }
        List<(int Number, string Dict, byte[]? StreamData)> objects = CheckTests.SinglePage("<< /Length 0 >>", []);
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /Audit (" + new string('A', 131072) + ") >>", null);
        byte[] pdf = CheckTests.Assemble(objects, 1);
        if (route != "qdf") { return pdf; }
        using InMemoryQpdfHost prepare = new();
        prepare.AddInput(Input, pdf, QpdfInputOwnership.Transfer);
        QpdfExecutionResult emitted = await Qpdf.ExecuteAsync(prepare,
            new QpdfCommandInvocation([Input.Value, "--qdf", Output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, emitted.Status);
        Assert.True(prepare.TryGetOutput(Output, out byte[] qdf));
        return qdf;
    }

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, string route) => route switch
    {
        "inspection" => Qpdf.ExecuteAsync(host, new QpdfCommandInvocation([Input.Value, "--show-object=4", "--filtered-stream-data", "--", Output.Value]), CancellationToken.None),
        "attachment" => Qpdf.ExecuteAsync(host, new QpdfCommandInvocation([Input.Value, "--show-attachment=data", "--", Output.Value]), CancellationToken.None),
        "appearances" => Qpdf.ExecuteAsync(host, new QpdfCommandInvocation([Input.Value, "--generate-appearances", "--pages", ".", "1", "--", Output.Value]), CancellationToken.None),
        "image" => Qpdf.ExecuteAsync(host, new QpdfCommandInvocation([Input.Value, "--optimize-images", Output.Value]), CancellationToken.None),
        "job" => Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","streamData":"preserve"}""", CancellationToken.None),
        "mixed" => Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["--job-json-file=settings", Input.Value, Output.Value]), CancellationToken.None),
        "response" => Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["@arguments"]), CancellationToken.None),
        "qdf" => Qpdf.FixQdfAsync(host, Input, Output, CancellationToken.None),
        "compress" => Qpdf.RunZlibFlateAsync(host, Input, Output, new QpdfZlibFlateOptions(QpdfZlibFlateMode.Compress, 1), CancellationToken.None),
        "uncompress" => Qpdf.RunZlibFlateAsync(host, Input, Output, new QpdfZlibFlateOptions(QpdfZlibFlateMode.Uncompress, null), CancellationToken.None),
        _ => Qpdf.ExecuteAsync(host, new QpdfCommandInvocation([Input.Value, "--stream-data=preserve", Output.Value]), CancellationToken.None),
    };

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task UnexpectedFailuresCleanUpBeforeReusingTheBorrowedBudget(string route, string phase, bool oom)
    {
        using InMemoryQpdfHost storage = new();
        byte[] source = await PrepareInput(route);
        storage.AddInput(Input, source, QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("settings"), "{}"u8.ToArray(), QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("arguments"), "input\n--stream-data=preserve\noutput\n"u8.ToArray(), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await storage.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        RecordingMemoryBudget root = new(16777216L);
        using IMemoryScope owner = root.CreateScope();
        Assert.True(owner.TryReserve(4096L, out IMemoryReservation? held));
        using (held)
        {
            Exception injected = oom ? new OutOfMemoryException("injected") : new InvalidOperationException("injected");
            Host host = new(storage, owner, phase, injected);
            Exception? observed = await Record.ExceptionAsync(async () => await Run(host, route));
            Assert.Same(injected, observed);
            Assert.Equal(0L, storage.OpenLeaseCount);
            Assert.Equal(4096L, held.Bytes);
            Assert.InRange(root.ReservedBytes, 4096L, 8192L);
            Assert.True(root.PeakBytes >= source.LongLength);
            Assert.True(storage.TryGetOutput(Output, out byte[] published));
            if (phase == "close") { Assert.False(published.AsSpan().SequenceEqual("prior"u8)); }
            else { Assert.Equal("prior"u8.ToArray(), published); }
            QpdfExecutionResult next = await Run(new Host(storage, owner, "none", injected), route);
            Assert.Equal(QpdfExecutionStatus.Completed, next.Status);
            Assert.Equal(0L, storage.OpenLeaseCount);
            Assert.InRange(root.ReservedBytes, 4096L, 16384L);
        }
    }

    [Theory]
    [InlineData("attachment")]
    [InlineData("appearances")]
    [InlineData("image")]
    [InlineData("inspection")]
    public async Task RefusalAtEachGrowthRetiresNewBuffersAndLeavesPriorOutput(string route)
    {
        byte[] source = await PrepareInput(route);
        RecordingMemoryBudget baseline = new(16777216L);
        int growths;
        using (IMemoryScope owner = baseline.CreateScope())
        using (InMemoryQpdfHost storage = new())
        {
            storage.AddInput(Input, source, QpdfInputOwnership.Copy);
            QpdfExecutionResult successful = await Run(new Host(storage, owner, "none", new InvalidOperationException()), route);
            Assert.Equal(QpdfExecutionStatus.Completed, successful.Status);
            growths = baseline.GrowthCalls;
        }
        Assert.Equal(0L, baseline.ReservedBytes);
        for (int failureIndex = 1; failureIndex <= growths; failureIndex++)
        {
            RecordingMemoryBudget root = new(16777216L) { RefuseOnGrowth = failureIndex };
            using (IMemoryScope owner = root.CreateScope())
            using (InMemoryQpdfHost storage = new())
            {
                storage.AddInput(Input, source, QpdfInputOwnership.Copy);
                await using (IQpdfOutputTransaction prior = await storage.CreateOutputAsync(Output, CancellationToken.None))
                { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
                QpdfExecutionResult result = await Run(new Host(storage, owner, "none", new InvalidOperationException()), route);
                Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
                // Subsequent codec probes or bounded failure reporting can make
                // new requests after refusal; the operation still fails safely.
                Assert.True(root.GrowthCalls >= failureIndex);
                Assert.Equal(0L, storage.OpenLeaseCount);
                Assert.True(storage.TryGetOutput(Output, out byte[] published));
                Assert.Equal("prior"u8.ToArray(), published);
            }
            Assert.Equal(0L, root.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("qdf")]
    [InlineData("compress")]
    [InlineData("uncompress")]
    public async Task RepeatedCompanionsRetainResultsWithoutAccumulatingWorkingBuffers(string route)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(Input, await PrepareInput(route), QpdfInputOwnership.Transfer);
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope owner = root.CreateScope();
        Host host = new(storage, owner, "none", new InvalidOperationException());
        List<QpdfExecutionResult> results = [];
        for (int index = 0; index < 32; index++)
        {
            QpdfExecutionResult result = await Run(host, route);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.InRange(result.Counters.LiveMemoryBytes, 1L, 1024L);
            Assert.Equal(0L, result.Counters.OpenHandles);
            results.Add(result);
        }
        Assert.InRange(root.ReservedBytes, 1L, 32768L);
        Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.All(results, result => Assert.Equal(QpdfExitCode.Success, result.ExitCode));
        results.Clear();
        owner.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task QdfMemberBufferAdmissionCanFailAfterTheInputReadAndRefundItsAttempt()
    {
        const int count = 2000;
        List<(int Number, string Dict, byte[]? StreamData)> objects = CheckTests.SinglePage("<< /Length 0 >>", []);
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /Audit [" +
            string.Join(' ', Enumerable.Range(6, count).Select(number => number + " 0 R")) + "] >>", null);
        for (int number = 6; number < 6 + count; number++) { objects.Add((number, "(kept)", null)); }
        using InMemoryQpdfHost storage = new();
        storage.AddInput(Input, CheckTests.Assemble(objects, 1), QpdfInputOwnership.Transfer);
        QpdfExecutionResult emitted = await Qpdf.ExecuteAsync(storage,
            new QpdfCommandInvocation([Input.Value, "--qdf", "--object-streams=generate", Output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, emitted.Status);
        Assert.True(storage.TryGetOutput(Output, out byte[] qdf));
        Assert.True(qdf.AsSpan().IndexOf("/ObjStm"u8) >= 0);
        // The old binary xref is discarded by repair. Clear it so arbitrary
        // offset bytes cannot hit the companion's explicit carriage-return rule.
        int xrefData = qdf.AsSpan().LastIndexOf("\nstream\n"u8) + 8;
        int xrefEnd = qdf.AsSpan(xrefData).IndexOf("\nendstream"u8) + xrefData;
        Assert.True(xrefData >= 8 && xrefEnd > xrefData);
        qdf.AsSpan(xrefData, xrefEnd - xrefData).Clear();
        Assert.DoesNotContain((byte)'\r', qdf);
        storage.AddInput(Input, qdf, QpdfInputOwnership.Transfer);
        QpdfExecutionResult repaired = await Run(storage, "qdf");
        Assert.Equal(QpdfExecutionStatus.Completed, repaired.Status);
        await using (IQpdfOutputTransaction prior = await storage.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        RecordingMemoryBudget root = new(qdf.LongLength + 32768L);
        using IMemoryScope owner = root.CreateScope();
        QpdfExecutionResult refused = await Run(new Host(storage, owner, "none", new InvalidOperationException()), "qdf");
        Assert.Equal(QpdfExecutionStatus.Failed, refused.Status);
        Assert.Contains(refused.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(root.PeakBytes >= qdf.LongLength);
        Assert.InRange(root.ReservedBytes, 1L, 1024L);
        Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.True(storage.TryGetOutput(Output, out byte[] unchanged));
        Assert.Equal("prior"u8.ToArray(), unchanged);
        owner.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }
}
