using System.Text;
using System.Text.Json;
using Lokad.Qpdf;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

// Clean consumer smoke for the PACKED Lokad.qpdf package. Uses only the
// public embedding surface with an in-memory host; no upstream checkout,
// reference executable, renderer, or native package is needed.
internal static class Program
{
    private static int Main()
    {
        try
        {
            RunAsync().GetAwaiter().GetResult();
            Console.WriteLine("CONSUMER SMOKE PASSED");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CONSUMER FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        byte[] input = TwoPagePdf();
        InMemoryQpdfHost typedHost = NewHost("inputs/in.pdf", input);
        PdfPageSelectionResult typed = await Qpdf.SelectPageAsync(
            typedHost,
            QpdfPath.From("inputs/in.pdf"),
            QpdfPath.From("outputs/typed.pdf"),
            new PdfPageSelectionOptions(2),
            CancellationToken.None);
        Check(typed.PageCount == 1 && typed.SourcePageCount == 2 && typed.SourcePages.Count == 1 && typed.SourcePages[0] == 2, "typed selection result");
        await CheckOutputAsync(typedHost, "outputs/typed.pdf", "Second");
        Console.WriteLine("PASS typed selection (page 2, opaque streams)");

        InMemoryQpdfHost multiHost = NewHost("inputs/in.pdf", input);
        PdfPageSelectionResult multi = await Qpdf.SelectPageAsync(
            multiHost,
            QpdfPath.From("inputs/in.pdf"),
            QpdfPath.From("outputs/multi.pdf"),
            PdfPageSelectionOptions.FromPages([2, 1, 2]),
            CancellationToken.None);
        Check(multi.PageCount == 3 && multi.SourcePageCount == 2, "multi selection counts");
        Check(multi.SourcePages.Count == 3 && multi.SourcePages[0] == 2 && multi.SourcePages[1] == 1 && multi.SourcePages[2] == 2, "multi source mapping");
        await CheckMultiOutputAsync(multiHost, "outputs/multi.pdf", ["Second", "First", "Second"]);
        Console.WriteLine("PASS typed multi-page selection with source mapping");

        InMemoryQpdfHost argvHost = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            argvHost,
            new QpdfCommandInvocation(["inputs/in.pdf", "--pages", ".", "1", "--", "outputs/argv.pdf"]),
            CancellationToken.None);
        Check(argv.Status == QpdfExecutionStatus.Completed && argv.ExitCode == QpdfExitCode.Success, "argv execution");
        await CheckOutputAsync(argvHost, "outputs/argv.pdf", "First");
        Console.WriteLine("PASS argv selection (page 1)");

        InMemoryQpdfHost jobHost = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            """{"inputFile": "inputs/in.pdf", "outputFile": "outputs/job.pdf", "pages": [{"file": ".", "range": "2"}]}""",
            CancellationToken.None);
        Check(job.Status == QpdfExecutionStatus.Completed && job.ExitCode == QpdfExitCode.Success, "job execution");
        await CheckOutputAsync(jobHost, "outputs/job.pdf", "Second");

        byte[] typedBytes = Published(typedHost, "outputs/typed.pdf");
        byte[] jobBytes = Published(jobHost, "outputs/job.pdf");
        Check(typedBytes.AsSpan().SequenceEqual(jobBytes), "typed/job parity");
        Console.WriteLine("PASS job selection with typed/job parity");

        InMemoryQpdfHost checkHost = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult check = await Qpdf.ExecuteAsync(
            checkHost,
            new QpdfCommandInvocation(["inputs/in.pdf", "--check"]),
            CancellationToken.None);
        Check(check.Status == QpdfExecutionStatus.Completed && check.ExitCode == QpdfExitCode.Success, "check execution");
        Console.WriteLine("PASS argv inspection (--check)");

