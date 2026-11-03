using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ResourcePruningRoleTests
{
    private static readonly byte[] FormProgram = "/Im Do"u8.ToArray();

    private static byte[] Fixture(bool rooted, string shape)
    {
        string aliases = rooted ? "/Original 5 0 R /OriginalOuter 10 0 R /OriginalResources 8 0 R" : "";
        string resources = "<< /XObject << /Target 5 0 R /Alias 5 0 R" + (shape == "flat" ? "" : " /Outer 10 0 R") + " >> >>";
        byte[] outer = Encoding.ASCII.GetBytes("/Target Do" + (shape == "cycle" ? " /Self Do" : ""));
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R " + aliases + " >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources " + resources + " /Contents 9 0 R " + aliases + " >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources " + resources + " /Contents 9 0 R " + aliases + " >>", null),
            (5, "<< /Subtype /Form /BBox [0 0 1 1] /Resources 8 0 R /Length " + FormProgram.Length + " >>", FormProgram),
            (6, "<< /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3 >>", new byte[] { 255, 0, 0 }),
            (7, "<< /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3 >>", new byte[] { 0, 255, 0 }),
            (8, "<< /XObject << /Im 6 0 R /Unused 7 0 R >> >>", null),
            (9, "<< /Length " + (shape == "flat" ? 10 : 9) + " >>", shape == "flat" ? "/Target Do"u8.ToArray() : "/Outer Do"u8.ToArray()),
            (10, "<< /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /Target 5 0 R /Alias 5 0 R /Self 10 0 R >> >> /Length " + outer.Length + " >>", outer)], 1);
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static QpdfObject Value(PdfDictionary node, string name)
    { Assert.True(node.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return value; }
    private static PdfDictionary Dictionary(QpdfDocument document, PdfDictionary node, string name)
        => Assert.IsType<PdfDictionary>(Resolve(document, Value(node, name)));
    private static PdfDictionary Objects(QpdfDocument document, PdfDictionary node)
        => Dictionary(document, Dictionary(document, node, "Resources"), "XObject");

    public static IEnumerable<object[]> Cases()
    {
        foreach (string shape in new[] { "flat", "nested", "cycle" })
        foreach (bool rooted in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [shape, rooted, "select", job]; }
        foreach (string mode in new[] { "duplicate", "split", "packed", "linear", "qdf", "preserve" })
        foreach (bool rooted in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return ["nested", rooted, mode, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PruningRedirectsResourceRolesAndKeepsOriginalFormAliases(string shape, bool rooted, string mode, bool job)
    {
        List<PdfStream> escaped = [];
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Fixture(rooted, shape), QpdfInputOwnership.Transfer);
            string range = mode == "duplicate" ? "1,1,2" : "1-2";
            List<string> argv = ["input", "--pages", ".", range, "--", "--remove-unreferenced-resources=yes", "--compress-streams=n", "--decode-level=none"];
            Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["pages"] = new[] { new { file = ".", range } }, ["removeUnreferencedResources"] = "yes", ["compressStreams"] = "n", ["decodeLevel"] = "none" };
            if (mode == "split") { argv.Add("--split-pages=1"); settings["splitPages"] = "1"; }
            if (mode == "packed") { argv.Add("--object-streams=generate"); settings["objectStreams"] = "generate"; }
            if (mode == "linear") { argv.Add("--linearize"); settings["linearize"] = true; }
            if (mode == "qdf") { argv.Add("--qdf"); settings["qdf"] = true; }
            if (mode == "preserve") { argv.Add("--preserve-unreferenced"); settings["preserveUnreferenced"] = true; }
            argv.Add("output");
            QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
            for (int group = 1; group <= (mode == "split" ? 2 : 1); group++)
            {
                Assert.True(host.TryGetOutput(QpdfPath.From(mode == "split" ? "output-" + group : "output"), out byte[] bytes));
                host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(document, Value(document.Engine.Trailer, "Root")));
                PdfReference? first = null;
                foreach (var location in document.PageReferences)
                {
                    PdfDictionary bindings = Objects(document, location.Page);
                    PdfDictionary parent = shape == "flat" ? bindings : Objects(document, Assert.IsType<PdfStream>(Resolve(document, Value(bindings, "Outer"))).Dictionary);
                    PdfReference target = Assert.IsType<PdfReference>(Value(parent, "Target"));
                    if (first is not null) { Assert.Equal(first, target); } first = target;
                    PdfStream transformed = Assert.IsType<PdfStream>(Resolve(document, target)); escaped.Add(transformed);
                    Assert.False(Objects(document, transformed.Dictionary).TryGet(PdfName.FromAscii("Unused"), out _));
                    if (rooted)
                    {
                        PdfDictionary roots = mode == "split" ? location.Page : catalog;
                        Assert.NotEqual(Value(roots, "Original"), target);
                        PdfStream original = Assert.IsType<PdfStream>(Resolve(document, Value(roots, "Original"))); escaped.Add(original);
                        Assert.True(Objects(document, original.Dictionary).TryGet(PdfName.FromAscii("Unused"), out _));
                        Assert.True(Dictionary(document, Dictionary(document, roots, "OriginalResources"), "XObject").TryGet(PdfName.FromAscii("Unused"), out _));
                        if (shape != "flat")
                        {
                            PdfStream outer = Assert.IsType<PdfStream>(Resolve(document, Value(roots, "OriginalOuter")));
                            Assert.Equal(Value(roots, "Original"), Value(Objects(document, outer.Dictionary), "Target"));
                        }
                    }
                }
            }
        }
        foreach (PdfStream stream in escaped) { Assert.Equal(FormProgram, stream.RawMemory.ToArray()); }
    }

    private sealed class FailureHost(InMemoryQpdfHost inner, IMemoryBudget budget, string phase, CancellationTokenSource cancellation)
        : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => inner.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new Transaction(await inner.CreateOutputAsync(path, token), phase, cancellation);
        private sealed class Transaction(IQpdfOutputTransaction inner, string phase, CancellationTokenSource cancellation) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            {
                if (phase == "write") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); }
                if (phase == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                return inner.WriteAsync(bytes, token);
            }
            public ValueTask CommitAsync(CancellationToken token) => phase == "commit"
                ? ValueTask.FromException(new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed)) : inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    public static IEnumerable<object[]> Failures()
    {
        foreach (string phase in new[] { "write", "commit", "cancel", "local", "shared" })
        foreach (bool job in new[] { false, true }) { yield return [phase, job]; }
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task FailedPruningPublicationKeepsThePriorTargetAndRefundsAdmission(string phase, bool job)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(true, "nested"), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        RecordingMemoryBudget root = new(phase == "shared" ? 4096 : 16777216);
        using CancellationTokenSource cancellation = new();
        using (IMemoryScope owner = root.CreateScope())
        {
            FailureHost execution = new(host, owner, phase, cancellation);
            QpdfLimits limits = phase == "local" ? QpdfLimits.Default with { MaxMemoryBytes = 4096 } : QpdfLimits.Default;
            QpdfExecutionOptions options = new(limits);
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(execution, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1-2"}],"removeUnreferencedResources":"yes"}""", options, cancellation.Token)
                : await Qpdf.ExecuteAsync(execution, new(["input", "--pages", ".", "1-2", "--", "--remove-unreferenced-resources=yes", "output"]), options, cancellation.Token);
            Assert.Equal(phase == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            if (phase is "local" or "shared") { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded); }
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
            Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}
