using System.Reflection;
using System.Text.Json;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class StaleFieldMembershipTests
{
    private static byte[] Fixture(int generation, string order)
    {
        string live = "6 " + generation + " R";
        string stale = "6 " + (generation == 0 ? 1 : 0) + " R";
        string kids = order switch { "stale-first" => stale + " " + live, "stale-last" => live + " " + stale, _ => live };
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /CO [5 0 R] >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [" + live + "] >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (5, "<< /FT /Tx /T (field) /Kids [" + kids + "] >>", null),
            (6, "<< /Type /Annot /Subtype /Widget /Rect [10 10 30 30] /Parent 5 0 R /P 3 0 R >>", null),
        ], 1, new Dictionary<int, int> { [6] = generation });
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (int generation in new[] { 0, 17 })
        foreach (string order in new[] { "good", "stale-first", "stale-last" })
        foreach (string selection in new[] { "one", "duplicate", "omitted", "whole" })
        foreach (string route in new[] { "typed", "argv", "job" })
        {
            if (selection == "whole" && route == "typed") { continue; }
            yield return [generation, order, selection, route];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task InvalidGenerationDoesNotSuppressLiveFieldRegistration(int generation, string order, string selection, string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(generation, order), QpdfInputOwnership.Transfer);
        int[] pages = selection switch { "duplicate" => [1, 1], "omitted" => [2], _ => [1] };
        if (route == "typed")
        {
            await Qpdf.SelectPagesAsync(host, [new PdfPageSource(QpdfPath.From("input"), PdfPageSelectionOptions.FromPages(pages))],
                QpdfPath.From("output"), CancellationToken.None);
        }
        else
        {
            string range = string.Join(",", pages);
            string[] argv = selection == "whole" ? ["input", "output"] : ["input", "--pages", ".", range, "--", "output"];
            object job = selection == "whole" ? new { inputFile = "input", outputFile = "output" }
                : new { inputFile = "input", outputFile = "output", pages = new[] { new { file = ".", range } } };
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        QpdfObject Get(QpdfObject value, string name)
        {
            Assert.True(Assert.IsType<PdfDictionary>(Resolve(value)).TryGet(PdfName.FromAscii(name), out QpdfObject child));
            return Resolve(child);
        }
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Get(document.Engine.Trailer, "Root"));
        int expected = selection == "omitted" ? 0 : selection == "duplicate" ? 2 : 1;
        if (!catalog.TryGet(PdfName.FromAscii("AcroForm"), out QpdfObject form)) { Assert.Equal(0, expected); return; }
        PdfArray fields = Assert.IsType<PdfArray>(Get(form, "Fields"));
        PdfArray calculation = Assert.IsType<PdfArray>(Get(form, "CO"));
        Assert.Equal(expected, fields.Count); Assert.Equal(expected, calculation.Count);
        HashSet<int> registrations = [];
        for (int i = 0; i < fields.Count; i++)
        {
            registrations.Add(Assert.IsType<PdfReference>(fields[i]).ObjectNumber);
            Assert.IsType<PdfByteString>(Get(fields[i], "T"));
        }
        Assert.Equal(expected, registrations.Count);
        for (int i = 0; i < calculation.Count; i++)
        { Assert.Contains(Assert.IsType<PdfReference>(calculation[i]).ObjectNumber, registrations); }
        for (int i = 0; i < document.PageCount; i++)
        {
            PdfDictionary page = document.PageReferences[i].Page;
            if (!page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject raw)) { continue; }
            PdfArray annots = Assert.IsType<PdfArray>(Resolve(raw));
            for (int j = 0; j < annots.Count; j++)
            {
                PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(annots[j]));
                Assert.True(widget.TryGet(PdfName.FromAscii("Parent"), out QpdfObject parent));
                Assert.Contains(Assert.IsType<PdfReference>(parent).ObjectNumber, registrations);
            }
        }
    }

    private static readonly Func<IIndirectResolver, PdfDictionary, int, QpdfLimits, ResourceGovernor, ParserMemoryScope, CancellationToken, HashSet<int>> Walk =
        (typeof(AcroFormCopy).GetMethod("WalkFieldMembers", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing live field walk."))
        .CreateDelegate<Func<IIndirectResolver, PdfDictionary, int, QpdfLimits, ResourceGovernor, ParserMemoryScope, CancellationToken, HashSet<int>>>();

    private sealed class InterruptingResolver(Action interrupt) : IIndirectResolver
    {
        public int Calls { get; private set; }
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        {
            Calls++; interrupt(); value = PdfNull.Instance; return true;
        }
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("governor")]
    [InlineData("admission")]
    public void InterruptedChildResolutionRetiresTraversalStorage(string interruption)
    {
        using CancellationTokenSource caller = new(), governed = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governed.Token);
        governor.ReserveMemory(97L);
        InterruptingResolver resolver = new(() =>
        {
            if (interruption == "caller") { caller.Cancel(); }
            else if (interruption == "governor") { governed.Cancel(); }
            else { governor.ReserveMemory(QpdfLimits.Default.MaxMemoryBytes); }
        });
        PdfDictionary root = new(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("Kids")] = new PdfArray([new PdfReference(2, 0)]) });
        using (ParserMemoryScope traversal = ParserMemoryScope.Create(governor))
        {
            if (interruption == "admission")
            { Assert.Throws<QpdfLimitException>(() => Walk(resolver, root, 1, QpdfLimits.Default, governor, traversal, caller.Token)); }
            else
            { Assert.Throws<OperationCanceledException>(() => Walk(resolver, root, 1, QpdfLimits.Default, governor, traversal, caller.Token)); }
        }
        Assert.Equal(1, resolver.Calls); Assert.Single(root.Keys);
        Assert.Equal(97L, governor.Snapshot().LiveMemoryBytes);
        governor.ReleaseMemory(97L); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
