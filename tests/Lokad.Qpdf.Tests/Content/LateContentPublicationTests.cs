using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Image;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class LateContentPublicationTests
{
    private sealed class Codec(Action<QpdfJpegCodecContext> observe) : IQpdfJpegCodec
    {
        internal int Calls { get; private set; }
        public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
            => LokadImageJpegCodec.Instance.Decode(encoded, frame, samples, context);
        public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context)
        {
            Calls++;
            Assert.Equal(new QpdfJpegFrame(32, 32, 3), frame);
            Assert.Equal(32 * 32 * 3, samples.Length);
            for (int offset = 0; offset < samples.Length; offset += 3)
            { Assert.Equal((byte)255, samples.Span[offset]); Assert.Equal((byte)0, samples.Span[offset + 1]); Assert.Equal((byte)0, samples.Span[offset + 2]); }
            observe(context);
            LokadImageJpegCodec.Instance.Encode(samples, frame, quality, encoded, context);
        }
    }

    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget, string phase, CancellationTokenSource cancellation)
        : IQpdfHost, IQpdfMemoryHost, IQpdfJpegCodecHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public IQpdfJpegCodec? JpegCodec => storage.JpegCodec;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new Transaction(await storage.CreateOutputAsync(path, token), phase, cancellation);
        private sealed class Transaction(IQpdfOutputTransaction inner, string phase, CancellationTokenSource cancellation) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            {
                if (phase == "write") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); }
                if (phase == "cancel-write") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                return inner.WriteAsync(bytes, token);
            }
            public ValueTask CommitAsync(CancellationToken token) => phase == "commit"
                ? ValueTask.FromException(new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed)) : inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private static async Task<InMemoryQpdfHost> Storage(string role, IQpdfJpegCodec? codec)
    {
        InMemoryQpdfHost storage = new(QpdfLimits.Default, codec);
        byte[] input = role == "appearance" ? LateContentTransformTests.Fixture("appearance", true, false)
            : CheckTests.Assemble(CheckTests.SinglePage("<< /Length 0 >>", []), 1);
        storage.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("source"), LateContentTransformTests.Fixture(role == "pruning" ? "unused" : "form", true, true), QpdfInputOwnership.Transfer);
        await using IQpdfOutputTransaction prior = await storage.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
        return storage;
    }

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, string role, bool selected, bool job, CancellationToken token)
    {
        List<string> argv = ["input", "--stream-data=uncompress"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["streamData"] = "uncompress" };
        if (role == "pruning") { argv.Add("--remove-unreferenced-resources=yes"); settings["removeUnreferencedResources"] = "yes"; }
        else
        {
            argv.AddRange(["--optimize-images", "--oi-min-width=0", "--oi-min-height=0", "--oi-min-area=0", "--ii-min-bytes=0"]);
            settings["optimizeImages"] = true; settings["oiMinWidth"] = "0"; settings["oiMinHeight"] = "0"; settings["oiMinArea"] = "0";
            settings["iiMinBytes"] = "0";
        }
        if (selected) { argv.AddRange(["--pages", ".", "1", "--"]); settings["pages"] = new[] { new { file = ".", range = "1" } }; }
        if (role == "appearance") { argv.Add("--flatten-annotations=all"); settings["flattenAnnotations"] = "all"; }
        else { argv.AddRange(["--overlay", "source", "--"]); settings["overlay"] = new { file = "source" }; }
        argv.Add("output");
        return job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), token) : Qpdf.ExecuteAsync(host, new(argv), token);
    }

    public static IEnumerable<object[]> Failures()
    {
        foreach (string role in new[] { "overlay", "appearance" })
        foreach (bool selected in new[] { false, true })
        foreach (bool job in new[] { false, true })
        foreach (string phase in new[] { "cancel-codec", "refuse-codec", "write", "commit", "cancel-write" })
        { yield return [role, selected, job, phase]; }
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task LateCodecAndPublicationFailuresPreserveTargetsAndRefund(string role, bool selected, bool job, string phase)
    {
        RecordingMemoryBudget root = new(16777216); using CancellationTokenSource cancellation = new();
        using (IMemoryScope owner = root.CreateScope())
        {
            IMemoryReservation? pressure = null;
            Codec codec = new(context =>
            {
                Assert.True(root.ReservedBytes >= 32 * 32 * 3);
                if (phase == "cancel-codec") { cancellation.Cancel(); context.CheckCancellation(); }
                if (phase == "refuse-codec")
                { Assert.True(owner.TryReserve(root.LimitBytes - root.ReservedBytes, out pressure)); }
            });
            using InMemoryQpdfHost storage = await Storage(role, codec);
            QpdfExecutionResult result;
            try { result = await Run(new Host(storage, owner, phase, cancellation), role, selected, job, cancellation.Token); }
            finally { pressure?.Dispose(); }
            Assert.Equal(1, codec.Calls);
            Assert.Equal(phase.StartsWith("cancel", StringComparison.Ordinal) ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            if (phase == "refuse-codec") { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded); }
            Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
            Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0L, root.ReservedBytes); Assert.Equal(0, root.LiveReservations);
    }

    [Theory]
    [InlineData("overlay", false)]
    [InlineData("overlay", true)]
    [InlineData("appearance", false)]
    [InlineData("appearance", true)]
    [InlineData("pruning", false)]
    [InlineData("pruning", true)]
    public async Task EveryGrowthCanRefuseThroughTheAssembledGraph(string role, bool job)
    {
        int growths;
        using (InMemoryQpdfHost storage = await Storage(role, LokadImageJpegCodec.Instance))
        {
            RecordingMemoryBudget root = new(16777216);
            using (IMemoryScope owner = root.CreateScope())
            using (CancellationTokenSource cancellation = new())
            {
                QpdfExecutionResult result = await Run(new Host(storage, owner, "none", cancellation), role, true, job, cancellation.Token);
                Assert.Equal(QpdfExecutionStatus.Completed, result.Status); growths = root.GrowthCalls;
                Assert.Equal(0L, storage.OpenLeaseCount);
            }
            Assert.Equal(0L, root.ReservedBytes);
        }
        Assert.InRange(growths, 1, 128);
        for (int growth = 1; growth <= growths; growth++)
        {
            RecordingMemoryBudget root = new(16777216) { RefuseOnGrowth = growth };
            using (IMemoryScope owner = root.CreateScope())
            using (CancellationTokenSource cancellation = new())
            using (InMemoryQpdfHost storage = await Storage(role, LokadImageJpegCodec.Instance))
            {
                QpdfExecutionResult result = await Run(new Host(storage, owner, "none", cancellation), role, true, job, cancellation.Token);
                Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
                Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
                Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
                Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
            }
            Assert.Equal(0L, root.ReservedBytes); Assert.Equal(0, root.LiveReservations);
        }
    }

    [Theory]
    [InlineData("overlay", false)]
    [InlineData("overlay", true)]
    [InlineData("appearance", false)]
    [InlineData("appearance", true)]
    public async Task LateImagePayloadEscapesDocumentAndHostOwnership(string role, bool job)
    {
        RecordingMemoryBudget root = new(16777216);
        using (IMemoryScope owner = root.CreateScope())
        using (CancellationTokenSource cancellation = new())
        {
            PdfStream escaped; byte[] original;
            using (InMemoryQpdfHost storage = await Storage(role, LokadImageJpegCodec.Instance))
            {
                QpdfExecutionResult result = await Run(new Host(storage, owner, "none", cancellation), role, true, job, cancellation.Token);
                Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
                Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output));
                storage.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
                await using (QpdfDocument document = await QpdfDocument.OpenAsync(new Host(storage, owner, "none", cancellation), QpdfPath.From("read"), cancellation.Token))
                {
                    escaped = Assert.Single(LateContentTransformTests.LiveImages(document));
                    original = escaped.CopyData(owner, cancellation.Token);
                    Assert.True(escaped.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject filter));
                    Assert.Equal(PdfName.FromAscii("DCTDecode"), filter);
                    Assert.Equal(new byte[] { 255, 216 }, original[..2]);
                }
                Assert.Equal(0L, storage.OpenLeaseCount);
            }
            Assert.Equal(original, escaped.CopyData(owner, cancellation.Token));
        }
        Assert.Equal(0L, root.ReservedBytes); Assert.Equal(0, root.LiveReservations);
    }

    [Theory]
    [InlineData("overlay", false, false)]
    [InlineData("overlay", true, true)]
    [InlineData("appearance", false, true)]
    [InlineData("appearance", true, false)]
    public async Task PreviouslyMissedEligibleImagesNowRequireTheHostCodec(string role, bool selected, bool job)
    {
        RecordingMemoryBudget root = new(16777216);
        using (IMemoryScope owner = root.CreateScope())
        using (CancellationTokenSource cancellation = new())
        using (InMemoryQpdfHost storage = await Storage(role, null))
        {
            QpdfExecutionResult result = await Run(new Host(storage, owner, "none", cancellation), role, selected, job, cancellation.Token);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, d => d.Message.Contains("codec supplied by the host", StringComparison.Ordinal));
            Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
            Assert.Equal(0L, storage.OpenLeaseCount);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MalformedLateFormFailsWithoutPublishing(bool appearance, bool job)
    {
        byte[] incomplete = "BI /W 2 /H 2 /BPC 8 /CS /RGB ID short"u8.ToArray();
        byte[] contents = appearance ? "q Q"u8.ToArray() : "/Bad Do"u8.ToArray();
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources " + (appearance ? "<<>> /Annots [6 0 R]" : "<< /XObject << /Bad 5 0 R >> >>") + " >>", null),
            (4, "<< /Length " + contents.Length + " >>", contents),
            (5, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources <<>> /Length " + incomplete.Length + " >>", incomplete),
            (6, "<< /Type /Annot /Subtype /Stamp /F 4 /Rect [0 0 100 100] /AP << /N 5 0 R >> >>", null)], 1);
        RecordingMemoryBudget root = new(16777216);
        using (IMemoryScope owner = root.CreateScope())
        using (CancellationTokenSource cancellation = new())
        using (InMemoryQpdfHost storage = await Storage("overlay", LokadImageJpegCodec.Instance))
        {
            storage.AddInput(QpdfPath.From(appearance ? "input" : "source"), source, QpdfInputOwnership.Transfer);
            QpdfExecutionResult result = await Run(new Host(storage, owner, "none", cancellation), appearance ? "appearance" : "overlay", true, job, cancellation.Token);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
            Assert.Equal(0L, storage.OpenLeaseCount);
        }
        Assert.Equal(0L, root.ReservedBytes); Assert.Equal(0, root.LiveReservations);
    }
}
