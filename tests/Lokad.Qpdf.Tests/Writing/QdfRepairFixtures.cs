using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

internal static class QdfRepairFixtures
{
    internal static byte[] Image(byte[] payload) => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im 5 0 R >> >> /Contents 4 0 R >>", null),
        (4, "<< /Length 11 >>", "q /Im Do Q\n"u8.ToArray()),
        (5, $"<< /Type /XObject /Subtype /Image /Width {payload.Length} /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length {payload.Length} >>", payload)], 1);

    internal static async Task<byte[]> Emit(byte[] input, bool objectStreams)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--qdf",
            "--object-streams=" + (objectStreams ? "generate" : "disable"), "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] qdf)); Assert.Equal(0L, host.OpenLeaseCount);
        return qdf;
    }

    internal static byte[] ObjectStream(int members, int padding)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(members, 3);
        StringBuilder text = new("%PDF-1.5\n%QDF-1.0\n\n1 0 obj\n<<\n  /Type /ObjStm\n  /N 0\n  /First 0\n  /Length 0\n>>\nstream\n0 0\n");
        for (int i = 0; i < members; i++)
        {
            text.Append("%% Object stream: object ").Append(i + 2).Append(", index ").Append(i).Append('\n');
            if (i == 0) { text.Append("<< /Type /Catalog /Pages 3 0 R /Padding (").Append('A', padding).Append(") >>\n"); }
            else if (i == 1) { text.Append("<< /Type /Pages /Count 1 /Kids [4 0 R] >>\n"); }
            else if (i == 2) { text.Append("<< /Type /Page /Parent 3 0 R /MediaBox [0 0 10 20] /Resources << >> >>\n"); }
            else { text.Append("null\n"); }
        }
        text.Append("endstream\nendobj\n\n").Append(members + 2).Append(" 0 obj\n<<\n  /Type /XRef\n  /Root 2 0 R\n  /Size ")
            .Append(members + 3).Append("\n  /Length 0\n  /W [ 1 1 1 ]\n>>\nstream\n\0\0\0\nendstream\nendobj\n\nstartxref\n0\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
