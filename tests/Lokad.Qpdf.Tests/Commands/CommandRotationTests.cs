using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class CommandRotationTests
{
    public static IEnumerable<object[]> Settings()
    {
        (string First, string Second, string Selection, int[] Expected)[] cases =
        [
            ("+90:1", "+90:1", "1", [90]),
            ("+90:1", "-90:1", "1", [270]),
            ("90:1-1", "180:1", "1", [90]),
            ("180:1", "90:1-1", "1", [90]),
            ("90:1", "+90:01", "1", [90]),
            ("+90:1", "+90:01", "1", [180]),
            ("+90", "+90:1-z", "1", [90]),
            ("+90:", "+90:1-z", "1", [90]),
            ("+90:1,1", "+90:1", "1", [270]),
            ("+90:1-1", "90:1", "1", [180]),
            ("+90:1-z", "+90:2", "1,1,1", [90, 180, 90]),
            ("+90:3", "+90:1", "1,1,1", [90, 0, 90]),
            ("+90:1-2", "180:2-3", "1,1,1", [90, 180, 180]),
            ("+90:1,1,3", "180:2", "1,1,1", [180, 180, 90]),
            ("90:2-3", "+90:1-z", "1,1,1", [90, 90, 90]),
        ];
        foreach (string route in new[] { "argv", "response", "job", "mixed" })
        foreach (var item in cases) { yield return [route, item.First, item.Second, item.Selection, item.Expected]; }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task CommandSettingsUseExactRangeKeysInOrdinalOrder(string route,
        string first, string second, string selection, int[] expected)
    {
        using InMemoryQpdfHost host = CreateHost();
        QpdfExecutionResult result;
        if (route == "job")
        {
            result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
            {
                inputFile = "input", outputFile = "output", rotate = new[] { first, second },
                pages = new[] { new { file = ".", range = selection } },
            }), CancellationToken.None);
        }
        else
        {
            List<string> args = ["input"];
            if (route == "mixed")
            {
                host.AddInput(QpdfPath.From("settings"), JsonSerializer.SerializeToUtf8Bytes(new { rotate = new[] { first } }), QpdfInputOwnership.Copy);
                args.Add("--job-json-file=settings");
            }
            else { args.Add("--rotate=" + first); }
            args.AddRange(["--rotate=" + second, "--pages", ".", selection, "--", "output"]);
            if (route == "response")
            {
                host.AddInput(QpdfPath.From("args"), Encoding.UTF8.GetBytes(string.Join('\n', args)), QpdfInputOwnership.Copy);
                args = ["@args"];
            }
            result = await Qpdf.ExecuteAsync(host, new(args), CancellationToken.None);
        }
        Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(0L, host.OpenLeaseCount);
        await AssertRotations(host, expected);
    }

    [Theory]
    [InlineData("argv", false)]
    [InlineData("argv", true)]
    [InlineData("job", false)]
    [InlineData("job", true)]
    public async Task InspectionUsesTheSameCommandRotationSettings(string route, bool check)
    {
        using InMemoryQpdfHost host = CreateHost();
        Dictionary<string, object> job = new()
        {
            ["inputFile"] = "input", ["showObject"] = "3", ["rotate"] = new[] { "+90:1", "+90:1" },
        };
        if (check) { job["check"] = ""; }
        QpdfExecutionResult result = route == "job"
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--show-object=3", "--rotate=+90:1", "--rotate=+90:1", .. (check ? new[] { "--check" } : [])]), CancellationToken.None);
        Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        string listing = Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message;
        Assert.Contains("/Rotate 90", listing, StringComparison.Ordinal);
        Assert.Null(result.OutputBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, "set-add", 180)]
    [InlineData(false, "add-set", 90)]
    [InlineData(false, "add-add", 180)]
    [InlineData(true, "set-add", 180)]
    [InlineData(true, "add-set", 90)]
    [InlineData(true, "add-add", 180)]
    public async Task TypedRotationListsKeepExplicitOrderAndRepeatedEntries(bool multi, string order, int expected)
    {
        using InMemoryQpdfHost host = CreateHost();
        PdfPageRotation add = new(PdfRotationMode.Add, 90, [1]);
        PdfPageRotation set = new(PdfRotationMode.Set, 90, [1]);
        PdfPageRotation[] rotations = order switch { "set-add" => [set, add], "add-set" => [add, set], _ => [add, add] };
        if (multi)
        {
            await Qpdf.SelectPagesAsync(host,
                [new PdfPageSource(QpdfPath.From("input"), new PdfPageSelectionOptions(1))],
                QpdfPath.From("output"), rotations, CancellationToken.None);
        }
        else
        {
            await Qpdf.SelectPageAsync(host, QpdfPath.From("input"), QpdfPath.From("output"),
                new PdfPageSelectionOptions(1), rotations, CancellationToken.None);
        }
        await AssertRotations(host, [expected]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommandOrderingRefusesOrCancelsBeforeLargeScratchAllocation(bool canceled)
    {
        OutputRotation rotation = OutputRotation.Parse("+90:1", 0L, null);
        List<OutputRotation> rotations = Enumerable.Repeat(rotation, 10000).ToList();
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 1024L }, cancellation.Token);
        if (canceled) { cancellation.Cancel(); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        Action normalize = () => OutputRotation.NormalizeCommandSettings(rotations, governor);
        if (canceled) { Assert.ThrowsAny<OperationCanceledException>(normalize); }
        else { Assert.Throws<QpdfLimitException>(normalize); }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0L, 65536L);
        Assert.Equal(10000, rotations.Count);
        Assert.Same(rotation, rotations[0]); Assert.Same(rotation, rotations[^1]);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private static InMemoryQpdfHost CreateHost()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        return host;
    }

    private static async Task AssertRotations(InMemoryQpdfHost host, int[] expected)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("reopen"), bytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reopen"), CancellationToken.None))
        {
            Assert.Equal(expected.Length, document.PageCount);
            for (int i = 0; i < expected.Length; i++)
            {
                PdfDictionary page = document.PageReferences[i].Page;
                long rotation = page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject value) ? Assert.IsType<PdfInteger>(value).Value : 0L;
                Assert.Equal(expected[i], rotation);
            }
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
