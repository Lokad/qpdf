using System.IO.Compression;
using System.Reflection;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentConstructionTests
{
    private sealed class Host(byte[] data, CancellationTokenSource? cancellation) : IQpdfHost
    {
        internal int Opens, Closes;
        internal byte[] Data => data;
        internal void Close() { Closes++; cancellation?.Cancel(); }
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        { Opens++; return ValueTask.FromResult<IQpdfInput>(new Input(path, this)); }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => throw new InvalidOperationException("Assembly must not publish.");
        private sealed class Input(QpdfPath path, Host host) : IQpdfInput
        {
            public QpdfPath Path => path;
            public string Identity => "attachment";
            public long Length => host.Data.Length;
            public ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken token)
                => ValueTask.FromResult<ReadOnlyMemory<byte>>(host.Data);
            public ValueTask DisposeAsync() { host.Close(); return ValueTask.CompletedTask; }
        }
    }

    // Test the live shared assembly without charging preowned primary PDF/request
    // inputs. This exposes payload identity and incremental construction costs.
    private static async Task Assemble(Host host, Dictionary<int, QpdfObject> pool, AttachmentEdits edits,
        SortedDictionary<string, PdfReference> existing, bool compress, QpdfLimits limits,
        ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
    {
        MethodInfo method = typeof(Qpdf).GetMethod("AssembleAttachmentsAsync", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing attachment assembly.");
        object pending = method.Invoke(null, [host, existing, pool, edits, null, compress, null, false,
            new List<(IReadOnlyDictionary<int, int> Map, HashSet<int> CompressedOrig)>(), QpdfRecoveryMode.Strict,
            limits, governor, new DiagnosticBag(governor), memory, token]) ?? throw new InvalidOperationException("Missing assembly result.");
        Task task = (Task)(pending.GetType().GetMethod("AsTask")?.Invoke(pending, null)
            ?? throw new InvalidOperationException("Missing assembly task."));
        await task;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedPayloadSurvivesInputAndConstructionClosure(bool compress)
    {
        byte[] bytes = new byte[131072]; new Random(42).NextBytes(bytes);
        Host host = new(bytes, null); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Dictionary<int, QpdfObject> pool = [];
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            await Assemble(host, pool, new([new("file", "key", "name", null, null, null, null, false)], [], []),
                new(StringComparer.Ordinal), compress, QpdfLimits.Default, governor, memory, CancellationToken.None);
            Assert.Equal(1, host.Closes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        PdfStream stream = Assert.Single(pool.Values.OfType<PdfStream>());
        if (!compress) { Assert.Same(bytes, stream.AllocationCarrier); Assert.Equal(bytes, stream.RawMemory.ToArray()); }
        else
        {
            using MemoryStream encoded = new(stream.RawMemory.ToArray()); using ZLibStream decoder = new(encoded, CompressionMode.Decompress);
            using MemoryStream decoded = new(); await decoder.CopyToAsync(decoded); Assert.Equal(bytes, decoded.ToArray());
        }
    }

    [Fact]
    public async Task CancellationAtAttachmentClosePrecedesPayloadAndMetadataConstruction()
    {
        using CancellationTokenSource cancellation = new(); Host host = new(new byte[1048576], cancellation);
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token); Dictionary<int, QpdfObject> pool = [];
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Assemble(host, pool,
                new([new("file", "key", "name", null, null, null, new string('x', 1048576), false)], [], []),
                new(StringComparer.Ordinal), false, QpdfLimits.Default, governor, memory, cancellation.Token));
            Assert.Empty(pool); Assert.Equal(1, host.Closes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task ManyConflictsNeedNoKeyLedgerAndStillProcessLaterAdds()
    {
        AttachmentAdd duplicate = new("file", "already", "name", null, null, null, null, false);
        AttachmentAdd[] adds = Enumerable.Repeat(duplicate, 262144).Append(duplicate with { Key = "later" }).ToArray();
        Host host = new([], null); QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096 };
        ResourceGovernor governor = new(limits, CancellationToken.None); Dictionary<int, QpdfObject> pool = [];
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            QpdfParseException failure = await Assert.ThrowsAsync<QpdfParseException>(() => Assemble(host, pool, new(adds, [], []),
                new(StringComparer.Ordinal) { ["already"] = new(7, 0) }, false, limits, governor, memory, CancellationToken.None));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 65536L);
            Assert.Equal("Attachments with conflicting keys already exist; use --replace to replace or --key to specify a different key. (offset 0).", failure.Message);
            Assert.Equal(1, host.Opens); Assert.Equal(1, host.Closes); Assert.Single(pool.Values.OfType<PdfStream>());
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
