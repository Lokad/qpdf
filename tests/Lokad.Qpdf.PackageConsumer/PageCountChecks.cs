using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.TestSupport;

internal static class PageCountChecks
{
    private static void Require(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }

    private sealed class RangeHost(InMemoryQpdfHost inner, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        internal long ReadBytes { get; private set; }
        public IMemoryBudget MemoryBudget => budget;
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => new Input(this, await inner.OpenInputAsync(path, token));
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => throw new InvalidOperationException("Page counting must not create output.");

        private sealed class Input(RangeHost host, IQpdfInput inner) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            {
                int count = await inner.ReadAsync(offset, destination[..Math.Min(destination.Length, 37)], token);
                host.ReadBytes += count; return count;
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    internal static async Task RunAsync(byte[] source)
    {
        Require(source.Length > 524288, "Page-count input must contain a large unrelated object.");
        QpdfPath input = QpdfPath.From("count.pdf");
        using InMemoryQpdfHost storage = new(); storage.AddInput(input, source, QpdfInputOwnership.Copy);
        using RecordingMemoryBudget root = new(131072);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 131072 };
        for (int route = 0; route < 3; route++)
        {
            QpdfPageCountResult result;
            using (IMemoryScope owner = root.CreateScope())
            {
                RangeHost host = new(storage, owner);
                result = route switch
                {
                    0 => await Qpdf.CountPagesAsync(host, input, CancellationToken.None),
                    1 => await Qpdf.CountPagesAsync(host, input, limits, CancellationToken.None),
                    _ => await Qpdf.CountPagesAsync(host, input, null, QpdfRecoveryMode.BoundedRepair, limits, CancellationToken.None),
                };
                Require(result.PageCount == 2 && result.Warnings.Count == 0, "Typed count validates both pages.");
                Require(host.ReadBytes is > 0 and < 262144, "Count omits the large unrelated payload.");
                Require(result.Counters.OpenHandles == 0 && storage.OpenLeaseCount == 0, "Count closes its input before returning.");
                Require(result.Counters.PeakMemoryBytes <= limits.MaxMemoryBytes, "Count respects the smaller allowance.");
                Require(root.ReservedBytes > 0, "Returned count/counters retain caller admission.");
            }
            Require(root.ReservedBytes == 0 && result.PageCount == 2 && result.Counters.OpenHandles == 0,
                "Caller closure refunds count owners without changing returned values.");
        }
        Console.WriteLine("PASS typed bounded page count through short range reads and all public overloads");
    }

    internal static async Task RunRangeSelectionAsync(byte[] source)
    {
        QpdfPath input = QpdfPath.From("source.pdf"), output = QpdfPath.From("selected.pdf");
        using InMemoryQpdfHost storage = new();
        storage.AddInput(input, source, QpdfInputOwnership.Transfer);
        PdfPageSelectionOptions selection = PdfPageSelectionOptions.FromRange("r1-r2");
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(storage, input, output, selection, CancellationToken.None);
        Require(result.SourcePages.SequenceEqual(new[] { 2, 1 }) && result.Counters.OpenHandles == 0,
            "Relative endpoints select the pages in reverse order and close the input.");
        Require(storage.TryGetOutput(output, out byte[] selected), "The range selection must publish a PDF.");
        storage.AddInput(output, selected, QpdfInputOwnership.Transfer);
        QpdfJsonOptions options = new(2, ["pages", "qpdf"], "inline", null, [], false);
        QpdfJsonResult json = await Qpdf.GetDocumentJsonAsync(storage, output, options, CancellationToken.None);
        using JsonDocument report = JsonDocument.Parse(json.Json);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        JsonElement pages = report.RootElement.GetProperty("pages");
        Require(pages.GetArrayLength() == 2, "The selected PDF must contain both pages.");
        for (int index = 0; index < 2; index++)
        {
            string? reference = pages[index].GetProperty("contents")[0].GetString();
            byte[] data = table.GetProperty("obj:" + reference).GetProperty("stream").GetProperty("data").GetBytesFromBase64();
            string expected = index == 0 ? "(Second)" : "(First)";
            Require(Encoding.ASCII.GetString(data).Contains(expected, StringComparison.Ordinal),
                "The selected page content must follow the range order.");
        }
        Console.WriteLine("PASS typed relative page range and selected PDF order");
    }
}
