using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class MixedFlattenFieldProjectionTests
{
    private static byte[] Source() => PdfTestDocument.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 8 0 R /AuditForm 8 0 R /AuditField 5 0 R >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R 4 17 R] /Count 2 /MediaBox [0 0 100 100] /Resources << >> >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /Annots [6 0 R] /Contents 10 0 R >>", null),
        (4, "<< /Type /Page /Parent 2 0 R /Annots [7 0 R] /Contents 11 0 R >>", null),
        (5, "<< /FT /Tx /T (Shared) /V (hello) /Kids [6 0 R 7 0 R] >>", null),
        (6, "<< /Type /Annot /Subtype /Widget /Rect [10 10 30 30] /Parent 5 0 R /P 3 0 R /AP << /N 9 0 R >> /Audit (left) >>", null),
        (7, "<< /Type /Annot /Subtype /Widget /Rect [10 10 30 30] /Parent 5 0 R /P 4 17 R /Audit (right) >>", null),
        (8, "<< /Fields [5 0 R] /CO [5 0 R] >>", null),
        (9, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Resources << >> /Length 15 >>", "0 0 10 10 re f\n"u8.ToArray()),
        (10, "<< /Length 18 >>", "10 10 m 20 20 l S\n"u8.ToArray()),
        (11, "<< /Length 18 >>", "11 11 m 22 22 l S\n"u8.ToArray()),
    ], 1, new Dictionary<int, int> { [4] = 17 });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedFlattenKeepsPaintAndProjectsTheActiveWidget(bool write)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("source"), Source(), QpdfInputOwnership.Transfer);
        string[] arguments = ["source", "--flatten-annotations=all"];
        if (write)
        {
            QpdfExecutionResult result = await Run(host, [.. arguments, "flattened"]);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.True(host.TryGetOutput(QpdfPath.From("flattened"), out byte[] output));
            host.AddInput(QpdfPath.From("reopen"), output, QpdfInputOwnership.Transfer);
            arguments = ["reopen"];
        }

        QpdfExecutionResult report = await Run(host,
            [.. arguments, "--json-output=2", "--json-stream-data=inline", "--decode-level=all"]);
        Assert.Equal(QpdfExitCode.Success, report.ExitCode);
        using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(report.OutputText));
        JsonElement table = json.RootElement.GetProperty("qpdf")[1];
        JsonElement catalog = Resolve(table, table.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        JsonElement pages = Resolve(table, catalog.GetProperty("/Pages")).GetProperty("/Kids");
        JsonElement painted = Resolve(table, pages[0]);
        JsonElement surviving = Resolve(table, pages[1]);
        Assert.False(painted.TryGetProperty("/Annots", out _));
        JsonElement contents = Resolve(table, painted.GetProperty("/Contents"));
        string placement = Encoding.ASCII.GetString(Payload(table, contents[contents.GetArrayLength() - 1]));
        Assert.Contains("/Fxo1 Do", placement, StringComparison.Ordinal);
        JsonElement resources = Resolve(table, painted.GetProperty("/Resources"));
        JsonElement xobjects = Resolve(table, resources.GetProperty("/XObject"));
        Assert.Equal("0 0 10 10 re f\n"u8.ToArray(), Payload(table, xobjects.GetProperty("/Fxo1")));

        JsonElement form = Resolve(table, catalog.GetProperty("/AcroForm"));
        JsonElement fieldRef = Assert.Single(Resolve(table, form.GetProperty("/Fields")).EnumerateArray());
        JsonElement field = Resolve(table, fieldRef);
        JsonElement widgetRef = Assert.Single(Resolve(table, field.GetProperty("/Kids")).EnumerateArray());
        JsonElement widget = Resolve(table, widgetRef);
        Assert.Equal(fieldRef.GetString(), widget.GetProperty("/Parent").GetString());
        Assert.Equal(pages[1].GetString(), widget.GetProperty("/P").GetString());
        Assert.Equal(widgetRef.GetString(), Assert.Single(Resolve(table, surviving.GetProperty("/Annots")).EnumerateArray()).GetString());
        Assert.Equal(fieldRef.GetString(), Assert.Single(Resolve(table, form.GetProperty("/CO")).EnumerateArray()).GetString());
        Assert.Equal("u:hello", field.GetProperty("/V").GetString());
        JsonElement privateFieldRef = catalog.GetProperty("/AuditField");
        Assert.NotEqual(fieldRef.GetString(), privateFieldRef.GetString());
        JsonElement privateField = Resolve(table, privateFieldRef);
        JsonElement privateChildren = Resolve(table, privateField.GetProperty("/Kids"));
        Assert.Equal(2, privateChildren.GetArrayLength());
        Assert.Equal("u:left", Resolve(table, privateChildren[0]).GetProperty("/Audit").GetString());
        Assert.Equal("u:right", Resolve(table, privateChildren[1]).GetProperty("/Audit").GetString());
        Assert.Equal(privateFieldRef.GetString(), Assert.Single(Resolve(table, Resolve(table, catalog.GetProperty("/AuditForm"))
            .GetProperty("/Fields")).EnumerateArray()).GetString());
    }

    private static ValueTask<QpdfExecutionResult> Run(InMemoryQpdfHost host, string[] arguments)
        => Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(arguments), CancellationToken.None);

    private static JsonElement Resolve(JsonElement table, JsonElement element)
        => element.ValueKind == JsonValueKind.String ? table.GetProperty("obj:" + element.GetString()).GetProperty("value") : element;

    private static byte[] Payload(JsonElement table, JsonElement link)
        => table.GetProperty("obj:" + link.GetString()).GetProperty("stream").GetProperty("data").GetBytesFromBase64();
}