        InMemoryQpdfHost jobCheckHost = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult jobCheck = await Qpdf.ExecuteJobAsync(
            jobCheckHost,
            """{"inputFile":"inputs/in.pdf","check":"","showNpages":""}""",
            CancellationToken.None);
        Check(jobCheck.Status == QpdfExecutionStatus.Completed && jobCheck.ExitCode == QpdfExitCode.Success, "job checker execution");
        Check(jobCheck.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.PageCount && d.Message.EndsWith(": 2", StringComparison.Ordinal)), "job checker page count");
        Check(jobCheck.Counters.TotalWork > 0 && jobCheck.Counters.OpenHandles == 0 && jobCheckHost.OpenLeaseCount == 0, "job checker counters and cleanup");
        InMemoryQpdfHost strictJobHost = NewHost("inputs/in.pdf", BreakStartXref(input));
        QpdfExecutionResult strictJobCheck = await Qpdf.ExecuteJobAsync(
            strictJobHost,
            """{"inputFile":"inputs/in.pdf","check":"","suppressRecovery":""}""",
            CancellationToken.None);
        Check(strictJobCheck.Status == QpdfExecutionStatus.Failed && strictJobCheck.ExitCode == QpdfExitCode.Error, "job checker damaged xref rejection");
        Check(strictJobCheck.Counters.TotalWork > 0 && strictJobCheck.Counters.OpenHandles == 0 && strictJobHost.OpenLeaseCount == 0, "failed job checker counters and cleanup");
        Console.WriteLine("PASS job checker, combined page count, damaged xref rejection and cleanup");

        InMemoryQpdfHost nestedHost = NewHost("inputs/in.pdf", input);
        nestedHost.AddInput(QpdfPath.From("inputs/child.json"), "{\"check\":\"\",\"showNpages\":\"\"}"u8.ToArray(), QpdfInputOwnership.Copy);
        nestedHost.AddInput(QpdfPath.From("inputs/parent.json"), "{\"inputFile\":\"inputs/in.pdf\",\"jobJsonFile\":\"inputs/child.json\"}"u8.ToArray(), QpdfInputOwnership.Copy);
        QpdfExecutionResult nested = await Qpdf.ExecuteJobAsync(nestedHost, """{"jobJsonFile":"inputs/parent.json"}""", CancellationToken.None);
        Check(nested.Status == QpdfExecutionStatus.Completed && nested.ExitCode == QpdfExitCode.Success, "nested checker execution");
        Check(nested.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.PageCount && d.Message.EndsWith(": 2", StringComparison.Ordinal)), "nested checker page count");
        QpdfExecutionResult nestedArgv = await Qpdf.ExecuteAsync(nestedHost, new QpdfCommandInvocation(["--job-json-file=inputs/parent.json"]), CancellationToken.None);
        Check(nestedArgv.Status == QpdfExecutionStatus.Completed && nestedArgv.ExitCode == QpdfExitCode.Success, "joined job-file execution");
        Check(nestedArgv.Counters.TotalWork > 0 && nestedArgv.Counters.OpenHandles == 0 && nestedHost.OpenLeaseCount == 0, "nested job cleanup");
        QpdfExecutionResult lastKey = await Qpdf.ExecuteJobAsync(nestedHost, """{"inputFile":"inputs/in.pdf","check":true,"check":""}""", CancellationToken.None);
        Check(lastKey.Status == QpdfExecutionStatus.Completed && lastKey.ExitCode == QpdfExitCode.Success, "duplicate job keys keep the final value");
        Console.WriteLine("PASS nested job files, joined argv, duplicate-key precedence and cleanup");

        InMemoryQpdfHost mixedHost = NewHost("inputs/in.pdf", input);
        mixedHost.AddInput(QpdfPath.From("inputs/version.json"), "{\"forceVersion\":\"1.5\"}"u8.ToArray(), QpdfInputOwnership.Copy);
        mixedHost.AddInput(QpdfPath.From("inputs/output.json"), "{\"outputFile\":\"outputs/mixed.pdf\"}"u8.ToArray(), QpdfInputOwnership.Copy);
        QpdfExecutionResult mixed = await Qpdf.ExecuteAsync(mixedHost,
            new QpdfCommandInvocation(["inputs/in.pdf", "--force-version=1.6", "--job-json-file=inputs/version.json", "--job-json-file=inputs/output.json", "--rotate=+90:1"]), CancellationToken.None);
        Check(mixed.Status == QpdfExecutionStatus.Completed && mixed.ExitCode == QpdfExitCode.Success, "mixed argv/file execution");
        byte[] mixedBytes = Published(mixedHost, "outputs/mixed.pdf");
        Check(Encoding.ASCII.GetString(mixedBytes).StartsWith("%PDF-1.5", StringComparison.Ordinal), "mixed setter precedence");
        mixedHost.AddInput(QpdfPath.From("inputs/mixed.pdf"), mixedBytes, QpdfInputOwnership.Copy);
        QpdfJsonResult rotatedJson = await Qpdf.GetDocumentJsonAsync(mixedHost,
            QpdfPath.From("inputs/mixed.pdf"), QpdfPath.From("outputs/mixed-inspection.json"), new QpdfJsonOptions(2, ["qpdf"]), CancellationToken.None);
        using (JsonDocument mixedDocument = JsonDocument.Parse(rotatedJson.Json))
        {
            Check(mixedDocument.RootElement.GetProperty("qpdf")[1].EnumerateObject().Any(entry =>
                entry.Value.TryGetProperty("value", out JsonElement value)
                && value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty("/Type", out JsonElement type) && type.GetString() == "/Page"
                && value.TryGetProperty("/Rotate", out JsonElement rotation) && rotation.GetInt32() == 90), "mixed rotation effect");
        }
        Check(mixed.Counters.TotalWork > 0 && mixed.Counters.OpenHandles == 0 && mixedHost.OpenLeaseCount == 0, "mixed argv cleanup");
        Console.WriteLine("PASS mixed argv/files, version precedence, rotation and cleanup");

        QpdfExecutionResult mixedJson = await Qpdf.ExecuteAsync(mixedHost,
            new QpdfCommandInvocation(["inputs/in.pdf", "--job-json-file=inputs/version.json", "--json=2", "--json-object=1", "--json-object=trailer"]), CancellationToken.None);
        Check(mixedJson.Status == QpdfExecutionStatus.Completed && mixedJson.OutputText is not null, "mixed JSON text result");
        using (JsonDocument inspection = JsonDocument.Parse(mixedJson.OutputText ?? throw new InvalidOperationException("Missing JSON text")))
        {
            Check(inspection.RootElement.GetProperty("version").GetInt32() == 2
                && inspection.RootElement.GetProperty("qpdf")[1].TryGetProperty("obj:1 0 R", out _)
                && inspection.RootElement.GetProperty("qpdf")[1].TryGetProperty("trailer", out _), "mixed JSON objects and trailer");
        }
        Check(mixedJson.Counters.OpenHandles == 0 && mixedHost.OpenLeaseCount == 0, "mixed JSON cleanup");
        Console.WriteLine("PASS mixed JSON inspection returns text without an output file");

        InMemoryQpdfHost inspectionHost = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult combined = await Qpdf.ExecuteAsync(inspectionHost,
            new QpdfCommandInvocation(["inputs/in.pdf", "--check", "--show-npages", "--show-pages", "--show-xref", "--show-object=1", "--list-attachments", "--show-linearization"]), CancellationToken.None);
        Check(combined.Status == QpdfExecutionStatus.Completed && combined.ExitCode == QpdfExitCode.Success, "combined checker inspections");
        Check(combined.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.PageCount && d.Message.EndsWith(": 2", StringComparison.Ordinal)), "combined count");
        Check(combined.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.ObjectListing && d.Message == "<< /Pages 2 0 R /Type /Catalog >>"), "original catalog identity");
        Check(combined.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.PageListing && d.Message == "page 1: 3 0 R"), "original page identity");
        Check(combined.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.XrefListing && d.Message.StartsWith("3/0: uncompressed; offset = ", StringComparison.Ordinal)), "original xref listing");
        Check(combined.Counters.TotalWork > 0 && combined.Counters.OpenHandles == 0 && inspectionHost.OpenLeaseCount == 0, "combined inspection cleanup");
        Check(combined.OutputBytes is null, "informational inspection has no binary payload");
        Console.WriteLine("PASS checking with count, page, object, xref, attachment and linearization inspections");

        InMemoryQpdfHost selectedHost = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult selectedInspection = await Qpdf.ExecuteJobAsync(selectedHost,
            """{"inputFile":"inputs/in.pdf","check":"","pages":[{"file":".","range":"2,1,1"}],"rotate":["+90:3"],"showNpages":"","showPages":"","showXref":"","showObject":"8"}""", CancellationToken.None);
        Check(selectedInspection.Status == QpdfExecutionStatus.Completed && selectedInspection.ExitCode == QpdfExitCode.Success, "selected checker execution");
        Check(selectedInspection.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.PageCount && d.Message.EndsWith(": 3", StringComparison.Ordinal)), "selected count");
        Check(selectedInspection.Diagnostics.Where(d => d.Code == QpdfDiagnosticCode.PageListing && d.Message.StartsWith("page ", StringComparison.Ordinal))
            .Select(d => d.Message).SequenceEqual(new[] { "page 1: 4 0 R", "page 2: 3 0 R", "page 3: 8 0 R" }), "selected order and duplicate identity");
        Check(selectedInspection.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.ObjectListing && d.Message.Contains("/Rotate 90", StringComparison.Ordinal)), "selected output-page rotation");
        Check(!selectedInspection.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.XrefListing && d.Message.StartsWith("8/", StringComparison.Ordinal)), "new objects stay outside physical xrefs");
        Check(selectedInspection.Counters.OpenHandles == 0L && selectedHost.OpenLeaseCount == 0L && selectedInspection.OutputBytes is null, "selected inspection cleanup");
        Console.WriteLine("PASS selected checking preserves original identities, duplicates, rotation and physical xrefs");

        selectedHost.AddInput(QpdfPath.From("inputs/selection.json"), "{\"pages\":[{\"file\":\".\",\"range\":\"1\"}]}"u8.ToArray(), QpdfInputOwnership.Copy);
        QpdfExecutionResult selectedBytes = await Qpdf.ExecuteAsync(selectedHost,
            new QpdfCommandInvocation(["inputs/in.pdf", "--job-json-file=inputs/selection.json", "--show-object=5", "--filtered-stream-data"]), CancellationToken.None);
        Check(selectedBytes.ExitCode == QpdfExitCode.Success && selectedBytes.OutputBytes.HasValue
            && selectedBytes.OutputBytes.GetValueOrDefault().Span.SequenceEqual("BT /F1 24 Tf 100 700 Td (First) Tj ET"u8), "selected fileless stream bytes");
        Check(selectedBytes.Counters.OpenHandles == 0L && selectedHost.OpenLeaseCount == 0L, "selected mixed stream cleanup");
        Console.WriteLine("PASS mixed selected inspection returns the original filtered stream bytes");

        InMemoryQpdfHost streamHost = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult streamResult = await Qpdf.ExecuteAsync(streamHost,
            new QpdfCommandInvocation(["inputs/in.pdf", "--show-object=5", "--raw-stream-data"]), CancellationToken.None);
        byte[] expectedStream = Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (First) Tj ET");
        Check(streamResult.Status == QpdfExecutionStatus.Completed && streamResult.OutputBytes.HasValue, "fileless raw stream result");
        Check(streamResult.OutputBytes.GetValueOrDefault().Span.SequenceEqual(expectedStream), "fileless raw stream bytes");
        Check(streamResult.Counters.TotalOutputBytes == expectedStream.Length && streamHost.OpenLeaseCount == 0L, "fileless stream counters and cleanup");
        Console.WriteLine("PASS fileless raw stream returns caller-owned bytes");

        byte[] attachmentBytes = [0, 255, 127, 10];
        InMemoryQpdfHost filelessHost = NewHost("inputs/in.pdf", input);
        filelessHost.AddInput(QpdfPath.From("inputs/payload.bin"), attachmentBytes, QpdfInputOwnership.Copy);
        await Qpdf.AddAttachmentsAsync(filelessHost, QpdfPath.From("inputs/in.pdf"), QpdfPath.From("outputs/embedded.pdf"),
            [new QpdfAttachmentSpec("inputs/payload.bin", "payload", "p.bin", null, "application/octet-stream", null, null, false)], CancellationToken.None);
        Check(filelessHost.TryGetOutput(QpdfPath.From("outputs/embedded.pdf"), out byte[] embedded), "embedded file for binary consumer");
        filelessHost.AddInput(QpdfPath.From("inputs/embedded.pdf"), embedded, QpdfInputOwnership.Copy);
        QpdfExecutionResult binaryResult = await Qpdf.ExecuteJobAsync(filelessHost,
            """{"inputFile":"inputs/embedded.pdf","check":"","showAttachment":"payload"}""", CancellationToken.None);
        Check(binaryResult.Status == QpdfExecutionStatus.Completed && binaryResult.OutputBytes.HasValue, "checking with fileless attachment result");
        ReadOnlyMemory<byte> returnedAttachment = binaryResult.OutputBytes.GetValueOrDefault();
        Check(returnedAttachment.Span.SequenceEqual(attachmentBytes) && binaryResult.Counters.TotalOutputBytes == attachmentBytes.Length, "actual fileless attachment bytes");
        Check(binaryResult.Counters.OpenHandles == 0L && filelessHost.OpenLeaseCount == 0L, "fileless attachment cleanup");
        Check(returnedAttachment.Span.SequenceEqual(attachmentBytes), "returned attachment remains usable after cleanup");
        Console.WriteLine("PASS checking with fileless attachment returns binary NUL/high bytes");

        byte[] damaged = BreakStartXref(input);
        InMemoryQpdfHost repairHost = NewHost("inputs/damaged.pdf", damaged);
        QpdfExecutionResult repair = await Qpdf.ExecuteAsync(
            repairHost,
            new QpdfCommandInvocation(["inputs/damaged.pdf", "--pages", ".", "1", "--", "outputs/repaired.pdf"]),
            CancellationToken.None);
        Check(repair.Status == QpdfExecutionStatus.Completed && repair.ExitCode == QpdfExitCode.Warning, "repair warning exit");
        await CheckOutputAsync(repairHost, "outputs/repaired.pdf", "First");
        Console.WriteLine("PASS bounded repair of damaged input");

        byte[] syntaxDamage = (byte[])input.Clone();
        int scalarAt = Encoding.ASCII.GetString(syntaxDamage).IndexOf("/BaseFont /Helvetica", StringComparison.Ordinal);
        Check(scalarAt >= 0, "parser smoke scalar marker");
        syntaxDamage[scalarAt + "/BaseFont ".Length] = (byte)' ';
        InMemoryQpdfHost parserHost = NewHost("inputs/syntax.pdf", syntaxDamage);
        QpdfExecutionResult repairedObject = await Qpdf.ExecuteAsync(parserHost,
            new QpdfCommandInvocation(["inputs/syntax.pdf", "--parser-max-errors=2", "--show-object=7"]), CancellationToken.None);
        Check(repairedObject.ExitCode == QpdfExitCode.Warning, "syntax repair warning");
        Check(repairedObject.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.RepairedObject), "public repair diagnostic");
        Check(repairedObject.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.ObjectListing && d.Message.Contains("/BaseFont (Helvetica)", StringComparison.Ordinal)), "repaired scalar is readable");
        Check(parserHost.OpenLeaseCount == 0L, "parser smoke leases closed");
        Console.WriteLine("PASS valid-xref object syntax repair and public diagnostic");

        string longReal = "1.00000000000000011102230246251565404236316680908203125" + new string('0', 2048) + "1";
        InMemoryQpdfHost realHost = NewHost("inputs/real.pdf", TwoPagePdf(longReal));
        await using (QpdfDocument realInput = await QpdfDocument.OpenAsync(realHost, QpdfPath.From("inputs/real.pdf"), CancellationToken.None))
        {
            Check(realInput.GetObject(8) is PdfReal real && real.Text == longReal
                && BitConverter.DoubleToUInt64Bits(real.Value) == BitConverter.DoubleToUInt64Bits(1.0) + 1UL, "long real text and rounding");
        }
        QpdfExecutionResult realRewrite = await Qpdf.ExecuteAsync(realHost,
            new QpdfCommandInvocation(["inputs/real.pdf", "--pages", ".", "1", "--", "outputs/real.pdf"]), CancellationToken.None);
        Check(realRewrite.ExitCode == QpdfExitCode.Success, "long real rewrite");
        realHost.AddInput(QpdfPath.From("inputs/real-reread.pdf"), Published(realHost, "outputs/real.pdf"), QpdfInputOwnership.Copy);
        await using (QpdfDocument reread = await QpdfDocument.OpenAsync(realHost, QpdfPath.From("inputs/real-reread.pdf"), CancellationToken.None))
        {
            Check(reread.GetObject(3) is PdfDictionary, "long real page");
            PdfDictionary realPage = (PdfDictionary)reread.GetObject(3);
            Check(realPage.TryGet(new PdfName("Audit"u8.ToArray()), out QpdfObject audit), "long real retained page value");
            QpdfObject value = audit is PdfReference reference ? reread.GetObject(reference.ObjectNumber) : audit;
            Check(value is PdfReal real && real.Text == longReal
                && BitConverter.DoubleToUInt64Bits(real.Value) == BitConverter.DoubleToUInt64Bits(1.0) + 1UL, "rewritten long real text and rounding");
        }
        Check(realHost.OpenLeaseCount == 0L, "long real smoke leases closed");
        Console.WriteLine("PASS long real text, exact rounding and rewrite");

        InMemoryQpdfHost lexicalHost = NewHost("inputs/lexical.pdf", TwoPagePdf("[ /A#00B (a\r\nb\rc\n) ]"));
        QpdfExecutionResult lexicalRewrite = await Qpdf.ExecuteAsync(lexicalHost,
            new QpdfCommandInvocation(["inputs/lexical.pdf", "--parser-max-errors=2", "--pages", ".", "1", "--", "outputs/lexical.pdf"]), CancellationToken.None);
        Check(lexicalRewrite.ExitCode == QpdfExitCode.Warning, "lexical repair warning");
        lexicalHost.AddInput(QpdfPath.From("inputs/lexical-reread.pdf"), Published(lexicalHost, "outputs/lexical.pdf"), QpdfInputOwnership.Copy);
        await using (QpdfDocument reread = await QpdfDocument.OpenAsync(lexicalHost, QpdfPath.From("inputs/lexical-reread.pdf"), CancellationToken.None))
        {
            Check(reread.GetObject(3) is PdfDictionary, "lexical page");
            PdfDictionary lexicalPage = (PdfDictionary)reread.GetObject(3);
            Check(lexicalPage.TryGet(new PdfName("Audit"u8.ToArray()), out QpdfObject audit), "lexical page value");
            QpdfObject value = audit is PdfReference reference ? reread.GetObject(reference.ObjectNumber) : audit;
            Check(value is PdfArray, "repaired lexical array");
            PdfArray array = (PdfArray)value;
            Check(array.Count == 2 && array[0] is PdfNull && array[1] is PdfByteString text
                && text.Value.AsSpan().SequenceEqual("a\nb\nc\n"u8), "NUL name repair and literal newline bytes");
        }
        Check(lexicalHost.OpenLeaseCount == 0L, "lexical smoke leases closed");
        Console.WriteLine("PASS NUL name repair and literal newline rewrite");

        foreach (long budget in new long[] { 0L, 3L })
        {
            string budgetText = budget.ToString(System.Globalization.CultureInfo.InvariantCulture);
            InMemoryQpdfHost dictionaryHost = NewHost("inputs/dictionary.pdf", TwoPagePdf("<< 1 2 @ /A 3 >>"));
            QpdfExecutionResult inspection = await Qpdf.ExecuteAsync(dictionaryHost,
                new QpdfCommandInvocation(["inputs/dictionary.pdf", "--parser-max-errors=" + budgetText, "--show-object=8"]), CancellationToken.None);
            string expected = budget == 0L ? "null" : "<< /A 3 /QPDFFake1 1 /QPDFFake2 2 /QPDFFake3 (@) >>";
            Check(inspection.ExitCode == QpdfExitCode.Warning && inspection.Diagnostics.Any(d =>
                d.Code == QpdfDiagnosticCode.ObjectListing && d.Message.Trim() == expected), "dictionary guard inspection");
            QpdfExecutionResult rewrite = await Qpdf.ExecuteJobAsync(dictionaryHost,
                "{\"inputFile\":\"inputs/dictionary.pdf\",\"outputFile\":\"outputs/dictionary.pdf\",\"pages\":[{\"file\":\".\",\"range\":\"1\"}],\"global\":{\"parserMaxErrors\":" + budgetText + "}}", CancellationToken.None);
            Check(rewrite.ExitCode == QpdfExitCode.Warning, "dictionary guard rewrite");
            dictionaryHost.AddInput(QpdfPath.From("inputs/dictionary-reread.pdf"), Published(dictionaryHost, "outputs/dictionary.pdf"), QpdfInputOwnership.Copy);
            await using (QpdfDocument reread = await QpdfDocument.OpenAsync(dictionaryHost, QpdfPath.From("inputs/dictionary-reread.pdf"), CancellationToken.None))
            {
                PdfDictionary page = (PdfDictionary)reread.GetObject(3);
                QpdfObject value = page.TryGet(new PdfName("Audit"u8.ToArray()), out QpdfObject audit) ? audit : PdfNull.Instance;
                if (value is PdfReference reference) { value = reread.GetObject(reference.ObjectNumber); }
                if (budget == 0L)
                {
                    Check(value is PdfNull, "zero-budget orphan guard survives rewrite");
                }
                else
                {
                    Check(value is PdfDictionary, "repaired dictionary survives rewrite");
                    PdfDictionary dictionary = (PdfDictionary)value;
                    Check(dictionary.TryGet(new PdfName("A"u8.ToArray()), out QpdfObject a) && a is PdfInteger integer && integer.Value == 3L,
                        "dictionary named value survives rewrite");
                    Check(dictionary.TryGet(new PdfName("QPDFFake3"u8.ToArray()), out QpdfObject fake) && fake is PdfByteString text
                        && text.Value.AsSpan().SequenceEqual("@"u8), "dictionary orphan bytes survive rewrite");
                }
            }
            Check(dictionaryHost.OpenLeaseCount == 0L, "dictionary smoke leases closed");
        }
        Console.WriteLine("PASS dictionary orphan/error guards through argv, job and rewrite");

        InMemoryQpdfHost binaryHost = NewHost("inputs/binary.pdf", TwoPagePdf("[ (\\000\\001\\037\\177\\200\\377) <414243> ]"));
        QpdfExecutionResult binaryInspection = await Qpdf.ExecuteAsync(binaryHost,
            new QpdfCommandInvocation(["inputs/binary.pdf", "--show-object=8"]), CancellationToken.None);
        Check(binaryInspection.ExitCode == QpdfExitCode.Success && binaryInspection.Diagnostics.Any(d =>
            d.Code == QpdfDiagnosticCode.ObjectListing && d.Message.Trim() == "[ <00011f7f80ff> (ABC) ]"), "decoded-byte string inspection");
        QpdfExecutionResult binaryRewrite = await Qpdf.ExecuteJobAsync(binaryHost,
            """{"inputFile":"inputs/binary.pdf","outputFile":"outputs/binary.pdf","pages":[{"file":".","range":"1"}]}""", CancellationToken.None);
        Check(binaryRewrite.ExitCode == QpdfExitCode.Success, "binary rewrite");
        binaryHost.AddInput(QpdfPath.From("inputs/binary-reread.pdf"), Published(binaryHost, "outputs/binary.pdf"), QpdfInputOwnership.Copy);
        await using (QpdfDocument reread = await QpdfDocument.OpenAsync(binaryHost, QpdfPath.From("inputs/binary-reread.pdf"), CancellationToken.None))
        {
            PdfDictionary page = (PdfDictionary)reread.GetObject(3);
            Check(page.TryGet(new PdfName("Audit"u8.ToArray()), out QpdfObject audit), "binary audit");
            QpdfObject value = audit is PdfReference reference ? reread.GetObject(reference.ObjectNumber) : audit;
            Check(value is PdfArray, "binary array");
            PdfArray array = (PdfArray)value;
            Check(array[0] is PdfByteString bytes && bytes.Value.AsSpan().SequenceEqual(new byte[] { 0, 1, 31, 127, 128, 255 }), "binary octal bytes");
            Check(array[1] is PdfByteString text && text.Hex && text.Value.AsSpan().SequenceEqual("ABC"u8), "hex source spelling preserved");
        }
        Check(binaryHost.OpenLeaseCount == 0L, "binary smoke leases closed");
        Console.WriteLine("PASS decoded-byte string display and binary rewrite");

        InMemoryQpdfHost nameHost = NewHost("inputs/names.pdf", TwoPagePdf("<< /A#2 1 /A#232 2 >>"));
        QpdfExecutionResult nameInspection = await Qpdf.ExecuteJobAsync(nameHost,
            """{"inputFile":"inputs/names.pdf","showObject":"8"}""", CancellationToken.None);
        Check(nameInspection.ExitCode == QpdfExitCode.Warning && nameInspection.Diagnostics.Any(d =>
            d.Code == QpdfDiagnosticCode.ObjectListing && d.Message.Trim() == "<< /A#2 1 /A#232 2 >>"), "stray/escaped hash identity");
        QpdfExecutionResult nameRewrite = await Qpdf.ExecuteAsync(nameHost,
            new QpdfCommandInvocation(["inputs/names.pdf", "--pages", ".", "1", "--", "outputs/names.pdf"]), CancellationToken.None);
        Check(nameRewrite.ExitCode == QpdfExitCode.Warning, "stray hash rewrite warning");
        nameHost.AddInput(QpdfPath.From("inputs/names-reread.pdf"), Published(nameHost, "outputs/names.pdf"), QpdfInputOwnership.Copy);
        await using (QpdfDocument reread = await QpdfDocument.OpenAsync(nameHost, QpdfPath.From("inputs/names-reread.pdf"), CancellationToken.None))
        {
            PdfDictionary page = (PdfDictionary)reread.GetObject(3);
            Check(page.TryGet(new PdfName("Audit"u8.ToArray()), out QpdfObject audit) && audit is PdfReference, "name audit reference");
            PdfReference reference = (PdfReference)audit;
            bool rejected = false;
            try { reread.GetObject(reference.ObjectNumber); }
            catch (QpdfParseException) { rejected = true; }
            Check(rejected, "strict reopening still rejects stray hashes");
            QpdfExecutionResult nameReopened = await Qpdf.ExecuteAsync(nameHost,
                new QpdfCommandInvocation(["inputs/names-reread.pdf", "--show-object=" + reference.ObjectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)]), CancellationToken.None);
            Check(nameReopened.ExitCode == QpdfExitCode.Warning && nameReopened.Diagnostics.Any(d =>
                d.Code == QpdfDiagnosticCode.ObjectListing && d.Message.Trim() == "<< /A#2 1 /A#232 2 >>"), "repaired name rewrite identity");
        }
        Check(nameHost.OpenLeaseCount == 0L, "name smoke leases closed");
        Console.WriteLine("PASS malformed name identity, rewrite warnings and strict rejection");

        InMemoryQpdfHost displayHost = NewHost("inputs/display.pdf", TwoPagePdf("<41414141ff>"));
        QpdfExecutionResult admittedDisplay = await Qpdf.ExecuteJobAsync(displayHost,
            """{"inputFile":"inputs/display.pdf","showObject":"8"}""",
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxDiagnostics = 1L, MaxDiagnosticBytes = 8L }), CancellationToken.None);
        Check(admittedDisplay.ExitCode == QpdfExitCode.Success && admittedDisplay.Diagnostics.Count == 1
            && admittedDisplay.Diagnostics[0].Message == "(AAAA\u00ff)", "display UTF-8 byte boundary and single diagnostic charge");
        Check(admittedDisplay.Counters.OpenHandles == 0L && admittedDisplay.Counters.PeakMemoryBytes > admittedDisplay.Counters.LiveMemoryBytes,
            "display snapshot after cleanup with retained result");
        QpdfExecutionResult rejectedDisplay = await Qpdf.ExecuteAsync(displayHost,
            new QpdfCommandInvocation(["inputs/display.pdf", "--show-object=8"]),
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxDiagnosticBytes = 7L }), CancellationToken.None);
        Check(rejectedDisplay.Status == QpdfExecutionStatus.Failed && rejectedDisplay.Diagnostics.Count == 1
            && rejectedDisplay.Diagnostics[0].Code == QpdfDiagnosticCode.BudgetExceeded
            && rejectedDisplay.Counters.TotalWork > 0L && rejectedDisplay.Counters.PeakMemoryBytes > 0L
            && rejectedDisplay.Counters.OpenHandles == 0L, "display byte rejection preserves observed counters and releases handles");
        Check(displayHost.OpenLeaseCount == 0L, "display consumer leases closed");
        Console.WriteLine("PASS display admission, diagnostic byte limits and observed failure counters");

        byte[] abandonedInput = TwoPagePdfWithAuditObjects(Enumerable.Repeat("[ (" + new string('A', 4096) + ") @ ]", 50).ToArray());
        QpdfLimits abandonedLimits = QpdfLimits.Default with { MaxMemoryBytes = abandonedInput.Length + 60000L, MaxParserErrors = 1L };
        foreach (bool useJob in new[] { false, true })
        {
            InMemoryQpdfHost abandonedHost = NewHost("inputs/abandoned.pdf", abandonedInput);
            await using (QpdfDocument control = await QpdfDocument.OpenAsync(abandonedHost, QpdfPath.From("inputs/abandoned.pdf"), abandonedLimits, CancellationToken.None))
            {
                Check(control.PageCount == 2, "discarded-value source fits the host memory ceiling");
            }
            QpdfExecutionOptions abandonedOptions = new(abandonedLimits);
            QpdfExecutionResult abandonedResult = useJob
                ? await Qpdf.ExecuteJobAsync(abandonedHost,
                    """{"inputFile":"inputs/abandoned.pdf","outputFile":"outputs/abandoned.json","json":"2","jsonKey":["qpdf"],"global":{"parserMaxErrors":1}}""", abandonedOptions, CancellationToken.None)
                : await Qpdf.ExecuteAsync(abandonedHost,
                    new QpdfCommandInvocation(["inputs/abandoned.pdf", "--json=2", "--json-key=qpdf", "--parser-max-errors=1", "--", "outputs/abandoned.json"]), abandonedOptions, CancellationToken.None);
            Check(abandonedResult.Status == QpdfExecutionStatus.Completed && abandonedResult.ExitCode == QpdfExitCode.Warning,
                "fifty abandoned repairs fit finite host memory through argv/job");
            Check(abandonedResult.Diagnostics.Count(d => d.Code == QpdfDiagnosticCode.RepairedObject) == 100, "all repairs retain their diagnostics");
            using JsonDocument repairedJson = JsonDocument.Parse(Published(abandonedHost, "outputs/abandoned.json"));
            JsonElement repairedObjects = repairedJson.RootElement.GetProperty("qpdf")[1];
            for (int number = 8; number < 58; number++)
            {
                Check(repairedObjects.GetProperty($"obj:{number} 0 R").GetProperty("value").ValueKind == JsonValueKind.Null, "aborted object is null in output");
            }
            Check(abandonedHost.OpenLeaseCount == 0L, "abandoned-repair consumer leases closed");
        }
        Console.WriteLine("PASS repeated aborted parser values under finite host memory (argv/job)");

        byte[] overwrittenInput = TwoPagePdfWithAuditObjects(Enumerable.Repeat("<< /A (" + new string('A', 4096) + ") /A null >>", 50).ToArray());
        QpdfLimits overwrittenLimits = QpdfLimits.Default with { MaxMemoryBytes = overwrittenInput.Length + 60000L, MaxParserErrors = 0L };
        foreach (bool useJob in new[] { false, true })
        {
            InMemoryQpdfHost overwrittenHost = NewHost("inputs/overwritten.pdf", overwrittenInput);
            await using (QpdfDocument control = await QpdfDocument.OpenAsync(overwrittenHost, QpdfPath.From("inputs/overwritten.pdf"), overwrittenLimits, CancellationToken.None))
            {
                Check(control.PageCount == 2, "successful-discard source fits the host memory ceiling");
            }
            QpdfExecutionOptions overwrittenOptions = new(overwrittenLimits);
            QpdfExecutionResult overwrittenResult = useJob
                ? await Qpdf.ExecuteJobAsync(overwrittenHost,
                    """{"inputFile":"inputs/overwritten.pdf","outputFile":"outputs/overwritten.json","json":"2","jsonKey":["qpdf"],"global":{"parserMaxErrors":0}}""", overwrittenOptions, CancellationToken.None)
                : await Qpdf.ExecuteAsync(overwrittenHost,
                    new QpdfCommandInvocation(["inputs/overwritten.pdf", "--json=2", "--json-key=qpdf", "--parser-max-errors=0", "--", "outputs/overwritten.json"]), overwrittenOptions, CancellationToken.None);
            Check(overwrittenResult.Status == QpdfExecutionStatus.Completed && overwrittenResult.ExitCode == QpdfExitCode.Warning,
                "fifty successful discards fit finite host memory through argv/job");
            Check(overwrittenResult.Diagnostics.Count(d => d.Code == QpdfDiagnosticCode.DuplicateKey) == 50, "successful overwrites retain duplicate warnings");
            using JsonDocument overwrittenJson = JsonDocument.Parse(Published(overwrittenHost, "outputs/overwritten.json"));
            JsonElement overwrittenObjects = overwrittenJson.RootElement.GetProperty("qpdf")[1];
            for (int number = 8; number < 58; number++)
            {
                JsonElement dictionary = overwrittenObjects.GetProperty($"obj:{number} 0 R").GetProperty("value");
                Check(dictionary.ValueKind == JsonValueKind.Object && !dictionary.EnumerateObject().Any(), "surviving null entries retain canonical JSON omission");
            }
            Check(overwrittenHost.OpenLeaseCount == 0L, "successful-discard consumer leases closed");
        }
        Console.WriteLine("PASS repeated successful parser discards under finite host memory (argv/job)");
        byte[] mismatchedInput = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(TwoPagePdf("(" + new string('A', 4096) + ")"))
            .Replace("8 0 obj\n", "9 0 obj\n", StringComparison.Ordinal));
        InMemoryQpdfHost mismatchedHost = NewHost("inputs/mismatched.pdf", mismatchedInput);
        QpdfLimits mismatchedLimits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(mismatchedHost, QpdfPath.From("inputs/mismatched.pdf"), mismatchedLimits, CancellationToken.None))
        {
            Check(document.PageCount == 2, "wrong-header source fits the host memory ceiling");
            long baseline = document.Counters.LiveMemoryBytes;
            for (int attempt = 0; attempt < 50; attempt++)
            {
                try { document.GetObject(8); throw new InvalidOperationException("Wrong object header accepted."); }
                catch (QpdfParseException error)
                {
                    Check(error.Message.Contains("header contradicts", StringComparison.Ordinal), "wrong-header rejection is preserved");
                }
                Check(document.Counters.LiveMemoryBytes == baseline, "rejected object releases only its parser admission");
            }
        }
        Check(mismatchedHost.OpenLeaseCount == 0L, "wrong-header consumer leases closed");
        Console.WriteLine("PASS repeated rejected object headers under finite host memory (typed)");
        string recoveredText = Encoding.ASCII.GetString(TwoPagePdfWithAuditObjects(Enumerable.Repeat("(" + new string('A', 4096) + ")", 50).ToArray()));
        int recoveredStart = recoveredText.LastIndexOf("startxref\n", StringComparison.Ordinal) + "startxref\n".Length;
        byte[] recoveredInput = Encoding.ASCII.GetBytes(recoveredText[..recoveredStart] + "0\n%%EOF\n");
        foreach (bool useJob in new[] { false, true })
        {
            InMemoryQpdfHost recoveredHost = NewHost("inputs/recovered.pdf", recoveredInput);
            QpdfExecutionOptions recoveredOptions = new(QpdfLimits.Default with { MaxMemoryBytes = recoveredInput.Length + 60000L });
            QpdfExecutionResult recoveredResult = useJob
                ? await Qpdf.ExecuteJobAsync(recoveredHost, """{"inputFile":"inputs/recovered.pdf","showNpages":""}""", recoveredOptions, CancellationToken.None)
                : await Qpdf.ExecuteAsync(recoveredHost, new QpdfCommandInvocation(["inputs/recovered.pdf", "--show-npages"]), recoveredOptions, CancellationToken.None);
            Check(recoveredResult.Status == QpdfExecutionStatus.Completed && recoveredResult.ExitCode == QpdfExitCode.Warning,
                "fifty successful recovery trials fit finite host memory");
            Check(recoveredResult.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.RecoveryEngaged)
                && recoveredResult.Diagnostics.Any(d => d.Message == "inputs/recovered.pdf: 2"), "reconstruction preserves page count and warning");
            Check(recoveredResult.Counters.TotalWork > 0L && recoveredHost.OpenLeaseCount == 0L, "recovery consumer records work and closes leases");
        }
        Console.WriteLine("PASS successful recovery trial cleanup under finite host memory (argv/job)");
        byte[] largeTableInput = TwoPagePdfWithAuditObjects(Enumerable.Repeat("null", 2000).ToArray());
        InMemoryQpdfHost largeTableHost = NewHost("inputs/table.pdf", largeTableInput);
        QpdfLimits tableLimits = QpdfLimits.Default with { MaxMemoryBytes = largeTableInput.Length + 100000L };
        bool tableRejected = false;
        try
        {
            await using QpdfDocument document = await QpdfDocument.OpenAsync(largeTableHost, QpdfPath.From("inputs/table.pdf"), tableLimits, CancellationToken.None);
        }
        catch (QpdfLimitException ex) when (ex.LimitName == nameof(QpdfLimits.MaxMemoryBytes)) { tableRejected = true; }
        Check(tableRejected && largeTableHost.OpenLeaseCount == 0L, "typed table admission rejects before allocation and closes leases");
        foreach (bool useJob in new[] { false, true })
        {
            QpdfExecutionResult tableResult = useJob
                ? await Qpdf.ExecuteJobAsync(largeTableHost, """{"inputFile":"inputs/table.pdf","showNpages":""}""", new QpdfExecutionOptions(tableLimits), CancellationToken.None)
                : await Qpdf.ExecuteAsync(largeTableHost, new QpdfCommandInvocation(["inputs/table.pdf", "--show-npages"]), new QpdfExecutionOptions(tableLimits), CancellationToken.None);
            Check(tableResult.Status == QpdfExecutionStatus.Failed && tableResult.ExitCode == QpdfExitCode.Error
                && tableResult.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.BudgetExceeded), "argv/job table admission respects finite host memory");
            Check(largeTableHost.OpenLeaseCount == 0L, "table admission consumer leases closed");
        }
        await using (QpdfDocument tableDocument = await QpdfDocument.OpenAsync(largeTableHost, QpdfPath.From("inputs/table.pdf"),
            QpdfLimits.Default with { MaxMemoryBytes = 1048576L }, CancellationToken.None))
        {
            Check(tableDocument.PageCount == 2, "generous table admission preserves page count");
        }
        Check(largeTableHost.OpenLeaseCount == 0L, "generous table consumer leases closed");
        Console.WriteLine("PASS xref collection capacity under finite host memory (typed/argv/job)");
        byte[] cacheInput = TwoPagePdfWithAuditObjects(Enumerable.Repeat("(" + new string('A', 32) + ")", 2000).ToArray());
        InMemoryQpdfHost cacheHost = NewHost("inputs/cache.pdf", cacheInput);
        QpdfLimits cacheLimits = QpdfLimits.Default with { MaxMemoryBytes = cacheInput.Length + 450000L };
        QpdfDocument cacheDocument = await QpdfDocument.OpenAsync(cacheHost, QpdfPath.From("inputs/cache.pdf"), cacheLimits, CancellationToken.None);
        PdfByteString cacheAlias = (PdfByteString)cacheDocument.GetObject(8);
        bool cacheRejected = false;
        try { for (int number = 9; number < 2008; number++) { cacheDocument.GetObject(number); } }
        catch (QpdfLimitException ex) when (ex.LimitName == nameof(QpdfLimits.MaxMemoryBytes)) { cacheRejected = true; }
        Check(cacheRejected && ReferenceEquals(cacheAlias, cacheDocument.GetObject(8)), "cache admission rejects growth while preserving the first object");
        long cacheBeforeDisposal = cacheDocument.Counters.LiveMemoryBytes;
        await cacheDocument.DisposeAsync();
        long cacheAfterDisposal = cacheDocument.Counters.LiveMemoryBytes;
        await cacheDocument.DisposeAsync();
        Check(cacheAfterDisposal < cacheBeforeDisposal && cacheDocument.Counters.LiveMemoryBytes == cacheAfterDisposal,
            "cache metadata is released once on disposal");
        Check(cacheAlias.Value.SequenceEqual(Encoding.ASCII.GetBytes(new string('A', 32))) && cacheHost.OpenLeaseCount == 0L,
            "returned cache bytes survive disposal and leases close");
        foreach (bool useJob in new[] { false, true })
        {
            QpdfExecutionResult cacheResult = useJob
                ? await Qpdf.ExecuteJobAsync(cacheHost, """{"inputFile":"inputs/cache.pdf","outputFile":"outputs/cache.json","json":"2","jsonKey":["qpdf"]}""", new QpdfExecutionOptions(cacheLimits), CancellationToken.None)
                : await Qpdf.ExecuteAsync(cacheHost, new QpdfCommandInvocation(["inputs/cache.pdf", "--json=2", "--json-key=qpdf", "--", "outputs/cache.json"]), new QpdfExecutionOptions(cacheLimits), CancellationToken.None);
            Check(cacheResult.Status == QpdfExecutionStatus.Failed && cacheResult.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.BudgetExceeded),
                "argv/job cache traversal respects finite host memory");
            Check(!cacheHost.TryGetOutput(QpdfPath.From("outputs/cache.json"), out _) && cacheHost.OpenLeaseCount == 0L,
                "rejected cache traversal publishes nothing and closes leases");
        }
        await using (QpdfDocument generousCache = await QpdfDocument.OpenAsync(cacheHost, QpdfPath.From("inputs/cache.pdf"),
            QpdfLimits.Default with { MaxMemoryBytes = 16 * 1024 * 1024L }, CancellationToken.None))
        {
            Check(((PdfByteString)generousCache.GetObject(2007)).Value.Length == 32, "generous cache reopening preserves the last object");
        }
        Console.WriteLine("PASS document cache admission and disposal (typed/argv/job)");
        byte[] lifetimeInput = TwoPagePdfWithAuditObjects(["(kept)", "null %" + new string('A', 1048576) + "\n"]);
        InMemoryQpdfHost lifetimeHost = NewHost("inputs/lifetime.pdf", lifetimeInput);
        QpdfLimits lifetimeLimits = QpdfLimits.Default with { MaxMemoryBytes = 2097152L };
        QpdfDocument lifetimeDocument = await QpdfDocument.OpenAsync(lifetimeHost, QpdfPath.From("inputs/lifetime.pdf"),
            lifetimeLimits, CancellationToken.None);
        PdfByteString lifetimeAlias = (PdfByteString)lifetimeDocument.GetObject(8);
        long lifetimeBefore = lifetimeDocument.Counters.LiveMemoryBytes;
        await lifetimeDocument.DisposeAsync();
        long lifetimeAfter = lifetimeDocument.Counters.LiveMemoryBytes;
        await lifetimeDocument.DisposeAsync();
        Check(lifetimeBefore - lifetimeAfter >= lifetimeInput.Length && lifetimeAfter < lifetimeInput.Length
            && lifetimeDocument.Counters.LiveMemoryBytes == lifetimeAfter, "exclusive document admission releases once");
        Check(lifetimeAlias.Value.SequenceEqual("kept"u8.ToArray()) && lifetimeDocument.PageCount == 2
            && lifetimeHost.OpenLeaseCount == 0L, "returned bytes and page count survive document disposal");
        Console.WriteLine("PASS document input/table disposal with returned byte alias");
        InMemoryQpdfHost lifetimeMergeHost = NewHost("inputs/a.pdf", lifetimeInput);
        lifetimeMergeHost.AddInput(QpdfPath.From("inputs/b.pdf"), lifetimeInput, QpdfInputOwnership.Copy);
        lifetimeMergeHost.AddInput(QpdfPath.From("inputs/c.pdf"), lifetimeInput, QpdfInputOwnership.Copy);
        PdfMultiPageSelectionResult lifetimeMerge = await Qpdf.SelectPagesAsync(lifetimeMergeHost,
            [new PdfPageSource(QpdfPath.From("inputs/a.pdf"), PdfPageSelectionOptions.FromPages([1])),
             new PdfPageSource(QpdfPath.From("inputs/b.pdf"), PdfPageSelectionOptions.FromPages([2])),
             new PdfPageSource(QpdfPath.From("inputs/c.pdf"), PdfPageSelectionOptions.FromPages([1]))],
            QpdfPath.From("outputs/lifetime-merge.pdf"), lifetimeLimits, CancellationToken.None);
        Check(lifetimeMerge.PageCount == 3 && lifetimeMergeHost.OpenLeaseCount == 0L,
            "multi-source selection reuses finite input-copy admission");
        await CheckMultiOutputAsync(lifetimeMergeHost, "outputs/lifetime-merge.pdf", ["First", "Second", "First"]);
        Console.WriteLine("PASS three-source selection under finite document memory");
        InMemoryQpdfHost responseHost = NewHost("inputs/in.pdf", input);
        responseHost.AddInput(
            QpdfPath.From("args.txt"),
            Encoding.ASCII.GetBytes("inputs/in.pdf\n--pages\n.\n1\n--\noutputs/response.pdf\n"),
            QpdfInputOwnership.Copy);
        QpdfExecutionResult response = await Qpdf.ExecuteAsync(
            responseHost,
            new QpdfCommandInvocation(["@args.txt"]),
            CancellationToken.None);
        Check(response.Status == QpdfExecutionStatus.Completed && response.ExitCode == QpdfExitCode.Success, "response file execution");
        await CheckOutputAsync(responseHost, "outputs/response.pdf", "First");
        Console.WriteLine("PASS response file selection (@file)");

        InMemoryQpdfHost helpHost = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult help = await Qpdf.ExecuteAsync(
            helpHost,
            new QpdfCommandInvocation(["--help"]),
            CancellationToken.None);
        Check(help.Status == QpdfExecutionStatus.Completed && help.ExitCode == QpdfExitCode.Success, "help execution");
        Console.WriteLine("PASS help identity (--help)");

        InMemoryQpdfHost mergeHost = NewHost("inputs/in.pdf", input);
        mergeHost.AddInput(QpdfPath.From("inputs/in2.pdf"), input, QpdfInputOwnership.Copy);
        PdfMultiPageSelectionResult merged = await Qpdf.SelectPagesAsync(
            mergeHost,
            [new PdfPageSource(QpdfPath.From("inputs/in.pdf"), new PdfPageSelectionOptions(2)),
             new PdfPageSource(QpdfPath.From("inputs/in2.pdf"), new PdfPageSelectionOptions(1))],
            QpdfPath.From("outputs/merged.pdf"),
            CancellationToken.None);
        Check(merged.PageCount == 2 && merged.SourcePageCounts.Count == 2, "merge counts");
        Check(merged.Mapping.Count == 2 && merged.Mapping[0].SourceIndex == 0 && merged.Mapping[1].SourceIndex == 1, "merge mapping");
        await CheckOutputAsync(mergeHost, "outputs/merged.pdf", "Second", 2);
        Console.WriteLine("PASS typed multi-source selection with identity");

        InMemoryQpdfHost rotateHost = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult rotated = await Qpdf.ExecuteAsync(
            rotateHost,
            new QpdfCommandInvocation(["inputs/in.pdf", "--pages", ".", "1", "--rotate=+90", "--", "outputs/rotated.pdf"]),
            CancellationToken.None);
        Check(rotated.Status == QpdfExecutionStatus.Completed && rotated.ExitCode == QpdfExitCode.Success, "rotated execution");
        Console.WriteLine("PASS rotated selection (--rotate)");
        InMemoryQpdfHost typedRotateHost = NewHost("inputs/in.pdf", input);
        PdfPageSelectionResult typedRotated = await Qpdf.SelectPageAsync(
            typedRotateHost,
            QpdfPath.From("inputs/in.pdf"),
            QpdfPath.From("outputs/typed-rotated.pdf"),
            new PdfPageSelectionOptions(1),
            [new PdfPageRotation(PdfRotationMode.Add, 90, null)],
            CancellationToken.None);
        Check(typedRotated.PageCount == 1, "typed rotation counts");
        await CheckOutputAsync(typedRotateHost, "outputs/typed-rotated.pdf", "First");
        Check(Published(typedRotateHost, "outputs/typed-rotated.pdf").AsSpan().SequenceEqual(Published(rotateHost, "outputs/rotated.pdf")), "typed/argv rotation parity");
        Console.WriteLine("PASS typed single-page rotation overloads");
        InMemoryQpdfHost labelHost = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult labeled = await Qpdf.ExecuteAsync(
            labelHost,
            new QpdfCommandInvocation(["inputs/in.pdf", "--pages", ".", "1", "--set-page-labels", "1:D", "--", "outputs/labeled.pdf"]),
            CancellationToken.None);
        Check(labeled.Status == QpdfExecutionStatus.Completed && labeled.ExitCode == QpdfExitCode.Success, "labeled execution");
        Console.WriteLine("PASS labeled selection (--set-page-labels)");
        InMemoryQpdfHost splitHost = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult split = await Qpdf.ExecuteAsync(
            splitHost,
            new QpdfCommandInvocation(["inputs/in.pdf", "--pages", ".", "1", ".", "2", "--split-pages", "--", "outputs/split.pdf"]),
            CancellationToken.None);
        Check(split.Status == QpdfExecutionStatus.Completed && split.ExitCode == QpdfExitCode.Success, "split execution");
        Console.WriteLine("PASS split publication (--split-pages)");
        InMemoryQpdfHost typedSplitHost = NewHost("inputs/in.pdf", input);
        PdfMultiPageSelectionResult typedSplit = await Qpdf.SelectPagesAsync(
            typedSplitHost,
            [new PdfPageSource(QpdfPath.From("inputs/in.pdf"), new PdfPageSelectionOptions(1)),
             new PdfPageSource(QpdfPath.From("inputs/in.pdf"), new PdfPageSelectionOptions(2))],
            QpdfPath.From("outputs/typed-split.pdf"),
            new PdfPageMergeOptions { SplitPages = 1 },
            CancellationToken.None);
        Check(typedSplit.PageCount == 2 && typedSplit.OutputPaths.Count == 2, "typed split counts");
        await CheckOutputAsync(typedSplitHost, "outputs/typed-split-1.pdf", "First");
        Console.WriteLine("PASS typed split publication (merge-options SplitPages)");

        InMemoryQpdfHost replaceHost = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult replaced = await Qpdf.ExecuteAsync(
            replaceHost,
            new QpdfCommandInvocation(["inputs/in.pdf", "--pages", ".", "1", "--replace-input"]),
            CancellationToken.None);
        Check(replaced.Status == QpdfExecutionStatus.Completed && replaced.ExitCode == QpdfExitCode.Success, "replace execution");
        Console.WriteLine("PASS in-place replacement (--replace-input)");
        InMemoryQpdfHost mergeArgvHost = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult mergeArgv = await Qpdf.ExecuteAsync(
            mergeArgvHost,
            new QpdfCommandInvocation(["inputs/in.pdf", "--pages", ".", "2", ".", "1", "--collate", "--", "outputs/argv-merge.pdf"]),
            CancellationToken.None);
        Check(mergeArgv.Status == QpdfExecutionStatus.Completed && mergeArgv.ExitCode == QpdfExitCode.Success, "argv merge execution");
        Console.WriteLine("PASS argv multi-file merge (--pages/--collate)");

        InMemoryQpdfHost cryptoHost = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult crypto = await Qpdf.ExecuteAsync(
            cryptoHost,
            new QpdfCommandInvocation(["inputs/in.pdf", "--pages", ".", "1", "--password=s3cret", "--", "outputs/x.pdf"]),
            CancellationToken.None);
        Check(crypto.Status == QpdfExecutionStatus.Failed && crypto.ExitCode == QpdfExitCode.Error, "crypto rejection");
        Check(crypto.Diagnostics.Count == 1 && crypto.Diagnostics[0].Code == QpdfDiagnosticCode.UnsupportedCryptoOperation, "crypto diagnostic code");
        Check(!crypto.Diagnostics[0].Message.Contains("s3cret", StringComparison.Ordinal), "no secret echo");
        Console.WriteLine("PASS crypto rejection without secret echo");

        InMemoryQpdfHost qdfHost = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult qdf = await Qpdf.ExecuteAsync(
            qdfHost,
            new QpdfCommandInvocation(["inputs/in.pdf", "--pages", ".", "1", "--qdf", "--", "outputs/qdf.pdf"]),
            CancellationToken.None);
        Check(qdf.Status == QpdfExecutionStatus.Completed && qdf.ExitCode == QpdfExitCode.Success, "qdf execution");
        byte[] qdfBytes = Published(qdfHost, "outputs/qdf.pdf");
        InMemoryQpdfHost fixHost = NewHost("inputs/qdf.pdf", qdfBytes);
        QpdfExecutionResult repaired = await Qpdf.FixQdfAsync(
            fixHost,
            QpdfPath.From("inputs/qdf.pdf"),
            QpdfPath.From("outputs/fixed.pdf"),
            CancellationToken.None);
        Check(repaired.Status == QpdfExecutionStatus.Completed && repaired.ExitCode == QpdfExitCode.Success, "fix-qdf execution");
        Check(Published(fixHost, "outputs/fixed.pdf").AsSpan().SequenceEqual(qdfBytes), "fix-qdf identity on pristine QDF");
        Console.WriteLine("PASS fix-qdf repair of QDF output");

        byte[] flateInput = Encoding.ASCII.GetBytes(new string('x', 1000));
        InMemoryQpdfHost flateHost = NewHost("inputs/flate.bin", flateInput);
        QpdfExecutionResult deflated = await Qpdf.RunZlibFlateAsync(
            flateHost,
            QpdfPath.From("inputs/flate.bin"),
            QpdfPath.From("outputs/flate.z"),
            new QpdfZlibFlateOptions(QpdfZlibFlateMode.Compress, 9),
            CancellationToken.None);
        Check(deflated.Status == QpdfExecutionStatus.Completed && deflated.ExitCode == QpdfExitCode.Success, "flate compress execution");
        InMemoryQpdfHost inflateHost = NewHost("inputs/flate.z", Published(flateHost, "outputs/flate.z"));
        QpdfExecutionResult inflated = await Qpdf.RunZlibFlateAsync(
            inflateHost,
            QpdfPath.From("inputs/flate.z"),
            QpdfPath.From("outputs/flate.bin"),
            new QpdfZlibFlateOptions(QpdfZlibFlateMode.Uncompress, null),
            CancellationToken.None);
        Check(inflated.Status == QpdfExecutionStatus.Completed && inflated.ExitCode == QpdfExitCode.Success, "flate uncompress execution");
        Check(Published(inflateHost, "outputs/flate.bin").AsSpan().SequenceEqual(flateInput), "flate round trip");
        Console.WriteLine("PASS zlib-flate companion round trip");

        InMemoryQpdfHost linHost = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult lin = await Qpdf.ExecuteAsync(
            linHost,
            new QpdfCommandInvocation(["inputs/in.pdf", "--pages", ".", "1", ".", "2", "--linearize", "--", "outputs/lin.pdf"]),
            CancellationToken.None);
        Check(lin.Status == QpdfExecutionStatus.Completed && lin.ExitCode == QpdfExitCode.Success, "linearize execution");
        byte[] linBytes = Published(linHost, "outputs/lin.pdf");
        InMemoryQpdfHost linCheckHost = NewHost("inputs/lin.pdf", linBytes);
        QpdfCheckResult linCheck = await Qpdf.CheckAsync(linCheckHost, QpdfPath.From("inputs/lin.pdf"), CancellationToken.None);
        Check(linCheck.Passed, "linearized check");
        Console.WriteLine("PASS linearized output with valid hints (--linearize)");
        InMemoryQpdfHost linPass1Host = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult linPass1 = await Qpdf.ExecuteAsync(
            linPass1Host,
            new QpdfCommandInvocation(["inputs/in.pdf", "--pages", ".", "1", ".", "2", "--linearize", "--linearize-pass1=outputs/p1.bin", "--", "outputs/lin2.pdf"]),
            CancellationToken.None);
        Check(linPass1.Status == QpdfExecutionStatus.Completed && linPass1.ExitCode == QpdfExitCode.Success, "linearize-pass1 execution");
        Check(Published(linPass1Host, "outputs/p1.bin").Length < Published(linPass1Host, "outputs/lin2.pdf").Length, "pass1 shorter than final");
        Console.WriteLine("PASS first-pass capture (--linearize-pass1)");
        byte[] outlined = OutlinedTwoPagePdf();
        InMemoryQpdfHost linOlHost = NewHost("inputs/ol.pdf", outlined);
        QpdfExecutionResult linOl = await Qpdf.ExecuteAsync(
            linOlHost,
            new QpdfCommandInvocation(["inputs/ol.pdf", "--pages", ".", "1", ".", "2", "--linearize", "--", "outputs/ol.pdf"]),
            CancellationToken.None);
        Check(linOl.Status == QpdfExecutionStatus.Completed && linOl.ExitCode == QpdfExitCode.Success, "outlined linearize execution");
        byte[] linOlBytes = Published(linOlHost, "outputs/ol.pdf");
        Check(Encoding.ASCII.GetString(linOlBytes).Contains("/Outlines"), "outlines preserved");
        InMemoryQpdfHost linOlCheckHost = NewHost("inputs/ol.pdf", linOlBytes);
        QpdfCheckResult linOlCheck = await Qpdf.CheckAsync(linOlCheckHost, QpdfPath.From("inputs/ol.pdf"), CancellationToken.None);
        Check(linOlCheck.Passed, "outlined linearized check");
        Console.WriteLine("PASS outlined linearized output (--linearize with outlines)");

        InMemoryQpdfHost linReportHost = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult linReport = await Qpdf.ExecuteAsync(
            linReportHost,
            new QpdfCommandInvocation(["inputs/in.pdf", "--check-linearization"]),
            CancellationToken.None);
        Check(linReport.Status == QpdfExecutionStatus.Completed && linReport.ExitCode == QpdfExitCode.Success, "check-linearization execution");
        Check(linReport.Diagnostics.Count == 1 && linReport.Diagnostics[0].Code == QpdfDiagnosticCode.LinearizationReport, "check-linearization report code");
        Console.WriteLine("PASS linearization inspection (--check-linearization)");
        QpdfExecutionResult linShow = await Qpdf.ExecuteAsync(
            linReportHost,
            new QpdfCommandInvocation(["inputs/in.pdf", "--show-linearization"]),
            CancellationToken.None);
        Check(linShow.Status == QpdfExecutionStatus.Completed && linShow.ExitCode == QpdfExitCode.Success, "show-linearization execution");
        Console.WriteLine("PASS linearization display (--show-linearization)");
    }

    private static InMemoryQpdfHost NewHost(string logical, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(logical), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] Published(InMemoryQpdfHost host, string logical)
    {
        if (!host.TryGetOutput(QpdfPath.From(logical), out byte[] bytes))
        {
            throw new InvalidOperationException($"Output was not published: {logical}.");
        }

        return bytes;
    }

    private static Task CheckOutputAsync(InMemoryQpdfHost host, string output, string marker)
    {
        return CheckOutputAsync(host, output, marker, 1);
    }

    private static async Task CheckOutputAsync(InMemoryQpdfHost host, string output, string marker, int pageCount)
    {
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, Published(host, output), QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
        Check(document.PageCount == pageCount, "output page count");
        bool found = false;
        for (int number = 1; number <= 12 && !found; number++)
        {
            try
            {
                if (document.GetObject(number) is PdfStream stream
                    && Encoding.ASCII.GetString(stream.Data).Contains(marker, StringComparison.Ordinal))
                {
                    found = true;
                }
            }
            catch (QpdfParseException)
            {
            }
        }

    }


    private static async Task CheckMultiOutputAsync(InMemoryQpdfHost host, string output, string[] markers)
    {
        QpdfPath reread = QpdfPath.From("inputs/reread-multi.pdf");
        host.AddInput(reread, Published(host, output), QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
        Check(document.PageCount == markers.Length, "multi output page count");
        List<string> ordered = new();
        for (int number = 1; number <= 64; number++)
        {
            QpdfObject candidate;
            try
            {
                candidate = document.GetObject(number);
            }
            catch (QpdfParseException)
            {
                continue;
            }

            if (candidate is not PdfDictionary page)
            {
                continue;
            }

            if (!page.TryGet(new PdfName(Encoding.ASCII.GetBytes("Type")), out QpdfObject typeRaw)
                || typeRaw is not PdfName typeName
                || typeName.DisplayText != "Page")
            {
                continue;
            }

            if (!page.TryGet(new PdfName(Encoding.ASCII.GetBytes("Contents")), out QpdfObject contentsRaw)
                || contentsRaw is not PdfReference contentsRef
                || document.GetObject(contentsRef.ObjectNumber) is not PdfStream contents)
            {
                throw new InvalidOperationException("Multi output page has no contents stream.");
            }

            ordered.Add(Encoding.ASCII.GetString(contents.Data));
        }

        Check(ordered.Count == markers.Length, "multi output page count");
        for (int i = 0; i < markers.Length; i++)
        {
            Check(ordered[i].Contains(markers[i], StringComparison.Ordinal), "multi output marker " + i.ToString());
        }
    }

    private static void Check(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Check failed: " + what + ".");
        }
    }

    private static byte[] BreakStartXref(byte[] input)
    {
        byte[] marker = Encoding.ASCII.GetBytes("startxref\n");
        int at = -1;
        for (int i = 0; i + marker.Length <= input.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < marker.Length; j++)
            {
                if (input[i + j] != marker[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                at = i;
            }
        }

        if (at < 0)
        {
            throw new InvalidOperationException("Test input has no startxref.");
        }

        byte[] copy = (byte[])input.Clone();
        for (int i = at + marker.Length; i < copy.Length && copy[i] >= (byte)'0' && copy[i] <= (byte)'9'; i++)
        {
            copy[i] = (byte)'9';
        }

        return copy;
    }

    private static byte[] TwoPagePdf()
    {
        return TwoPagePdf(null);
    }

    private static byte[] TwoPagePdf(string? auditBody)
    {
        return TwoPagePdfWithAuditObjects(auditBody is null ? [] : [auditBody]);
    }

    private static byte[] TwoPagePdfWithAuditObjects(IReadOnlyList<string> auditBodies)
    {
        Encoding ascii = Encoding.ASCII;
        byte[] first = ascii.GetBytes("BT /F1 24 Tf 100 700 Td (First) Tj ET");
        byte[] second = ascii.GetBytes("BT /F1 24 Tf 100 700 Td (Second) Tj ET");
        List<byte[]> parts = [ascii.GetBytes("%PDF-1.4\n")];
        Dictionary<int, int> offsets = new();
        void AddObject(int number, byte[] body)
        {
            int offset = 0;
            foreach (byte[] part in parts)
            {
                offset += part.Length;
            }

            offsets[number] = offset;
            parts.Add(ascii.GetBytes(number + " 0 obj\n"));
            parts.Add(body);
            parts.Add(ascii.GetBytes("\nendobj\n"));
        }

        AddObject(1, ascii.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"));
        AddObject(2, ascii.GetBytes("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"));
        string audit = auditBodies.Count == 0 ? "" : " /Audit 8 0 R";
        AddObject(3, ascii.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << /Font << /F1 7 0 R >> >>" + audit + " >>"));
        AddObject(4, ascii.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>"));
        AddObject(5, Concat(ascii.GetBytes("<< /Length " + first.Length + " >>\nstream\n"), first, ascii.GetBytes("\nendstream")));
        AddObject(6, Concat(ascii.GetBytes("<< /Length " + second.Length + " >>\nstream\n"), second, ascii.GetBytes("\nendstream")));
        AddObject(7, ascii.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        for (int i = 0; i < auditBodies.Count; i++) { AddObject(8 + i, ascii.GetBytes(auditBodies[i])); }
        int xref = 0;
        foreach (byte[] part in parts)
        {
            xref += part.Length;
        }

        int size = 8 + auditBodies.Count;
        parts.Add(ascii.GetBytes("xref\n0 " + size + "\n"));
        parts.Add(ascii.GetBytes("0000000000 65535 f \n"));
        for (int n = 1; n < size; n++)
        {
            parts.Add(ascii.GetBytes(offsets[n].ToString("D10") + " 00000 n \n"));
        }

        parts.Add(ascii.GetBytes("trailer\n<< /Size " + size + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n"));
        using MemoryStream output = new();
        foreach (byte[] part in parts)
        {
            output.Write(part, 0, part.Length);
        }

        return output.ToArray();
    }

    private static byte[] OutlinedTwoPagePdf()
    {
        Encoding ascii = Encoding.ASCII;
        byte[] first = ascii.GetBytes("BT /F1 24 Tf 100 700 Td (First) Tj ET");
        byte[] second = ascii.GetBytes("BT /F1 24 Tf 100 700 Td (Second) Tj ET");
        List<byte[]> parts = [ascii.GetBytes("%PDF-1.4\n")];
        Dictionary<int, int> offsets = new();
        void AddObject(int number, byte[] body)
        {
            int offset = 0;
            foreach (byte[] part in parts)
            {
                offset += part.Length;
            }

            offsets[number] = offset;
            parts.Add(ascii.GetBytes(number + " 0 obj\n"));
            parts.Add(body);
            parts.Add(ascii.GetBytes("\nendobj\n"));
        }

        AddObject(1, ascii.GetBytes("<< /Type /Catalog /Pages 2 0 R /Outlines 8 0 R >>"));
        AddObject(2, ascii.GetBytes("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"));
        AddObject(3, ascii.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << /Font << /F1 7 0 R >> >> >>"));
        AddObject(4, ascii.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>"));
        AddObject(5, Concat(ascii.GetBytes("<< /Length " + first.Length + " >>\nstream\n"), first, ascii.GetBytes("\nendstream")));
        AddObject(6, Concat(ascii.GetBytes("<< /Length " + second.Length + " >>\nstream\n"), second, ascii.GetBytes("\nendstream")));
        AddObject(7, ascii.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        AddObject(8, ascii.GetBytes("<< /Type /Outlines /First 9 0 R /Last 10 0 R /Count 2 >>"));
        AddObject(9, ascii.GetBytes("<< /Title (Ch1) /Parent 8 0 R /Next 10 0 R /Dest [3 0 R /Fit] >>"));
        AddObject(10, ascii.GetBytes("<< /Title (Ch2) /Parent 8 0 R /Prev 9 0 R /Dest [4 0 R /Fit] >>"));
        int xref = 0;
        foreach (byte[] part in parts)
        {
            xref += part.Length;
        }

        parts.Add(ascii.GetBytes("xref\n0 11\n"));
        parts.Add(ascii.GetBytes("0000000000 65535 f \n"));
        for (int n = 1; n <= 10; n++)
        {
            parts.Add(ascii.GetBytes(offsets[n].ToString("D10") + " 00000 n \n"));
        }

        parts.Add(ascii.GetBytes("trailer\n<< /Size 11 /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n"));
        using MemoryStream output = new();
        foreach (byte[] part in parts)
        {
            output.Write(part, 0, part.Length);
        }

        return output.ToArray();
    }

    private static byte[] Concat(byte[] first, byte[] second, byte[] third)
    {
        byte[] result = new byte[first.Length + second.Length + third.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        Buffer.BlockCopy(third, 0, result, first.Length + second.Length, third.Length);
        return result;
    }
}
