using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class GeneratedGeometryTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input");
    private static readonly QpdfPath Output = QpdfPath.From("output");

    internal static byte[] Fixture(bool appearance, string width, bool overflow)
    {
        string left = overflow && appearance ? "-" + width : "0";
        string form = appearance ? "/AcroForm << /Fields [5 0 R] /NeedAppearances true /DA (/Helv 12 Tf) /DR << /Font << /Helv 7 0 R >> >> >>" : "";
        string page = appearance ? "/Annots [5 0 R]" : "/Rotate 90" + (overflow ? " /UserUnit 2" : "");
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R " + form + " >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + (appearance ? "400" : width) + " 800] /Resources << >> /Contents 4 0 R " + page + " >>", null),
            (4, "<< /Length 4 >>", "q Q\n"u8.ToArray()),
            (5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Text) /V (Hello) /Rect [" + left + " 0 " + width + " 20] >>", null),
            (6, "null", null), (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null)], 1);
    }

    private static async Task Prepare(InMemoryQpdfHost host, string operation, string width, bool overflow)
    {
        byte[] subject = Fixture(operation == "appearance", width, overflow);
        host.AddInput(Input, operation == "appearance" ? subject : Fixture(false, "100", false), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("stamp"), subject, QpdfInputOwnership.Copy);
        await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
    }

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, string operation, bool job, QpdfLimits limits, CancellationToken token)
    {
        List<string> argv = ["input", "--stream-data=uncompress"];
        Dictionary<string, object> config = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["streamData"] = "uncompress" };
        if (operation == "appearance") { argv.Add("--generate-appearances"); config["generateAppearances"] = true; }
        else { argv.AddRange(["--" + operation, "stamp", "--"]); config[operation] = new { file = "stamp" }; }
        argv.Add("output");
        return job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(config), new QpdfExecutionOptions(limits), token)
            : Qpdf.ExecuteAsync(host, new(argv), new QpdfExecutionOptions(limits), token);
    }

    private static QpdfObject Get(QpdfDocument document, PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    }

    public static IEnumerable<object[]> FiniteCases()
    {
        foreach (string operation in new[] { "appearance", "overlay", "underlay" })
        foreach (bool job in new[] { false, true })
        foreach (string width in new[] { "200", "100000000000000000000.0", "9223372036854776000.0" })
        { yield return [operation, job, width]; }
    }

    [Theory]
    [MemberData(nameof(FiniteCases))]
    public async Task GeneratedCoordinatesRemainReadableAfterDocumentsAndHostClose(string operation, bool job, string width)
    {
        PdfArray escaped;
        using (InMemoryQpdfHost host = new())
        {
            await Prepare(host, operation, width, false);
            QpdfExecutionResult result = await Run(host, operation, job, QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.True(host.TryGetOutput(Output, out byte[] output));
            host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Copy);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
            {
                foreach (Match match in Regex.Matches(Encoding.ASCII.GetString(output), @"(?m)^(\d+) 0 obj$"))
                { _ = document.GetObject(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)); }
                PdfDictionary page = document.PageReferences[0].Page;
                if (operation == "appearance")
                {
                    PdfArray annotations = Assert.IsType<PdfArray>(Get(document, page, "Annots"));
                    PdfReference link = Assert.IsType<PdfReference>(annotations[0]);
                    PdfDictionary widget = Assert.IsType<PdfDictionary>(document.GetObject(link.ObjectNumber));
                    PdfDictionary appearance = Assert.IsType<PdfDictionary>(Get(document, widget, "AP"));
                    PdfStream stream = Assert.IsType<PdfStream>(Get(document, appearance, "N"));
                    escaped = Assert.IsType<PdfArray>(Get(document, stream.Dictionary, "BBox"));
                }
                else
                {
                    PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(document, page, "Resources"));
                    PdfDictionary forms = Assert.IsType<PdfDictionary>(Get(document, resources, "XObject"));
                    PdfStream stream = Assert.IsType<PdfStream>(Get(document, forms, "Fx1"));
                    escaped = Assert.IsType<PdfArray>(Get(document, stream.Dictionary, "Matrix"));
                }
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        QpdfObject coordinate = escaped[operation == "appearance" ? 2 : 5];
        if (width == "200") { Assert.Equal(200, Assert.IsType<PdfInteger>(coordinate).Value); }
        else
        {
            PdfReal real = Assert.IsType<PdfReal>(coordinate);
            Assert.Contains('.', real.Text); Assert.True(double.IsFinite(real.Value));
            double expected = double.Parse(width, CultureInfo.InvariantCulture);
            Assert.InRange(Math.Abs(real.Value / expected - 1.0), 0.0, 1e-14);
        }
    }

    [Theory]
    [InlineData("appearance", false)]
    [InlineData("appearance", true)]
    [InlineData("overlay", false)]
    [InlineData("overlay", true)]
    [InlineData("underlay", false)]
    [InlineData("underlay", true)]
    public async Task NonFiniteGeneratedCoordinatesPreservePriorOutput(string operation, bool job)
    {
        using InMemoryQpdfHost host = new();
        await Prepare(host, operation, "1" + new string('0', 308) + ".0", true);
        QpdfExecutionResult result = await Run(host, operation, job, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("non-finite geometry", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(Output, out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
    }

    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget, string phase, CancellationTokenSource cancellation) : IQpdfHost, IQpdfMemoryHost
    {
        internal int Closed;
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new Transaction(this, await storage.CreateOutputAsync(path, token), phase, cancellation);
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
            public ValueTask DisposeAsync() { host.Closed++; return inner.DisposeAsync(); }
        }
    }

    public static IEnumerable<object[]> PublicationCases()
    {
        foreach (string operation in new[] { "appearance", "overlay", "underlay" })
        foreach (string phase in new[] { "write", "commit", "cancel" })
        { yield return [operation, phase]; }
    }

    [Theory]
    [MemberData(nameof(PublicationCases))]
    public async Task GeneratedGeometryKeepsOutputTransactionalAndRefundsCallerAdmission(string operation, string phase)
    {
        using InMemoryQpdfHost storage = new(); await Prepare(storage, operation, "100000000000000000000.0", false);
        RecordingMemoryBudget root = new(16777216);
        using CancellationTokenSource cancellation = new();
        using (IMemoryScope caller = root.CreateScope())
        {
            Host host = new(storage, caller, phase, cancellation);
            QpdfExecutionResult result = await Run(host, operation, true, QpdfLimits.Default, cancellation.Token);
            Assert.Equal(phase == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(1, host.Closed); Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, storage.OpenLeaseCount);
            Assert.True(storage.TryGetOutput(Output, out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData("appearance", false)]
    [InlineData("appearance", true)]
    [InlineData("overlay", false)]
    [InlineData("overlay", true)]
    [InlineData("underlay", false)]
    [InlineData("underlay", true)]
    public async Task GeneratedGeometryRespectsLocalAndSharedRefusal(string operation, bool shared)
    {
        using InMemoryQpdfHost storage = new(); await Prepare(storage, operation, "100000000000000000000.0", false);
        RecordingMemoryBudget root = new(shared ? 16384 : 16777216);
        using CancellationTokenSource cancellation = new();
        using (IMemoryScope caller = root.CreateScope())
        {
            Host host = new(storage, caller, "none", cancellation);
            QpdfLimits limits = shared ? QpdfLimits.Default : QpdfLimits.Default with { MaxMemoryBytes = 16384 };
            QpdfExecutionResult result = await Run(host, operation, false, limits, CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
            Assert.True(storage.TryGetOutput(Output, out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
            Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, storage.OpenLeaseCount);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}
