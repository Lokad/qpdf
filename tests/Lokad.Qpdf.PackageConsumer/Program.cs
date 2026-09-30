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
        Check(typed.PageCount == 1 && typed.SourcePageNumber == 2, "typed selection result");
        await CheckOutputAsync(typedHost, "outputs/typed.pdf", "Second");
        Console.WriteLine("PASS typed selection (page 2, opaque streams)");

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

        InMemoryQpdfHost cryptoHost = NewHost("inputs/in.pdf", input);
        QpdfExecutionResult crypto = await Qpdf.ExecuteAsync(
            cryptoHost,
            new QpdfCommandInvocation(["inputs/in.pdf", "--pages", ".", "1", "--password=s3cret", "--", "outputs/x.pdf"]),
            CancellationToken.None);
        Check(crypto.Status == QpdfExecutionStatus.Failed && crypto.ExitCode == QpdfExitCode.Error, "crypto rejection");
        Check(crypto.Diagnostics.Count == 1 && crypto.Diagnostics[0].Code == QpdfDiagnosticCode.UnsupportedCryptoOperation, "crypto diagnostic code");
        Check(!crypto.Diagnostics[0].Message.Contains("s3cret", StringComparison.Ordinal), "no secret echo");
        Console.WriteLine("PASS crypto rejection without secret echo");
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

    private static async Task CheckOutputAsync(InMemoryQpdfHost host, string output, string marker)
    {
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, Published(host, output), QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
        Check(document.PageCount == 1, "output page count");
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

        Check(found, "output stream marker " + marker);
    }

    private static void Check(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Check failed: " + what + ".");
        }
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
