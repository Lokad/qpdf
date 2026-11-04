using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonStringIdentityTests
{
    private static readonly string[] Values = ["4B", "FEFF004B", "FEFF03A9", "FFFEA903", "EFBBBFCEA9", "FEFFD83DDE00",
        "FEFFD83D", "FEFFDE00", "FEFF0041FF", "EFBBBFCA", "FEFF", "EFBBBF", "FEFF00FE00FF", "FEFF00FF00FE",
        "FEFF00EF00BB00BF", "FEFFFEFF", "00FF", "A0", "FEFF20AC", "5C22", "EFBB", "FEFF0041D83DDE000042"];

    private static (string First, string Last) Keys(string kind) => kind switch
    {
        "ascii" => ("4B", "FEFF004B"), "euro" => ("A0", "FEFF20AC"),
        "endian" => ("FEFF03A9", "FFFEA903"), "utf8" => ("EFBBBFCEA9", "FEFF03A9"),
        _ => throw new ArgumentException("Unknown byte key fixture.", nameof(kind)),
    };

    private static byte[] Source(string kind, bool indirect)
    {
        (string first, string last) = Keys(kind);
        string pairs = "[<" + first + "> [3 0 R /Fit] <" + last + "> [4 0 R /Fit]]";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /Dests 8 0 R >> /Audit 10 0 R /Alias 11 0 R >>", null),
            (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [5 0 R 6 0 R] >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (5, "<< /Type /Annot /Subtype /Link /Rect [1 1 10 10] /A << /S /GoTo /D <" + first + "> >> >>", null),
            (6, "<< /Type /Annot /Subtype /Link /Rect [20 20 30 30] /A << /S /GoTo /D <" + last + "> >> >>", null),
            (7, "null", null), (8, "<< /Kids [9 0 R] >>", null),
            (9, "<< /Limits [<" + first + "> <" + last + ">] /Names " + (indirect ? "14 0 R" : pairs) + " >>", null),
            (10, "<< /Values [" + string.Join(' ', Values.Select(hex => "<" + hex + ">")) + "] /Shared 12 0 R >>", null),
            (11, "<< /String 12 0 R >>", null), (12, "<FEFF004B>", null), (13, "null", null), (14, pairs, null)], 1);
    }

    private static async Task<QpdfExecutionResult?> Export(IQpdfHost host, string route, QpdfLimits limits, CancellationToken token)
    {
        if (route == "typed")
        { await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("json"), new QpdfJsonOptions(2, ["qpdf"]), limits, token); return null; }
        return route == "argv"
            ? await Qpdf.ExecuteAsync(host, new(["input", "--json-output=2", "--", "json"]), new QpdfExecutionOptions(limits), token)
            : await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"json\",\"jsonOutput\":\"2\"}", new QpdfExecutionOptions(limits), token);
    }

    private static async Task Import(InMemoryQpdfHost host, string route, bool update)
    {
        if (route == "typed")
        {
            if (update) { await Qpdf.UpdatePdfFromJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("json"), QpdfPath.From("output"), CancellationToken.None); }
            else { await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("json"), QpdfPath.From("output"), CancellationToken.None); }
            return;
        }
        QpdfExecutionResult result = route == "argv"
            ? await Qpdf.ExecuteAsync(host, new(update ? ["input", "--update-from-json=json", "--", "output"] : ["json", "--json-input", "--", "output"]), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, update ? "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"updateFromJson\":\"json\"}"
                : "{\"inputFile\":\"json\",\"outputFile\":\"output\",\"jsonInput\":true}", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
    }

    public static IEnumerable<object[]> RoundTrips()
    {
        foreach (string kind in new[] { "ascii", "euro", "endian", "utf8" })
        foreach (bool indirect in new[] { false, true })
        foreach (bool update in new[] { false, true })
        foreach ((string export, string import) in new[] { ("typed", "argv"), ("argv", "job"), ("job", "typed") })
        { yield return [kind, indirect, update, export, import]; }
    }

    [Theory]
    [MemberData(nameof(RoundTrips))]
    public async Task ExportImportKeepsRawKeysLimitsActionsAndIndependentByteValues(string kind, bool indirect, bool update, string export, string import)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Source(kind, indirect), QpdfInputOwnership.Transfer);
        QpdfExecutionResult? exported = await Export(host, export, QpdfLimits.Default, CancellationToken.None);
        if (exported is not null) { Assert.Equal(QpdfExitCode.Success, exported.ExitCode); }
        Assert.True(host.TryGetOutput(QpdfPath.From("json"), out byte[] json)); host.AddInput(QpdfPath.From("json"), json, QpdfInputOwnership.Transfer);
        await Import(host, import, update); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        PdfByteString escaped;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
        {
            QpdfObject Resolve(QpdfObject raw) => raw is PdfReference reference ? document.GetObject(reference.ObjectNumber) : raw;
            QpdfObject Read(PdfDictionary owner, string key) { Assert.True(owner.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
            PdfDictionary Dict(QpdfObject raw) => Assert.IsType<PdfDictionary>(Resolve(raw));
            PdfArray Array(QpdfObject raw) => Assert.IsType<PdfArray>(Resolve(raw));
            void Bytes(string hex, QpdfObject raw) => Assert.Equal(Convert.FromHexString(hex), Assert.IsType<PdfByteString>(Resolve(raw)).Value);
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1)), audit = Dict(Read(catalog, "Audit"));
            PdfArray values = Array(Read(audit, "Values")); Assert.Equal(Values.Length, values.Count);
            for (int index = 0; index < values.Count; index++) { Bytes(Values[index], values[index]); }
            Assert.Equal(Read(audit, "Shared"), Read(Dict(Read(catalog, "Alias")), "String")); escaped = Assert.IsType<PdfByteString>(Resolve(Read(audit, "Shared")));
            PdfDictionary tree = Dict(Read(Dict(Read(catalog, "Names")), "Dests")); tree = Dict(Assert.Single(Array(Read(tree, "Kids")).Items));
            PdfArray pairs = Array(Read(tree, "Names")), limits = Array(Read(tree, "Limits")); (string first, string last) = Keys(kind);
            Bytes(first, pairs[0]); Bytes(last, pairs[2]); Bytes(first, limits[0]); Bytes(last, limits[1]);
            Assert.NotEqual(pairs[1], pairs[3]);
            PdfArray annots = Array(Read(document.PageReferences[0].Page, "Annots"));
            Bytes(first, Read(Dict(Read(Dict(annots[0]), "A")), "D")); Bytes(last, Read(Dict(Read(Dict(annots[1]), "A")), "D"));
        }
        await Qpdf.SelectPageAsync(host, QpdfPath.From("read"), QpdfPath.From("selected"), PdfPageSelectionOptions.FromPages([1, 2]), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("selected"), out byte[] selected)); host.AddInput(QpdfPath.From("selected-read"), selected, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("selected-read"), CancellationToken.None))
        {
            QpdfObject Resolve(QpdfObject raw) => raw is PdfReference reference ? document.GetObject(reference.ObjectNumber) : raw;
            PdfArray annots = Assert.IsType<PdfArray>(Resolve(Read(document.PageReferences[0].Page, "Annots")));
            QpdfObject Read(PdfDictionary owner, string key) { Assert.True(owner.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(value); }
            for (int index = 0; index < 2; index++)
            {
                PdfDictionary annotation = Assert.IsType<PdfDictionary>(Resolve(annots[index]));
                PdfArray destination = Assert.IsType<PdfArray>(Read(Assert.IsType<PdfDictionary>(Read(annotation, "A")), "D"));
                Assert.Equal(document.PageReferences[index].Source, destination[0]);
            }
        }
        Assert.Equal(0L, host.OpenLeaseCount); host.Dispose(); Assert.Equal(Convert.FromHexString("FEFF004B"), escaped.Value);
    }

    public static IEnumerable<object[]> Encodings()
    {
        string[] expected = ["u:K", "b:feff004b", "u:Ω", "b:fffea903", "b:efbbbfcea9", "u:😀", "b:feffd83d", "b:feffde00",
            "b:feff0041ff", "b:efbbbfca", "b:feff", "b:efbbbf", "u:þÿ", "u:ÿþ", "u:ï»¿", "u:\uFEFF", "b:00ff", "b:a0", "b:feff20ac", "u:\\\"", "b:efbb", "u:A😀B"];
        for (int index = 0; index < Values.Length; index++) { yield return [Values[index], expected[index]]; }
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public void TextEncodingIsUsedOnlyWhenItPreservesTheOriginalBytes(string hex, string expected)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using DisplayBuffer buffer = DisplayBuffer.CreateJson(governor); PdfJsonWriter.AppendPdfString(buffer, Convert.FromHexString(hex), 2);
        using GovernedText text = buffer.Finish(); Assert.Equal(expected, JsonSerializer.Deserialize<string>(text.Value));
    }

    [Theory]
    [InlineData("FEFF03A9")]
    [InlineData("FEFF004B")]
    [InlineData("EFBBBFCEA9")]
    public void BothTextValidationAndBinaryWritingObserveCancellation(string hex)
    {
        using CancellationTokenSource cancel = new(); ResourceGovernor governor = new(QpdfLimits.Default, cancel.Token);
        using (DisplayBuffer buffer = DisplayBuffer.CreateJson(governor))
        { cancel.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => PdfJsonWriter.AppendPdfString(buffer, Convert.FromHexString(hex), 2)); }
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    private static async Task Seed(InMemoryQpdfHost storage)
    {
        await using IQpdfOutputTransaction prior = await storage.CreateOutputAsync(QpdfPath.From("json"), CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
    }

    private static void AssertPrior(InMemoryQpdfHost storage)
    {
        Assert.Equal(0L, storage.OpenLeaseCount); Assert.True(storage.TryGetOutput(QpdfPath.From("json"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task EveryObservedMemoryRefusalPreservesTheTargetAndRefunds(string route)
    {
        async Task<int> Attempt(int failAt)
        {
            RecordingMemoryBudget budget = new(16L << 20) { RefuseOnGrowth = failAt }; using IMemoryScope owner = budget.CreateScope();
            using InMemoryQpdfHost storage = new(); storage.AddInput(QpdfPath.From("input"), Source("ascii", true), QpdfInputOwnership.Transfer); await Seed(storage);
            View host = new(storage, owner, "ordinary"); bool refused = false;
            try
            {
                QpdfExecutionResult? result = await Export(host, route, QpdfLimits.Default, CancellationToken.None);
                refused = result?.ExitCode == QpdfExitCode.Error;
                if (refused) { Assert.Contains(result?.Diagnostics ?? [], d => d.Code == QpdfDiagnosticCode.BudgetExceeded); }
            }
            catch (QpdfMemoryAdmissionException) { Assert.Equal("typed", route); refused = true; }
            Assert.Equal(0L, storage.OpenLeaseCount);
            if (failAt == 0) { Assert.False(refused); }
            else { Assert.True(refused); AssertPrior(storage); }
            int growths = budget.GrowthCalls; storage.Dispose(); owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations); return growths;
        }
        int growths = await Attempt(0); Assert.InRange(growths, 1, 64);
        for (int index = 1; index <= growths; index++) { await Attempt(index); }
    }

    [Theory]
    [InlineData("typed", "write")]
    [InlineData("typed", "commit")]
    [InlineData("argv", "write")]
    [InlineData("argv", "commit")]
    [InlineData("job", "write")]
    [InlineData("job", "commit")]
    public async Task OutputFailureKeepsThePriorTargetAndRefunds(string route, string fault)
    {
        RecordingMemoryBudget budget = new(16L << 20); using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost storage = new(); storage.AddInput(QpdfPath.From("input"), Source("ascii", true), QpdfInputOwnership.Transfer); await Seed(storage);
        View host = new(storage, owner, fault);
        if (route == "typed") { await Assert.ThrowsAsync<QpdfHostException>(() => Export(host, route, QpdfLimits.Default, CancellationToken.None)); }
        else { QpdfExecutionResult? result = await Export(host, route, QpdfLimits.Default, CancellationToken.None); Assert.NotNull(result); Assert.Equal(QpdfExitCode.Error, result.ExitCode); }
        AssertPrior(storage); storage.Dispose(); owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    [Theory]
    [InlineData("typed", false)]
    [InlineData("typed", true)]
    [InlineData("argv", false)]
    [InlineData("argv", true)]
    [InlineData("job", false)]
    [InlineData("job", true)]
    public async Task BinaryExpansionObeysTheExactPublicationLimit(string route, bool below)
    {
        byte[] source = Source("ascii", false); long length;
        using (InMemoryQpdfHost control = new())
        {
            control.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Copy);
            QpdfExecutionResult? result = await Export(control, route, QpdfLimits.Default, CancellationToken.None);
            if (result is not null) { Assert.Equal(QpdfExitCode.Success, result.ExitCode); }
            Assert.True(control.TryGetOutput(QpdfPath.From("json"), out byte[] output)); length = output.Length;
        }
        using InMemoryQpdfHost storage = new(); storage.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer); await Seed(storage);
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = length - (below ? 1 : 0) };
        if (below && route == "typed") { await Assert.ThrowsAsync<QpdfLimitException>(() => Export(storage, route, limits, CancellationToken.None)); }
        else
        {
            QpdfExecutionResult? result = await Export(storage, route, limits, CancellationToken.None);
            if (result is not null) { Assert.Equal(below ? QpdfExitCode.Error : QpdfExitCode.Success, result.ExitCode); }
        }
        if (below) { AssertPrior(storage); }
        else { Assert.True(storage.TryGetOutput(QpdfPath.From("json"), out byte[] output)); Assert.Equal(length, output.LongLength); Assert.Equal(0L, storage.OpenLeaseCount); }
    }

    private sealed class View(InMemoryQpdfHost storage, IMemoryBudget owner, string fault) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => owner;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new FaultOutput(await storage.CreateOutputAsync(path, token), fault);
    }

    private sealed class FaultOutput(IQpdfOutputTransaction inner, string fault) : IQpdfOutputTransaction
    {
        public QpdfPath Path => inner.Path;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token) => fault == "write"
            ? ValueTask.FromException(new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed)) : inner.WriteAsync(data, token);
        public ValueTask CommitAsync(CancellationToken token) => fault == "commit"
            ? ValueTask.FromException(new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed)) : inner.CommitAsync(token);
        public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
