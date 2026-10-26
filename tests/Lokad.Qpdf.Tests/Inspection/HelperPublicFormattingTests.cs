using System.Buffers;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Inspection;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class HelperPublicFormattingTests
{
    private static long Reject(byte[] prepared, string name, string[] flags)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), prepared, QpdfInputOwnership.Transfer);
        QpdfCommandInvocation invocation = new([name, .. flags]);
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxDiagnosticBytes = 128L });
        long before = GC.GetAllocatedBytesForCurrentThread();
        ValueTask<QpdfExecutionResult> operation = Qpdf.ExecuteAsync(host, invocation, options, CancellationToken.None);
        if (!operation.IsCompletedSuccessfully) { throw new InvalidOperationException("Expected the prepared synchronous host."); }
        QpdfExecutionResult result = operation.GetAwaiter().GetResult();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains(nameof(QpdfLimits.MaxDiagnosticBytes), StringComparison.Ordinal));
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
        return allocated;
    }

    [Fact]
    public void LinearizationQuotaRejectsBeforeFormattingItsLongInputName()
    {
        byte[] prepared = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "linearized-2page.pdf"));
        string shortName = "input.pdf";
        string longName = new('P', QpdfPath.MaxLength);
        string[] flags = ["--show-linearization"];
        string[] control = ["--show-xref"];
        {
            // Public invocation serialization can miss an 8 KiB BCL pool
            // buffer. Warm two slots so the paired measurement isolates report text.
            byte[] first = ArrayPool<byte>.Shared.Rent(8192);
            byte[] second = ArrayPool<byte>.Shared.Rent(8192);
            ArrayPool<byte>.Shared.Return(first);
            ArrayPool<byte>.Shared.Return(second);
            _ = Reject(prepared, shortName, flags);
            _ = Reject(prepared, longName, flags);
            _ = Reject(prepared, shortName, control);
            _ = Reject(prepared, longName, control);
            long shortAllocation = Reject(prepared, shortName, flags);
            long longAllocation = Reject(prepared, longName, flags);
            long shortControl = Reject(prepared, shortName, control);
            long longControl = Reject(prepared, longName, control);
            // Both public routes copy the logical path through JSON configuration.
            // Difference the same short/long path control to isolate report text.
            long reportGrowth = longAllocation - shortAllocation;
            long controlGrowth = longControl - shortControl;
            Assert.True(reportGrowth - controlGrowth <= 4096L,
                $"Report grew by {reportGrowth} bytes; the shared invocation control grew by {controlGrowth}.");
        }
    }

    private static byte[] Images(int nameBytes)
    {
        string escaped = string.Concat(Enumerable.Repeat("#23", nameBytes));
        List<(int Number, string Dict, byte[]? StreamData)> objects = CheckTests.SinglePage("<< /Length 0 >>", []);
        objects[2] = (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /XObject << /"
            + escaped + "A 6 0 R /" + escaped + "B 7 0 R >> >> >>", null);
        objects.Add((6, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>", [128]));
        objects.Add((7, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>", [128]));
        return CheckTests.Assemble(objects, 1);
    }

    [Fact]
    public void ImageQuotaRejectsWithoutPreparingEscapedDisplayNamesForSorting()
    {
        byte[] shortInput = Images(128);
        byte[] longInput = Images(4096);
        string[] flags = ["--show-pages", "--with-images"];
        _ = Reject(shortInput, "images.pdf", flags);
        _ = Reject(longInput, "images.pdf", flags);
        long shortAllocation = Reject(shortInput, "images.pdf", flags);
        long longAllocation = Reject(longInput, "images.pdf", flags);
        Assert.True(longAllocation - shortAllocation <= 24L * (4096L - 128L),
            $"Escaped-name phase grew by {longAllocation - shortAllocation} bytes.");
    }

    [Fact]
    public async Task ImageListingUsesRawOrderWithEscapedDisplayNamesAndDimensions()
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects = CheckTests.SinglePage("<< /Length 0 >>", []);
        objects[2] = (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /XObject << /A 6 0 R /#FF 7 0 R /#23 8 0 R >> >> >>", null);
        for (int number = 6; number <= 8; number++)
        {
            objects.Add((number, "<< /Type /XObject /Subtype /Image /Width 2 /Height 3 /Length 0 >>", []));
        }
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("images.pdf"), CheckTests.Assemble(objects, 1), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["images.pdf", "--show-pages", "--with-images"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(new[] { "    /#23: 8 0 R, 2 x 3", "    /A: 6 0 R, 2 x 3", "    /#FF: 7 0 R, 2 x 3" },
            result.Diagnostics.Where(diagnostic => diagnostic.Message.StartsWith("    /", StringComparison.Ordinal)).Select(diagnostic => diagnostic.Message));
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
