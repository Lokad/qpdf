using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ExtensionVersionTests
{
    private static byte[] Fixture(bool indirect, int level)
    {
        string adbe = "<< /BaseVersion " + (indirect ? "8 0 R" : "/1.7") + " /ExtensionLevel "
            + (indirect ? "7 0 R" : level.ToString()) + " /Private (kept) >>";
        string extensions = "<< /ADBE " + (indirect ? "6 0 R" : adbe)
            + " /ACME << /BaseVersion /1.7 /ExtensionLevel 42 >> >>";
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /Extensions " + (indirect ? "5 0 R" : extensions) + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 20] /Resources << >> >>", null),
        ];
        if (indirect) { objects.AddRange([(4, "null", null), (5, extensions, null), (6, adbe, null), (7, level.ToString(), null), (8, "/1.7", null)]); }
        byte[] bytes = CheckTests.Assemble(objects, 1); bytes[7] = (byte)'7'; return bytes;
    }

    public static IEnumerable<object[]> WriterCases()
    {
        foreach (string route in new[] { "argv", "job" })
        foreach (bool indirect in new[] { false, true })
        foreach ((string mode, int expected) in new[] { ("default", 8), ("minimum-lower", 8), ("minimum-equal", 8),
            ("minimum-higher", 9), ("minimum-base-higher", 0), ("force-base", 0), ("force-lower", 3), ("force-equal", 8), ("force-higher", 9),
            ("qdf", 8), ("linearized", 8), ("selection", 8), ("merge", 9), ("split", 8) })
        { yield return [route, indirect, mode, expected]; }
    }

    private static async Task<QpdfExecutionResult> Run(InMemoryQpdfHost host, string route, string mode)
    {
        List<string> argv = ["input.pdf"];
        Dictionary<string, object> job = new() { ["inputFile"] = "input.pdf" };
        string? min = mode switch { "minimum-lower" => "1.7.3", "minimum-equal" => "1.7.8", "minimum-higher" => "1.7.9", "minimum-base-higher" => "2.0", _ => null };
        string? force = mode switch { "force-base" => "1.7", "force-lower" => "1.7.3", "force-equal" => "1.7.8", "force-higher" => "1.7.9", _ => null };
        if (min is not null) { argv.Add("--min-version=" + min); job["minVersion"] = min; }
        if (force is not null) { argv.Add("--force-version=" + force); job["forceVersion"] = force; }
        if (mode is "qdf" or "linearized")
        { string key = mode == "qdf" ? "qdf" : "linearize"; argv.Add("--" + key); job[key] = ""; }
        if (mode == "selection") { argv.AddRange(["--pages", ".", "1", "--"]); job["pages"] = new[] { new { file = ".", range = "1" } }; }
        if (mode == "merge")
        {
            host.AddInput(QpdfPath.From("second.pdf"), Fixture(false, 9), QpdfInputOwnership.Copy);
            argv.AddRange(["--pages", ".", "1", "second.pdf", "1", "--"]);
            job["pages"] = new[] { new { file = ".", range = "1" }, new { file = "second.pdf", range = "1" } };
        }
        if (mode == "split") { argv.Add("--split-pages=1"); job["splitPages"] = "1"; }
        string output = mode == "split" ? "out-%d.pdf" : "output.pdf";
        argv.Add(output); job["outputFile"] = output;
        return route == "argv" ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static QpdfObject Entry(QpdfDocument document, PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(document, value); }

    private static async Task AssertExtensions(InMemoryQpdfHost host, string path, int level, bool vendor, bool privateEntry)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From(path), out byte[] bytes));
        host.AddInput(QpdfPath.From("readback.pdf"), bytes, QpdfInputOwnership.Copy);
        PdfDictionary? escapedVendor = null;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback.pdf"), CancellationToken.None))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(Entry(document, document.Engine.Trailer, "Root"));
            PdfDictionary extensions = Assert.IsType<PdfDictionary>(Entry(document, catalog, "Extensions"));
            if (vendor)
            {
                escapedVendor = Assert.IsType<PdfDictionary>(Entry(document, extensions, "ACME"));
                Assert.Equal(42, Assert.IsType<PdfInteger>(Entry(document, escapedVendor, "ExtensionLevel")).Value);
            }
            else { Assert.False(extensions.TryGet(PdfName.FromAscii("ACME"), out _)); }
            if (level > 0)
            {
                PdfDictionary adbe = Assert.IsType<PdfDictionary>(Entry(document, extensions, "ADBE"));
                Assert.Equal(level, Assert.IsType<PdfInteger>(Entry(document, adbe, "ExtensionLevel")).Value);
                Assert.Equal("1.7", Assert.IsType<PdfName>(Entry(document, adbe, "BaseVersion")).DisplayText);
                Assert.Equal(privateEntry, adbe.TryGet(PdfName.FromAscii("Private"), out _));
            }
            else { Assert.False(extensions.TryGet(PdfName.FromAscii("ADBE"), out _)); }
        }
        if (escapedVendor is not null)
        { Assert.True(escapedVendor.TryGet(PdfName.FromAscii("BaseVersion"), out QpdfObject version)); Assert.Equal("1.7", Assert.IsType<PdfName>(version).DisplayText); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [MemberData(nameof(WriterCases))]
    public async Task WriterPreservesVendorsAndNegotiatesAdobeLevels(string route, bool indirect, string mode, int expected)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input.pdf"), Fixture(indirect, 8), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, route, mode);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await AssertExtensions(host, mode == "split" ? "out-1.pdf" : "output.pdf", expected, mode != "split", expected == 8 && mode != "split");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedAttachmentEditKeepsExtensionDeclarations(bool indirect)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input.pdf"), Fixture(indirect, 8), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("payload.bin"), [17], QpdfInputOwnership.Copy);
        await Qpdf.AddAttachmentsAsync(host, QpdfPath.From("input.pdf"), QpdfPath.From("output.pdf"),
            [new QpdfAttachmentSpec("payload.bin", "added", null, null, null, null, null, false)], CancellationToken.None);
        await AssertExtensions(host, "output.pdf", 8, true, true);
    }

    public static IEnumerable<object[]> JsonCases()
    {
        foreach (string route in new[] { "argv", "job" })
        foreach (bool update in new[] { false, true })
        foreach ((string mode, int expected) in new[] { ("default", 8), ("minimum-lower", 8), ("minimum-higher", 9), ("force-base", 0), ("force-lower", 3) })
        { yield return [route, update, mode, expected]; }
    }

    [Theory]
    [MemberData(nameof(JsonCases))]
    public async Task JsonImportAndUpdateApplyTheSameExtensionRules(string route, bool update, string mode, int expected)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input.pdf"), Fixture(true, 8), QpdfInputOwnership.Copy);
        QpdfExecutionResult exported = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input.pdf", "--json-output=2", "--", "source.json"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, exported.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("source.json"), out byte[] json));
        host.AddInput(QpdfPath.From("source.json"), json, QpdfInputOwnership.Copy);
        List<string> argv = update ? ["input.pdf", "--update-from-json=source.json"] : ["source.json", "--json-input"];
        Dictionary<string, string> job = new() { ["inputFile"] = update ? "input.pdf" : "source.json", ["outputFile"] = "output.pdf" };
        job[update ? "updateFromJson" : "jsonInput"] = update ? "source.json" : "";
        string? min = mode switch { "minimum-lower" => "1.7.3", "minimum-higher" => "1.7.9", _ => null };
        string? force = mode switch { "force-base" => "1.7", "force-lower" => "1.7.3", _ => null };
        if (min is not null) { argv.Add("--min-version=" + min); job["minVersion"] = min; }
        if (force is not null) { argv.Add("--force-version=" + force); job["forceVersion"] = force; }
        argv.AddRange(["--", "output.pdf"]);
        QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await AssertExtensions(host, "output.pdf", expected, true, expected == 8);
    }

    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget, string phase, CancellationTokenSource cancellation) : IQpdfHost, IQpdfMemoryHost
    {
        internal int Closes;
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            IQpdfOutputTransaction transaction = await storage.CreateOutputAsync(path, token);
            return new Transaction(this, transaction, phase, cancellation);
        }
        private sealed class Transaction(Host host, IQpdfOutputTransaction inner, string phase, CancellationTokenSource cancellation) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            {
                if (phase == "write") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); }
                if (phase == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                return inner.WriteAsync(bytes, token);
            }
            public ValueTask CommitAsync(CancellationToken token)
            {
                if (phase == "commit") { throw new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed); }
                return inner.CommitAsync(token);
            }
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() { host.Closes++; return inner.DisposeAsync(); }
        }
    }

    [Theory]
    [InlineData("write")]
    [InlineData("commit")]
    [InlineData("cancel")]
    public async Task ExtensionRebuildingKeepsPublicationTransactional(string phase)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input.pdf"), Fixture(true, 8), QpdfInputOwnership.Copy);
        byte[] prior = [19, 23];
        await using (IQpdfOutputTransaction seed = await storage.CreateOutputAsync(QpdfPath.From("output.pdf"), CancellationToken.None))
        { await seed.WriteAsync(prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        RecordingMemoryBudget root = new(1048576);
        using CancellationTokenSource cancellation = new();
        using (IMemoryScope caller = root.CreateScope())
        {
            Host host = new(storage, caller, phase, cancellation);
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
                new QpdfCommandInvocation(["input.pdf", "--min-version=1.7.9", "output.pdf"]), cancellation.Token);
            Assert.Equal(phase == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, storage.OpenLeaseCount);
            Assert.Equal(1, host.Closes);
            Assert.True(storage.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] bytes)); Assert.Equal(prior, bytes);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WideVendorDeclarationsRefuseUnderLocalAndSharedAdmission(bool shared)
    {
        string vendors = string.Concat(Enumerable.Range(0, 4096).Select(index => "/V" + index + " << /ExtensionLevel 1 >> "));
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Extensions << " + vendors + " >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 20] >>", null)], 1);
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input.pdf"), input, QpdfInputOwnership.Copy);
        RecordingMemoryBudget root = new(shared ? 524288 : 16777216);
        using CancellationTokenSource cancellation = new();
        using (IMemoryScope caller = root.CreateScope())
        {
            Host host = new(storage, caller, "none", cancellation);
            QpdfExecutionOptions options = new(shared ? QpdfLimits.Default : QpdfLimits.Default with { MaxMemoryBytes = 524288 });
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input.pdf", "--force-version=1.7.3", "output.pdf"]), options, CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
            Assert.False(storage.TryGetOutput(QpdfPath.From("output.pdf"), out _)); Assert.Equal(0L, storage.OpenLeaseCount);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData("null", "null")]
    [InlineData("0", "null")]
    [InlineData("-1", "null")]
    [InlineData("/eight", "null")]
    [InlineData("99 0 R", "null")]
    [InlineData("4 0 R", "4 0 R")]
    public async Task MalformedAdobeLevelsRespectBoundedResolutionAndCatalogDropPolicy(string level, string extra)
    {
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Extensions << /ADBE << /BaseVersion /1.7 /ExtensionLevel " + level + " >> /ACME << /BaseVersion /1.7 /ExtensionLevel 42 >> >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 20] >>", null),
            (4, extra, null)], 1);
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input.pdf"), input, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, "argv", "default");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        if (level == "99 0 R")
        {
            // Unresolvable opaque extras retain the catalog copier's drop policy.
            Assert.True(host.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] output));
            host.AddInput(QpdfPath.From("readback.pdf"), output, QpdfInputOwnership.Copy);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback.pdf"), CancellationToken.None))
            {
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(Entry(document, document.Engine.Trailer, "Root"));
                Assert.False(catalog.TryGet(PdfName.FromAscii("Extensions"), out _));
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        else { await AssertExtensions(host, "output.pdf", 0, true, false); }
    }
}
