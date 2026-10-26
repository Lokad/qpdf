using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class FutureXrefTests
{
    internal static byte[] Fixture(ulong kind, bool incremental, bool invalidFields)
    {
        using MemoryStream data = new(); List<long> offsets = [0];
        void Text(string value) => data.Write(Encoding.ASCII.GetBytes(value));
        Text("%PDF-1.7\n");
        string[] bodies = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Probe 5 0 R >>", "(padding)", "(stale-value)"];
        for (int i = 0; i < bodies.Length; i++)
        { offsets.Add(data.Position); Text((i + 1) + " 0 obj\n" + bodies[i] + "\nendobj\n"); }
        long previous = data.Position;
        if (incremental)
        {
            Text("xref\n0 6\n0000000000 65535 f \n");
            foreach (long offset in offsets.Skip(1)) { Text(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n"); }
            Text("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n" + previous + "\n%%EOF\n");
        }
        long current = data.Position; offsets.Add(current);
        int first = incremental ? 5 : 0;
        byte[] rows = new byte[(7 - first) * 14];
        for (int number = first; number < 7; number++)
        {
            Span<byte> row = rows.AsSpan((number - first) * 14, 14);
            ulong type = number == 0 ? 0 : number == 5 ? kind : 1;
            uint target = number == 5 && (kind > 2 || invalidFields) ? uint.MaxValue : type == 1 ? (uint)offsets[number] : 0;
            BinaryPrimitives.WriteUInt64BigEndian(row, type);
            BinaryPrimitives.WriteUInt32BigEndian(row[8..], target);
            BinaryPrimitives.WriteUInt16BigEndian(row[12..], number == 0 || number == 5 && kind > 2 ? ushort.MaxValue : (ushort)0);
        }
        Text("6 0 obj\n<< /Type /XRef /Size 7 /Root 1 0 R /W [8 4 2] /Length " + rows.Length
            + (incremental ? " /Index [5 2] /Prev " + previous : "") + " >>\nstream\n");
        data.Write(rows); Text("\nendstream\nendobj\nstartxref\n" + current + "\n%%EOF\n");
        return data.ToArray();
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (ulong kind in new ulong[] { 0, 1, 3, 5, 255, 0x8000000000000005 })
        foreach (bool incremental in new[] { false, true })
        foreach (string operation in new[] { "inspect", "rewrite" })
        foreach (string route in new[] { "typed", "argv", "job" })
        { yield return new object[] { kind, incremental, operation, route }; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FutureRowsResolveToNullAndMaskOlderEntries(ulong kind, bool incremental, string operation, string route)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(kind, incremental, false), QpdfInputOwnership.Transfer);
        QpdfObject? retained = null; string display = "";
        if (route == "typed")
        {
            if (operation == "inspect")
            {
                await using QpdfDocument input = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
                retained = input.GetObject(5);
            }
            else { await Qpdf.SelectPageAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new(1), CancellationToken.None); }
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(operation == "inspect" ? ["input", "--show-object=5"] : ["input", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, operation == "inspect" ? """{"inputFile":"input","showObject":"5"}""" : """{"inputFile":"input","outputFile":"output"}""", CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity != QpdfDiagnosticSeverity.Info);
            display = string.Join("\n", result.Diagnostics.Where(d => d.Severity == QpdfDiagnosticSeverity.Info).Select(d => d.Message)).Trim();
        }
        if (operation == "rewrite")
        {
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
            host.AddInput(QpdfPath.From("readback"), bytes, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
            PdfDictionary page = document.PageReferences[0].Page;
            retained = PdfNull.Instance;
            if (page.TryGet(PdfName.FromAscii("Probe"), out QpdfObject value))
            { retained = value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value; }
        }
        host.Dispose(); Assert.Equal(0L, host.OpenLeaseCount);
        if (retained is not null)
        {
            if (kind == 1) { Assert.Equal("stale-value", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(retained).ValueMemory.Span)); }
            else { Assert.IsType<PdfNull>(retained); }
        }
        else { Assert.Equal(kind == 1 ? "(stale-value)" : "null", display); }
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(2UL)]
    public async Task KnownTypesStillValidateTheirFields(ulong kind)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(kind, false, true), QpdfInputOwnership.Transfer);
        await Assert.ThrowsAsync<QpdfParseException>(() => QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None).AsTask());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task InvalidKnownRowPreservesPriorOutput(string route)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(1, false, true), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await seed.WriteAsync(new byte[] { 17, 23 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = route == "argv"
            ? await Qpdf.ExecuteAsync(host, new(["input", "--suppress-recovery", "output"]), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","suppressRecovery":true}""", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.Equal(new byte[] { 17, 23 }, prior);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
