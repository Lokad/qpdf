using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Inspection;

[Collection(nameof(NameTableCollection))]
public sealed class FormNameLookupTests
{
    private delegate bool Predicate(PdfDictionary dictionary, ResourceGovernor governor, CancellationToken token);
    private delegate QpdfCheckOutcome FormCheck(QpdfDocument document, ResourceGovernor governor,
        DiagnosticBag diagnostics, CancellationToken token);

    // The preceding helper takes only the dictionary. This explicit private
    // context adapter lets the same assembly exercise its behavior with the
    // same prepared values, budgets and tokens; no case relies on a loader.
    private static Predicate Bind(string name)
    {
        MethodInfo method = typeof(DocumentCheck).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing form metadata helper.");
        if (method.GetParameters().Length == 1)
        {
            Func<PdfDictionary, bool> legacy = method.CreateDelegate<Func<PdfDictionary, bool>>();
            return (dictionary, governor, token) => legacy(dictionary);
        }
        return method.CreateDelegate<Predicate>();
    }

    private static readonly Predicate Field = Bind("IsFieldLike");
    private static readonly Predicate Widget = Bind("IsCheckWidget");
    private static readonly FormCheck RunForms = (typeof(DocumentCheck).GetMethod("CheckFormTree",
        BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing form checker."))
        .CreateDelegate<FormCheck>();

    private static PdfDictionary Dictionary(string key, QpdfObject value)
        => new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii(key)] = value });

    [Theory]
    [InlineData("exact", true)]
    [InlineData("case", false)]
    [InlineData("last-byte", false)]
    [InlineData("zero-suffix", false)]
    [InlineData("binary", false)]
    [InlineData("non-name", false)]
    [InlineData("null", false)]
    [InlineData("missing", false)]
    public void WidgetClassificationUsesRawBytesAndPreservesAbsentNullAndWrongTypes(string shape, bool expected)
    {
        QpdfObject subtype = shape switch
        {
            "exact" => PdfName.FromAscii("Widget"), "case" => PdfName.FromAscii("widget"),
            "last-byte" => PdfName.FromAscii("WidgeU"), "zero-suffix" => new PdfName([87, 105, 100, 103, 101, 116, 0]),
            "binary" => new PdfName(new byte[8192]), "non-name" => new PdfInteger("0", 0L), _ => PdfNull.Instance,
        };
        PdfDictionary dictionary = shape == "missing" ? new(new Dictionary<PdfName, QpdfObject>()) : Dictionary("Subtype", subtype);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(expected, Widget(dictionary, governor, CancellationToken.None));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        if (shape != "missing") { Assert.True(dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject found)); Assert.Same(subtype, found); }
    }

    [Theory]
    [InlineData("title", true)]
    [InlineData("kids", true)]
    [InlineData("type", true)]
    [InlineData("null-title-kids", true)]
    [InlineData("wrong-title-type", true)]
    [InlineData("null", false)]
    [InlineData("missing", false)]
    public void FieldClassificationPreservesNonNullTruthAndShortCircuitOrder(string shape, bool expected)
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        if (shape == "title") { entries.Add(PdfName.FromAscii("T"), new PdfByteString([], false)); }
        if (shape is "kids" or "null-title-kids") { entries.Add(PdfName.FromAscii("Kids"), new PdfArray([])); }
        if (shape == "type") { entries.Add(PdfName.FromAscii("FT"), PdfName.FromAscii("Tx")); }
        if (shape == "wrong-title-type") { entries.Add(PdfName.FromAscii("T"), new PdfInteger("0", 0L)); }
        if (shape is "null" or "null-title-kids") { entries.Add(PdfName.FromAscii("T"), PdfNull.Instance); }
        PdfDictionary dictionary = new(entries); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(expected, Field(dictionary, governor, CancellationToken.None)); Assert.Equal(entries.Count, dictionary.Count);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        if (shape is "title" or "wrong-title-type") { }
    }

    private static PdfDictionary Wide(bool found, bool widget)
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        for (int index = 0; index < 512; index++) { entries.Add(PdfName.FromAscii("other" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)), PdfNull.Instance); }
        if (found) { entries.Add(PdfName.FromAscii(widget ? "Subtype" : "T"), widget ? PdfName.FromAscii("Widget") : new PdfByteString(new byte[2 * 1024 * 1024], false)); }
        return new(entries);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EitherPreCanceledContextRejectsBeforeAnyVisit(bool widget, bool governorToken)
    {
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        Assert.ThrowsAny<OperationCanceledException>(() => (widget ? Widget : Field)(Wide(false, widget), governor,
            governorToken ? CancellationToken.None : cancellation.Token));
Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationAfterCompletedEntryVisitsPreservesPriorFundingAndBorrowing(bool widget, bool governorToken)
    {
        PdfDictionary dictionary = Wide(false, widget); Predicate predicate = widget ? Widget : Field;
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        CancellationToken token = governorToken ? CancellationToken.None : cancellation.Token;
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L); long before = governor.Snapshot().LiveMemoryBytes;
        using ManualResetEventSlim reached = new(), resume = new();
        Task<bool> worker = Task.Factory.StartNew(() =>
        {
            bool first = predicate(dictionary, governor, token); reached.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Form metadata observation did not resume."); }
            return first || predicate(dictionary, governor, token);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10)));             cancellation.Cancel(); resume.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes); Assert.Equal(512, dictionary.Count);
        }
        finally { cancellation.Cancel(); resume.Set(); try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { } }
    }

    [Theory]
    [InlineData("field")]
    [InlineData("widget-exact")]
    [InlineData("widget-binary")]
    public void WarmedBorrowedClassificationAllocatesNoNameKeyOrDisplayString(string shape)
    {
        Predicate predicate = shape == "field" ? Field : Widget;
        PdfDictionary dictionary = shape == "field" ? Dictionary("T", new PdfByteString(new byte[2 * 1024 * 1024], false))
            : Dictionary("Subtype", shape == "widget-exact" ? PdfName.FromAscii("Widget") : new PdfName(new byte[8192]));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 128L }, CancellationToken.None);
        for (int index = 0; index < 32; index++) { predicate(dictionary, governor, CancellationToken.None); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 128; index++) { predicate(dictionary, governor, CancellationToken.None); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(dictionary);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PdfDictionary Dictionary, WeakReference Governor, WeakReference Source) Completed(bool widget)
    {
        PdfDictionary dictionary = widget ? Dictionary("Subtype", PdfName.FromAscii("Widget")) : Dictionary("T", new PdfByteString([65], false));
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, source.Token);
        Assert.True((widget ? Widget : Field)(dictionary, governor, source.Token)); return (dictionary, new(governor), new(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClassifiedBorrowedValuesRetainNoCompletedGovernorOrTokenSource(bool widget)
    {
        var state = Completed(widget);
        for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(state.Governor.IsAlive); Assert.False(state.Source.IsAlive); Assert.Single(state.Dictionary.Keys); GC.KeepAlive(state);
    }

    [Theory]
    [InlineData("4 0 R", false)]
    [InlineData("4 7 R", false)]
    [InlineData("6 0 R", true)]
    [InlineData("null", true)]
    [InlineData("missing", true)]
    public async Task FormTraversalPreservesParentNumberMatchingAndWarningText(string parent, bool warning)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [5 0 R] >>", null),
            (4, "<< /T (F) /Kids [5 0 R] >>", null),
            (5, "<< /Subtype /Widget " + (parent == "missing" ? "" : "/Parent " + parent) + " >>", null),
        ];
        InMemoryQpdfHost host = new(); QpdfPath path = QpdfPath.From("forms.pdf"); host.AddInput(path, CheckTests.Assemble(objects, 1), QpdfInputOwnership.Transfer);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        QpdfDocument document = await QpdfDocument.OpenCore(host, path, QpdfLimits.Default, governor, diagnostics,
            QpdfRecoveryMode.Strict, CancellationToken.None);
        try
        {
            QpdfCheckOutcome result = RunForms(document, governor, diagnostics, CancellationToken.None); Assert.Equal(QpdfCheckStatus.Passed, result.Status);
            Assert.StartsWith("2 field nodes traversed, 1 widgets reached.", result.Detail, StringComparison.Ordinal);
            QpdfDiagnostic[] findings = diagnostics.Entries.Where(item => item.Code == QpdfDiagnosticCode.FormStructureWarning).ToArray();
            Assert.Equal(warning ? 1 : 0, findings.Length);
            if (warning) { Assert.Equal("Field node 5 has an invalid /Parent entry while traversing /AcroForm.", findings[0].Message); }
        }
        finally { await document.DisposeAsync(); Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, governor.Snapshot().OpenHandles); }
    }
}
