using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentNameOrderTests
{
    private static readonly string[] Keys = ["aé", "z", "a", "ä", "b漢", "c", "aa"];

    private static QpdfObject Get(QpdfDocument document, PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    }

    private static byte[] KeyBytes(string key) => key.All(character => character < 128)
        ? Encoding.ASCII.GetBytes(key) : [0xfe, 0xff, .. Encoding.BigEndianUnicode.GetBytes(key)];

    [Theory]
    [InlineData("typed", "add")]
    [InlineData("argv", "add")]
    [InlineData("job", "add")]
    [InlineData("typed", "copy")]
    [InlineData("argv", "copy")]
    [InlineData("job", "copy")]
    [InlineData("typed", "remove")]
    [InlineData("argv", "remove")]
    [InlineData("job", "remove")]
    [InlineData("argv", "rewrite")]
    [InlineData("job", "rewrite")]
    public async Task MixedEncodingTreesSupportByteSearchWithoutChangingListingOrPayloads(string route, string operation)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        QpdfAttachmentSpec[] attachments = new QpdfAttachmentSpec[Keys.Length];
        for (int index = 0; index < Keys.Length; index++)
        {
            string payload = "payload-" + index;
            host.AddInput(QpdfPath.From(payload), [(byte)index], QpdfInputOwnership.Copy);
            attachments[index] = new(payload, Keys[index], "name", null, null, null, null, false);
        }
        await Qpdf.AddAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("source"), attachments, CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("source"), out byte[] source));
        host.AddInput(QpdfPath.From("source"), source, QpdfInputOwnership.Copy);
        List<string> argv = [operation == "add" ? "input" : "source", "--stream-data=uncompress"];
        Dictionary<string, object> job = new() { ["inputFile"] = argv[0], ["outputFile"] = "output", ["streamData"] = "uncompress" };
        if (operation == "add")
        {
            foreach (QpdfAttachmentSpec item in attachments) { argv.AddRange(["--add-attachment", item.File, "--key=" + item.Key, "--"]); }
            job["addAttachment"] = attachments.Select(item => new { file = item.File, key = item.Key }).ToArray();
        }
        else if (operation == "copy")
        {
            argv[0] = "input"; job["inputFile"] = "input";
            argv.AddRange(["--copy-attachments-from", "source", "--prefix=copy-", "--"]);
            job["copyAttachmentsFrom"] = new[] { new { file = "source", prefix = "copy-" } };
        }
        else if (operation == "remove") { argv.Add("--remove-attachment=a"); job["removeAttachment"] = new[] { "a" }; }
        argv.Add("output");
        if (route == "typed")
        {
            if (operation == "add") { await Qpdf.AddAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), attachments, CancellationToken.None); }
            else if (operation == "copy") { await Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), [new(QpdfPath.From("source"), "copy-")], CancellationToken.None); }
            else { await Qpdf.RemoveAttachmentsAsync(host, QpdfPath.From("source"), QpdfPath.From("output"), ["a"], CancellationToken.None); }
        }
        else
        {
            QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Copy);
        PdfArray escaped;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            PdfDictionary names = Assert.IsType<PdfDictionary>(Get(document, catalog, "Names"));
            PdfDictionary tree = Assert.IsType<PdfDictionary>(Get(document, names, "EmbeddedFiles"));
            escaped = Assert.IsType<PdfArray>(Get(document, tree, "Names"));
            for (int index = 2; index < escaped.Count; index += 2)
            { Assert.True(Assert.IsType<PdfByteString>(escaped[index - 2]).Value.AsSpan().SequenceCompareTo(Assert.IsType<PdfByteString>(escaped[index]).Value) < 0); }
            for (int index = 0; index < Keys.Length; index++)
            {
                if (operation == "remove" && Keys[index] == "a") { continue; }
                byte[] key = KeyBytes((operation == "copy" ? "copy-" : "") + Keys[index]);
                int first = 0, last = escaped.Count / 2 - 1, found = -1;
                while (first <= last)
                {
                    int middle = (first + last) / 2;
                    int order = Assert.IsType<PdfByteString>(escaped[2 * middle]).Value.AsSpan().SequenceCompareTo(key);
                    if (order == 0) { found = middle; break; }
                    if (order < 0) { first = middle + 1; } else { last = middle - 1; }
                }
                Assert.True(found >= 0);
                PdfReference link = Assert.IsType<PdfReference>(escaped[2 * found + 1]);
                PdfDictionary spec = Assert.IsType<PdfDictionary>(document.GetObject(link.ObjectNumber));
                PdfDictionary ef = Assert.IsType<PdfDictionary>(Get(document, spec, "EF"));
                PdfStream stream = Assert.IsType<PdfStream>(Get(document, ef, "F"));
                Assert.Equal(new[] { (byte)index }, stream.Data);
            }
        }
        var listing = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("readback"), CancellationToken.None);
        string[] expected = Keys.Where(key => operation != "remove" || key != "a").Select(key => (operation == "copy" ? "copy-" : "") + key).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, listing.Select(item => item.Key));
        host.Dispose();
        Assert.Equal(expected.Length * 2, escaped.Count); Assert.Equal(0L, host.OpenLeaseCount);
    }
}
