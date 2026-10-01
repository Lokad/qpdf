using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentTests
{
    private static readonly byte[] PageData = Encoding.ASCII.GetBytes("BT (Page) Tj ET");

    private static readonly byte[] NoteData = Encoding.ASCII.GetBytes("Attachment notes body.");

    private static readonly byte[] BlobRaw = Encoding.ASCII.GetBytes("Compressed attachment payload.");

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] FlateBytes(byte[] raw)
    {
        using MemoryStream output = new();
        using (ZLibStream compressor = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
    }

    private static string ChecksumOf(byte[] data)
    {
        return Convert.ToHexStringLower(MD5.HashData(data));
    }

    private static byte[] BuildPdf(string catalogExtra, string trailerExtra, List<(string Dict, byte[]? StreamData)> extras)
    {
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R" + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            "<< /Length " + PageData.Length.ToString() + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        foreach ((string dict, byte[]? _) in extras)
        {
            bodies.Add(dict);
        }

        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[]? streamData = null;
            if (number == 4)
            {
                streamData = PageData;
            }
            else if (number > 5)
            {
                streamData = extras[number - 6].StreamData;
            }

            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " 0 obj\n" + bodies[i] + "\n");
            if (streamData is null)
            {
                byte[] close = Encoding.ASCII.GetBytes("endobj\n");
                offsets.Add(position);
                position += open.Length + close.Length;
                byte[] plain = new byte[open.Length + close.Length];
                Buffer.BlockCopy(open, 0, plain, 0, open.Length);
                Buffer.BlockCopy(close, 0, plain, open.Length, close.Length);
                parts.Add(plain);
                continue;
            }

            byte[] streamOpen = Encoding.ASCII.GetBytes("stream\n");
            byte[] streamClose = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
            offsets.Add(position);
            position += open.Length + streamOpen.Length + streamData.Length + streamClose.Length;
            byte[] payload = new byte[open.Length + streamOpen.Length + streamData.Length + streamClose.Length];
            int at = 0;
            Buffer.BlockCopy(open, 0, payload, at, open.Length);
            at += open.Length;
            Buffer.BlockCopy(streamOpen, 0, payload, at, streamOpen.Length);
            at += streamOpen.Length;
            Buffer.BlockCopy(streamData, 0, payload, at, streamData.Length);
            at += streamData.Length;
            Buffer.BlockCopy(streamClose, 0, payload, at, streamClose.Length);
            parts.Add(payload);
        }

        int size = bodies.Count + 1;
        StringBuilder xref = new();
        xref.Append("xref\n0 " + size.ToString() + "\n");
        xref.Append("0000000000 65535 f \n");
        foreach (long offset in offsets)
        {
            xref.Append(offset.ToString("D10") + " 00000 n \n");
        }

        xref.Append("trailer\n<< /Size " + size.ToString() + " /Root 1 0 R" + trailerExtra + " >>\n");
        xref.Append("startxref\n" + position.ToString() + "\n%%EOF\n");
        byte[] tail = Encoding.ASCII.GetBytes(xref.ToString());
        parts.Add(tail);
        int total = 0;
        foreach (byte[] part in parts)
        {
            total += part.Length;
        }

        byte[] output = new byte[total];
        int cursor = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, output, cursor, part.Length);
            cursor += part.Length;
        }

        return output;
    }

    private static byte[] AttachmentFixture()
    {
        byte[] blob = FlateBytes(BlobRaw);
        return BuildPdf(
            " /Names 6 0 R",
            string.Empty,
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /EmbeddedFiles 7 0 R >>", null),
                ("<< /Names [(data.bin) 9 0 R (notes.txt) 8 0 R] >>", null),
                ("<< /Type /Filespec /F (notes.txt) /UF (notes.txt) /Desc (Test notes) /EF << /F 10 0 R >> >>", null),
                ("<< /Type /Filespec /F (data.bin) /EF << /F 11 0 R >> >>", null),
                ("<< /Type /EmbeddedFile /Length " + NoteData.Length.ToString() + " /Subtype /text#2Fplain /Params << /Size " + NoteData.Length.ToString() + " /CreationDate (D:20240101000000Z) /ModDate (D:20240202000000Z) /CheckSum <" + ChecksumOf(NoteData).ToUpperInvariant() + "> >> >>", NoteData),
                ("<< /Type /EmbeddedFile /Length " + blob.Length.ToString() + " /Filter /FlateDecode /Params << /Size " + BlobRaw.Length.ToString() + " >> >>", blob),
            });
    }

    private static byte[] KidsFixture()
    {
        return BuildPdf(
            " /Names 6 0 R",
            string.Empty,
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /EmbeddedFiles 7 0 R >>", null),
                ("<< /Names [(a.txt) 8 0 R] /Kids [10 0 R] >>", null),
                ("<< /Type /Filespec /F (a.txt) /EF << /F 9 0 R >> >>", null),
                ("<< /Type /EmbeddedFile /Length " + NoteData.Length.ToString() + " >>", NoteData),
                ("<< /Names [(b.txt) 11 0 R] >>", null),
                ("<< /Type /Filespec /F (b.txt) /EF << /F 12 0 R >> >>", null),
                ("<< /Type /EmbeddedFile /Length " + BlobRaw.Length.ToString() + " >>", BlobRaw),
            });
    }

    private static byte[] UnicodeKeyFixture()
    {
        return BuildPdf(
            " /Names 6 0 R",
            string.Empty,
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /EmbeddedFiles 7 0 R >>", null),
                ("<< /Names [<FEFF00E9002E007400780074> 8 0 R] >>", null),
                ("<< /Type /Filespec /F <FEFF00E9002E007400780074> /EF << /F 9 0 R >> >>", null),
                ("<< /Type /EmbeddedFile /Length " + NoteData.Length.ToString() + " >>", NoteData),
            });
    }

    private static byte[] DanglingEntryFixture()
    {
        return BuildPdf(
            " /Names 6 0 R",
            string.Empty,
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /EmbeddedFiles 7 0 R >>", null),
                ("<< /Names [(ghost.txt) 8 0 R (notes.txt) 9 0 R] >>", null),
                ("<< /Type /Filespec /F (ghost.txt) >>", null),
                ("<< /Type /Filespec /F (notes.txt) /EF << /F 10 0 R >> >>", null),
                ("<< /Type /EmbeddedFile /Length " + NoteData.Length.ToString() + " >>", NoteData),
            });
    }

    private static byte[] BrokenTreeFixture()
    {
        return BuildPdf(
            " /Names 6 0 R",
            string.Empty,
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /EmbeddedFiles 7 0 R >>", null),
                ("<< /Names [(lonely.txt)] >>", null),
            });
    }

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        return Assert.Single(result.Diagnostics);
    }

    private static List<string> Messages(QpdfExecutionResult result)
    {
        List<string> messages = new(result.Diagnostics.Count);
        foreach (QpdfDiagnostic diagnostic in result.Diagnostics)
        {
            messages.Add(diagnostic.Message);
        }

        return messages;
    }

    [Fact]
    public async Task TypedListReturnsAttachments()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", AttachmentFixture());
        IReadOnlyList<QpdfAttachmentInfo> infos = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        Assert.Equal(2, infos.Count);
        Assert.Equal("data.bin", infos[0].Key);
        Assert.Equal("data.bin", infos[0].FileName);
        Assert.Equal(string.Empty, infos[0].Description);
        Assert.Equal(BlobRaw.Length, infos[0].Size);
        Assert.Equal("notes.txt", infos[1].Key);
        Assert.Equal("notes.txt", infos[1].FileName);
        Assert.Equal("Test notes", infos[1].Description);
        Assert.Equal("text/plain", infos[1].MimeType);
        Assert.Equal("D:20240101000000Z", infos[1].CreationDate);
        Assert.Equal("D:20240202000000Z", infos[1].ModDate);
        Assert.Equal(ChecksumOf(NoteData), infos[1].Checksum);
        Assert.Equal(NoteData.Length, infos[1].Size);
        Assert.Equal(10, infos[1].StreamObjectNumber);
    }

    [Fact]
    public async Task TypedListEmptyReturnsNone()
    {
        byte[] bytes = BuildPdf(string.Empty, string.Empty, new List<(string Dict, byte[]? StreamData)>());
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        IReadOnlyList<QpdfAttachmentInfo> infos = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        Assert.Empty(infos);
    }

    [Fact]
    public async Task TypedExtractReturnsRawAndDecodedBytes()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", AttachmentFixture());
        QpdfAttachmentExtraction raw = await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("inputs/a.pdf"), "notes.txt", QpdfPath.From("outputs/note.bin"), CancellationToken.None);
        Assert.Equal("notes.txt", raw.Key);
        Assert.Equal(NoteData.Length, raw.ByteCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/note.bin"), out byte[] note));
        Assert.Equal(NoteData, note);
        QpdfAttachmentExtraction decoded = await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("inputs/a.pdf"), "data.bin", QpdfPath.From("outputs/data.bin"), CancellationToken.None);
        Assert.Equal(BlobRaw.Length, decoded.ByteCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/data.bin"), out byte[] data));
        Assert.Equal(BlobRaw, data);
    }

    [Fact]
    public async Task TypedExtractUnknownKeyThrows()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", AttachmentFixture());
        await Assert.ThrowsAsync<QpdfParseException>(
            () => Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("inputs/a.pdf"), "missing.txt", QpdfPath.From("outputs/x.bin"), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task TypedExtractEmptyKeyThrows()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", AttachmentFixture());
        await Assert.ThrowsAsync<ArgumentException>(
            () => Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("inputs/a.pdf"), string.Empty, QpdfPath.From("outputs/x.bin"), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task KidsTreeListsAllAttachments()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", KidsFixture());
        IReadOnlyList<QpdfAttachmentInfo> infos = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        Assert.Equal(2, infos.Count);
        Assert.Equal("a.txt", infos[0].Key);
        Assert.Equal("b.txt", infos[1].Key);
        QpdfAttachmentExtraction extracted = await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("inputs/a.pdf"), "b.txt", QpdfPath.From("outputs/b.bin"), CancellationToken.None);
        Assert.Equal(BlobRaw.Length, extracted.ByteCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/b.bin"), out byte[] data));
        Assert.Equal(BlobRaw, data);
    }

    [Fact]
    public async Task UnicodeKeysMatchAndList()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", UnicodeKeyFixture());
        IReadOnlyList<QpdfAttachmentInfo> infos = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        Assert.Equal("é.txt", infos[0].Key);
        Assert.Equal("é.txt", infos[0].FileName);
        QpdfAttachmentExtraction extracted = await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("inputs/a.pdf"), "é.txt", QpdfPath.From("outputs/e.bin"), CancellationToken.None);
        Assert.Equal(NoteData.Length, extracted.ByteCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/e.bin"), out byte[] data));
        Assert.Equal(NoteData, data);
    }

    [Fact]
    public async Task ListShowsKeysAndStreams()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", AttachmentFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--list-attachments"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> messages = Messages(result);
        Assert.Contains("data.bin -> 11,0", messages);
        Assert.Contains("notes.txt -> 10,0", messages);
        Assert.Contains("  filename: notes.txt", messages);
        Assert.Contains("  description: Test notes", messages);
        Assert.Contains("  mime type: text/plain", messages);
    }

    [Fact]
    public async Task ListEmptyReportsNone()
    {
        byte[] bytes = BuildPdf(string.Empty, string.Empty, new List<(string Dict, byte[]? StreamData)>());
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--list-attachments"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains("inputs/a.pdf has no embedded files", Messages(result));
    }

    [Fact]
    public async Task ShowExtractsToOutputFile()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", AttachmentFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-attachment=notes.txt", "--", "outputs/note.bin"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/note.bin"), out byte[] note));
        Assert.Equal(NoteData, note);
    }

    [Fact]
    public async Task ShowDecodesStreams()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", AttachmentFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-attachment=data.bin", "--", "outputs/data.bin"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/data.bin"), out byte[] data));
        Assert.Equal(BlobRaw, data);
    }

    [Fact]
    public async Task ShowDecodesDctStream()
    {
        byte[] jpeg;
        using (Image<Rgb24> image = new(8, 6))
        {
            for (int y = 0; y < 6; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    image[x, y] = new Rgb24((byte)((x * 32) & 0xFF), (byte)((y * 32) & 0xFF), 128);
                }
            }

            using MemoryStream buffer = new();
            image.Save(buffer, new JpegEncoder { Quality = 75 });
            jpeg = buffer.ToArray();
        }

        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] expected = DctFilter.Decode(jpeg, QpdfLimits.Default, governor);
        Assert.Equal(144, expected.Length);
        byte[] fixture = BuildPdf(
            " /Names 6 0 R",
            string.Empty,
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /EmbeddedFiles 7 0 R >>", null),
                ("<< /Names [(photo.jpg) 8 0 R] >>", null),
                ("<< /Type /Filespec /F (photo.jpg) /EF << /F 9 0 R >> >>", null),
                ("<< /Type /EmbeddedFile /Length " + jpeg.Length.ToString() + " /Filter /DCTDecode /Params << /Size " + expected.Length.ToString() + " >> >>", jpeg),
            });
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-attachment=photo.jpg", "--", "outputs/photo.bin"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/photo.bin"), out byte[] data));
        Assert.Equal(expected, data);
    }    [Fact]
    public async Task ShowLastKeyWins()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", AttachmentFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-attachment=missing.txt", "--show-attachment=notes.txt", "--", "outputs/note.bin"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/note.bin"), out byte[] note));
        Assert.Equal(NoteData, note);
    }

    [Fact]
    public async Task ShowUnknownKeyFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", AttachmentFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-attachment=missing.txt", "--", "outputs/x.bin"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/x.bin"), out byte[] _));
    }

    [Fact]
    public async Task DanglingEntryWarnsButListsRest()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", DanglingEntryFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--list-attachments"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains("notes.txt -> 10,0", Messages(result));
    }

    [Fact]
    public async Task BrokenTreeFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BrokenTreeFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--list-attachments"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Theory]
    [InlineData("--list-attachments", "--", "outputs/x.pdf")]
    [InlineData("--pages", ".", "1", "--list-attachments", "--", "outputs/a.pdf")]
    [InlineData("--check", "--list-attachments")]
    [InlineData("--show-attachment=notes.txt")]
    [InlineData("--show-attachment=")]
    [InlineData("--show-attachment")]
    public async Task MalformedAttachmentsFail(params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", AttachmentFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _));
    }

    [Fact]
    public async Task ShowOutputAliasingInputFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", AttachmentFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-attachment=notes.txt", "--", "inputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task JobListSucceedsWithoutOutput()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", AttachmentFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host,
            "{\"inputFile\": \"inputs/a.pdf\", \"listAttachments\": \"\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains("notes.txt -> 10,0", Messages(result));
    }

    [Fact]
    public async Task JobShowExtracts()
    {
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", AttachmentFixture());
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            argvHost, new QpdfCommandInvocation(["inputs/a.pdf", "--show-attachment=notes.txt", "--", "outputs/note.bin"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argv.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/note.bin"), out byte[] a));
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", AttachmentFixture());
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/note.bin\", \"showAttachment\": \"notes.txt\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/note.bin"), out byte[] b));
        Assert.Equal(a, b);
        Assert.Equal(NoteData, b);
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showAttachment\": \"notes.txt\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"showAttachment\": \"\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"listAttachments\": \"\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}]}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"listAttachments\": true}")]
    public async Task JobMalformedAttachmentsFail(string jobJson)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", AttachmentFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }
}
