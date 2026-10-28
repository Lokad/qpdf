using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class ArgumentErrorMemoryTests
{
    private static readonly QpdfLimits Limits = QpdfLimits.Default with
    {
        MaxMemoryBytes = 65536L, MaxDiagnostics = 1L, MaxDiagnosticBytes = 16L,
    };

    [Theory]
    [InlineData("main")]
    [InlineData("equals")]
    [InlineData("known-prefix")]
    [InlineData("pages")]
    [InlineData("overlay")]
    [InlineData("copy")]
    [InlineData("add")]
    [InlineData("quality")]
    [InlineData("check")]
    [InlineData("show")]
    [InlineData("rotation")]
    [InlineData("label")]
    public async Task LargeArgumentsHaveBoundedErrorsBeforeHostAccess(string context)
    {
        // These three paths already admit configuration copies. Stay below
        // that allowance to exercise their errors rather than admission refusal.
        bool admittedContext = context is "label" or "check" or "show";
        string text = new('q', admittedContext ? 4096 : 1048576);
        string option = "--" + text;
        string[] argv = context switch
        {
            "main" => [option],
            "equals" => [option + "=not-part-of-name"],
            "known-prefix" => ["--check" + text],
            "pages" => ["in.pdf", "--pages", ".", option, "--", "out.pdf"],
            "overlay" => ["in.pdf", "--overlay", "overlay.pdf", option, "--", "out.pdf"],
            "copy" => ["in.pdf", "--copy-attachments-from", "source.pdf", option, "--", "out.pdf"],
            "add" => ["in.pdf", "--add-attachment", "attachment.txt", option, "--", "out.pdf"],
            "quality" => ["--jpeg-quality", text],
            "check" => ["in.pdf", "--check", text],
            "show" => ["in.pdf", "--show-npages", text],
            "rotation" => ["in.pdf", "--rotate", text, "out.pdf"],
            "label" => ["in.pdf", "--set-page-labels", text, "--", "out.pdf"],
            _ => throw new ArgumentException(context),
        };
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv),
            new QpdfExecutionOptions(admittedContext ? Limits with { MaxMemoryBytes = 1048576L } : Limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic failure = Assert.Single(result.Diagnostics);
        Assert.True(failure.Code == QpdfDiagnosticCode.InvalidArguments, failure.Message);
        Assert.InRange(failure.Message.Length, 1, 512);
        // These contexts already report a constant error without echoing text.
        if (context is not ("overlay" or "check" or "show"))
        { Assert.Contains("...", failure.Message, StringComparison.Ordinal); }
        Assert.Equal(0L, result.Counters.HostOperations);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0, host.OpenLeaseCount);
        Assert.False(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] _));
    }

    [Fact]
    public async Task GovernedValueCopiesStillRefuseBeforeFormatting()
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["--jpeg-quality=" + new string('q', 1048576)]),
            new QpdfExecutionOptions(Limits), CancellationToken.None);
        QpdfDiagnostic failure = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, failure.Code);
        Assert.InRange(failure.Message.Length, 1, 512);
        Assert.Equal(0L, result.Counters.HostOperations);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task UnknownNameDoesNotAllocateInputSizedTransientCopies()
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionOptions options = new(Limits);
        _ = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["--unknown"]), options,
            CancellationToken.None);
        QpdfCommandInvocation invocation = new(["--" + new string('q', 1048576)]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        ValueTask<QpdfExecutionResult> pending = Qpdf.ExecuteAsync(host, invocation, options, CancellationToken.None);
        // Parse rejection is synchronous and never opens storage. Measure only
        // this thread, excluding caller input and concurrent tests' allocations.
        Assert.True(pending.IsCompletedSuccessfully);
        QpdfExecutionResult result = await pending;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.InRange(allocated, 1L, 65536L);
    }

    [Theory]
    [InlineData(1024L, QpdfDiagnosticCode.InvalidArguments)]
    [InlineData(0L, QpdfDiagnosticCode.BudgetExceeded)]
    public async Task SharedAllowanceBoundsFailureWithoutLeakingPrivateStorage(long bytes, QpdfDiagnosticCode code)
    {
        RecordingMemoryBudget budget = new(bytes);
        using (IMemoryScope caller = budget.CreateScope())
        using (InMemoryQpdfHost host = new(Limits, null, caller))
        {
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
                new QpdfCommandInvocation(["--" + new string('q', 1048576)]),
                new QpdfExecutionOptions(Limits), CancellationToken.None);
            QpdfDiagnostic failure = Assert.Single(result.Diagnostics);
            Assert.Equal(code, failure.Code);
            Assert.InRange(failure.Message.Length, 1, 512);
            Assert.InRange(budget.PeakBytes, 0L, bytes);
            Assert.Equal(0L, result.Counters.HostOperations);
            Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.Equal(0, host.OpenLeaseCount);
        }
        Assert.Equal(0L, budget.ReservedBytes);
    }

    [Fact]
    public async Task MixedConfigurationRejectsLongNamesWithoutChangingCryptoPrecedence()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("settings.json"), "{}"u8.ToArray(), QpdfInputOwnership.Copy);
        string[] argv = ["--job-json-file=settings.json", "--" + new string('q', 1048576)];
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        QpdfDiagnostic failure = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, failure.Code);
        Assert.InRange(failure.Message.Length, 1, 512);
        Assert.Contains("(offset 1)", failure.Message, StringComparison.Ordinal);
        QpdfExecutionResult crypto = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation([.. argv, "--password=" + new string('s', 1048576)]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, Assert.Single(crypto.Diagnostics).Code);
        Assert.Equal(0L, crypto.Counters.HostOperations);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CancellationWinsBeforeLongOptionScanning()
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["--" + new string('q', 1048576)]), new CancellationToken(true));
        Assert.Equal(QpdfExecutionStatus.Canceled, result.Status);
        Assert.Equal(0L, result.Counters.HostOperations);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    public async Task PreviewBoundaryPreservesShortErrorsAndArgumentPosition(int length)
    {
        using InMemoryQpdfHost host = new();
        string name = new('q', length);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["in.pdf", "--" + name]), CancellationToken.None);
        string preview = length <= 128 ? name : name[..128] + "...";
        Assert.Equal($"Unsupported option '--{preview}' in the narrow selection profile. (offset 1).",
            Assert.Single(result.Diagnostics).Message);
    }

    [Fact]
    public async Task PreviewDoesNotSplitUnicodeSurrogatePairs()
    {
        using InMemoryQpdfHost host = new();
        string prefix = new('q', 127);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["--" + prefix + "\U0001F600suffix"]), CancellationToken.None);
        Assert.Equal($"Unsupported option '--{prefix}...' in the narrow selection profile. (offset 0).",
            Assert.Single(result.Diagnostics).Message);
    }
}
