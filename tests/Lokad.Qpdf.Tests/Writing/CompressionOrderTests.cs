using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class CompressionOrderTests
{
    private static readonly string[] Routes = ["argv", "job", "mixed", "chain"];
    private static readonly byte[] Page = "q /Fm1 Do /Im1 Do Q\nBI /W 4 /H 1 /BPC 8 /CS /G ID abcd EI\n"u8.ToArray();
    private static readonly byte[] Form = "q Q\nBI /W 4 /H 1 /BPC 8 /CS /G ID abcd EI\n"u8.ToArray();

    // Encodings observed on qpdf 12.4.2, including lexical job-key order.
    private static readonly string[][] Cases =
    [
        ["", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["sd:preserve", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode"],
        ["sd:preserve;cs:y", "FlateDecode", "ASCIIHexDecode", "FlateDecode", "ASCIIHexDecode"],
        ["cs:y;sd:preserve", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode"],
        ["sd:preserve;cs:n", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode"],
        ["cs:n;sd:preserve", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode"],
        ["sd:preserve;dl:none", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode"],
        ["dl:none;sd:preserve", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode"],
        ["sd:preserve;dl:generalized", "", "ASCIIHexDecode", "", "ASCIIHexDecode"],
        ["dl:generalized;sd:preserve", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode"],
        ["sd:preserve;dl:specialized", "", "ASCIIHexDecode", "", "ASCIIHexDecode"],
        ["dl:specialized;sd:preserve", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode"],
        ["sd:preserve;dl:all", "", "ASCIIHexDecode", "", "ASCIIHexDecode"],
        ["dl:all;sd:preserve", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode"],
        ["sd:compress", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["sd:compress;cs:y", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["cs:y;sd:compress", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["sd:compress;cs:n", "", "FlateDecode", "", "FlateDecode"],
        ["cs:n;sd:compress", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["sd:compress;dl:none", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["dl:none;sd:compress", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["sd:compress;dl:generalized", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["dl:generalized;sd:compress", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["sd:compress;dl:specialized", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["dl:specialized;sd:compress", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["sd:compress;dl:all", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["dl:all;sd:compress", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["sd:uncompress", "", "", "", ""],
        ["sd:uncompress;cs:y", "FlateDecode", "", "FlateDecode", ""],
        ["cs:y;sd:uncompress", "", "", "", ""],
        ["sd:uncompress;cs:n", "", "", "", ""],
        ["cs:n;sd:uncompress", "", "", "", ""],
        ["sd:uncompress;dl:none", "ASCIIHexDecode", "", "ASCIIHexDecode", ""],
        ["dl:none;sd:uncompress", "", "", "", ""],
        ["sd:uncompress;dl:generalized", "", "", "", ""],
        ["dl:generalized;sd:uncompress", "", "", "", ""],
        ["sd:uncompress;dl:specialized", "", "", "", ""],
        ["dl:specialized;sd:uncompress", "", "", "", ""],
        ["sd:uncompress;dl:all", "", "", "", ""],
        ["dl:all;sd:uncompress", "", "", "", ""],
        ["sd:compress;sd:preserve", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode"],
        ["cs:y;cs:n", "", "", "", "FlateDecode"],
        ["dl:none;dl:all", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["cl:5;cl:6", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["rf:;rf:", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["qdf:;sd:preserve", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode"],
        ["sd:preserve;qdf:", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode"],
        ["qdf:;sd:compress", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["sd:compress;qdf:", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["qdf:;cs:y", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["cs:y;qdf:", "FlateDecode", "FlateDecode", "FlateDecode", "FlateDecode"],
        ["qdf:;cs:n", "", "", "", ""],
        ["cs:n;qdf:", "", "", "", ""],
        ["qdf:;dl:none", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode"],
        ["dl:none;qdf:", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode", "ASCIIHexDecode"],
        ["qdf:;dl:all", "", "", "", ""],
        ["dl:all;qdf:", "", "", "", ""],
    ];

    public static IEnumerable<object[]> OrderedCases()
    {
        foreach (string[] row in Cases)
        {
            for (int route = 0; route < Routes.Length; route++)
            { yield return [row[0], Routes[route], row[route + 1]]; }
        }
    }

    private static byte[] Fixture()
    {
        static (string Header, byte[] Data) Hex(byte[] data)
        {
            byte[] encoded = Encoding.ASCII.GetBytes(Convert.ToHexString(data).ToLowerInvariant() + ">");
            return ("<< /Length " + encoded.Length + " /Filter /ASCIIHexDecode", encoded);
        }
        (string pageHeader, byte[] page) = Hex(Page);
        (string formHeader, byte[] form) = Hex(Form);
        (string imageHeader, byte[] image) = Hex([127]);
        return CheckTests.Assemble(
        [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents 4 0 R /Resources << /XObject << /Fm1 5 0 R /Im1 6 0 R >> >> >>", null),
            (4, pageHeader + " /TestKind /PageContent >>", page),
            (5, formHeader + " /TestKind /Form /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << >> >>", form),
            (6, imageHeader + " /TestKind /Image /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray >>", image),
        ], 1);
    }

    private static string Key(string key) => key switch
    {
        "sd" => "streamData", "cs" => "compressStreams", "dl" => "decodeLevel",
        "cl" => "compressionLevel", "rf" => "recompressFlate", "qdf" => "qdf",
        _ => throw new ArgumentException(key),
    };

    private static string Flag(string key) => key switch
    {
        "sd" => "stream-data", "cs" => "compress-streams", "dl" => "decode-level",
        "cl" => "compression-level", "rf" => "recompress-flate", "qdf" => "qdf",
        _ => throw new ArgumentException(key),
    };

    private static List<(string Key, string Value)> Setters(string packed)
        => packed.Length == 0 ? [] : packed.Split(';').Select(part =>
        { int at = part.IndexOf(':'); return (part[..at], part[(at + 1)..]); }).ToList();

    private static IEnumerable<string> Flags(IEnumerable<(string Key, string Value)> setters)
        => setters.Select(pair => "--" + Flag(pair.Key) + (pair.Value.Length == 0 ? "" : "=" + pair.Value));

    private static Dictionary<string, object> Settings(IEnumerable<(string Key, string Value)> setters)
    {
        Dictionary<string, object> settings = new()
        {
            ["objectStreams"] = "disable", ["removeUnreferencedResources"] = "no",
            ["normalizeContent"] = "n", ["iiMinBytes"] = "0",
        };
        foreach ((string key, string value) in setters) { settings[Key(key)] = value; }
        return settings;
    }

    private static void AddJson(InMemoryQpdfHost host, string path, Dictionary<string, object> settings)
        => host.AddInput(QpdfPath.From(path), JsonSerializer.SerializeToUtf8Bytes(settings), QpdfInputOwnership.Copy);

    private static async Task<QpdfExecutionResult> Execute(InMemoryQpdfHost host, string route,
        string packed, string mode, QpdfLimits limits, CancellationToken token)
    {
        List<(string Key, string Value)> setters = Setters(packed);
        if (route == "job" || route == "chain")
        {
            int middle = route == "chain" ? setters.Count / 2 : 0;
            Dictionary<string, object> job = Settings(setters.Skip(middle));
            job["inputFile"] = "inputs/a.pdf"; job["outputFile"] = "outputs/a.pdf";
            if (mode != "whole")
            {
                job["pages"] = mode == "multi"
                    ? new[] { new { file = ".", range = "1" }, new { file = "inputs/b.pdf", range = "1" } }
                    : new[] { new { file = ".", range = "1" } };
            }
            if (route == "chain")
            {
                AddJson(host, "inputs/child.json", Settings(setters.Take(middle)));
                job["jobJsonFile"] = "inputs/child.json";
            }
            return await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), new QpdfExecutionOptions(limits), token);
        }
        List<string> argv = ["inputs/a.pdf"];
        if (route == "mixed")
        {
            int middle = setters.Count / 2;
            AddJson(host, "inputs/settings.json", Settings(setters.Take(middle)));
            argv.Add("--job-json-file=inputs/settings.json");
            argv.AddRange(Flags(setters.Skip(middle)));
        }
        else
        {
            argv.AddRange(["--object-streams=disable", "--remove-unreferenced-resources=no", "--normalize-content=n", "--ii-min-bytes=0"]);
            argv.AddRange(Flags(setters));
        }
        if (mode != "whole")
        {
            argv.AddRange(["--pages", ".", "1"]);
            if (mode == "multi") { argv.AddRange(["inputs/b.pdf", "1"]); }
            argv.Add("--");
        }
        argv.Add("outputs/a.pdf");
        return await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), new QpdfExecutionOptions(limits), token);
    }

    private static InMemoryQpdfHost Host()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), Fixture(), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("inputs/b.pdf"), Fixture(), QpdfInputOwnership.Copy);
        return host;
    }

    private static async Task AssertStreams(InMemoryQpdfHost host, string expected, int count)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] bytes));
        host.AddInput(QpdfPath.From("inputs/output.pdf"), bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/output.pdf"), CancellationToken.None);
        int seen = 0;
        for (int number = 1; number < 20; number++)
        {
            QpdfObject value;
            try { value = document.GetObject(number); }
            catch (QpdfParseException) { continue; }
            if (value is not PdfStream stream || !stream.Dictionary.TryGet(PdfName.FromAscii("TestKind"), out QpdfObject kind)) { continue; }
            string name = Assert.IsType<PdfName>(kind).DisplayText;
            byte[] raw = name switch { "PageContent" => Page, "Form" => Form, "Image" => [127], _ => throw new ArgumentException(name) };
            bool filtered = stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject filter);
            Assert.Equal(expected.Length != 0, filtered);
            if (filtered) { Assert.Equal(expected, Assert.IsType<PdfName>(filter).DisplayText); }
            byte[] data = stream.Data;
            if (expected == "ASCIIHexDecode") { data = Convert.FromHexString(Encoding.ASCII.GetString(data)[..^1]); }
            if (expected == "FlateDecode")
            {
                using MemoryStream input = new(data);
                using ZLibStream decoder = new(input, CompressionMode.Decompress);
                using MemoryStream decoded = new(); decoder.CopyTo(decoded); data = decoded.ToArray();
            }
            Assert.Equal(raw, data); seen++;
        }
        Assert.Equal(count, seen);
    }

    [Theory]
    [MemberData(nameof(OrderedCases))]
    public async Task SettersChoosePageFormAndImageEncoding(string setters, string route, string expected)
    {
        InMemoryQpdfHost host = Host();
        QpdfExecutionResult result = await Execute(host, route, setters, "whole", QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        await AssertStreams(host, expected, 3);
    }

    [Theory]
    [InlineData("argv", "selection", "FlateDecode")]
    [InlineData("job", "selection", "ASCIIHexDecode")]
    [InlineData("mixed", "selection", "FlateDecode")]
    [InlineData("chain", "selection", "ASCIIHexDecode")]
    [InlineData("argv", "multi", "FlateDecode")]
    [InlineData("job", "multi", "ASCIIHexDecode")]
    [InlineData("mixed", "multi", "FlateDecode")]
    [InlineData("chain", "multi", "ASCIIHexDecode")]
    public async Task SelectedPlansKeepEffectiveConfiguration(string route, string mode, string expected)
    {
        InMemoryQpdfHost host = Host();
        QpdfExecutionResult result = await Execute(host, route, "sd:preserve;cs:y", mode, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await AssertStreams(host, expected, mode == "multi" ? 6 : 3);
    }

    [Fact]
    public async Task TypedSelectionKeepsOpaqueEncoding()
    {
        InMemoryQpdfHost host = Host();
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.pdf"),
            PdfPageSelectionOptions.FromRange("1"), CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        Assert.Empty(result.Warnings);
        await AssertStreams(host, "ASCIIHexDecode", 3);
    }

    public static IEnumerable<object[]> InvalidLayers()
    {
        foreach (string setter in new[] { "sd:invalid;sd:preserve", "cs:maybe;cs:n", "dl:invalid;dl:none", "cl:0;cl:5" })
        {
            foreach (string route in Routes) { yield return [setter, route]; }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidLayers))]
    public async Task InvalidEarlierSetterCannotBeOverwritten(string setters, string route)
    {
        InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Execute(host, route, setters, "whole", QpdfLimits.Default, CancellationToken.None);
        if (route == "job")
        {
            // Duplicate JSON members collapse before configuration; the final value survives.
            Assert.Equal(QpdfDiagnosticCode.HostOperationFailed, Assert.Single(result.Diagnostics).Code);
        }
        else
        {
            Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        }
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("mixed", false)]
    [InlineData("chain", false)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    [InlineData("mixed", true)]
    [InlineData("chain", true)]
    public async Task LimitsAndCancellationPreservePriorOutput(string route, bool cancel)
    {
        InMemoryQpdfHost host = Host();
        byte[] prior = [9, 8, 7];
        await using (IQpdfOutputTransaction transaction = await host.CreateOutputAsync(QpdfPath.From("outputs/a.pdf"), CancellationToken.None))
        { await transaction.WriteAsync(prior, CancellationToken.None); await transaction.CommitAsync(CancellationToken.None); }
        using CancellationTokenSource source = new();
        if (cancel) { source.Cancel(); }
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Execute(host, route, "sd:preserve;cs:y", "whole", limits, source.Token);
        Assert.Equal(cancel ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        if (cancel) { Assert.Empty(result.Diagnostics); }
        else { Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(result.Diagnostics).Code); }
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] actual));
        Assert.Equal(prior, actual);
    }
}
