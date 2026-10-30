using System.Text.Json;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class FormFlagStorageTests
{
    private static PdfDictionary LargeForm(int count)
    {
        Dictionary<PdfName, QpdfObject> entries = new() { [PdfName.FromAscii("NeedAppearances")] = new PdfBoolean(true) };
        for (int i = 0; i < count; i++) { entries.Add(PdfName.FromAscii("Extra" + i), PdfNull.Instance); }
        return new(entries);
    }

    [Fact]
    public void LargeRebuildRefusesBeforeCopyingAndRollsBack()
    {
        PdfDictionary source = LargeForm(16384);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => Qpdf.ClearNeedAppearances(source, governor, CancellationToken.None));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0L, 131072L);
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
        Assert.Equal(16385, source.Count);
        Assert.True(source.TryGet(PdfName.FromAscii("NeedAppearances"), out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherCancellationTokenStopsRebuilding(bool governorToken)
    {
        PdfDictionary source = LargeForm(16384);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        Assert.Throws<OperationCanceledException>(() => Qpdf.ClearNeedAppearances(source, governor,
            governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(0L, governor.CounterValues.PeakMemoryBytes);
        Assert.Equal(16385, source.Count);
    }

    [Fact]
    public void SharedRefusalLeavesSourceAndCallerBudgetIntact()
    {
        PdfDictionary source = LargeForm(16384);
        RecordingMemoryBudget budget = new(4096);
        using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
        Assert.Throws<QpdfMemoryAdmissionException>(() => Qpdf.ClearNeedAppearances(source, governor, CancellationToken.None));
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
        Assert.Equal(16385, source.Count);
        owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes);
    }

    [Fact]
    public void MissingNullAndBorrowedValuesPreserveTheirIdentity()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        Assert.Null(Qpdf.ClearNeedAppearances(null, governor, CancellationToken.None));
        PdfDictionary absent = new(new Dictionary<PdfName, QpdfObject>());
        PdfDictionary nullFlag = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("NeedAppearances")] = PdfNull.Instance });
        Assert.Same(absent, Qpdf.ClearNeedAppearances(absent, governor, CancellationToken.None));
        Assert.Same(nullFlag, Qpdf.ClearNeedAppearances(nullFlag, governor, CancellationToken.None));
        PdfName key = PdfName.FromAscii("Extra");
        PdfByteString borrowed = new(new byte[2 * 1024 * 1024], false);
        PdfDictionary source = new(new Dictionary<PdfName, QpdfObject>
            { [PdfName.FromAscii("NeedAppearances")] = new PdfBoolean(false), [key] = borrowed });
        PdfDictionary result = Assert.IsType<PdfDictionary>(Qpdf.ClearNeedAppearances(source, governor, CancellationToken.None));
        Assert.Same(key, Assert.Single(result.Keys));
        Assert.True(result.TryGet(key, out QpdfObject value)); Assert.Same(borrowed, value);
        Assert.True(source.TryGet(PdfName.FromAscii("NeedAppearances"), out _));
        Assert.InRange(governor.CounterValues.LiveMemoryBytes, 1L, 4096L);
        governor.CompletePrivateStorage();
    }

    [Theory]
    [InlineData(false, "whole")]
    [InlineData(true, "whole")]
    [InlineData(false, "select")]
    [InlineData(true, "select")]
    [InlineData(false, "merge")]
    [InlineData(true, "merge")]
    [InlineData(false, "split")]
    [InlineData(true, "split")]
    public async Task GeneratedFormsKeepExtrasAcrossPublication(bool job, string path)
    {
        const int Count = 2048;
        string extras = string.Join(' ', Enumerable.Range(0, Count).Select(i => "/Extra" + i + " 1"));
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /NeedAppearances true /DR << /Font << /Helv 6 0 R >> >> " + extras + " >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R /Annots [5 0 R] >>", null),
            (4, "<< /Length 0 >>", []),
            (5, "<< /Type /Annot /Subtype /Widget /FT /Tx /Rect [10 10 180 30] /T (field) /V (hello) /DA (/Helv 12 Tf) /P 3 0 R >>", null),
            (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null)], 1);
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Copy);
        List<string> argv = ["input", "--generate-appearances"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["generateAppearances"] = true };
        if (path is "select" or "merge")
        {
            argv.AddRange(["--pages", ".", "1"]);
            List<object> pages = [new { file = ".", range = "1" }];
            if (path == "merge") { argv.AddRange([".", "1"]); pages.Add(new { file = ".", range = "1" }); }
            argv.Add("--"); settings["pages"] = pages;
        }
        if (path == "split") { argv.Add("--split-pages=1"); settings["splitPages"] = "1"; }
        argv.Add("output");
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.TryGetOutput(QpdfPath.From(path == "split" ? "output-1" : "output"), out byte[] bytes));
        host.Dispose();
        using InMemoryQpdfHost reader = new(); reader.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(reader, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary form = Assert.IsType<PdfDictionary>(AcroFormCopy.TryGetSourceForm(document.Engine));
        Assert.False(form.TryGet(PdfName.FromAscii("NeedAppearances"), out _));
        for (int i = 0; i < Count; i++)
        { Assert.True(form.TryGet(PdfName.FromAscii("Extra" + i), out QpdfObject value)); Assert.Equal(1L, Assert.IsType<PdfInteger>(value).Value); }
        Assert.True(form.TryGet(PdfName.FromAscii("Fields"), out QpdfObject fields));
        PdfReference reference = Assert.IsType<PdfReference>(Assert.IsType<PdfArray>(fields)[0]);
        Assert.True(Assert.IsType<PdfDictionary>(document.GetObject(reference.ObjectNumber)).TryGet(PdfName.FromAscii("AP"), out _));
    }
}
