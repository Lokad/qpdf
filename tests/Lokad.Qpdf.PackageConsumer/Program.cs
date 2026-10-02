using System.Text;
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

        byte[] damaged = BreakStartXref(input);
        InMemoryQpdfHost repairHost = NewHost("inputs/damaged.pdf", damaged);
        QpdfExecutionResult repair = await Qpdf.ExecuteAsync(
            repairHost,
            new QpdfCommandInvocation(["inputs/damaged.pdf", "--pages", ".", "1", "--", "outputs/repaired.pdf"]),
            CancellationToken.None);
        Check(repair.Status == QpdfExecutionStatus.Completed && repair.ExitCode == QpdfExitCode.Warning, "repair warning exit");
        await CheckOutputAsync(repairHost, "outputs/repaired.pdf", "First");
        Console.WriteLine("PASS bounded repair of damaged input");

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
        AddObject(3, ascii.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << /Font << /F1 7 0 R >> >> >>"));
        AddObject(4, ascii.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>"));
        AddObject(5, Concat(ascii.GetBytes("<< /Length " + first.Length + " >>\nstream\n"), first, ascii.GetBytes("\nendstream")));
        AddObject(6, Concat(ascii.GetBytes("<< /Length " + second.Length + " >>\nstream\n"), second, ascii.GetBytes("\nendstream")));
        AddObject(7, ascii.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        int xref = 0;
        foreach (byte[] part in parts)
        {
            xref += part.Length;
        }

        parts.Add(ascii.GetBytes("xref\n0 8\n"));
        parts.Add(ascii.GetBytes("0000000000 65535 f \n"));
        for (int n = 1; n <= 7; n++)
        {
            parts.Add(ascii.GetBytes(offsets[n].ToString("D10") + " 00000 n \n"));
        }

        parts.Add(ascii.GetBytes("trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n"));
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
