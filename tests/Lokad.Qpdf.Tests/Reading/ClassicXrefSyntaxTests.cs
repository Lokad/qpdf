using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class ClassicXrefSyntaxTests
{
    public static IEnumerable<object[]> Representations()
    {
        yield return ["\n", "\n", " ", false, false];
        yield return ["\r\n", "\r\n", " ", false, false];
        yield return ["\r", "\r", " ", false, false];
        yield return ["\r", "\n", " ", false, false];
        yield return ["\n", "\r", " ", false, false];
        yield return ["\n", "\n", "\t", false, false];
        yield return ["\n", "\n", "\f", false, false];
        yield return ["\r", "\n", "\f", true, true];
        yield return ["\n", "\r", "\t", true, true];
        yield return ["\r\n", "\n", " \t\f", true, false];
        yield return ["\n", "\n", " ", true, false];
        yield return ["\r", "\r\n", " ", true, true];
    }

    private static byte[] Fixture(string subsectionEnd, string entryEnd, string padding, bool signed, bool incremental, bool sparse, string fault)
    {
        using MemoryStream pdf = new();
        void Text(string text) => pdf.Write(Encoding.ASCII.GetBytes(text));
        long[] offsets = new long[6];
        void Object(int number, string body) { offsets[number] = pdf.Length; Text($"{number} 0 obj\n{body}\nendobj\n"); }
        Text("%PDF-1.7\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>"); Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        string Page(string marker) => "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Contents 4 0 R /Marker (" + marker + ") >>";
        Object(3, Page("Base")); Object(4, "<< /Length 3 >>\nstream\nq Q\nendstream"); Object(5, "(" + (sparse ? new string('x', 9 * 1048576) : "unused") + ")");
        if (fault == "nul-padding") { padding = "\0"; }
        long previous = pdf.Length; string end = entryEnd.Length == 2 ? entryEnd : " " + entryEnd;
        string bounds = fault == "bounds-overflow" ? "1 9223372036854775807" : fault == "first-plus" ? "+0 6" : fault == "count-plus" ? "0 +6" : fault == "bounds-negative" ? "-0 6" : "0 6";
        Text("xref\n" + bounds + padding + subsectionEnd + "0000000000 65535 f" + end);
        for (int number = 1; number < offsets.Length; number++)
        {
            string row = $"{offsets[number]:D10} 00000 n";
            if (number == 3 && fault == "offset") { row = "not-offset 00000 n"; }
            if (number == 3 && fault == "generation") { row = $"{offsets[number]:D10} x0000 n"; }
            if (number == 3 && fault == "kind") { row = $"{offsets[number]:D10} 00000 x"; }
            Text(row + (fault == "line" && number == 3 ? " \v" : end));
        }
        void Trailer(long position, string extra) => Text($"trailer\n<< /Size 6 /Root 1 0 R {extra} >>\nstartxref\n{(signed ? "+" : "")}{position}\n%%EOF\n");
        Trailer(previous, "");
        if (incremental)
        {
            Object(3, Page("Updated")); long latest = pdf.Length;
            Text("xref\n3 1" + padding + subsectionEnd + $"{offsets[3]:D10} 00000 n" + end); Trailer(latest, "/Prev " + previous);
        }
        return pdf.ToArray();
    }

    [Theory]
    [MemberData(nameof(Representations))]
    public async Task TypedAndCommandReadersAcceptEquivalentSyntax(string subsectionEnd, string entryEnd, string padding, bool signed, bool incremental)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(subsectionEnd, entryEnd, padding, signed, incremental, false, ""), QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None))
        { AssertPage(document, incremental); }
        foreach (bool job in new[] { false, true })
        {
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","suppressRecovery":true,"pages":[{"file":".","range":"1"}]}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--suppress-recovery", "--pages", ".", "1", "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None); AssertPage(document, incremental);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("\r", "\r", "\t", false)]
    [InlineData("\n", "\r", "\f", false)]
    [InlineData("\r", "\r\n", "\t", false)]
    [InlineData("\n", "\n", " ", true)]
    public async Task SelectiveReadsAndIncrementalFallbackAgreeWithCompleteInput(string subsectionEnd, string entryEnd, string padding, bool incremental)
    {
        byte[] source = Fixture(subsectionEnd, entryEnd, padding, true, incremental, true, "");
        using InMemoryQpdfHost complete = new(); complete.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
        SelectiveInputTests.Host selected = new(source, incremental ? int.MaxValue : 37);
        using InMemoryQpdfHost selectedOutput = selected.Outputs;
        foreach (bool job in new[] { false, true })
        {
            ValueTask<QpdfExecutionResult> Run(IQpdfHost host) => job
                ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","suppressRecovery":true,"pages":[{"file":".","range":"1"}]}""", CancellationToken.None)
                : Qpdf.ExecuteAsync(host, new(["input", "--suppress-recovery", "--pages", ".", "1", "--", "output"]), CancellationToken.None);
            long before = selected.ReadBytes; QpdfExecutionResult actual = await Run(selected); QpdfExecutionResult expected = await Run(complete);
            Assert.Equal(QpdfExitCode.Success, actual.ExitCode); Assert.Equal(expected.ExitCode, actual.ExitCode);
            Assert.Empty(actual.Diagnostics);
            // Incremental windows deliberately fall back to the complete reader.
            // Ordinary classic inputs must actually take the bounded-read path.
            Assert.InRange(selected.ReadBytes - before, incremental ? source.LongLength : 1L,
                incremental ? source.LongLength + 262144L : 262144L);
            Assert.True(selected.Outputs.TryGetOutput(QpdfPath.From("output"), out byte[] actualBytes));
            Assert.True(complete.TryGetOutput(QpdfPath.From("output"), out byte[] expectedBytes)); Assert.Equal(expectedBytes, actualBytes);
            Assert.Equal(0, selected.Leases);
        }
    }

    [Theory]
    [InlineData("first-plus")]
    [InlineData("count-plus")]
    [InlineData("bounds-negative")]
    [InlineData("bounds-overflow")]
    [InlineData("nul-padding")]
    [InlineData("offset")]
    [InlineData("generation")]
    [InlineData("kind")]
    [InlineData("line")]
    public async Task MalformedRowsAndSignedSubsectionBoundsStillReject(string fault)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("\n", "\n", " ", false, false, false, fault), QpdfInputOwnership.Transfer);
        await Assert.ThrowsAsync<QpdfParseException>(async () => await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None));
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--suppress-recovery", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode); Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.Equal("prior"u8.ToArray(), output); Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static void AssertPage(QpdfDocument document, bool incremental)
    {
        Assert.Equal(1, document.PageCount); Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Marker"), out QpdfObject value));
        Assert.Equal(incremental ? "Updated"u8.ToArray() : "Base"u8.ToArray(), Assert.IsType<PdfByteString>(value).ValueMemory.ToArray());
    }
}
