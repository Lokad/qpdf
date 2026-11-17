using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Security;

public sealed class XrefStreamRoleTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output"), Json = QpdfPath.From("json");
    private static readonly QpdfPassword Reader = QpdfPassword.FromText("reader");
    private static readonly byte[] Payload = "private-stream-secret"u8.ToArray(), Note = "private-header-secret"u8.ToArray();
    private static byte[] Native() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r6-xref-private.pdf"));

    private static QpdfObject Get(PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }

    private static PdfDictionary Catalog(QpdfDocument document)
        => Assert.IsType<PdfDictionary>(document.GetObject(Assert.IsType<PdfReference>(Get(document.Engine.Trailer, "Root"))));

    private static void PrivateValue(PdfStream stream)
    {
        Assert.Equal(Note, Assert.IsType<PdfByteString>(Get(stream.Dictionary, "Note")).Value);
        Assert.Equal(Payload, stream.Data);
        Assert.Equal(PdfName.FromAscii("XRef"), Get(stream.Dictionary, "Type"));
    }

    [Fact]
    public async Task NativeEncryptedTaggedGraphValuesDecryptAndSurviveClosing()
    {
        byte[] source = Native(), before = (byte[])source.Clone();
        using InMemoryQpdfHost host = new(); host.AddInput(Input, source, QpdfInputOwnership.Transfer);
        PdfStream stream;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, Reader, QpdfLimits.Default, CancellationToken.None))
        {
            Assert.Equal(1, document.PageCount);
            Assert.False(document.Engine.IsCrossReferenceStream(3));
            stream = Assert.IsType<PdfStream>(document.GetObject(new PdfReference(3, 0)));
            PrivateValue(stream);
        }
        PrivateValue(stream); Assert.Equal(before, source); Assert.Equal(0L, host.OpenLeaseCount);
    }

    // Reframe the recorded native ciphertext. Only physical index framing changes;
    // no managed writer or encryption primitive supplies the expected private bytes.
    internal static byte[] Reframe(string shape)
    {
        byte[] source = Native();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using XRefTable table = XRefTable.Parse(source, QpdfLimits.Default, governor, new(governor), QpdfRecoveryMode.Strict, false);
        using MemoryStream result = new(); result.Write(source.AsSpan(0, (int)table.XrefOffset));
        PdfArray ids = Assert.IsType<PdfArray>(Get(table.Trailer, "ID"));
        string id = "/ID [<" + Convert.ToHexString(Assert.IsType<PdfByteString>(ids[0]).Value)
            + "><" + Convert.ToHexString(Assert.IsType<PdfByteString>(ids[1]).Value) + ">]";
        string trailer = "/Size 7 /Root 1 0 R /Encrypt 5 0 R " + id;
        long section = result.Position;
        byte[] rows = new byte[49]; rows[0] = 0; BinaryPrimitives.WriteUInt16BigEndian(rows.AsSpan(5, 2), 65535);
        for (int number = 1; number <= 6; number++)
        {
            int offset = number * 7; rows[offset] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(rows.AsSpan(offset + 1, 4), checked((uint)(number == 6 ? section : table.Entries[number].First)));
        }
        void Write(string text) => result.Write(Encoding.ASCII.GetBytes(text));
        Write("6 0 obj\n<< /Type /XRef /W [1 4 2] " + trailer + " /Note (clear-xref-header) /Length 49 >>\nstream\n");
        result.Write(rows); Write("\nendstream\nendobj\n");
        long start = section;
        if (shape is "hybrid" or "ignored" or "prev" or "reused")
        {
            long reused = 0;
            if (shape == "reused")
            {
                // R6 uses the file key independently of object number/generation.
                // Reuse the native private object's ciphertext at a new physical slot.
                reused = result.Position; Write("6 1 obj\n");
                int body = (int)table.Entries[3].First + "3 0 obj\n".Length;
                int end = source.AsSpan(body).IndexOf("\nendobj"u8) + body;
                result.Write(source.AsSpan(body, end - body)); Write("\nendobj\n");
            }
            start = result.Position;
            if (shape is "hybrid" or "ignored")
            {
                Write("xref\n0 7\n0000000000 65535 f \n");
                for (int number = 1; number <= 6; number++)
                { Write((number == 6 ? section : table.Entries[number].First).ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n"); }
                Write("trailer\n<< " + trailer + " /XRefStm " + section.ToString(CultureInfo.InvariantCulture) + " >>\n");
            }
            else
            {
                Write("xref\n0 1\n0000000000 65535 f \n");
                if (shape == "reused") { Write("6 1\n" + reused.ToString("D10", CultureInfo.InvariantCulture) + " 00001 n \n"); }
                Write("trailer\n<< " + trailer + " /Prev " + section.ToString(CultureInfo.InvariantCulture) + " >>\n");
            }
        }
        Write("startxref\n" + (shape == "repair" ? "1" : start.ToString(CultureInfo.InvariantCulture)) + "\n%%EOF\n");
        return result.ToArray();
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("hybrid")]
    [InlineData("ignored")]
    [InlineData("prev")]
    [InlineData("reused")]
    [InlineData("repair")]
    public async Task StructuralExceptionsFollowPhysicalSectionsAndActiveOffsets(string shape)
    {
        byte[] input = Reframe(shape), before = (byte[])input.Clone();
        using InMemoryQpdfHost host = new(); host.AddInput(Input, input, QpdfInputOwnership.Transfer);
        QpdfLimits limits = QpdfLimits.Default with { IgnoreXrefStreams = shape == "ignored" };
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, Reader,
            shape == "repair" ? QpdfRecoveryMode.BoundedRepair : QpdfRecoveryMode.Strict, limits, CancellationToken.None))
        {
            PrivateValue(Assert.IsType<PdfStream>(document.GetObject(new PdfReference(3, 0))));
            Assert.Equal(shape != "reused", document.Engine.IsCrossReferenceStream(6));
            PdfStream section = Assert.IsType<PdfStream>(document.GetObject(new PdfReference(6, shape == "reused" ? 1 : 0)));
            if (shape == "reused")
            { PrivateValue(section); Assert.IsType<PdfNull>(document.GetObject(new PdfReference(6, 0))); }
            else
            {
                Assert.Equal("clear-xref-header"u8.ToArray(), Assert.IsType<PdfByteString>(Get(section.Dictionary, "Note")).Value);
                Assert.Equal(49, section.Data.Length); Assert.Equal(0, section.Data[0]); Assert.Equal(1, section.Data[7]);
            }
            Assert.Equal(shape == "repair", document.Warnings.Count != 0);
        }
        Assert.Equal(before, input); Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static byte[] Plain(bool reachable) => PdfTestDocument.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R" + (reachable ? " /Private 4 0 R" : "") + " >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
        (4, "<< /Type /XRef /Note (private-header-secret) /Length 21 >>", Payload)], 1);

    private static string[] Flags(string layout) => layout switch
    {
        "packed" => ["--object-streams=generate"], "linear" => ["--linearize"],
        "linear-packed" => ["--linearize", "--object-streams=generate"], "qdf" => ["--qdf"],
        "qdf-packed" => ["--qdf", "--object-streams=generate"], _ => ["--object-streams=disable"],
    };

    [Theory]
    [InlineData("classic", false, false)]
    [InlineData("packed", false, false)]
    [InlineData("linear", false, false)]
    [InlineData("linear-packed", false, false)]
    [InlineData("qdf", false, false)]
    [InlineData("qdf-packed", false, false)]
    [InlineData("packed", false, true)]
    [InlineData("linear", true, false)]
    [InlineData("qdf-packed", true, false)]
    public async Task WritersProtectTaggedObjectsWithoutDiscardingTheirIdentity(string layout, bool template, bool job)
    {
        byte[] source = Plain(true), before = (byte[])source.Clone();
        using InMemoryQpdfHost host = new(); host.AddInput(Input, source, QpdfInputOwnership.Transfer);
        QpdfPath templatePath = QpdfPath.From("template");
        if (template) { host.AddInput(templatePath, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r4-ascii.pdf")), QpdfInputOwnership.Transfer); }
        QpdfExecutionResult result;
        if (job)
        {
            result = await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","streamData":"preserve","objectStreams":"generate","encrypt":{"userPassword":"reader","ownerPassword":"owner","256bit":{}}}""", CancellationToken.None);
        }
        else
        {
            string[] security = template ? ["--copy-encryption=template", "--encryption-file-password=fixture-owner"]
                : ["--encrypt", "reader", "owner", "256", "--"];
            result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--stream-data=preserve", .. Flags(layout), .. security, "output"]), CancellationToken.None);
        }
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.TryGetOutput(Output, out byte[] output)); Protected(output);
        host.AddInput(Output, output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Output, Reader, QpdfLimits.Default, CancellationToken.None))
        {
            Assert.Equal(template ? 4 : 6, document.Encryption?.Revision); Assert.Equal(1, document.PageCount);
            PdfDictionary catalog = Catalog(document);
            PrivateValue(Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(Get(catalog, "Private")))));
        }
        Assert.Equal(before, source); Assert.Equal(0L, host.OpenLeaseCount);
        using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(output, new UglyToad.PdfPig.ParsingOptions { Password = "reader" });
        Assert.Equal(1, independent.NumberOfPages);
    }

    private static void Protected(byte[] output)
    {
        Assert.Equal(-1, output.AsSpan().IndexOf(Payload)); Assert.Equal(-1, output.AsSpan().IndexOf(Note));
        Assert.DoesNotContain(Convert.ToHexString(Note), Encoding.Latin1.GetString(output), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedJsonCreationAndUpdateProtectAuthoredTaggedStreams(bool update)
    {
        using InMemoryQpdfHost host = new();
        if (update) { host.AddInput(Input, Native(), QpdfInputOwnership.Transfer); }
        var values = new Dictionary<string, object>();
        if (!update)
        {
            values["obj:1 0 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Catalog", ["/Pages"] = "2 0 R", ["/Private"] = "4 0 R" } };
            values["obj:2 0 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Pages", ["/Count"] = 1, ["/Kids"] = new[] { "3 0 R" } } };
            values["obj:3 0 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Page", ["/Parent"] = "2 0 R", ["/MediaBox"] = new[] { 0, 0, 100, 100 }, ["/Resources"] = new Dictionary<string, object>() } };
            values["trailer"] = new { value = new Dictionary<string, object> { ["/Root"] = "1 0 R" } };
        }
        values[update ? "obj:3 0 R" : "obj:4 0 R"] = new { stream = new { dict = new Dictionary<string, object> { ["/Type"] = "/XRef", ["/Note"] = "u:private-header-secret" }, data = Convert.ToBase64String(Payload) } };
        host.AddInput(Json, JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2, pdfversion = "1.4" }, values } }), QpdfInputOwnership.Transfer);
        QpdfJsonImportResult result = update
            ? await Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, Reader, QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None)
            : await Qpdf.CreatePdfFromJsonAsync(host, Json, Output, QpdfOutputEncryption.CreateAes256(Reader, QpdfPassword.FromText("owner"), -4, true), QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(1, result.PageCount); Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.TryGetOutput(Output, out byte[] output)); Protected(output);
        host.AddInput(Output, output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Output, Reader, QpdfLimits.Default, CancellationToken.None);
        PdfDictionary catalog = Catalog(document);
        PrivateValue(Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(Get(catalog, "Private")))));
    }

    [Fact]
    public async Task ExplicitUnreferencedPreservationKeepsTaggedPrivateStreamsInQdf()
    {
        using InMemoryQpdfHost host = new(); host.AddInput(Input, Plain(false), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--preserve-unreferenced", "--qdf", "--encrypt", "reader", "owner", "256", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.True(host.TryGetOutput(Output, out byte[] output)); Protected(output);
        host.AddInput(Output, output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Output, Reader, QpdfLimits.Default, CancellationToken.None);
        PdfStream stream = Assert.Single(document.Engine.XRefEntries.Where(entry => entry.Value.Kind != XrefEntryKind.Free)
            .Select(entry => document.GetObject(entry.Key)).OfType<PdfStream>());
        PrivateValue(stream);
    }

    [Fact]
    public Task AdmissionAndPublicationFailuresRetirePhysicalRoleStorageAndPreserveTargets()
        => PreparationAdmissionChecks.VerifyAsync(() => PreparationAdmissionChecks.StorageAsync(Reframe("hybrid"), Output),
            (host, token) => Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--password=reader", "--object-streams=generate", "output"]), token),
            Output, 128, ["create", "write", "commit", "cancel", "close"]);
}
