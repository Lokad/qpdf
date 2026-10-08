using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using UglyToad.PdfPig;

namespace Lokad.Qpdf.Tests.Writing;

/// <summary>Complete native hints checked against independently measured output bytes.</summary>
public sealed class LinearizedHintNativeTests
{
    private static readonly JsonObject Corpus = Load();

    private static JsonObject Load()
    {
        using Stream stream = typeof(LinearizedHintNativeTests).Assembly.GetManifestResourceStream(
            "Lokad.Qpdf.Tests.Writing.LinearizedHintNativeCases.json") ?? throw new InvalidOperationException();
        return Assert.IsType<JsonObject>(JsonNode.Parse(stream));
    }

    private static string Text(JsonNode? value) => value?.GetValue<string>() ?? throw new InvalidOperationException();

    public static IEnumerable<object[]> NativeCases()
    {
        foreach (JsonObject row in Assert.IsType<JsonArray>(Corpus["Cases"]).OfType<JsonObject>())
        { yield return [Text(row["Id"])]; }
    }

    [Theory]
    [MemberData(nameof(NativeCases))]
    public async Task EntireHintMatchesNativeFieldsAndItsPhysicalLayout(string id)
    {
        JsonObject row = Assert.IsType<JsonArray>(Corpus["Cases"]).OfType<JsonObject>().Single(item => Text(item["Id"]) == id);
        string hash = Text(row["Fixture"]);
        byte[] fixture = Convert.FromBase64String(Text(Corpus["Fixtures"]?[hash]));
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(fixture)));
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), fixture, QpdfInputOwnership.Copy);
        foreach ((string path, JsonNode? value) in Assert.IsType<JsonObject>(row["Files"]))
        { host.AddInput(QpdfPath.From(path), Encoding.UTF8.GetBytes(Text(value)), QpdfInputOwnership.Copy); }
        QpdfExecutionResult result = id.EndsWith("-job", StringComparison.Ordinal) || id.EndsWith("-chain", StringComparison.Ordinal)
            ? await Qpdf.ExecuteJobAsync(host, Text(row["Files"]?["inputs/job.json"]), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(Assert.IsType<JsonArray>(row["Args"]).Select(Text).ToArray()), CancellationToken.None);
        Assert.Equal((QpdfExitCode)(row["NativeExit"]?.GetValue<int>() ?? throw new InvalidOperationException()), result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        Assert.Equal(0L, host.OpenLeaseCount);
        (byte[] actual, PdfDictionary header) = await HintAsync(pdf);
        Assert.Equal((long)(row["NativeHint"]?["Header"]?["/S"]?.GetValue<int>() ?? throw new InvalidOperationException()), Integer(header, "S"));
        JsonObject nativeEncoded = Assert.IsType<JsonObject>(row["NativeEncodedHint"]?["Header"]);
        Assert.Equal(nativeEncoded.ContainsKey("/Filter"), header.TryGet(PdfName.FromAscii("Filter"), out QpdfObject filter));
        if (nativeEncoded.ContainsKey("/Filter")) { Assert.Equal("FlateDecode", Assert.IsType<PdfName>(filter).DisplayText); }
        Assert.Equal(new[] { "Length", "S" }.Concat(nativeEncoded.ContainsKey("/Filter") ? ["Filter"] : Array.Empty<string>()).Order(), header.Keys.Select(key => key.DisplayText).Order());

        string text = Encoding.Latin1.GetString(pdf);
        Match linear = Regex.Match(text, @"/Linearized\s+1\s+/L\s+(\d+)\s+/H\s*\[\s*(\d+)\s+(\d+)\s*\]\s+/O\s+(\d+)\s+/E\s+(\d+)\s+/N\s+(\d+)", RegexOptions.CultureInvariant);
        Assert.True(linear.Success);
        int Number(int group) => int.Parse(linear.Groups[group].Value, CultureInfo.InvariantCulture);
        Assert.Equal(pdf.Length, Number(1));
        Assert.Equal(1, Number(6));
        Dictionary<int, (int Offset, int Length)> objects = Regex.Matches(text, @"(?m)^(\d+) 0 obj\n", RegexOptions.CultureInvariant)
            .ToDictionary(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match =>
                (match.Index, text.IndexOf("endobj\n", match.Index, StringComparison.Ordinal) + "endobj\n".Length - match.Index));
        int page = Number(4);
        int[] lengths = Enumerable.Range(page, 4).Select(number => objects[number].Length).ToArray();
        for (int number = page; number < page + 3; number++)
        { Assert.Equal(objects[number + 1].Offset, objects[number].Offset + objects[number].Length); }
        Assert.Equal(Number(5), objects[page + 3].Offset + objects[page + 3].Length);
        Assert.Contains(objects.Values, value => value.Offset == Number(2) && value.Length == Number(3));

        byte[] native = Convert.FromBase64String(Text(row["NativeHint"]?["Data"]));
        byte[] expected = ExpectedForLayout(native, objects[page].Offset - Number(3), lengths);
        Assert.Equal(expected, actual);
        using PdfDocument reread = PdfDocument.Open(new MemoryStream(pdf));
        Assert.Equal(1, reread.NumberOfPages);
    }

    // The one-page native table has no page delta arrays. Preserve its entire
    // header and shared header, replacing only physically determined fields.
    // Group bits are packed here independently of the library hint encoder.
    private static byte[] ExpectedForLayout(byte[] native, int firstPageOffset, int[] lengths)
    {
        Assert.Equal(4U, BinaryPrimitives.ReadUInt32BigEndian(native));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(native.AsSpan(32)));
        Assert.Equal(4U, BinaryPrimitives.ReadUInt32BigEndian(native.AsSpan(44)));
        Assert.Equal(4U, BinaryPrimitives.ReadUInt32BigEndian(native.AsSpan(48)));
        int minimum = lengths.Min();
        int difference = lengths.Max() - minimum;
        int width = difference == 0 ? 0 : 32 - int.LeadingZeroCount(difference);
        byte[] expected = new byte[60 + (4 * width + 7) / 8 + 1];
        native.AsSpan(0, 60).CopyTo(expected);
        BinaryPrimitives.WriteUInt32BigEndian(expected.AsSpan(4), checked((uint)firstPageOffset));
        BinaryPrimitives.WriteUInt32BigEndian(expected.AsSpan(10), checked((uint)lengths.Sum()));
        BinaryPrimitives.WriteUInt32BigEndian(expected.AsSpan(22), checked((uint)lengths.Sum()));
        BinaryPrimitives.WriteUInt32BigEndian(expected.AsSpan(54), checked((uint)minimum));
        BinaryPrimitives.WriteUInt16BigEndian(expected.AsSpan(58), checked((ushort)width));
        int bit = 60 * 8;
        foreach (int length in lengths)
        {
            int delta = length - minimum;
            for (int position = width - 1; position >= 0; position--, bit++)
            { expected[bit / 8] |= (byte)(((delta >> position) & 1) << (7 - bit % 8)); }
        }
        return expected;
    }

    private static long Integer(PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return Assert.IsType<PdfInteger>(value).Value;
    }

    private static async Task<(byte[] Data, PdfDictionary Header)> HintAsync(byte[] pdf)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("reread.pdf"), pdf, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread.pdf"), CancellationToken.None);
        PdfStream hint = Assert.Single(document.Engine.XRefEntries.Keys.Where(number => number != 0).Select(document.GetObject).OfType<PdfStream>(),
            stream => stream.Dictionary.TryGet(PdfName.FromAscii("S"), out _));
        byte[] data = hint.Data;
        if (hint.Dictionary.TryGet(PdfName.FromAscii("Filter"), out _))
        {
            using MemoryStream input = new(data);
            using ZLibStream decoder = new(input, CompressionMode.Decompress);
            using MemoryStream output = new();
            decoder.CopyTo(output);
            data = output.ToArray();
        }
        return (data, hint.Dictionary);
    }

    public static IEnumerable<object[]> SharedPageCases()
    {
        foreach (string fixture in new[] { "minimal-2page.pdf", "forms-2page.pdf", "outlined-2page.pdf" })
        foreach (string mode in new[] { "disable", "generate" })
        foreach (bool compress in new[] { false, true })
        foreach (bool job in new[] { false, true })
        { yield return [fixture, mode, compress, job]; }
    }

    [Theory]
    [MemberData(nameof(SharedPageCases))]
    public async Task SharedPagesUseZeroWidthNumeratorsAndRemainReadable(string fixture, string mode, bool compress, bool job)
    {
        byte[] data = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input.pdf"), data, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, new JsonObject
            {
                ["inputFile"] = "input.pdf", ["outputFile"] = "output.pdf", ["linearize"] = "",
                ["objectStreams"] = mode, ["compressStreams"] = compress ? "y" : "n"
            }.ToJsonString(), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation([
                "input.pdf", "--linearize", "--object-streams=" + mode, "--compress-streams=" + (compress ? "y" : "n"), "output.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] pdf));
        (byte[] hint, _) = await HintAsync(pdf);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(hint.AsSpan(32)));
        using PdfDocument reread = PdfDocument.Open(new MemoryStream(pdf));
        Assert.Equal(2, reread.NumberOfPages);
        InMemoryQpdfHost checkHost = new(); checkHost.AddInput(QpdfPath.From("check.pdf"), pdf, QpdfInputOwnership.Copy);
        QpdfCheckResult check = await Qpdf.CheckAsync(checkHost, QpdfPath.From("check.pdf"), CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(check.Checks, outcome => outcome.Name == "linearization").Status);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, checkHost.OpenLeaseCount);
    }
}
