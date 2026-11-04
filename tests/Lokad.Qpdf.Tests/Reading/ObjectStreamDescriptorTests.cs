using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class ObjectStreamDescriptorTests
{
    private static readonly byte[] Attachment = [0, 255, 1, 2, 13, 10];

    public static IEnumerable<object[]> ReadCases()
    {
        foreach (int mask in Enumerable.Range(0, 8))
        foreach (bool encoded in new[] { false, true })
        foreach (int generation in new[] { 0, 17 }) { yield return [mask, encoded, generation]; }
    }

    [Theory]
    [MemberData(nameof(ReadCases))]
    public async Task DescriptorReferencesResolveBeforeCachingMembersAndKeepEscapedValues(int mask, bool encoded, int generation)
    {
        RecordingMemoryBudget budget = new(16L * 1048576L);
        using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        host.AddInput(QpdfPath.From("input"), Fixture("info", mask, encoded, generation, null, null, false, false), QpdfInputOwnership.Transfer);
        PdfDictionary escaped;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None))
        {
            Assert.Equal(1, document.PageCount);
            PdfDictionary info = Assert.IsType<PdfDictionary>(document.GetObject(5));
            Assert.Equal("Retained info", Text(info, "Title"));
            escaped = Assert.IsType<PdfDictionary>(document.GetObject(6));
            Assert.Equal("Retained private", Text(escaped, "Marker"));
            Assert.Same(escaped, document.GetObject(6));
        }
        host.Dispose(); Assert.True(budget.ReservedBytes > 0L);
        Assert.Equal("Retained private", Text(escaped, "Marker"));
        owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationOrSharedRefusalClosesTheReadAndRefusalAllowsRetry(bool encoded, bool cancel)
    {
        const long capacity = 16L * 1048576L;
        RecordingMemoryBudget budget = new(capacity);
        using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        using CancellationTokenSource stop = new();
        host.AddInput(QpdfPath.From("input"), Fixture("info", 7, encoded, 17, null, null, true, false), QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), stop.Token))
        {
            if (cancel)
            {
                stop.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() => document.GetObject(5));
            }
            else
            {
                Assert.True(owner.TryReserve(capacity - budget.ReservedBytes, out IMemoryReservation? reserved));
                using (IMemoryReservation pressure = reserved ?? throw new InvalidOperationException("Missing pressure reservation."))
                { Assert.Throws<QpdfMemoryAdmissionException>(() => document.GetObject(5)); }
                PdfDictionary info = Assert.IsType<PdfDictionary>(document.GetObject(5));
                Assert.Equal("Retained info", Text(info, "Title"));
                PdfDictionary member = Assert.IsType<PdfDictionary>(document.GetObject(6));
                Assert.True(member.TryGet(PdfName.FromAscii("Values"), out QpdfObject values));
                Assert.Equal(8192, Assert.IsType<PdfArray>(values).Count);
            }
        }
        Assert.Equal(0L, host.OpenLeaseCount); host.Dispose(); owner.Dispose();
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    public static IEnumerable<object[]> PublicationCases()
    {
        foreach (string role in new[] { "info", "attachment", "page" })
        foreach (int mask in Enumerable.Range(0, 8))
        foreach (bool encoded in new[] { false, true })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [role, mask, encoded, route]; }
    }

    [Theory]
    [MemberData(nameof(PublicationCases))]
    public async Task EquivalentDescriptorsPreserveMetadataAttachmentsAndPages(string role, int mask, bool encoded, string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(role, mask, encoded, mask % 2 == 0 ? 0 : 17, null, null, false, false), QpdfInputOwnership.Transfer);
        if (route == "typed")
        {
            PdfPageSelectionResult result = await Qpdf.SelectPageAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), PdfPageSelectionOptions.FromPages([1]), CancellationToken.None);
            Assert.Empty(result.Warnings);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--suppress-recovery", "--pages", ".", "1", "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","suppressRecovery":true,"pages":[{"file":".","range":"1"}]}""", CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
        {
            Assert.Equal(1, document.PageCount);
            PdfDictionary info = Assert.IsType<PdfDictionary>(Get(document, document.Engine.Trailer, "Info"));
            Assert.Equal("Retained info", Text(info, "Title"));
            if (role == "page") { Assert.Equal("Retained page", Text(document.PageReferences[0].Page, "PageMarker")); }
            if (route != "typed")
            {
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(Get(document, document.Engine.Trailer, "Root"));
                Assert.Equal("Retained private", Text(Assert.IsType<PdfDictionary>(Get(document, catalog, "Private")), "Marker"));
            }
        }
        if (role == "attachment")
        {
            IReadOnlyList<QpdfAttachmentInfo> attachments = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("read"), CancellationToken.None);
            Assert.Single(attachments); Assert.Equal("K", attachments[0].Key);
            _ = await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("read"), "K", QpdfPath.From("extracted"), CancellationToken.None);
            Assert.True(host.TryGetOutput(QpdfPath.From("extracted"), out byte[] bytes)); Assert.Equal(Attachment, bytes);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> MalformedCases()
    {
        foreach (string attribute in new[] { "Type", "N", "First" })
        foreach (string fault in new[] { "stale", "missing", "wrong", "null", "negative", "self", "in-stream" })
        foreach (bool job in new[] { false, true }) { yield return [attribute, fault, job]; }
    }

    [Theory]
    [MemberData(nameof(MalformedCases))]
    public async Task InvalidRequiredDescriptorsRejectBeforePublication(string attribute, string fault, bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("page", 0, true, 17, attribute, fault, false, false), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","suppressRecovery":true}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--suppress-recovery", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        Assert.Equal(0L, host.OpenLeaseCount);
        await Assert.ThrowsAsync<QpdfParseException>(async () =>
        { await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None); });
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, 2, false)]
    [InlineData(false, 3, true)]
    [InlineData(true, 2, false)]
    [InlineData(true, 3, true)]
    public async Task DescriptorsInAnotherObjectStreamKeepDecoderScopesAndLoadDepthBounds(bool encoded, int depth, bool accepted)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("info", 7, encoded, 0, null, null, false, true), QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), QpdfLimits.Default with { MaxNestingDepth = depth }, CancellationToken.None))
        {
            if (accepted)
            {
                PdfDictionary info = Assert.IsType<PdfDictionary>(document.GetObject(5));
                Assert.Equal("Retained info", Text(info, "Title"));
                PdfDictionary original = Assert.IsType<PdfStream>(document.GetObject(4)).Dictionary;
                Assert.True(original.TryGet(PdfName.FromAscii("Type"), out QpdfObject type));
                Assert.Equal(new PdfReference(9, 0), type);
            }
            else { Assert.Throws<QpdfParseException>(() => document.GetObject(5)); }
            // A rejected deeper load must clear its active sets. Reading the
            // descriptor stream directly fits even the shallower load budget.
            Assert.Equal(PdfName.FromAscii("ObjStm"), document.GetObject(9));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static string Text(PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(new PdfName(Encoding.ASCII.GetBytes(key)), out QpdfObject value)); return Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(value).ValueMemory.Span); }

    private static QpdfObject Get(QpdfDocument document, PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(new PdfName(Encoding.ASCII.GetBytes(key)), out QpdfObject value)); return value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value; }

    private static byte[] Fixture(string role, int mask, bool encoded, int generation, string? attribute, string? fault, bool expanded, bool nested)
    {
        string first = role == "page" ? "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /PageMarker (Retained page) >>"
            : role == "attachment" ? "<< /Type /Filespec /F (K) /EF << /F 8 0 R >> >>" : "<< /Title (Retained info) /Custom 6 0 R >>";
        string second = "<< /Title (Retained info) /Marker (Retained private)"
            + (expanded ? " /Values [" + string.Join(' ', Enumerable.Repeat("0", 8192)) + "]" : "") + " >>";
        string pairs = "5 0 6 " + (Encoding.ASCII.GetByteCount(first) + 1).ToString(CultureInfo.InvariantCulture) + " ";
        string[] names = ["Type", "N", "First"];
        string[] values = ["/ObjStm", "2", Encoding.ASCII.GetByteCount(pairs).ToString(CultureInfo.InvariantCulture)];
        string[] descriptors = new string[3];
        for (int i = 0; i < descriptors.Length; i++)
        {
            int number = i + 9;
            descriptors[i] = (mask & (1 << i)) == 0 ? values[i] : $"{number} {generation} R";
            if (attribute != names[i]) { continue; }
            descriptors[i] = fault == "missing" ? "30 0 R" : fault == "in-stream" ? "6 0 R" : $"{number} {(fault == "stale" ? 0 : generation)} R";
            values[i] = fault switch { "null" => "null", "wrong" => i == 0 ? "42" : "2.5", "negative" => "-1", "self" => $"{number} {generation} R", _ => values[i] };
        }
        byte[] raw = Encoding.ASCII.GetBytes(pairs + first + " " + second);
        if (encoded)
        {
            using MemoryStream compressed = new();
            using (ZLibStream codec = new(compressed, CompressionLevel.Fastest, true)) { codec.Write(raw); }
            raw = compressed.ToArray();
        }
        int size = nested ? 15 : 13;
        using MemoryStream pdf = new(); long[] offsets = new long[size];
        void Write(string text) => pdf.Write(Encoding.ASCII.GetBytes(text));
        void Object(int number, int gen, string body) { offsets[number] = pdf.Length; Write($"{number} {gen} obj\n{body}\nendobj\n"); }
        void Stream(int number, string entries, byte[] data)
        { offsets[number] = pdf.Length; Write($"{number} 0 obj\n<< /Length {data.Length} {entries} >>\nstream\n"); pdf.Write(data); Write("\nendstream\nendobj\n"); }
        Write("%PDF-1.7\n");
        Object(1, 0, "<< /Type /Catalog /Pages 2 0 R /Private 6 0 R" + (role == "attachment" ? " /Names << /EmbeddedFiles << /Names [(K) 5 0 R] >> >>" : "") + " >>");
        Object(2, 0, "<< /Type /Pages /Count 1 /Kids [" + (role == "page" ? 5 : 3) + " 0 R] >>");
        Object(3, 0, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>");
        Stream(4, $"/Type {descriptors[0]} /N {descriptors[1]} /First {descriptors[2]}" + (encoded ? " /Filter /FlateDecode" : ""), raw);
        Stream(8, "/Type /EmbeddedFile", Attachment);
        if (nested)
        {
            string header = $"9 0 10 {values[0].Length + 1} 11 {values[0].Length + values[1].Length + 2} ";
            byte[] data = Encoding.ASCII.GetBytes(header + string.Join(' ', values));
            if (encoded)
            {
                using MemoryStream compressed = new();
                using (ZLibStream codec = new(compressed, CompressionLevel.Fastest, true)) { codec.Write(data); }
                data = compressed.ToArray();
            }
            Stream(14, $"/Type /ObjStm /N 3 /First {header.Length}" + (encoded ? " /Filter /FlateDecode" : ""), data);
        }
        else { for (int i = 0; i < values.Length; i++) { Object(i + 9, generation, values[i]); } }
        offsets[12] = pdf.Length; byte[] xref = new byte[size * 7];
        for (int number = 0; number < size; number++)
        {
            bool member = number is 5 or 6;
            bool descriptor = nested && number is >= 9 and <= 11;
            long position = member ? 4L : descriptor ? 14L : offsets[number];
            int kind = member || descriptor ? 2 : position == 0 ? 0 : 1;
            int gen = number == 0 ? 65535 : member ? number - 5 : descriptor ? number - 9 : number is >= 9 and <= 11 ? generation : 0;
            xref[number * 7] = (byte)kind;
            for (int index = 0; index < 4; index++) { xref[number * 7 + index + 1] = (byte)(position >> (8 * (3 - index))); }
            xref[number * 7 + 5] = (byte)(gen >> 8); xref[number * 7 + 6] = (byte)gen;
        }
        Stream(12, "/Type /XRef /Size " + size + " /W [1 4 2] /Root 1 0 R /Info " + (role == "info" ? 5 : 6) + " 0 R", xref);
        Write($"startxref\n{offsets[12]}\n%%EOF\n"); return pdf.ToArray();
    }
}
