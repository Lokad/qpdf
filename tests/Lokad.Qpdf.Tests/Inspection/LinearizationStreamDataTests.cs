using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class LinearizationStreamDataTests
{
    public static IEnumerable<object[]> Representations()
    {
        foreach (string filter in new[] { "name", "array", "reference", "array-reference", "null-parms", "stale-parms", "raw" })
        foreach (string file in new[] { "absent", "null", "file", "filespec", "reference", "missing", "stale" })
        {
            if (filter == "raw" && file is not ("absent" or "file")) { continue; }
            yield return [filter, file];
        }
    }

    private static async Task<(byte[] Bytes, int Hint)> Fixture(string filter, string file)
    {
        // Raw tables provide room for equivalent encoded headers in the same
        // physical hint span, keeping all xrefs and layout claims unchanged.
        int[] pages = Enumerable.Range(8, 31).Prepend(3).ToArray();
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /FilterValue 5 0 R /ParamsValue 6 0 R /FileValue 7 0 R >>", null),
            (2, "<< /Type /Pages /Kids [" + string.Join(' ', pages.Select(x => x + " 0 R")) + "] /Count 32 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Contents 4 0 R >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (5, "/FlateDecode", null), (6, "<< /Predictor 1 >>", null), (7, "(external-file)", null),
        ];
        foreach (int page in pages.Skip(1))
        {
            objects.Add((page, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Contents 4 0 R /Audit (" + new string('x', page) + ") >>", null));
        }
        byte[] source = CheckTests.Assemble(objects, 1);
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("source"), source, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["source", "--linearize", "--compress-streams=n", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("linear"), bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("linear"), CancellationToken.None);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using LinearizationDataOwner hints = Assert.IsType<LinearizationDataOwner>(LinearizationCheck.TryReadOwnedData(
            document.Engine, document.PageCount, QpdfLimits.Default, governor, CancellationToken.None));
        LinearizationData data = hints.Data;
        PdfStream hint = Assert.IsType<PdfStream>(document.GetObject(data.HintObject));
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.Engine.Load(
            Assert.IsType<PdfReference>(document.Engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject root) ? root : null).ObjectNumber));
        int Number(string key) => Assert.IsType<PdfReference>(catalog.TryGet(PdfName.FromAscii(key), out QpdfObject value) ? value : null).ObjectNumber;
        byte[] payload = hint.Data;
        if (filter != "raw")
        {
            using MemoryStream compressed = new();
            using (ZLibStream codec = new(compressed, CompressionLevel.SmallestSize, true)) { codec.Write(payload); }
            payload = compressed.ToArray();
        }
        string filterValue = filter switch
        {
            "raw" => "", "array" => "/Filter[/FlateDecode]",
            "reference" => $"/Filter {Number("FilterValue")} 0 R",
            "array-reference" => $"/Filter[{Number("FilterValue")} 0 R]",
            "null-parms" => "/Filter[/FlateDecode]/DecodeParms[null]",
            "stale-parms" => $"/Filter/FlateDecode/DecodeParms {Number("ParamsValue")} 1 R",
            "parameters" => "/Filter/FlateDecode/DecodeParms<<>>",
            "reference-parameters" => $"/Filter/FlateDecode/DecodeParms {Number("ParamsValue")} 0 R",
            "unknown" => "/Filter/RunLengthDecode", "chain" => "/Filter[/FlateDecode/FlateDecode]",
            "stale-filter" => $"/Filter {Number("FilterValue")} 1 R",
            _ => "/Filter/FlateDecode",
        };
        string fileValue = file switch
        {
            "null" => "/F null", "file" => "/F(x)", "filespec" => "/F<</F(x)>>",
            "reference" => $"/F {Number("FileValue")} 0 R", "missing" => "/F 99 0 R",
            "stale" => $"/F {Number("FileValue")} 1 R", _ => "",
        };
        PdfInteger shared = Assert.IsType<PdfInteger>(hint.Dictionary.TryGet(PdfName.FromAscii("S"), out QpdfObject sharedValue) ? sharedValue : null);
        ReplaceHint(bytes, data.HintObject, checked((int)data.HintOffset), checked((int)data.HintLength), shared.Value,
            payload, filterValue + fileValue);
        hints.Dispose(); governor.CompletePrivateStorage();
        return (bytes, data.HintObject);
    }

    private static void ReplaceHint(byte[] bytes, int number, int at, int length, long? shared, byte[] payload, string fields)
    {
        string sharedEntry = shared.HasValue ? "/S " + shared.Value : "";
        byte[] start = Encoding.ASCII.GetBytes($"{number} 0 obj<</Length {payload.Length}{sharedEntry}{fields}>>stream\n");
        byte[] tail = "\nendstream\n"u8.ToArray();
        byte[] end = "endobj\n"u8.ToArray();
        Assert.True(start.Length + payload.Length + tail.Length + end.Length <= length, "The replacement must fit the original physical hint span.");
        bytes.AsSpan(at, length).Fill((byte)' ');
        start.CopyTo(bytes, at); payload.CopyTo(bytes, at + start.Length); tail.CopyTo(bytes, at + start.Length + payload.Length);
        end.CopyTo(bytes, at + length - end.Length);
    }

    [Theory]
    [MemberData(nameof(Representations))]
    public async Task HintCoverageAndReportsAgreeAcrossRepresentations(string filter, string file)
    {
        (byte[] bytes, int _) = await Fixture(filter, file);
        bool external = file is "file" or "filespec" or "reference";
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnostics = 4096 };
        using InMemoryQpdfHost host = new(limits, null); host.AddInput(QpdfPath.From("input"), bytes, QpdfInputOwnership.Transfer);
        QpdfCheckResult typed = await Qpdf.CheckAsync(host, QpdfPath.From("input"), new(), limits, CancellationToken.None);
        QpdfCheckOutcome linearization = Assert.Single(typed.Checks, x => x.Name == "linearization");
        Assert.True(linearization.Status == (external ? QpdfCheckStatus.Skipped : QpdfCheckStatus.Passed), linearization.Detail);
        Assert.True(typed.Passed);
        foreach (string operation in new[] { "check", "check-linearization", "show-linearization" })
        foreach (bool job in new[] { false, true })
        {
            Dictionary<string, object> values = new() { ["inputFile"] = "input" };
            values[operation == "check" ? "check" : operation == "check-linearization" ? "checkLinearization" : "showLinearization"] = operation == "check" ? "" : true;
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(values), new QpdfExecutionOptions(limits), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--" + operation]), new QpdfExecutionOptions(limits), CancellationToken.None);
            Assert.Equal(external ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
            Assert.DoesNotContain(result.Diagnostics, x => x.Code == QpdfDiagnosticCode.CheckFailed);
            if (external)
            {
                Assert.Contains(result.Diagnostics, x => x.Code == QpdfDiagnosticCode.CheckSkipped && x.Message.Contains("linearization", StringComparison.Ordinal));
                Assert.DoesNotContain(result.Diagnostics, x => x.Code == QpdfDiagnosticCode.LinearizationReport);
            }
            Assert.Null(result.OutputBytes);
            Assert.Equal(0L, host.OpenLeaseCount);
        }
    }

    [Theory]
    [InlineData("parameters")]
    [InlineData("reference-parameters")]
    [InlineData("unknown")]
    [InlineData("chain")]
    [InlineData("stale-filter")]
    public async Task UnsupportedOrMalformedHintPlansDoNotBecomeValid(string filter)
    {
        (byte[] bytes, int _) = await Fixture(filter, "absent");
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), bytes, QpdfInputOwnership.Transfer);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("input"), CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Failed, Assert.Single(result.Checks, x => x.Name == "linearization").Status);
        Assert.False(result.Passed); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("name", true)]
    [InlineData("array", true)]
    [InlineData("raw", false)]
    public async Task HintDecodingObservesTheFilterCeiling(string filter, bool skipped)
    {
        (byte[] bytes, int _) = await Fixture(filter, "absent");
        QpdfLimits limits = QpdfLimits.Default with { MaxStreamFilters = 0 };
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), bytes, QpdfInputOwnership.Transfer);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("input"), new(), limits, CancellationToken.None);
        Assert.Equal(skipped ? QpdfCheckStatus.Skipped : QpdfCheckStatus.Passed, Assert.Single(result.Checks, x => x.Name == "linearization").Status);
        // Full stream checking retains its own filter-limit failure policy.
        Assert.Equal(!skipped, result.Passed); Assert.Equal(0L, result.Counters.TotalDecodedBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedHintStorageIsRetryableAndRawAliasesOutliveTheReader(bool external)
    {
        (byte[] bytes, int hint) = await Fixture("reference", external ? "reference" : "absent");
        const long capacity = 16L * 1048576L;
        RecordingMemoryBudget budget = new(capacity); using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        host.AddInput(QpdfPath.From("input"), bytes, QpdfInputOwnership.Transfer);
        PdfStream escaped;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None))
        {
            escaped = Assert.IsType<PdfStream>(document.GetObject(hint));
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            Assert.True(owner.TryReserve(capacity - budget.ReservedBytes, out IMemoryReservation? reserved));
            using (IMemoryReservation pressure = reserved ?? throw new Exception())
            { Assert.Throws<QpdfMemoryAdmissionException>(() => LinearizationCheck.Inspect(document.Engine, document.PageCount, QpdfLimits.Default, governor, CancellationToken.None)); }
            LinearizationInspection result = LinearizationCheck.Inspect(document.Engine, document.PageCount, QpdfLimits.Default, governor, CancellationToken.None);
            Assert.True(result.Status == (external ? LinearizationStatus.Unavailable : LinearizationStatus.Valid), result.ErrorMessage);
            governor.CompletePrivateStorage();
        }
        host.Dispose(); Assert.NotEmpty(escaped.CopyData(owner, CancellationToken.None));
        owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task EitherExternalHintPartMakesLinearizationUnavailable(int part)
    {
        byte[] bytes = LinearizationDataOwnershipTests.Input("overflow-36-valid");
        using InMemoryQpdfHost source = new(); source.AddInput(QpdfPath.From("input"), bytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(source, QpdfPath.From("input"), CancellationToken.None))
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            using LinearizationDataOwner owner = Assert.IsType<LinearizationDataOwner>(LinearizationCheck.TryReadOwnedData(
                document.Engine, document.PageCount, QpdfLimits.Default, governor, CancellationToken.None));
            PdfDictionary lindict = Assert.IsType<PdfDictionary>(document.GetObject(owner.Data.LindictObject));
            PdfArray spans = Assert.IsType<PdfArray>(lindict.TryGet(PdfName.FromAscii("H"), out QpdfObject value) ? value : null);
            int at = checked((int)Assert.IsType<PdfInteger>(spans[2 * part]).Value);
            int length = checked((int)Assert.IsType<PdfInteger>(spans[2 * part + 1]).Value);
            int number = document.Engine.XRefEntries.Single(x => x.Value.Kind == XrefEntryKind.Uncompressed && x.Value.First == at).Key;
            PdfStream hint = Assert.IsType<PdfStream>(document.GetObject(number));
            long? shared = hint.Dictionary.TryGet(PdfName.FromAscii("S"), out QpdfObject offset) ? Assert.IsType<PdfInteger>(offset).Value : null;
            ReplaceHint(bytes, number, at, length, shared, hint.Data, "/F()");
            owner.Dispose(); governor.CompletePrivateStorage();
        }
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), bytes, QpdfInputOwnership.Transfer);
        QpdfCheckResult typed = await Qpdf.CheckAsync(host, QpdfPath.From("input"), CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Skipped, Assert.Single(typed.Checks, x => x.Name == "linearization").Status);
        Assert.True(typed.Passed);
        foreach (string operation in new[] { "--check", "--check-linearization", "--show-linearization" })
        {
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", operation]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
            Assert.Contains(result.Diagnostics, x => x.Code == QpdfDiagnosticCode.CheckSkipped);
            Assert.DoesNotContain(result.Diagnostics, x => x.Code == QpdfDiagnosticCode.LinearizationReport);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EitherCancellationRouteIsPreservedBeforeUnavailableHintInspection(bool governorToken)
    {
        (byte[] bytes, int hint) = await Fixture("reference", "reference");
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        using CancellationTokenSource stop = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? stop.Token : CancellationToken.None);
        stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => LinearizationCheck.Inspect(document.Engine, document.PageCount,
            QpdfLimits.Default, governor, governorToken ? CancellationToken.None : stop.Token));
        governor.CompletePrivateStorage();
        Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
        Assert.IsType<PdfStream>(document.GetObject(hint));
    }
}
