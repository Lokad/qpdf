using System.Security.Cryptography;
using System.Text;
using UglyToad.PdfPig;

namespace Lokad.Qpdf.Probe;

internal sealed record IndependentResult(
    string InputSha256,
    string HeaderVersion,
    int Npages);

internal static class IndependentReader
{
    internal static void WritePageText(string inputPdf)
    {
        using PdfDocument document = PdfDocument.Open(inputPdf);
        var pages = Enumerable.Range(1, document.NumberOfPages)
            .Select(number => document.GetPage(number).Text).ToArray();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(pages));
    }

    internal static IndependentResult Read(string inputPdf, bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(inputPdf))
        {
            throw new ProbeException("Missing input PDF path. Supply --input <pdf>.", 1);
        }

        if (!File.Exists(inputPdf))
        {
            throw new ProbeException($"Input PDF not found: {inputPdf}.", 1);
        }

        string sha = ComputeSha256(inputPdf);
        string headerVersion = ReadHeaderVersion(inputPdf);
        int npages = ReadPageCount(inputPdf, allowEmpty);
        return new IndependentResult(sha, headerVersion, npages);
    }

    private static string ReadHeaderVersion(string path)
    {
        byte[] prefix = new byte[16];
        using (FileStream stream = File.OpenRead(path))
        {
            int read = stream.Read(prefix, 0, prefix.Length);
            if (read < 8)
            {
                throw new ProbeException($"Input is too short to be a PDF: {path} ({read} bytes).", 1);
            }
        }

        string text = Encoding.ASCII.GetString(prefix);
        const string marker = "%PDF-";
        if (!text.StartsWith(marker, StringComparison.Ordinal))
        {
            throw new ProbeException($"Input does not start with %PDF-: {path}.", 1);
        }

        int end = text.IndexOf('\n');
        if (end < 0)
        {
            end = text.Length;
        }

        string header = text[..end].Trim();
        string version = header[marker.Length..].Trim();
        if (version.Length == 0 || version.Length > 8)
        {
            throw new ProbeException($"Unrecognized PDF header version in '{header}'.", 1);
        }

        return version;
    }

    private static int ReadPageCount(string path, bool allowEmpty)
    {
        try
        {
            using PdfDocument document = PdfDocument.Open(path);
            int count = document.NumberOfPages;
            if (count < 0 || (count == 0 && !allowEmpty))
            {
                throw new ProbeException($"Independent reader reports no pages: {path}.", 1);
            }

            return count;
        }
        catch (ProbeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProbeException($"Independent reader (PdfPig 0.1.16) failed to open '{path}': {ex.GetType().Name}: {ex.Message}", 1);
        }
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
