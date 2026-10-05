using System.Globalization;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class TolerantObjectLoadingTests
{
    internal static byte[] Pdf(string[] bodies, bool brokenXref)
    {
        List<string> objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R /Audit 5 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
        ];
        objects.AddRange(bodies);
        using MemoryStream bytes = new();
        void Write(string text) => bytes.Write(Encoding.Latin1.GetBytes(text));
        Write("%PDF-1.4\n");
        List<long> offsets = [0L];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(bytes.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        long xref = bytes.Position;
        Write($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (long offset in offsets.Skip(1))
        {
            Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        }

        Write($"trailer\n<< /Root 1 0 R /Size {offsets.Count} >>\nstartxref\n{(brokenXref ? 0L : xref)}\n%%EOF\n");
        return bytes.ToArray();
    }

    private static InMemoryQpdfHost Host(byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    [Theory]
    [InlineData(false, 1, "null")]
    [InlineData(false, 2, "[ 1 (@) 2 ]")]
    [InlineData(false, 0, "[ 1 (@) 2 ]")]
    [InlineData(true, 1, "null")]
    [InlineData(true, 2, "[ 1 null 2 ]")]
    [InlineData(true, 0, "[ 1 null 2 ]")]
    public async Task ActualLoadsRepairBothValidAndBrokenXrefs(bool broken, int budget, string expected)
    {
        InMemoryQpdfHost host = Host(Pdf(["[ 1 @ 2 ]"], broken));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--parser-max-errors=" + budget.ToString(), "--show-object=5"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Message.Trim() == expected);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.RepairedObject);
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(host,
            "{\"inputFile\":\"inputs/a.pdf\",\"showObject\":\"5\",\"global\":{\"parserMaxErrors\":" + budget.ToString() + "}}", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, job.ExitCode);
        Assert.Contains(job.Diagnostics, d => d.Message.Trim() == expected);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RewrittenRepairedValuesRemainReadableWithArgvJobParity(bool broken)
    {
        byte[] bytes = Pdf(["[ 1 @ 2 ]"], broken);
        InMemoryQpdfHost argv = Host(bytes);
        InMemoryQpdfHost job = Host(bytes);
        QpdfExecutionResult a = await Qpdf.ExecuteAsync(argv,
            new QpdfCommandInvocation(["inputs/a.pdf", "--parser-max-errors=2", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        QpdfExecutionResult b = await Qpdf.ExecuteJobAsync(job,
            """{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.pdf","pages":[{"file":".","range":"1"}],"global":{"parserMaxErrors":2}}""", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, a.ExitCode);
        Assert.Equal(QpdfExitCode.Warning, b.ExitCode);
        Assert.True(argv.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] aBytes));
        Assert.True(job.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] bBytes));
        Assert.Equal(aBytes, bBytes);
        argv.AddInput(QpdfPath.From("inputs/reopened.pdf"), aBytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument output = await QpdfDocument.OpenAsync(argv, QpdfPath.From("inputs/reopened.pdf"), CancellationToken.None))
        {
            Assert.Equal(1, output.PageCount);
            PdfDictionary page = Assert.IsType<PdfDictionary>(output.GetObject(3));
            Assert.True(page.TryGet(PdfName.FromAscii("Audit"), out QpdfObject audit));
            PdfReference reference = Assert.IsType<PdfReference>(audit);
            PdfArray value = Assert.IsType<PdfArray>(output.GetObject(reference.ObjectNumber));
            Assert.Equal(3, value.Count);
            if (broken)
            {
                Assert.IsType<PdfNull>(value[1]);
            }
            else
            {
                Assert.Equal("@"u8.ToArray(), Assert.IsType<PdfByteString>(value[1]).Value);
            }
        }

        Assert.Equal(0L, argv.OpenLeaseCount);
        Assert.Equal(0L, job.OpenLeaseCount);
    }

    public static IEnumerable<object?[]> NumericPublicCases()
    {
        (string Body, string Display, long? First)[] cases =
        [
            ("[ -9223372036854775808 2 ]", "[ -9223372036854775808 2 ]", long.MinValue),
            ("[ " + new string('0', 2048) + "1 2 ]", "[ 1 2 ]", 1L),
            ("[ 9223372036854775808 2 ]", "null", null),
            ("[ -9223372036854775809 2 ]", "null", null),
            ("[ 2147483648 0 R 2 ]", "null", null),
            ("[ 1 65536 R 2 ]", "[ null 2 ]", null),
        ];
        foreach ((string body, string display, long? first) in cases)
        {
            foreach (bool broken in new[] { false, true })
            {
                yield return [body, display, first, broken];
            }
        }
    }

    [Theory]
    [MemberData(nameof(NumericPublicCases))]
    public async Task NumericValuesSurvivePublicInspectionAndRewrite(string body, string display, long? first, bool broken)
    {
        InMemoryQpdfHost argv = Host(Pdf([body], broken));
        InMemoryQpdfHost job = Host(Pdf([body], broken));
        QpdfExitCode expectedExit = broken || first is null ? QpdfExitCode.Warning : QpdfExitCode.Success;
        QpdfExecutionResult inspection = await Qpdf.ExecuteAsync(argv,
            new QpdfCommandInvocation(["inputs/a.pdf", "--parser-max-errors=2", "--show-object=5"]), CancellationToken.None);
        QpdfExecutionResult jobInspection = await Qpdf.ExecuteJobAsync(job,
            """{"inputFile":"inputs/a.pdf","showObject":"5","global":{"parserMaxErrors":2}}""", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, inspection.Status);
        Assert.Equal(expectedExit, inspection.ExitCode);
        Assert.Equal(expectedExit, jobInspection.ExitCode);
        Assert.Contains(inspection.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing && d.Message.Trim() == display);
        Assert.Contains(jobInspection.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing && d.Message.Trim() == display);

        QpdfExecutionResult a = await Qpdf.ExecuteAsync(argv,
            new QpdfCommandInvocation(["inputs/a.pdf", "--parser-max-errors=2", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        QpdfExecutionResult b = await Qpdf.ExecuteJobAsync(job,
            """{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.pdf","pages":[{"file":".","range":"1"}],"global":{"parserMaxErrors":2}}""", CancellationToken.None);
        Assert.Equal(expectedExit, a.ExitCode);
        Assert.Equal(expectedExit, b.ExitCode);
        Assert.True(argv.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] aBytes));
        Assert.True(job.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] bBytes));
        Assert.Equal(aBytes, bBytes);
        argv.AddInput(QpdfPath.From("inputs/reopened.pdf"), aBytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument output = await QpdfDocument.OpenAsync(argv, QpdfPath.From("inputs/reopened.pdf"), CancellationToken.None))
        {
            PdfDictionary page = Assert.IsType<PdfDictionary>(output.GetObject(3));
            QpdfObject value = page.TryGet(PdfName.FromAscii("Audit"), out QpdfObject audit) ? audit : PdfNull.Instance;
            if (value is PdfReference reference)
            {
                value = output.GetObject(reference.ObjectNumber);
            }

            if (display == "null")
            {
                Assert.IsType<PdfNull>(value);
            }
            else
            {
                PdfArray array = Assert.IsType<PdfArray>(value);
                Assert.Equal(2, array.Count);
                if (first is long number)
                {
                    Assert.Equal(number, Assert.IsType<PdfInteger>(array[0]).Value);
                }
                else
                {
                    Assert.IsType<PdfNull>(array[0]);
                }
            }
        }

        Assert.Equal(0L, argv.OpenLeaseCount);
        Assert.Equal(0L, job.OpenLeaseCount);
    }

    public static IEnumerable<object[]> RealPublicCases()
    {
        (string Text, ulong Bits)[] cases =
        [
            ("0." + new string('0', 2048) + "1", 0UL),
            (new string('0', 2048) + "0.5", BitConverter.DoubleToUInt64Bits(0.5)),
            ("-" + new string('0', 2048) + ".0000", 1UL << 63),
            ("1.00000000000000011102230246251565404236316680908203125" + new string('0', 2048), BitConverter.DoubleToUInt64Bits(1.0)),
            ("1.00000000000000011102230246251565404236316680908203125" + new string('0', 2048) + "1", BitConverter.DoubleToUInt64Bits(1.0) + 1UL),
        ];
        foreach ((string text, ulong bits) in cases)
        {
            foreach (bool broken in new[] { false, true }) { yield return [text, bits, broken]; }
        }
    }

    public static IEnumerable<object[]> LexicalPublicCases()
    {
        foreach (bool broken in new[] { false, true })
        {
            foreach ((string body, string kept, int errors, bool warns) in LexicalParserTests.Cases(broken))
            {
                foreach (long budget in new long[] { 0L, 1L, 2L, 3L, 6L, 15L })
                {
                    string expected = budget != 0L && errors >= budget ? "null" : kept;
                    yield return [body, budget, expected, warns, broken];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(LexicalPublicCases))]
    public async Task LexicalValuesSurvivePublicInspectionAndRewrite(string body, long budget, string display, bool warns, bool broken)
    {
        await AssertPublicValueAndRewrite(body, budget, display, warns, broken, false, false);
    }

    public static IEnumerable<object[]> DictionaryPublicCases() => DictionaryRepairTests.OracleCases();

    [Theory]
    [MemberData(nameof(DictionaryPublicCases))]
    public async Task DictionaryGuardsSurvivePublicInspectionAndRewrite(string name, string body, bool broken, long budget, string display)
    {
        Assert.NotEmpty(name);
        await AssertPublicValueAndRewrite(body, budget, display, true, broken, name == "duplicate-reference-value" && display != "null", false);
    }

    public static IEnumerable<object[]> DamagedPublicCases() => DamagedSyntaxTests.OracleCases();

    [Theory]
    [MemberData(nameof(DamagedPublicCases))]
    public async Task DamagedValuesSurvivePublicInspectionAndRewrite(string name, string body, bool broken, long budget, string display, bool warns)
    {
        await AssertPublicValueAndRewrite(body, budget, display, warns, broken, false, DamagedSyntaxTests.HasMalformedName(name));
    }

    public static IEnumerable<object[]> StrictDamagedPublicCases() => DamagedSyntaxTests.StrictDamageCases();

    [Theory]
    [MemberData(nameof(StrictDamagedPublicCases))]
    public async Task StrictPublicPathsRejectDamagedTokensAndContainers(string name, string body)
    {
        Assert.NotEmpty(name);
        await StrictPublicPathsRejectValidXrefSyntaxDamage(body);
    }

    private static async Task AssertPublicValueAndRewrite(string body, long budget, string display, bool warns, bool broken, bool rejectsPageTreeReference, bool preservesMalformedNames)
    {
        InMemoryQpdfHost argv = Host(Pdf([body], broken));
        InMemoryQpdfHost job = Host(Pdf([body], broken));
        string budgetText = budget.ToString(CultureInfo.InvariantCulture);
        QpdfExitCode expectedExit = warns || broken ? QpdfExitCode.Warning : QpdfExitCode.Success;
        QpdfExecutionResult inspection = await Qpdf.ExecuteAsync(argv,
            new QpdfCommandInvocation(["inputs/a.pdf", "--parser-max-errors=" + budgetText, "--show-object=5"]), CancellationToken.None);
        QpdfExecutionResult jobInspection = await Qpdf.ExecuteJobAsync(job,
            "{\"inputFile\":\"inputs/a.pdf\",\"showObject\":\"5\",\"global\":{\"parserMaxErrors\":" + budgetText + "}}", CancellationToken.None);
        Assert.Equal(expectedExit, inspection.ExitCode);
        Assert.Equal(expectedExit, jobInspection.ExitCode);
        Assert.Contains(inspection.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing && d.Message.Trim() == display);
        Assert.Contains(jobInspection.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing && d.Message.Trim() == display);

        QpdfExecutionResult a = await Qpdf.ExecuteAsync(argv,
            new QpdfCommandInvocation(["inputs/a.pdf", "--parser-max-errors=" + budgetText, "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        QpdfExecutionResult b = await Qpdf.ExecuteJobAsync(job,
            "{\"inputFile\":\"inputs/a.pdf\",\"outputFile\":\"outputs/a.pdf\",\"pages\":[{\"file\":\".\",\"range\":\"1\"}],\"global\":{\"parserMaxErrors\":" + budgetText + "}}", CancellationToken.None);
        if (rejectsPageTreeReference)
        {
            // This fixture points /A at the source page tree. The supported
            // contained-copy policy rejects importing page-tree/catalog nodes.
            Assert.Equal(QpdfExecutionStatus.Failed, a.Status);
            Assert.Equal(QpdfExecutionStatus.Failed, b.Status);
            Assert.False(argv.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out _));
            Assert.False(job.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out _));
            Assert.Equal(0L, argv.OpenLeaseCount);
            Assert.Equal(0L, job.OpenLeaseCount);
            return;
        }
        Assert.Equal(expectedExit, a.ExitCode);
        Assert.Equal(expectedExit, b.ExitCode);
        Assert.True(argv.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] aBytes));
        Assert.True(job.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] bBytes));
        Assert.Equal(aBytes, bBytes);
        argv.AddInput(QpdfPath.From("inputs/reopened.pdf"), aBytes, QpdfInputOwnership.Copy);
        int? malformedObject = null;
        await using (QpdfDocument output = await QpdfDocument.OpenAsync(argv, QpdfPath.From("inputs/reopened.pdf"), CancellationToken.None))
        {
            PdfDictionary page = Assert.IsType<PdfDictionary>(output.GetObject(3));
            QpdfObject value = page.TryGet(PdfName.FromAscii("Audit"), out QpdfObject audit) ? audit : PdfNull.Instance;
            if (preservesMalformedNames)
            {
                // The oracle preserves stray hashes in its rewrite and warns
                // again on reopening. Strict routes keep rejecting that syntax.
                PdfReference reference = Assert.IsType<PdfReference>(value);
                malformedObject = reference.ObjectNumber;
                Assert.Throws<QpdfParseException>(() => output.GetObject(reference.ObjectNumber));
            }
            else
            {
                if (value is PdfReference reference) { value = output.GetObject(reference.ObjectNumber); }
                ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
                using (var rendered = CanonicalDisplay.Render(value, QpdfLimits.Default, governor)) { Assert.Equal(display, rendered.Value); }
            }
        }
        if (malformedObject is int objectNumber)
        {
            QpdfExecutionResult reopened = await Qpdf.ExecuteAsync(argv,
                new QpdfCommandInvocation(["inputs/reopened.pdf", "--show-object=" + objectNumber.ToString(CultureInfo.InvariantCulture)]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Warning, reopened.ExitCode);
            Assert.Contains(reopened.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing && d.Message.Trim() == display);
        }
        Assert.Equal(0L, argv.OpenLeaseCount);
        Assert.Equal(0L, job.OpenLeaseCount);
    }

    [Theory]
    [MemberData(nameof(RealPublicCases))]
    public async Task LongRealTextAndValueSurvivePublicRewrite(string text, ulong expectedBits, bool broken)
    {
        byte[] bytes = Pdf(["[ " + text + " 2 ]"], broken);
        InMemoryQpdfHost argv = Host(bytes);
        InMemoryQpdfHost job = Host(bytes);
        QpdfExitCode expectedExit = broken ? QpdfExitCode.Warning : QpdfExitCode.Success;
        QpdfExecutionResult inspection = await Qpdf.ExecuteAsync(argv,
            new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=5"]), CancellationToken.None);
        Assert.Equal(expectedExit, inspection.ExitCode);
        Assert.Contains(inspection.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing && d.Message.Trim() == "[ " + text + " 2 ]");

        QpdfExecutionResult a = await Qpdf.ExecuteAsync(argv,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        QpdfExecutionResult b = await Qpdf.ExecuteJobAsync(job,
            """{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.pdf","pages":[{"file":".","range":"1"}]}""", CancellationToken.None);
        Assert.Equal(expectedExit, a.ExitCode);
        Assert.Equal(expectedExit, b.ExitCode);
        Assert.True(argv.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] aBytes));
        Assert.True(job.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] bBytes));
        Assert.Equal(aBytes, bBytes);
        argv.AddInput(QpdfPath.From("inputs/reopened.pdf"), aBytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument output = await QpdfDocument.OpenAsync(argv, QpdfPath.From("inputs/reopened.pdf"), CancellationToken.None))
        {
            PdfDictionary page = Assert.IsType<PdfDictionary>(output.GetObject(3));
            Assert.True(page.TryGet(PdfName.FromAscii("Audit"), out QpdfObject audit));
            PdfReference reference = Assert.IsType<PdfReference>(audit);
            PdfReal value = Assert.IsType<PdfReal>(Assert.IsType<PdfArray>(output.GetObject(reference.ObjectNumber))[0]);
            Assert.Equal(text, value.Text);
            Assert.Equal(expectedBits, BitConverter.DoubleToUInt64Bits(value.Value));
        }
        Assert.Equal(0L, argv.OpenLeaseCount);
        Assert.Equal(0L, job.OpenLeaseCount);
    }

    [Fact]
    public async Task ManyDamagedObjectsDoNotShareAnErrorBudget()
    {
        string[] bodies = Enumerable.Range(5, 20).Select(n => n == 24 ? $"[ @ {n} ]" : $"[ @ {n} {n + 1} 0 R ]").ToArray();
        InMemoryQpdfHost host = Host(Pdf(bodies, false));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--parser-max-errors=2", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] rewritten));
        host.AddInput(QpdfPath.From("inputs/reopened.pdf"), rewritten, QpdfInputOwnership.Copy);
        await using (QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reopened.pdf"), CancellationToken.None))
        {
            PdfDictionary page = Assert.IsType<PdfDictionary>(output.GetObject(3));
            Assert.True(page.TryGet(PdfName.FromAscii("Audit"), out QpdfObject audit));
            PdfReference reference = Assert.IsType<PdfReference>(audit);
            for (int i = 5; i < 25; i++)
            {
                PdfArray array = Assert.IsType<PdfArray>(output.GetObject(reference.ObjectNumber));
                Assert.Equal("@"u8.ToArray(), Assert.IsType<PdfByteString>(array[0]).Value);
                Assert.Equal(i, Assert.IsType<PdfInteger>(array[1]).Value);
                if (i < 24)
                {
                    reference = Assert.IsType<PdfReference>(array[2]);
                }
            }
        }

        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task NoDefaultLimitsLiftsOnlyUnspecifiedErrorThreshold()
    {
        InMemoryQpdfHost host = Host(Pdf(["[ @ @ @ @ ]"], false));
        QpdfExecutionResult unlimited = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--no-default-limits", "--show-object=5"]), CancellationToken.None);
        QpdfExecutionResult finite = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--no-default-limits", "--parser-max-errors=2", "--show-object=5"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, unlimited.ExitCode);
        Assert.Equal(QpdfExitCode.Warning, finite.ExitCode);
        Assert.Contains(unlimited.Diagnostics, d => d.Message.Trim() == "[ (@) (@) (@) (@) ]");
        Assert.Contains(finite.Diagnostics, d => d.Message.Trim() == "null");
    }

    [Theory]
    [InlineData("[ 1 @ 2 ]")]
    [InlineData("[ 1{2 3}4 ]")]
    [InlineData("[ true{false}null ]")]
    [InlineData("[ /A{2}/B ]")]
    [InlineData("[ /A#00B 2 ]")]
    [InlineData("/A#00B")]
    [InlineData("<< /A#00B 1 /C 2 >>")]
    [InlineData("<< 1 2 @ /A 3 >>")]
    [InlineData("<< 1 2 /A [ @ ] @ /B 4 >>")]
    [InlineData("<< /QPDFFake1 9 1 2 @ /A 3 >>")]
    public async Task StrictPublicPathsRejectValidXrefSyntaxDamage(string body)
    {
        InMemoryQpdfHost host = Host(Pdf([body], false));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--suppress-recovery", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.pdf","suppressRecovery":true,"pages":[{"file":".","range":"1"}]}""", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, job.Status);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out _));
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None))
        {
            Assert.Throws<QpdfParseException>(() => document.GetObject(5));
        }

        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task HostCeilingPreventsInvocationFromRaisingItsErrorBudget()
    {
        InMemoryQpdfHost host = Host(Pdf(["[ 1 @ 2 ]"], false));
        QpdfLimits limits = QpdfLimits.Default with { MaxParserErrors = 1L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--parser-max-errors=2", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            new QpdfExecutionOptions(limits), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(result.Diagnostics).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(15, 0)]
    [InlineData(15, 16)]
    public async Task FiniteExplicitHostErrorCeilingRejectsUnlimitedOrLargerInvocation(int hostErrors, int requested)
    {
        InMemoryQpdfHost host = Host(Pdf(["[ 1 @ 2 ]"], false));
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxParserErrors = hostErrors });
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--parser-max-errors=" + requested.ToString(), "--show-object=5"]), options, CancellationToken.None);
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(host,
            "{\"inputFile\":\"inputs/a.pdf\",\"showObject\":\"5\",\"global\":{\"parserMaxErrors\":" + requested.ToString() + "}}", options, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(argv.Diagnostics).Code);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(job.Diagnostics).Code);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ExplicitUnlimitedHostAllowsFiniteInvocationThreshold()
    {
        InMemoryQpdfHost host = Host(Pdf(["[ 1 @ 2 ]"], false));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--parser-max-errors=2", "--show-object=5"]),
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxParserErrors = 0L }), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Message.Trim() == "[ 1 (@) 2 ]");
    }

}
