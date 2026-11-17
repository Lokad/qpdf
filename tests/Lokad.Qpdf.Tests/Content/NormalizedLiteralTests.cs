using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Content;

public sealed class NormalizedLiteralTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return ["(a\r\nb\\015\\012)", "a\nb\r\n", "(a\\nb\\r\\n)", "core", true];
        yield return ["(\\q\\1\\77)", "q\x01?", "(q\\001?)", "argv", false];
        yield return ["(a(b)c)", "a(b)c", "(a\\(b\\)c)", "job", false];
        yield return ["(a\0b\rc)", "a\0b\nc", "(a\\000b\\nc)", "qdf-argv", true];
        yield return ["(" + new string('x', 8191) + "\r\nY)", new string('x', 8191) + "\nY",
            "(" + new string('x', 8191) + "\\nY)", "qdf-job", true];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NormalizationKeepsLiteralSemanticBytes(string body, string expected, string encoded, string route, bool marker)
    {
        byte[] content = [.. "/Span << /ActualText "u8, .. Encoding.Latin1.GetBytes(body), .. " >> BDC EMC\n"u8];
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            byte[] normalized;
            if (route == "core")
            { normalized = ContentTransforms.NormalizeContentBytesOwned(content, null, null, QpdfLimits.Default, governor, new(governor), CancellationToken.None,
                QpdfLimits.Default.MaxDecodedBytes, nameof(QpdfLimits.MaxDecodedBytes), memory, out _); }
            else
            {
                byte[] source = PdfTestDocument.Assemble([
                    (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
                    (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R >>", null),
                    (4, "<< /Length " + content.Length + " >>", content)], 1);
                using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Copy);
                bool qdf = route.StartsWith("qdf", StringComparison.Ordinal);
                Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["streamData"] = "uncompress" };
                settings[qdf ? "qdf" : "normalizeContent"] = qdf ? true : "y";
                QpdfExecutionResult result = route.EndsWith("job", StringComparison.Ordinal)
                    ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
                    : await Qpdf.ExecuteAsync(host, new(["input", qdf ? "--qdf" : "--normalize-content=y", "--stream-data=uncompress", "output"]), CancellationToken.None);
                Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
                Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
                Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject raw));
                PdfReference link = Assert.IsType<PdfReference>(raw); normalized = Assert.IsType<PdfStream>(document.GetObject(link.ObjectNumber)).RawMemory.ToArray();
            }
            ContentTokenizer tokenizer = new(normalized, governor, CancellationToken.None); int literals = 0;
            while (tokenizer.TryReadToken(out ContentToken token))
            {
                if (token.Kind != ContentTokenKind.LiteralString) { continue; }
                literals++;
                Assert.Equal(Encoding.Latin1.GetBytes(encoded), token.Data.ToArray());
                Assert.NotEqual(Encoding.Latin1.GetBytes(body), token.Data.ToArray());
                int after = checked((int)token.Offset + token.Data.Length);
                Assert.Equal(marker, after < normalized.Length && normalized[after] == (byte)'\n');
                PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
                PdfByteString value = Assert.IsType<PdfByteString>(parser.ParseValue(new PdfTokenizer(token.Data), 0, memory));
                Assert.Equal(Encoding.Latin1.GetBytes(expected), value.ValueMemory.ToArray());
            }
            Assert.Equal(1, literals);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherCancellationRoutePrecedesNewStorage(bool governorRoute)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 256 }, governorRoute ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(61L);
        long baseline = governor.Snapshot().LiveMemoryBytes; cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ContentTransforms.NormalizeContentBytesOwned("(a\r\nb)"u8.ToArray(), null, null, QpdfLimits.Default, governor, new(governor),
            governorRoute ? CancellationToken.None : cancellation.Token, 1048576, "test", memory, out _));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }
}
