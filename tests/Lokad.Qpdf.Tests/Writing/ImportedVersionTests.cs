using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ImportedVersionTests
{
    public static IEnumerable<object[]> PlacementCases()
    {
        foreach (string source in new[] { "header", "catalog", "indirect" })
        foreach (bool job in new[] { false, true })
        foreach (bool pages in new[] { false, true })
        foreach (bool underlay in new[] { false, true })
        { yield return [source, job, pages, underlay, "ordinary"]; }
        foreach (string mode in new[] { "force", "minimum", "minimum-lower", "empty", "packed", "linear" })
        foreach (bool job in new[] { false, true })
        { yield return [mode == "linear" ? "indirect" : "catalog", job, false, false, mode]; }
    }

    [Theory]
    [MemberData(nameof(PlacementCases))]
    public async Task PlacementsIncludeEffectiveSourceVersionsBeforeMinimumAndForce(
        string source, bool job, bool pages, bool underlay, string mode)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("1.3", "header", false, false), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("source"), Fixture(source == "header" ? "1.7" : "1.3", source, false, false), QpdfInputOwnership.Transfer);
        List<string> argv = ["input"];
        Dictionary<string, object> fields = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
        if (pages)
        { argv.AddRange(["--pages", ".", "1", "--"]); fields["pages"] = new[] { new { file = ".", range = "1" } }; }
        string operation = underlay ? "underlay" : "overlay";
        Dictionary<string, object> placement = new() { ["file"] = "source" };
        argv.AddRange(["--" + operation, "source"]);
        if (mode == "empty") { argv.Add("--to=:even"); placement["to"] = ":even"; }
        argv.Add("--"); fields[operation] = placement;
        if (mode == "force") { argv.Add("--force-version=1.3"); fields["forceVersion"] = "1.3"; }
        if (mode is "minimum" or "minimum-lower")
        {
            string minimum = mode == "minimum" ? "2.0" : "1.4";
            argv.Add("--min-version=" + minimum); fields["minVersion"] = minimum;
        }
        if (mode == "packed") { argv.Add("--object-streams=generate"); fields["objectStreams"] = "generate"; }
        if (mode == "linear") { argv.Add("--linearize"); fields["linearize"] = true; }
        argv.Add("output");
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        string expected = mode == "force" ? "1.3" : mode == "minimum" ? "2.0" : "1.7";
        AssertHeader(host, expected);
    }

    public static IEnumerable<object[]> AuthoredCases()
    {
        foreach (string route in new[] { "argv", "job", "typed" })
        foreach (string header in new[] { "1.0", "1.2", "1.3", "1.4", "1.7", "2.0" })
        foreach (string feature in new[] { "attachment", "labels" })
        { yield return [route, header, feature, false]; }
        foreach (string route in new[] { "argv", "job" })
        foreach (string header in new[] { "1.0", "1.7" })
        foreach (string feature in new[] { "attachment", "labels" })
        { yield return [route, header, feature, true]; }
    }

    [Theory]
    [MemberData(nameof(AuthoredCases))]
    public async Task AuthoredStructuresSetKnownVersionFloorsAndForceStillWins(string route, string header, string feature, bool force)
    {
        using InMemoryQpdfHost host = new(); QpdfPath input = QpdfPath.From("input"), output = QpdfPath.From("output");
        host.AddInput(input, Fixture(header, "header", false, false), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("payload"), [19], QpdfInputOwnership.Transfer);
        if (route == "typed")
        {
            if (feature == "attachment")
            { await Qpdf.AddAttachmentsAsync(host, input, output, [new("payload", "K", null, null, null, null, null, false)], CancellationToken.None); }
            else
            {
                await Qpdf.SelectPagesAsync(host, [new(input, new PdfPageSelectionOptions(1))], output,
                    new PdfPageMergeOptions { Labels = [new(1, PdfPageLabelStyle.Decimal, null, null)] }, CancellationToken.None);
            }
        }
        else
        {
            List<string> argv = ["input"];
            Dictionary<string, object> fields = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
            if (feature == "attachment")
            {
                argv.AddRange(["--add-attachment", "payload", "--key=K", "--"]);
                fields["addAttachment"] = new[] { new { file = "payload", key = "K" } };
            }
            else { argv.AddRange(["--set-page-labels", "1:D", "--"]); fields["setPageLabels"] = new[] { "1:D" }; }
            if (force) { argv.Add("--force-version=1.2"); fields["forceVersion"] = "1.2"; }
            argv.Add("output");
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        string expected = force ? "1.2" : feature == "attachment" ? header == "2.0" ? "2.0" : "1.7"
            : header is "1.0" or "1.2" ? "1.3" : header;
        AssertHeader(host, expected);
    }

    public static IEnumerable<object[]> AttachmentCopyCases()
    {
        foreach (string route in new[] { "argv", "job", "typed" })
        foreach (string version in new[] { "header", "catalog", "indirect", "higher-header" })
        foreach (bool empty in new[] { false, true })
        { yield return [route, version, empty, false]; }
        foreach (string route in new[] { "argv", "job" })
        { yield return [route, "higher-header", false, true]; }
    }

    [Theory]
    [MemberData(nameof(AttachmentCopyCases))]
    public async Task AttachmentImportsRetainValidatedSourceVersionRequirements(string route, string version, bool empty, bool force)
    {
        using InMemoryQpdfHost host = new(); QpdfPath input = QpdfPath.From("input"), output = QpdfPath.From("output");
        host.AddInput(input, Fixture("1.3", "header", false, false), QpdfInputOwnership.Transfer);
        string sourceHeader = version == "higher-header" ? "2.0" : version == "header" ? "1.7" : "1.3";
        host.AddInput(QpdfPath.From("source"), Fixture(sourceHeader, version, !empty, false), QpdfInputOwnership.Transfer);
        if (route == "typed")
        { await Qpdf.CopyAttachmentsAsync(host, input, output, [new(QpdfPath.From("source"), null)], CancellationToken.None); }
        else
        {
            List<string> argv = ["input", "--copy-attachments-from", "source", "--"];
            Dictionary<string, object> fields = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["copyAttachmentsFrom"] = new[] { new { file = "source" } } };
            if (force) { argv.Add("--force-version=1.2"); fields["forceVersion"] = "1.2"; }
            argv.Add("output");
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        AssertHeader(host, force ? "1.2" : version == "higher-header" ? "2.0" : "1.7");
    }

    [Theory]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("typed", false)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    [InlineData("typed", true)]
    public async Task RebuiltSupportedTreesHaveTheirOwnVersionFloors(string route, bool labels)
    {
        using InMemoryQpdfHost host = new(); QpdfPath input = QpdfPath.From("input"), output = QpdfPath.From("output");
        host.AddInput(input, Fixture("1.0", "header", !labels, labels), QpdfInputOwnership.Transfer);
        if (route == "typed") { await Qpdf.SelectPageAsync(host, input, output, new(1), CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output" }), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        AssertHeader(host, labels ? "1.3" : "1.4");
    }

    private static void AssertHeader(InMemoryQpdfHost host, string expected)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        Assert.Equal("%PDF-" + expected, Encoding.ASCII.GetString(bytes, 0, 8)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NullLabelTreesDoNotRaiseTheBasicWriterProfile(bool job, bool indirect)
    {
        List<(int, string, byte[]?)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /PageLabels " + (indirect ? "4 0 R" : "null") + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null),
            (4, "null", null),
        ];
        byte[] bytes = CheckTests.Assemble(objects, 1); bytes[7] = (byte)'0';
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), bytes, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output" }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); AssertHeader(host, "1.2");
    }

    public static IEnumerable<object[]> BasicWriterCases()
    {
        foreach (string route in new[] { "argv", "job" })
        foreach (string mode in new[] { "ordinary", "uncompress", "linear", "qdf", "packed", "force" })
        { yield return [route, mode]; }
        yield return ["typed", "ordinary"];
    }

    [Theory]
    [MemberData(nameof(BasicWriterCases))]
    public async Task BasicWriterProfileDeclares12UnlessExplicitlyForced(string route, string mode)
    {
        using InMemoryQpdfHost host = new(); QpdfPath input = QpdfPath.From("input"), output = QpdfPath.From("output");
        host.AddInput(input, Fixture("1.0", "header", false, false), QpdfInputOwnership.Transfer);
        if (route == "typed") { await Qpdf.SelectPageAsync(host, input, output, new(1), CancellationToken.None); }
        else
        {
            List<string> argv = ["input"];
            Dictionary<string, object> fields = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
            if (mode == "uncompress") { argv.Add("--stream-data=uncompress"); fields["streamData"] = "uncompress"; }
            if (mode is "linear" or "qdf")
            { string flag = mode == "linear" ? "linearize" : "qdf"; argv.Add("--" + flag); fields[flag] = true; }
            if (mode == "packed") { argv.Add("--object-streams=generate"); fields["objectStreams"] = "generate"; }
            if (mode == "force") { argv.Add("--force-version=1.0"); fields["forceVersion"] = "1.0"; }
            argv.Add("output");
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        AssertHeader(host, mode == "force" ? "1.0" : mode == "packed" ? "1.5" : "1.2");
    }

    private static byte[] Fixture(string header, string version, bool attachment, bool labels)
    {
        string declaration = version switch { "catalog" => " /Version /1.7", "indirect" => " /Version 5 0 R", _ => "" };
        if (attachment) { declaration += " /Names << /EmbeddedFiles << /Names [(K) 6 0 R] >> >>"; }
        if (labels) { declaration += " /PageLabels << /Nums [0 << /S /D >>] >>"; }
        List<(int, string, byte[]?)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R" + declaration + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Contents 4 0 R >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()),
        ];
        if (version == "indirect") { objects.Add((5, "/1.7", null)); }
        if (attachment)
        {
            if (version != "indirect") { objects.Add((5, "null", null)); }
            objects.Add((6, "<< /Type /Filespec /F (K) /EF << /F 7 0 R >> >>", null));
            objects.Add((7, "<< /Length 1 >>", [19]));
        }
        byte[] bytes = CheckTests.Assemble(objects, 1);
        bytes[5] = (byte)header[0]; bytes[7] = (byte)header[2]; return bytes;
    }
}
