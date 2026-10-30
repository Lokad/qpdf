using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentConstructionTests
{
    private sealed class Host(byte[] data, CancellationTokenSource? cancellation, IMemoryBudget? budget) : IQpdfHost, IQpdfMemoryHost
    {
        internal Host(byte[] data, CancellationTokenSource? cancellation) : this(data, cancellation, null) { }
        public IMemoryBudget? MemoryBudget => budget;
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
            public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            {

                token.ThrowIfCancellationRequested();
                if (offset < 0 || offset > Length) { throw new ArgumentOutOfRangeException(nameof(offset)); }
                int count = Math.Min(destination.Length, host.Data.Length - (int)offset);
                host.Data.AsMemory((int)offset, count).CopyTo(destination);
                return new(count);
            }
            public ValueTask DisposeAsync() { host.Close(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class CancelingGrowth(IMemoryBudget inner, CancellationTokenSource cancellation) : IMemoryBudget
    {
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            bool accepted = inner.TryReserve(bytes, out IMemoryReservation? storage);
            reservation = storage is null ? null : new Reservation(storage, cancellation); return accepted;
        }
        private sealed class Reservation(IMemoryReservation inner, CancellationTokenSource cancellation) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public bool TryResize(long bytes)
            { bool accepted = inner.TryResize(bytes); if (bytes >= 65536) { cancellation.Cancel(); } return accepted; }
            public void Dispose() => inner.Dispose();
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
    [Theory]
    [InlineData("key", false)]
    [InlineData("name", false)]
    [InlineData("description", false)]
    [InlineData("mime", false)]
    [InlineData("creation", false)]
    [InlineData("modification", false)]
    [InlineData("key", true)]
    [InlineData("name", true)]
    [InlineData("description", true)]
    [InlineData("mime", true)]
    [InlineData("creation", true)]
    [InlineData("modification", true)]
    public async Task LargeMetadataRefusesBeforeEncodingAndPreservesEarlierStorage(string field, bool shared)
    {
        string large = new(field == "mime" ? 'x' : '\u00e9', 1048576);
        AttachmentAdd add = new("file", field == "key" ? large : "key", field == "name" ? large : "name",
            field == "creation" ? large : null, field == "modification" ? large : null,
            field == "mime" ? large : null, field == "description" ? large : null, false);
        RecordingMemoryBudget root = new(4096); using IMemoryScope caller = root.CreateScope();
        Host host = new([], null, shared ? caller : null);
        QpdfLimits limits = shared ? QpdfLimits.Default : QpdfLimits.Default with { MaxMemoryBytes = 4096 };
        ResourceGovernor governor = new(limits, CancellationToken.None, host);
        using (ParserMemoryScope prior = ParserMemoryScope.Create(governor))
        {
            prior.Reserve(17L); long baseline = governor.Snapshot().LiveMemoryBytes;
            using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                Task Run() => Assemble(host, [], new([add], [], []), new(StringComparer.Ordinal), false, limits, governor, memory, CancellationToken.None);
                if (shared) { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Run); }
                else { await Assert.ThrowsAsync<QpdfLimitException>(Run); }
                Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 65536L);
            }
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(host.Opens, host.Closes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(129)]
    public async Task NameTreeBoundariesAndUnicodeRoundTripThroughThePublicPipeline(int count)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("file"), "payload"u8.ToArray(), QpdfInputOwnership.Copy);
        const string name = "Euro \u20ac \ud83d\ude00";
        QpdfAttachmentSpec[] specs = Enumerable.Range(0, count).Select(index =>
            new QpdfAttachmentSpec("file", index.ToString("D3") + name, name, new string('a', 255) + "\ud83d\ude00", "text/plain", "D:20240101000000Z", null, false)).ToArray();
        await Qpdf.AddAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), specs, CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] pdf));
        host.AddInput(QpdfPath.From("reread"), pdf, QpdfInputOwnership.Copy);
        IReadOnlyList<QpdfAttachmentInfo> listing = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("reread"),
            CancellationToken.None);
        Assert.Equal(count, listing.Count);
        for (int index = 0; index < count; index++)
        {
            Assert.Equal(specs[index].Key, listing[index].Key); Assert.Equal(name, listing[index].FileName);
            Assert.Equal(specs[index].Description, listing[index].Description);
            Assert.Equal("D:20240101000000Z", listing[index].CreationDate);
        }
        foreach (int index in new[] { 0, count - 1 }.Distinct())
        {
            QpdfAttachmentExtraction extracted = await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("reread"),
                specs[index].Key ?? throw new InvalidOperationException("Missing fixture key."), QpdfPath.From("payload"), CancellationToken.None);
            Assert.Equal(7L, extracted.ByteCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("payload"), out byte[] payload));
            Assert.Equal("payload"u8.ToArray(), payload);
        }
        QpdfAttachmentEditResult copied = await Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("input"),
            QpdfPath.From("copied"), [new(QpdfPath.From("reread"), "copy-")], CancellationToken.None);
        Assert.Equal(count, copied.AttachmentCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("copied"), out byte[] copiedPdf));
        host.AddInput(QpdfPath.From("copy-read"), copiedPdf, QpdfInputOwnership.Transfer);
        string removedKey = "copy-" + specs[0].Key;
        QpdfAttachmentEditResult removed = await Qpdf.RemoveAttachmentsAsync(host, QpdfPath.From("copy-read"),
            QpdfPath.From("removed"), [removedKey], CancellationToken.None);
        Assert.Equal(count - 1, removed.AttachmentCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("removed"), out byte[] removedPdf));
        host.AddInput(QpdfPath.From("removed-read"), removedPdf, QpdfInputOwnership.Transfer);
        IReadOnlyList<QpdfAttachmentInfo> remaining = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("removed-read"),
            CancellationToken.None);
        Assert.Equal(count - 1, remaining.Count);
        Assert.DoesNotContain(remaining, item => item.Key == removedKey);
        Assert.Equal(specs.Skip(1).Select(spec => "copy-" + spec.Key), remaining.Select(item => item.Key));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public void NameArrayCapacityRejectsBeforeAccessingEntries()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            Assert.Throws<QpdfParseException>(() => AttachmentBuilder.BuildNameTree(new ExcessivePairs(), governor, memory, CancellationToken.None));
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class ExcessivePairs : IReadOnlyList<KeyValuePair<string, PdfReference>>
    {
        public int Count => Array.MaxLength / 2 + 1;
        public KeyValuePair<string, PdfReference> this[int index] => throw new InvalidOperationException("Capacity must reject before entry access.");
        public IEnumerator<KeyValuePair<string, PdfReference>> GetEnumerator() => throw new InvalidOperationException("Capacity must reject before enumeration.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NameArrayRefusalPreservesEarlierStorage(bool shared)
    {
        RecordingMemoryBudget root = new(4096L); using IMemoryScope caller = root.CreateScope();
        Host host = new([], null, shared ? caller : null);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = shared ? 1048576L : 4096L }, CancellationToken.None, host);
        KeyValuePair<string, PdfReference>[] pairs = Enumerable.Range(0, 1024).Select(i => new KeyValuePair<string, PdfReference>("key", new(i + 1, 0))).ToArray();
        using (ParserMemoryScope prior = ParserMemoryScope.Create(governor))
        {
            prior.Reserve(17L); long baseline = governor.Snapshot().LiveMemoryBytes;
            using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
            {
                if (shared) { Assert.Throws<QpdfMemoryAdmissionException>(() => AttachmentBuilder.BuildNameTree(pairs, governor, memory, CancellationToken.None)); }
                else { Assert.Throws<QpdfLimitException>(() => AttachmentBuilder.BuildNameTree(pairs, governor, memory, CancellationToken.None)); }
            }
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(17L, prior.ReservedBytes);
        }
        Assert.Equal(0L, root.ReservedBytes); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterNameArrayAdmissionPrecedesAllocation(bool governorToken)
    {
        RecordingMemoryBudget root = new(1048576L); using IMemoryScope caller = root.CreateScope();
        using CancellationTokenSource cancellation = new();
        Host host = new([], null, new CancelingGrowth(caller, cancellation));
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None, host);
        KeyValuePair<string, PdfReference>[] pairs = Enumerable.Range(0, 8192).Select(i => new KeyValuePair<string, PdfReference>("key", new(i + 1, 0))).ToArray();
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.ThrowsAny<OperationCanceledException>(() => AttachmentBuilder.BuildNameTree(pairs, governor, memory,
                governorToken ? CancellationToken.None : cancellation.Token));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 65536L);
        }
        Assert.True(cancellation.IsCancellationRequested); Assert.Equal(0L, root.ReservedBytes);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("ASCII")]
    [InlineData("Latin \u00e9")]
    [InlineData("\ud800")]
    public void EncodedMetadataKeepsTheExistingAsciiAndUtf16Representation(string value)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfByteString name;
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            PdfDictionary spec = AttachmentBuilder.BuildFilespecDictionary(value, null, 3, governor, memory, CancellationToken.None);
            Assert.True(spec.TryGet(PdfName.FromAscii("F"), out QpdfObject raw)); name = Assert.IsType<PdfByteString>(raw);
        }
        bool ascii = value.All(unit => unit <= 127);
        byte[] expected = ascii ? System.Text.Encoding.ASCII.GetBytes(value)
            : new byte[] { 0xfe, 0xff }.Concat(System.Text.Encoding.BigEndianUnicode.GetBytes(value)).ToArray();
        Assert.Equal(expected, name.ValueMemory.ToArray()); Assert.Equal(!ascii, name.Hex);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterTextAdmissionPrecedesEncodingAndRefundsBothTokens(bool governorToken)
    {
        string value = new('\u00e9', 1048576);
        RecordingMemoryBudget root = new(8 * 1024 * 1024); using IMemoryScope caller = root.CreateScope();
        using CancellationTokenSource cancellation = new(); Host host = new([], null, new CancelingGrowth(caller, cancellation));
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None, host);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            Assert.ThrowsAny<OperationCanceledException>(() => AttachmentBuilder.BuildFilespecDictionary(value, null, 3,
                governor, memory, governorToken ? CancellationToken.None : cancellation.Token));
            Assert.True(cancellation.IsCancellationRequested);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 65536L);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }

}
