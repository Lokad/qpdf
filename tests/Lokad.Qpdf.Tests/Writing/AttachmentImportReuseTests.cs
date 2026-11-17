using System.Text;
using System.Text.RegularExpressions;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Operations;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentImportReuseTests
{
    private sealed class Host(InMemoryQpdfHost storage) : IQpdfHost
    {
        internal readonly Dictionary<string, int> Opens = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, long> Bytes = new(StringComparer.Ordinal);
        internal int Live, Peak;
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            IQpdfInput input = await storage.OpenInputAsync(path, token);
            Opens[path.Value] = Opens.GetValueOrDefault(path.Value) + 1;
            Live++; Peak = Math.Max(Peak, Live);
            return new Input(input, this);
        }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => storage.CreateOutputAsync(path, token);
        private sealed class Input(IQpdfInput inner, Host host) : IQpdfInput
        {
            private bool _disposed;
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            {
                int count = await inner.ReadAsync(offset, destination, token);
                host.Bytes[Path.Value] = host.Bytes.GetValueOrDefault(Path.Value) + count;
                return count;
            }
            public async ValueTask DisposeAsync()
            {
                if (_disposed) { return; }
                _disposed = true;
                try { await inner.DisposeAsync(); }
                finally { host.Live--; }
            }
        }
    }

    private static byte[] Primary() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf"));

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(16, false)]
    [InlineData(3, true)]
    public async Task CompatibleRequestsReuseAcquisitionAndPayloadGraphs(int count, bool independent)
    {
        byte[] source = AttachmentCopyTests.SourceFixture();
        using InMemoryQpdfHost storage = new(QpdfLimits.Default with { MaxOpenHandles = 2 }, null);
        storage.AddInput(QpdfPath.From("input"), Primary(), QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("source"), source, QpdfInputOwnership.Copy);
        storage.AddInput(QpdfPath.From("other"), source, QpdfInputOwnership.Copy);
        Host host = new(storage);
        QpdfAttachmentCopy[] copies = Enumerable.Range(0, count).Select(index => new QpdfAttachmentCopy(
            QpdfPath.From(independent && index == 1 ? "other" : "source"), "p" + index + "-")).ToArray();
        QpdfAttachmentEditResult result = await Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), copies, CancellationToken.None);
        Assert.Equal(2 * count, result.AttachmentCount); Assert.Empty(result.Warnings);
        Assert.Equal(1, host.Opens["source"]); Assert.Equal(source.Length, host.Bytes["source"]);
        Assert.Equal(independent ? 1 : 0, host.Opens.GetValueOrDefault("other"));
        Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        storage.AddInput(QpdfPath.From("reread"), output, QpdfInputOwnership.Transfer);
        IReadOnlyList<QpdfAttachmentInfo> infos = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("reread"), CancellationToken.None);
        foreach (string suffix in new[] { "alpha.txt", "beta.txt" })
        {
            QpdfAttachmentInfo[] group = infos.Where(info => info.Key.EndsWith(suffix, StringComparison.Ordinal)).ToArray();
            Assert.Equal(count, group.Length);
            Assert.Equal(independent ? 2 : 1, group.Select(info => info.StreamObjectNumber).Distinct().Count());
        }
        Assert.InRange(host.Peak, 1, 2); Assert.Equal(0, host.Live); Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Fact]
    public async Task BoundedReuseClosesEachReaderBeforeOpeningIndependentSources()
    {
        using InMemoryQpdfHost storage = new(QpdfLimits.Default with { MaxOpenHandles = 2 }, null);
        storage.AddInput(QpdfPath.From("input"), Primary(), QpdfInputOwnership.Transfer);
        byte[] source = AttachmentCopyTests.SourceFixture();
        List<QpdfAttachmentCopy> copies = [];
        for (int index = 0; index < 35; index++)
        {
            string path = "source" + index;
            storage.AddInput(QpdfPath.From(path), source, QpdfInputOwnership.Copy);
            copies.Add(new(QpdfPath.From(path), "p" + index + "-"));
        }
        copies.Add(new(QpdfPath.From("source0"), "first-"));
        copies.Add(new(QpdfPath.From("source34"), "last-"));
        Host host = new(storage);
        QpdfAttachmentEditResult result = await Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), copies, CancellationToken.None);
        Assert.Equal(74, result.AttachmentCount); Assert.Empty(result.Warnings);
        Assert.Equal(1, host.Opens["source0"]); Assert.Equal(2, host.Opens["source34"]);
        Assert.InRange(host.Peak, 1, 2); Assert.Equal(0, host.Live); Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Fact]
    public async Task ASecondCredentialAuthenticatesBeforeReusingAnyGraph()
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), Primary(), QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("source"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r6-attachment.pdf")), QpdfInputOwnership.Transfer);
        QpdfPath output = QpdfPath.From("output");
        await using (IQpdfOutputTransaction prior = await storage.CreateOutputAsync(output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        Host host = new(storage);
        await Assert.ThrowsAsync<QpdfSecurityException>(async () => await Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("input"), output,
            [new(QpdfPath.From("source"), "good-"), new(QpdfPath.From("source"), "wrong-", QpdfPassword.FromText("incorrect"))], CancellationToken.None));
        Assert.Equal(2, host.Opens["source"]);
        Assert.True(storage.TryGetOutput(output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0, host.Live); Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EquivalentCredentialsReuseButDistinctRepresentationsRemainIndependent(bool rawSecond)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), Primary(), QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("source"), AttachmentCopyTests.SourceFixture(), QpdfInputOwnership.Transfer);
        Host host = new(storage);
        QpdfPassword second = rawSecond ? QpdfPassword.FromBytes("ignored"u8.ToArray()) : QpdfPassword.FromText("ignored");
        QpdfAttachmentEditResult result = await Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"),
            [new(QpdfPath.From("source"), "a-", QpdfPassword.FromText("ignored")),
             new(QpdfPath.From("source"), "b-", second),
             new(QpdfPath.From("source"), "c-", QpdfPassword.FromText("ignored"))], CancellationToken.None);
        Assert.Equal(6, result.AttachmentCount); Assert.Empty(result.Warnings);
        Assert.Equal(rawSecond ? 2 : 1, host.Opens["source"]);
        Assert.Equal(0, host.Live); Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandOriginsShareTheImportOwner(bool job)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), Primary(), QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("source"), AttachmentCopyTests.SourceFixture(), QpdfInputOwnership.Transfer);
        Host host = new(storage);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","copyAttachmentsFrom":[{"file":"source","prefix":"a-"},{"file":"source","prefix":"b-"}]}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--copy-attachments-from", "source", "--prefix=a-", "--",
                "--copy-attachments-from", "source", "--prefix=b-", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.Equal(1, host.Opens["source"]); Assert.Equal(0, host.Live); Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Fact]
    public void CacheMetadataAdmitsBeforeAllocationAndRefundsIndependently()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(17L);
        long beforeCache = governor.Snapshot().LiveMemoryBytes;
        using AttachmentImportCache cache = AttachmentImportCache.Create(governor, CancellationToken.None);
        AttachmentCopy copy = new("source", null);
        using (ParserMemoryScope preparation = ParserMemoryScope.Create(governor))
        {
            long before = governor.Snapshot().LiveMemoryBytes;
            Assert.Null(cache.CreateEntries(4097, preparation));
            Assert.Throws<QpdfLimitException>(() => cache.CreateEntries(4096, preparation));
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, preparation.ReservedBytes);
            EmbeddedFileEntry[] entries = Assert.IsType<EmbeddedFileEntry[]>(cache.CreateEntries(1, preparation));
            entries[0] = new("K", new PdfByteString("K"u8.ToArray(), false), new PdfReference(1, 0));
            cache.Remember(copy, entries, preparation);
            Assert.Equal(0L, preparation.ReservedBytes); Assert.Same(entries, cache.Find(copy));
        }
        cache.Dispose();
        Assert.Equal(beforeCache, governor.Snapshot().LiveMemoryBytes); Assert.Equal(17L, prior.ReservedBytes);
        Assert.Throws<ObjectDisposedException>(() => cache.Find(copy));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarningSourcesRepeatTheirDiagnosticsIndependentlyOfPresentation(bool suppress)
    {
        string damaged = Regex.Replace(Encoding.Latin1.GetString(AttachmentCopyTests.SourceFixture()), @"startxref\n\d+", "startxref\n0");
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), Primary(), QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("source"), Encoding.Latin1.GetBytes(damaged), QpdfInputOwnership.Transfer);
        Host host = new(storage);
        List<string> baseline = ["input"];
        if (suppress) { baseline.Add("--no-warn"); }
        baseline.AddRange(["--copy-attachments-from", "source", "--prefix=one-", "--", "single"]);
        QpdfExecutionResult once = await Qpdf.ExecuteAsync(host, new(baseline), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, once.Status); Assert.Equal(QpdfExitCode.Warning, once.ExitCode);
        List<string> argv = ["input"];
        if (suppress) { argv.Add("--no-warn"); }
        argv.AddRange(["--copy-attachments-from", "source", "--prefix=a-", "--", "--copy-attachments-from", "source", "--prefix=b-", "--", "output"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Equal(3, host.Opens["source"]);
        Assert.Equal(2 * once.Diagnostics.Count(diagnostic => diagnostic.Severity == QpdfDiagnosticSeverity.Warning),
            result.Diagnostics.Count(diagnostic => diagnostic.Severity == QpdfDiagnosticSeverity.Warning));
        if (suppress) { Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == QpdfDiagnosticSeverity.Warning); }
        else { Assert.True(result.Diagnostics.Count(diagnostic => diagnostic.Severity == QpdfDiagnosticSeverity.Warning) >= 2); }
        Assert.Equal(0, host.Live); Assert.Equal(0L, storage.OpenLeaseCount);
    }
}
