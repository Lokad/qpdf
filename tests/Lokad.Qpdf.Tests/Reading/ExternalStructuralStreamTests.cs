using System.Globalization;
using System.IO.Compression;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class ExternalStructuralStreamTests
{
    public static IEnumerable<object[]> Sources()
    {
        foreach (string role in new[] { "info", "attachment", "page", "xref" })
        foreach (string descriptor in new[] { "absent", "null", "file", "reference" })
        foreach (bool encoded in new[] { false, true }) { yield return [role, descriptor, encoded]; }
    }

    private static bool External(string descriptor) => descriptor is "file" or "reference";

    private static byte[] Fixture(string role, string descriptor, bool encoded)
    {
        string file = descriptor switch { "absent" => "", "null" => "/F null", "file" => "/F (external.bin)", _ => "/F 7 0 R" };
        string member = role == "page" ? "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Marker (Keep) >>"
            : role == "attachment" ? "<< /Type /Filespec /F (K) /EF << /F 4 0 R >> /Marker (Keep) >>" : "<< /Title (Keep) /Marker (Keep) >>";
        byte[] raw = Encoding.ASCII.GetBytes("6 0 " + member);
        if (encoded)
        {
            using MemoryStream buffer = new(); using (ZLibStream zlib = new(buffer, CompressionLevel.Fastest, true)) { zlib.Write(raw); }
            raw = buffer.ToArray();
        }
        using MemoryStream pdf = new(); long[] offsets = new long[9];
        void Write(string text) => pdf.Write(Encoding.ASCII.GetBytes(text));
        void Object(int n, string body) { offsets[n] = pdf.Length; Write($"{n} 0 obj\n{body}\nendobj\n"); }
        void Stream(int n, string header, byte[] bytes)
        { offsets[n] = pdf.Length; Write($"{n} 0 obj\n<< /Length {bytes.Length} {header} >>\nstream\n"); pdf.Write(bytes); Write("\nendstream\nendobj\n"); }
        Write("%PDF-1.7\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R /Private 6 0 R" + (role == "attachment" ? " /Names << /EmbeddedFiles << /Names [(K) 6 0 R] >> >>" : "") + " >>");
        Object(2, "<< /Type /Pages /Count 1 /Kids [" + (role == "page" ? 6 : 3) + " 0 R] >>");
        Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>");
        Stream(4, "", "q Q\n"u8.ToArray());
        Stream(5, "/Type /ObjStm /N 1 /First 4 " + (role == "xref" ? "" : file) + (encoded ? " /Filter /FlateDecode" : ""), raw);
        Object(7, "(" + (descriptor == "large-reference" ? new string('x', 256 * 1024) : "external.bin") + ")"); offsets[8] = pdf.Length;
        byte[] xref = new byte[9 * 7];
        for (int n = 0; n < 9; n++)
        {
            long first = n == 6 ? 5L : offsets[n];
            xref[n * 7] = n == 0 ? (byte)0 : n == 6 ? (byte)2 : (byte)1;
            for (int b = 0; b < 4; b++) { xref[n * 7 + b + 1] = (byte)(first >> (24 - 8 * b)); }
            if (n == 0) { xref[5] = 255; xref[6] = 255; }
        }
        Stream(8, "/Type /XRef /Size 9 /Root 1 0 R /W [1 4 2]" + (role == "info" ? " /Info 6 0 R" : "") + (role == "xref" ? " " + file : ""), xref);
        Write("startxref\n" + offsets[8].ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n"); return pdf.ToArray();
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task StructuralReadsRefuseExternalMembersWithoutLosingRawAliasesOrOwners(string role, string descriptor, bool encoded)
    {
        RecordingMemoryBudget budget = new(16L * 1048576L);
        using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        host.AddInput(QpdfPath.From("input"), Fixture(role, descriptor, encoded), QpdfInputOwnership.Transfer);
        bool external = External(descriptor);
        PdfStream? escaped = null;
        if (external && role is "page" or "xref")
        { await Assert.ThrowsAnyAsync<QpdfException>(async () => await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None)); }
        else
        {
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
            escaped = Assert.IsType<PdfStream>(document.GetObject(5));
            if (external) { Assert.ThrowsAny<QpdfException>(() => document.GetObject(6)); }
            else { Assert.IsType<PdfDictionary>(document.GetObject(6)); }
            Assert.IsType<PdfStream>(document.GetObject(4));
        }
        host.Dispose();
        if (escaped is not null) { Assert.NotEmpty(escaped.RawMemory.ToArray()); }
        owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task CommandRewritesRefuseUnavailableStructureBeforePublication(string role, string descriptor, bool encoded)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(role, descriptor, encoded), QpdfInputOwnership.Transfer);
        foreach (bool job in new[] { false, true })
        {
            await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
            { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output"}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "output"]), CancellationToken.None);
            Assert.Equal(External(descriptor) ? QpdfExitCode.Error : QpdfExitCode.Success, result.ExitCode);
            if (External(descriptor))
            {
                Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.UnsupportedFeature);
                Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
    }

    [Theory]
    [InlineData("info", false)]
    [InlineData("info", true)]
    [InlineData("attachment", false)]
    [InlineData("attachment", true)]
    public async Task OptionalExternalMembersReportIncompleteGraphCoverage(string role, bool encoded)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(role, "file", encoded), QpdfInputOwnership.Transfer);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("input"), CancellationToken.None);
        Assert.True(result.Passed); Assert.Equal(QpdfCheckStatus.Skipped, Assert.Single(result.Checks, x => x.Name == "object-graph").Status);
        Assert.Equal(QpdfCheckStatus.Skipped, Assert.Single(result.Checks, x => x.Name == "streams").Status);
        Assert.Contains(result.Diagnostics, x => x.Code == QpdfDiagnosticCode.CheckSkipped);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DescriptorAdmissionAndCancellationPreserveTheirFailureAndAllowCleanup(bool canceled)
    {
        const long capacity = 16L * 1048576L;
        RecordingMemoryBudget budget = new(capacity);
        using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        host.AddInput(QpdfPath.From("input"), Fixture("info", "large-reference", false), QpdfInputOwnership.Transfer);
        using CancellationTokenSource stop = new();
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), stop.Token))
        {
            Assert.IsType<PdfStream>(document.GetObject(5));
            if (canceled)
            {
                stop.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => document.GetObject(6));
            }
            else
            {
                Assert.True(owner.TryReserve(capacity - budget.ReservedBytes, out IMemoryReservation? reserved));
                using (IMemoryReservation pressure = reserved ?? throw new Exception())
                { Assert.Throws<QpdfMemoryAdmissionException>(() => document.GetObject(6)); }
                QpdfException failure = Assert.ThrowsAny<QpdfException>(() => document.GetObject(6));
                Assert.Contains("External-file", failure.Message, StringComparison.Ordinal);
                Assert.IsType<PdfStream>(document.GetObject(4));
            }
        }
        host.Dispose(); owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
