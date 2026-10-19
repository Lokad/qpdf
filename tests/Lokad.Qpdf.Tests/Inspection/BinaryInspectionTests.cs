using System.IO.Compression;
using System.Text;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Inspection;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class BinaryInspectionTests
{
    private static readonly byte[] StreamBytes = [0, 1, 2, 127, 128, 255, 13, 10, 65];
    private static readonly byte[] AttachmentBytes = [66, 0, 255, 10];

    private static byte[] Fixture(bool compressed, bool emptyStream)
    {
        byte[] data = emptyStream ? [] : StreamBytes;
        if (compressed)
        {
            using MemoryStream encoded = new();
            using (ZLibStream zlib = new(encoded, CompressionLevel.Optimal, true)) { zlib.Write(data); }
            data = encoded.ToArray();
        }
        var objects = CheckTests.SinglePage("<< /Length 0 >>", []);
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /Audit 8 0 R /Names << /EmbeddedFiles << /Names [(payload) 6 0 R] >> >> >>", null);
        objects.Add((5, "<< /Length " + data.Length + (compressed ? " /Filter /FlateDecode" : string.Empty) + " >>", data));
        objects.Add((6, "<< /Type /Filespec /F (p.bin) /EF << /F 7 0 R >> >>", null));
        objects.Add((7, "<< /Type /EmbeddedFile /Length 4 >>", AttachmentBytes));
        // This reachable stream is never extracted. Its decoding proves
        // that checking ran rather than only the requested binary operation.
        using MemoryStream checkedStream = new();
        using (ZLibStream zlib = new(checkedStream, CompressionLevel.Optimal, true))
        {
            zlib.Write(StreamBytes);
            zlib.Write(AttachmentBytes);
        }
        byte[] checkedData = checkedStream.ToArray();
        objects.Add((8, "<< /Length " + checkedData.Length + " /Filter /FlateDecode >>", checkedData));
        return CheckTests.Assemble(objects, 1);
    }

    private static InMemoryQpdfHost Host(bool compressed, bool emptyStream)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("a.pdf"), Fixture(compressed, emptyStream), QpdfInputOwnership.Copy);
        return host;
    }

    private static ValueTask<QpdfExecutionResult> Execute(
        InMemoryQpdfHost host, string route, string properties, string[] flags, QpdfLimits limits)
    {
        QpdfExecutionOptions options = new(limits);
        if (route == "job")
        {
            return Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"a.pdf\"," + properties + "}", options, CancellationToken.None);
        }
        if (route == "mixed")
        {
            host.AddInput(QpdfPath.From("settings.json"), Encoding.UTF8.GetBytes("{" + properties + "}"), QpdfInputOwnership.Copy);
            return Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["a.pdf", "--job-json-file=settings.json"]), options, CancellationToken.None);
        }
        return Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["a.pdf", .. flags]), options, CancellationToken.None);
    }

    // Reflection lets the same compiled regressions exercise the preceding DLL
    // without turning a missing public getter into a loader/JIT failure.
    private static ReadOnlyMemory<byte> OutputBytes(QpdfExecutionResult result)
    {
        var property = typeof(QpdfExecutionResult).GetProperty("OutputBytes");
        Assert.NotNull(property);
        return Assert.IsType<ReadOnlyMemory<byte>>(property.GetValue(result));
    }

    private static void AssertNoOutputBytes(QpdfExecutionResult result)
    {
        var property = typeof(QpdfExecutionResult).GetProperty("OutputBytes");
        Assert.NotNull(property);
        Assert.Null(property.GetValue(result));
    }

    public static IEnumerable<object[]> PayloadCases()
    {
        foreach (string route in new[] { "job", "argv", "mixed" })
        {
            foreach (string mode in new[] { "raw", "filtered", "attachment", "check-attachment", "check-both", "empty" })
            {
                yield return [route, mode];
            }
        }
    }

    [Theory]
    [MemberData(nameof(PayloadCases))]
    public async Task FilelessInspectionReturnsExactCallerBytes(string route, string mode)
    {
        InMemoryQpdfHost host = Host(mode == "filtered", mode == "empty");
        (string properties, string[] flags) = Settings(mode);
        QpdfExecutionResult result = await Execute(host, route, properties, flags, QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        byte[] expected = mode switch
        {
            "attachment" or "check-attachment" => AttachmentBytes,
            "check-both" => [.. StreamBytes, .. AttachmentBytes],
            "empty" => [],
            _ => StreamBytes,
        };
        ReadOnlyMemory<byte> payload = OutputBytes(result);
        Assert.Equal(expected, payload.ToArray());
        Assert.Null(result.OutputText);
        Assert.Equal(expected.Length, result.Counters.TotalOutputBytes);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.False(host.TryGetOutput(QpdfPath.From("p.bin"), out _));
        if (mode.StartsWith("check-", StringComparison.Ordinal))
        {
            Assert.True(result.Counters.TotalDecodedBytes >= StreamBytes.Length + AttachmentBytes.Length);
        }
        host = new InMemoryQpdfHost();
        host.AddInput(QpdfPath.From("other.pdf"), "%PDF-invalid"u8.ToArray(), QpdfInputOwnership.Copy);
        GC.Collect();
        Assert.Equal(expected, payload.ToArray());
    }

    private static (string Properties, string[] Flags) Settings(string mode) => mode switch
    {
        "filtered" => ("\"showObject\":\"5\",\"filteredStreamData\":\"\"", ["--show-object=5", "--filtered-stream-data"]),
        "attachment" => ("\"showAttachment\":\"payload\"", ["--show-attachment=payload"]),
        "check-attachment" => ("\"check\":\"\",\"showAttachment\":\"payload\"", ["--check", "--show-attachment=payload"]),
        "check-both" => ("\"check\":\"\",\"showObject\":\"5\",\"rawStreamData\":\"\",\"showAttachment\":\"payload\"", ["--check", "--show-object=5", "--raw-stream-data", "--show-attachment=payload"]),
        _ => ("\"showObject\":\"5\",\"rawStreamData\":\"\"", ["--show-object=5", "--raw-stream-data"]),
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedBinaryFlagsPreserveActualPayload(bool filtered)
    {
        InMemoryQpdfHost host = Host(filtered, false);
        string flag = filtered ? "--filtered-stream-data" : "--raw-stream-data";
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["a.pdf", "--show-object=5", flag, flag]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(StreamBytes, OutputBytes(result).ToArray());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("job", false)]
    [InlineData("argv", false)]
    [InlineData("mixed", false)]
    [InlineData("job", true)]
    [InlineData("argv", true)]
    [InlineData("mixed", true)]
    public async Task StreamOutputConflictOccursAfterChecking(string route, bool filtered)
    {
        InMemoryQpdfHost host = Host(filtered, false);
        string key = filtered ? "filteredStreamData" : "rawStreamData";
        string flag = filtered ? "--filtered-stream-data" : "--raw-stream-data";
        QpdfExecutionResult result = await Execute(host, route, "\"check\":\"\",\"showObject\":\"5\",\"" + key + "\":\"\"",
            ["--check", "--show-object=5", flag], QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.True(result.Counters.TotalDecodedBytes >= StreamBytes.Length + AttachmentBytes.Length);
        Assert.Contains(result.Diagnostics, d => d.Severity == QpdfDiagnosticSeverity.Error);
        Assert.Equal(0L, result.Counters.TotalOutputBytes);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("mixed")]
    public async Task RawFlagOnNonStreamKeepsActualObjectInspection(string route)
    {
        InMemoryQpdfHost host = Host(false, false);
        QpdfExecutionResult result = await Execute(host, route, "\"check\":\"\",\"showObject\":\"3\",\"rawStreamData\":\"\"",
            ["--check", "--show-object=3", "--raw-stream-data"], QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(result.Counters.TotalDecodedBytes >= StreamBytes.Length + AttachmentBytes.Length);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing && d.Message.Contains("/Contents 4 0 R", StringComparison.Ordinal));
        Assert.Equal(0L, result.Counters.TotalOutputBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("job", "raw")]
    [InlineData("argv", "raw")]
    [InlineData("mixed", "raw")]
    [InlineData("job", "check-both")]
    [InlineData("argv", "check-both")]
    [InlineData("mixed", "check-both")]
    public async Task OutputCeilingBindsActualAggregateBytes(string route, string mode)
    {
        InMemoryQpdfHost host = Host(false, false);
        (string properties, string[] flags) = Settings(mode);
        QpdfExecutionResult control = await Execute(host, route, properties, flags, QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Completed, control.Status);
        Assert.Equal(mode == "raw" ? 9 : 13, OutputBytes(control).Length);
        long ceiling = mode == "raw" ? 8L : 12L;
        QpdfExecutionResult result = await Execute(host, route, properties, flags, QpdfLimits.Default with { MaxOutputBytes = ceiling });
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("mixed")]
    public async Task CheckerOutputPathRejectsBeforePdfAccess(string route)
    {
        InMemoryQpdfHost host = new();
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("prior.pdf"), CancellationToken.None))
        {
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        QpdfExecutionResult result = await Execute(host, route,
            "\"check\":\"\",\"showObject\":\"5\",\"rawStreamData\":\"\",\"outputFile\":\"prior.pdf\"",
            ["--check", "--show-object=5", "--raw-stream-data", "prior.pdf"], QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0L, result.Counters.TotalDecodedBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("prior.pdf"), out byte[] bytes));
        Assert.Equal("prior"u8.ToArray(), bytes);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("mixed")]
    public async Task FatalCheckingOfAnUnextractedStreamStopsAttachmentOutput(string route)
    {
        var objects = CheckTests.SinglePage("<< /Length 0 >>", []);
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(payload) 6 0 R] >> >> >>", null);
        objects.Add((6, "<< /Type /Filespec /F (p.bin) /EF << /F 7 0 R >> >>", null));
        objects.Add((7, "<< /Type /EmbeddedFile /Length 4 >>", AttachmentBytes));
        objects.Add((8, "<< /Length 7 /Filter /FlateDecode >>", "invalid"u8.ToArray()));
        objects[2] = (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 8 0 R >>", null);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("a.pdf"), CheckTests.Assemble(objects, 1), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Execute(host, route, "\"check\":\"\",\"showAttachment\":\"payload\"",
            ["--check", "--show-attachment=payload"], QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.CheckFailed);
        Assert.Equal(0L, result.Counters.TotalOutputBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
        AssertNoOutputBytes(result);
    }

    public static IEnumerable<object[]> DefaultCases()
    {
        foreach (string route in new[] { "job", "argv", "mixed" })
        {
            foreach (string mode in new[] { "raw-check", "filtered-count", "raw-attachment", "disabled", "disabled-count", "disabled-output", "unused-write", "unused-no-output" })
            {
                yield return [route, mode];
            }
        }
    }

    [Theory]
    [MemberData(nameof(DefaultCases))]
    public async Task StreamDefaultsPreserveNativeInspectionAndWriterChoice(string route, string mode)
    {
        InMemoryQpdfHost host = Host(false, false);
        (string Properties, string[] Flags) settings = mode switch
        {
            "raw-check" => ("\"check\":\"\",\"rawStreamData\":\"\"", ["--check", "--raw-stream-data"]),
            "filtered-count" => ("\"showNpages\":\"\",\"filteredStreamData\":\"\"", ["--show-npages", "--filtered-stream-data"]),
            "raw-attachment" => ("\"check\":\"\",\"showAttachment\":\"payload\",\"rawStreamData\":\"\"", ["--check", "--show-attachment=payload", "--raw-stream-data"]),
            "disabled" => ("\"showObject\":\"x\",\"rawStreamData\":\"\"", ["--show-object=x", "--raw-stream-data"]),
            "disabled-count" => ("\"showObject\":\"0\",\"rawStreamData\":\"\",\"showNpages\":\"\"", ["--show-object=0", "--raw-stream-data", "--show-npages"]),
            "disabled-output" => ("\"showObject\":\"0\",\"rawStreamData\":\"\",\"outputFile\":\"out.pdf\"", ["--show-object=0", "--raw-stream-data", "out.pdf"]),
            "unused-write" => ("\"rawStreamData\":\"\",\"outputFile\":\"out.pdf\"", ["--raw-stream-data", "out.pdf"]),
            _ => ("\"rawStreamData\":\"\"", ["--raw-stream-data"]),
        };
        QpdfExecutionResult result = await Execute(host, route, settings.Properties, settings.Flags, QpdfLimits.Default);
        bool failed = mode is "disabled-output" or "unused-no-output";
        Assert.Equal(failed ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(failed ? QpdfExitCode.Error : QpdfExitCode.Success, result.ExitCode);
        if (failed) { Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code); }
        if (mode == "raw-attachment") { Assert.Equal(AttachmentBytes, OutputBytes(result).ToArray()); }
        else { AssertNoOutputBytes(result); }
        if (mode is "filtered-count" or "disabled-count")
        {
            Assert.EndsWith(": 1", Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount).Message, StringComparison.Ordinal);
        }
        if (mode.StartsWith("disabled", StringComparison.Ordinal))
        {
            Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing);
        }
        if (mode is "raw-check" or "raw-attachment")
        {
            Assert.True(result.Counters.TotalDecodedBytes >= StreamBytes.Length + AttachmentBytes.Length);
        }
        if (mode == "unused-write")
        {
            Assert.True(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] rewritten));
            InMemoryQpdfHost readerHost = new();
            readerHost.AddInput(QpdfPath.From("rewritten.pdf"), rewritten, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(readerHost, QpdfPath.From("rewritten.pdf"), CancellationToken.None);
            Assert.Equal(1, document.PageCount);
        }
        else { Assert.False(host.TryGetOutput(QpdfPath.From("out.pdf"), out _)); }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("mixed")]
    public async Task AttachmentFailureDiscardsAnEarlierBinaryPayload(string route)
    {
        InMemoryQpdfHost host = Host(false, false);
        QpdfExecutionResult result = await Execute(host, route,
            "\"check\":\"\",\"showObject\":\"5\",\"rawStreamData\":\"\",\"showAttachment\":\"missing\"",
            ["--check", "--show-object=5", "--raw-stream-data", "--show-attachment=missing"], QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("requested attachment was not found", StringComparison.Ordinal));
        Assert.True(result.Counters.TotalDecodedBytes >= StreamBytes.Length + AttachmentBytes.Length);
        Assert.Equal(StreamBytes.Length, result.Counters.TotalOutputBytes);
        AssertNoOutputBytes(result);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.False(host.TryGetOutput(QpdfPath.From("p.bin"), out _));
    }

    [Theory]
    [InlineData("job", "raw")]
    [InlineData("argv", "raw")]
    [InlineData("mixed", "raw")]
    [InlineData("job", "check-attachment")]
    [InlineData("argv", "check-attachment")]
    [InlineData("mixed", "check-attachment")]
    public async Task CancellationAfterAnObservedReadReturnsNoBinaryPayload(string route, string mode)
    {
        var completed = await CompleteCancellation(route, mode);
        CompletedCancellationObservation.Retire(completed.Governor, completed.Result);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(ResourceGovernor Governor, QpdfExecutionResult Result)> CompleteCancellation(string route, string mode)
    {
        InMemoryQpdfHost source = Host(false, false);
        (string properties, string[] flags) = Settings(mode);
        source.AddInput(QpdfPath.From("settings.json"), Encoding.UTF8.GetBytes("{" + properties + "}"), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await source.CreateOutputAsync(QpdfPath.From("prior.pdf"), CancellationToken.None))
        {
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        WaitingHost host = new(source);
        using CancellationTokenSource cancellation = new();
        Task<QpdfExecutionResult> running = route == "job"
            ? Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"a.pdf\"," + properties + "}", cancellation.Token).AsTask()
            : Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(route == "mixed"
                ? ["a.pdf", "--job-json-file=settings.json"] : ["a.pdf", .. flags]), cancellation.Token).AsTask();
        try
        {
            Assert.Same(host.ReadStarted.Task, await Task.WhenAny(running, host.ReadStarted.Task).WaitAsync(TimeSpan.FromSeconds(10)));
            ResourceGovernor governor = CompletedCancellationObservation.FindGovernor(running);
            cancellation.Cancel();
            QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(QpdfExecutionStatus.Canceled, result.Status);
            Assert.True(result.Counters.HostOperations > 0);
            // The immutable snapshot can include independently owned names
            // awaiting natural collection; exact retirement is observed later.
            Assert.InRange(result.Counters.LiveMemoryBytes, ExecutionResultAdmission.MemoryBytes, result.Counters.PeakMemoryBytes);
            Assert.Equal(0L, result.Counters.OpenHandles);
            AssertNoOutputBytes(result);
            Assert.Null(result.OutputText);
            Assert.Equal(0, host.OutputCreates);
            Assert.Equal(0L, source.OpenLeaseCount);
            Assert.True(source.TryGetOutput(QpdfPath.From("prior.pdf"), out byte[] prior));
            Assert.Equal("prior"u8.ToArray(), prior);
            return (governor, result);
        }
        finally { cancellation.Cancel(); }
    }

    private sealed class WaitingHost(InMemoryQpdfHost source) : IQpdfHost
    {
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int OutputCreates { get; private set; }
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            IQpdfInput input = await source.OpenInputAsync(path, cancellationToken);
            return path.Value == "a.pdf" ? new WaitingInput(input, ReadStarted) : input;
        }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            OutputCreates++;
            return source.CreateOutputAsync(path, cancellationToken);
        }
    }

    private sealed class WaitingInput(IQpdfInput inner, TaskCompletionSource started) : IQpdfInput
    {
        public QpdfPath Path => inner.Path;
        public string Identity => inner.Identity;
        public long Length => inner.Length;
        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int length, CancellationToken cancellationToken)
        {
            ReadOnlyMemory<byte> data = await inner.ReadAsync(offset, length, cancellationToken);
            Assert.True(data.Length > 0);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return data;
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    public static IEnumerable<object[]> NormalizationCases()
    {
        foreach (string route in new[] { "job", "argv", "mixed" })
        {
            foreach (string mode in new[] { "default", "yes", "no", "raw", "qdf", "qdf-no", "checked" })
            {
                yield return [route, mode];
            }
        }
    }

    [Theory]
    [MemberData(nameof(NormalizationCases))]
    public async Task FilteredBytesHonorNormalizationAndQdfDefaults(string route, string mode)
    {
        InMemoryQpdfHost host = Host(false, false);
        byte[] input = ContentFixture();
        byte[] original = "q\rQ  \r\n  "u8.ToArray();
        host.AddInput(QpdfPath.From("a.pdf"), input, QpdfInputOwnership.Copy);
        string properties = "\"showObject\":\"5\",\"" + (mode == "raw" ? "rawStreamData" : "filteredStreamData") + "\":\"\"";
        List<string> flags = ["--show-object=5", mode == "raw" ? "--raw-stream-data" : "--filtered-stream-data"];
        if (mode is "yes" or "raw" or "checked") { properties += ",\"normalizeContent\":\"y\""; flags.Add("--normalize-content=y"); }
        if (mode is "no" or "qdf-no") { properties += ",\"normalizeContent\":\"n\""; flags.Add("--normalize-content=n"); }
        if (mode is "qdf" or "qdf-no") { properties += ",\"qdf\":\"\""; flags.Add("--qdf"); }
        if (mode == "checked")
        {
            properties += ",\"check\":\"\",\"showAttachment\":\"payload\"";
            flags.AddRange(["--check", "--show-attachment=payload"]);
        }
        QpdfExecutionResult result = await Execute(host, route, properties, [.. flags], QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        byte[] normalized = "q\nQ  \n  "u8.ToArray();
        byte[] expected = mode is "yes" or "qdf" or "checked" ? normalized : original;
        if (mode == "checked") { expected = [.. expected, .. AttachmentBytes]; }
        Assert.Equal(expected, OutputBytes(result).ToArray());
        Assert.Equal(expected.Length, result.Counters.TotalOutputBytes);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static byte[] ContentFixture()
    {
        byte[] input = Fixture(false, false);
        string pdf = Encoding.ASCII.GetString(input);
        int dataAt = pdf.IndexOf("stream\n", pdf.IndexOf("5 0 obj", StringComparison.Ordinal), StringComparison.Ordinal) + 7;
        "q\rQ  \r\n  "u8.CopyTo(input.AsSpan(dataAt, 9));
        return input;
    }

    [Theory]
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("mixed")]
    public async Task NormalizedOutputIsBoundedBeforePublication(string route)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("a.pdf"), ContentFixture(), QpdfInputOwnership.Copy);
        const string properties = "\"showObject\":\"5\",\"filteredStreamData\":\"\",\"normalizeContent\":\"y\"";
        string[] flags = ["--show-object=5", "--filtered-stream-data", "--normalize-content=y"];
        QpdfExecutionResult control = await Execute(host, route, properties, flags, QpdfLimits.Default);
        Assert.Equal("q\nQ  \n  "u8.ToArray(), OutputBytes(control).ToArray());
        QpdfExecutionResult result = await Execute(host, route, properties, flags, QpdfLimits.Default with { MaxOutputBytes = 7L });
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded
            && d.Message.Contains(nameof(QpdfLimits.MaxOutputBytes), StringComparison.Ordinal));
        AssertNoOutputBytes(result);
        Assert.Equal(0L, result.Counters.TotalOutputBytes);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("mixed")]
    public async Task EmptyLiteralNormalizationPreservesAllocationOwners(string route)
    {
        byte[] input = ContentFixture();
        string pdf = Encoding.ASCII.GetString(input);
        int dataAt = pdf.IndexOf("stream\n", pdf.IndexOf("5 0 obj", StringComparison.Ordinal), StringComparison.Ordinal) + 7;
        "() () () "u8.CopyTo(input.AsSpan(dataAt, 9));
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("a.pdf"), input, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Execute(host, route,
            "\"showObject\":\"5\",\"filteredStreamData\":\"\",\"normalizeContent\":\"y\"",
            ["--show-object=5", "--filtered-stream-data", "--normalize-content=y"], QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal("() () () "u8.ToArray(), OutputBytes(result).ToArray());
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("mixed")]
    public async Task UnreferencedMalformedStreamsDoNotStopNativeCheckerExtraction(string route)
    {
        byte[] input = Fixture(false, false);
        string pdf = Encoding.ASCII.GetString(input);
        int streamAt = pdf.IndexOf("stream\n", pdf.IndexOf("8 0 obj", StringComparison.Ordinal), StringComparison.Ordinal) + 7;
        input[streamAt] = 0;
        // Remove the catalog's edge without changing xref offsets.
        int referenceAt = pdf.IndexOf("/Audit 8 0 R", StringComparison.Ordinal);
        input.AsSpan(referenceAt, "/Audit 8 0 R".Length).Fill((byte)' ');
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("a.pdf"), input, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Execute(host, route, "\"check\":\"\",\"showAttachment\":\"payload\"",
            ["--check", "--show-attachment=payload"], QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(AttachmentBytes, OutputBytes(result).ToArray());
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.CheckFailed);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
