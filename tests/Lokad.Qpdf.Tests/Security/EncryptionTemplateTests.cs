using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class EncryptionTemplateTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Template = QpdfPath.From("template.pdf"), Output = QpdfPath.From("output.pdf");

    public static IEnumerable<object[]> Cases()
    {
        foreach (int revision in new[] { 4, 6 })
        foreach (bool owner in new[] { false, true })
        foreach (bool split in new[] { false, true }) { yield return [revision, owner, split]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TypedTemplatesRetainBothPasswordsIdentifiersAndPermissions(int revision, bool owner, bool split)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r6-ascii.pdf");
        Add(host, Template, revision == 4 ? "security-r4-pdfdoc.pdf" : "security-r6-empty.pdf");
        QpdfPassword? user = revision == 4 ? QpdfPassword.FromText("café•") : null;
        QpdfPassword ownerPassword = revision == 4 ? QpdfPassword.FromBytes(Convert.FromHexString("6f776e6572e991")) : QpdfPassword.FromText("fixture-owner");
        QpdfOutputEncryption policy = QpdfOutputEncryption.CopyFrom(Template, owner ? ownerPassword : user);
        Assert.DoesNotContain("café", policy.ToString(), StringComparison.Ordinal); Assert.Equal("{}", JsonSerializer.Serialize(policy));
        byte[] identifier, content; Dictionary<string, byte[]> entries = new();
        await using (QpdfDocument primary = await QpdfDocument.OpenAsync(host, Input, QpdfPassword.FromText("reader"), QpdfLimits.Default, CancellationToken.None))
        { content = Assert.IsType<PdfStream>(Resolve(primary, Get(primary.PageReferences[0].Page, "Contents"))).Data; }
        await using (QpdfDocument template = await QpdfDocument.OpenAsync(host, Template, ownerPassword, QpdfLimits.Default, CancellationToken.None))
        {
            identifier = Identifier(template);
            PdfDictionary encryption = Assert.IsType<PdfDictionary>(Resolve(template, Get(template.Engine.Trailer, "Encrypt")));
            foreach (string key in revision == 4 ? new[] { "U", "O" } : new[] { "U", "O", "UE", "OE", "Perms" })
            { entries.Add(key, Assert.IsType<PdfByteString>(Get(encryption, key)).Value); }
        }
        if (split)
        {
            _ = await Qpdf.SelectPagesAsync(host, [new PdfPageSource(Input, PdfPageSelectionOptions.FromPages([1, 1]), QpdfPassword.FromText("reader"))],
                Output, new PdfPageMergeOptions { SplitPages = 1, OutputEncryption = policy }, CancellationToken.None);
        }
        else { _ = await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), QpdfPassword.FromText("reader"), policy, CancellationToken.None); }
        foreach (QpdfPath path in split ? new[] { QpdfPath.From("output-1.pdf"), QpdfPath.From("output-2.pdf") } : new[] { Output })
        {
            Assert.True(host.TryGetOutput(path, out byte[] bytes)); QpdfPath copy = QpdfPath.From("copy-" + path.Value); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
            foreach (QpdfPassword? password in new[] { user, ownerPassword })
            {
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, password, QpdfLimits.Default, CancellationToken.None);
                Assert.Equal(1, document.PageCount); Assert.Equal(revision, document.Encryption?.Revision);
                Assert.Equal(-3388, document.Encryption?.Permissions); Assert.Equal(revision == 4, document.Encryption?.EncryptMetadata);
                Assert.Equal(identifier, Identifier(document));
                Assert.Equal(content, Assert.IsType<PdfStream>(Resolve(document, Get(document.PageReferences[0].Page, "Contents"))).Data);
                PdfDictionary encryption = Assert.IsType<PdfDictionary>(Resolve(document, Get(document.Engine.Trailer, "Encrypt")));
                foreach (var entry in entries) { Assert.Equal(entry.Value, Assert.IsType<PdfByteString>(Get(encryption, entry.Key)).Value); }
            }
            QpdfCheckResult check = await Qpdf.CheckAsync(host, copy, new QpdfCheckOptions { Password = ownerPassword }, QpdfLimits.Default, CancellationToken.None);
            Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task PlaintextTemplatesRemoveInputProtection()
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r6-ascii.pdf"); Add(host, Template, "meta-1page.pdf");
        _ = await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), QpdfPassword.FromText("reader"),
            QpdfOutputEncryption.CopyFrom(Template, QpdfPassword.FromText("ignored")), CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("copy"), bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy"), CancellationToken.None);
        Assert.Null(document.Encryption); Assert.Equal(1, document.PageCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrMissingTemplateCredentialsPreservePriorOutput(bool supplied)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r6-empty.pdf"); Add(host, Template, "security-r4-pdfdoc.pdf");
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfSecurityException failure = await Assert.ThrowsAsync<QpdfSecurityException>(() => Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), null,
            QpdfOutputEncryption.CopyFrom(Template, supplied ? QpdfPassword.FromText("private-invalid") : null), CancellationToken.None).AsTask());
        Assert.Equal(supplied ? QpdfSecurityFailure.InvalidPassword : QpdfSecurityFailure.PasswordRequired, failure.Reason);
        Assert.DoesNotContain("private-invalid", failure.Message, StringComparison.Ordinal);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task EveryTemplateAdmissionFailureRefundsBeforeSplitPublication()
    {
        RecordingMemoryBudget baseline = new(4194304); await Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++) { await Run(new(4194304) { RefuseOnGrowth = growth }, true); }
        async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost storage = new(); Add(storage, Input, "security-r6-ascii.pdf"); Add(storage, Template, "security-r4-pdfdoc.pdf");
            using IMemoryScope caller = budget.CreateScope(); TemplateHost host = new(storage, caller, false);
            Task<PdfMultiPageSelectionResult> Write() => Qpdf.SelectPagesAsync(host,
                [new PdfPageSource(Input, PdfPageSelectionOptions.FromPages([1, 1]), QpdfPassword.FromText("reader"))], Output,
                new PdfPageMergeOptions { SplitPages = 1, OutputEncryption = QpdfOutputEncryption.CopyFrom(Template, QpdfPassword.FromText("café•")) }, CancellationToken.None).AsTask();
            if (refuses)
            {
                await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Write);
                Assert.False(storage.TryGetOutput(QpdfPath.From("output-1.pdf"), out _)); Assert.False(storage.TryGetOutput(QpdfPath.From("output-2.pdf"), out _));
            }
            else { _ = await Write(); Assert.Equal(1, host.TemplateCloses); }
            Assert.Equal(0, storage.OpenLeaseCount); caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }

    [Fact]
    public async Task ATemplateCloseFailureReleasesThePreparedKeyAndPreventsPublication()
    {
        using InMemoryQpdfHost storage = new(); Add(storage, Input, "meta-1page.pdf"); Add(storage, Template, "security-r6-empty.pdf");
        RecordingMemoryBudget budget = new(4194304); using IMemoryScope caller = budget.CreateScope(); TemplateHost host = new(storage, caller, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), null,
            QpdfOutputEncryption.CopyFrom(Template, null), CancellationToken.None).AsTask());
        Assert.Equal(1, host.TemplateCloses); Assert.Equal(0, storage.OpenLeaseCount); Assert.False(storage.TryGetOutput(Output, out _));
        caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
    }

    private sealed class TemplateHost(InMemoryQpdfHost storage, IMemoryBudget budget, bool failClose) : IQpdfHost, IQpdfMemoryHost
    {
        private bool _templateOpen;
        internal int TemplateCloses { get; private set; }
        public IMemoryBudget MemoryBudget => budget;
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            IQpdfInput input = await storage.OpenInputAsync(path, token);
            if (path != Template) { return input; }
            _templateOpen = true; return new TemplateLease(this, input);
        }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Assert.False(_templateOpen); return storage.CreateOutputAsync(path, token); }
        private sealed class TemplateLease(TemplateHost owner, IQpdfInput input) : IQpdfInput
        {
            public QpdfPath Path => input.Path;
            public string Identity => input.Identity;
            public long Length => input.Length;
            public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token) => input.ReadAsync(offset, destination, token);
            public async ValueTask DisposeAsync()
            {
                await input.DisposeAsync(); owner._templateOpen = false; owner.TemplateCloses++;
                if (owner.FailClose) { throw new InvalidOperationException("template-close"); }
            }
        }
        private bool FailClose => failClose;
    }
    private static byte[] Identifier(QpdfDocument document)
        => Assert.IsType<PdfByteString>(Assert.IsType<PdfArray>(Get(document.Engine.Trailer, "ID"))[0]).Value;
    private static QpdfObject Get(PdfDictionary dictionary, string name)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return value; }
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static void Add(InMemoryQpdfHost host, QpdfPath path, string file)
        => host.AddInput(path, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Copy);
}
