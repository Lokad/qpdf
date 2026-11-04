using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class ObjectStreamIntegerTests
{
    private const string First = "<< /Marker (Keep) /Shared 7 0 R >>";
    private const string Second = "<< /Marker (Shared) >>";

    public static IEnumerable<object[]> Spellings()
    {
        foreach (string shape in new[] { "plain", "numbers", "offsets", "both", "padded", "negative-zero", "long-zero" })
        foreach (bool flate in new[] { false, true }) { yield return [shape, flate]; }
    }

    private static byte[] Fixture(string shape, bool flate)
    {
        string number = shape is "numbers" or "both" ? "+6" : shape == "long-zero" ? "+" + new string('0', 10000) + "6" : shape == "padded" ? "0006" : "6";
        string zero = shape is "offsets" or "both" ? "+0" : shape == "negative-zero" ? "-0" : "0";
        string last = (Encoding.ASCII.GetByteCount(First) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string pairs = number + " " + zero + " " + (shape is "numbers" or "both" ? "+7" : "7") + " "
            + (shape is "offsets" or "both" ? "+" : "") + last + " ";
        return ReaderAcceptanceTests.ObjectStreamPdf(First, Second, flate, true, pairs, "/Resources << >> /Private 6 0 R");
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public async Task EquivalentHeaderIntegersRetainMembersAndEscapedValues(string shape, bool flate)
    {
        RecordingMemoryBudget budget = new(16L * 1048576L);
        using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        host.AddInput(QpdfPath.From("input"), Fixture(shape, flate), QpdfInputOwnership.Transfer);
        PdfDictionary escaped;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None))
        {
            escaped = Assert.IsType<PdfDictionary>(document.GetObject(6));
            Assert.Same(escaped, document.GetObject(6));
            Assert.Equal("Keep", Marker(escaped)); Assert.Equal("Shared", Marker(Assert.IsType<PdfDictionary>(document.GetObject(7))));
        }
        host.Dispose(); Assert.Equal("Keep", Marker(escaped));
        owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public async Task BothCommandRoutesCopyTheSameMembers(string shape, bool flate)
    {
        foreach (bool job in new[] { false, true })
        {
            using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(shape, flate), QpdfInputOwnership.Transfer);
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","suppressRecovery":true,"pages":[{"file":".","range":"1"}]}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--suppress-recovery", "--pages", ".", "1", "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
            {
                Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Private"), out QpdfObject value));
                PdfReference link = Assert.IsType<PdfReference>(value); PdfDictionary member = Assert.IsType<PdfDictionary>(document.GetObject(link.ObjectNumber));
                Assert.Equal("Keep", Marker(member)); Assert.True(member.TryGet(PdfName.FromAscii("Shared"), out QpdfObject shared));
                Assert.Equal("Shared", Marker(Assert.IsType<PdfDictionary>(document.GetObject(Assert.IsType<PdfReference>(shared).ObjectNumber))));
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
    }

    [Theory]
    [InlineData("0 0 7 1 ")]
    [InlineData("-6 0 7 1 ")]
    [InlineData("6 -1 7 1 ")]
    [InlineData("2147483648 0 7 1 ")]
    [InlineData("+9223372036854775808 0 7 1 ")]
    [InlineData("6 +2147483648 7 1 ")]
    [InlineData("6 0 7 0 ")]
    public async Task StructuralAndNumericBoundsStillReject(string header)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), ReaderAcceptanceTests.ObjectStreamPdf(First, Second, true, true, header), QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None))
        {
            Assert.Throws<QpdfParseException>(() => document.GetObject(6));
            Assert.IsType<PdfStream>(document.GetObject(4));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static string Marker(PdfDictionary dictionary)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii("Marker"), out QpdfObject value));
        return Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(value).ValueMemory.Span);
    }
}
