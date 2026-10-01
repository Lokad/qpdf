using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
namespace Lokad.Qpdf.Tests.Content;
public sealed class FlattenAnnotationsTests
{
private static string NL()
{
return new string((char)10, 1);
}
private static string SP()
{
return new string((char)32, 1);
}
private static byte[] BuildPdf(string catalogExtra, string pageExtra, List<(string Dict, byte[]? StreamData)> extras)
{
List<string> bodies = new()
{
string.Concat("<< /Type /Catalog /Pages 2 0 R", catalogExtra, " >>"),
"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
string.Concat("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]", pageExtra, " >>"),
};
foreach ((string dict, byte[]? _) in extras)
{
bodies.Add(dict);
}
List<byte[]?> payloads = new() { null, null, null };
foreach ((string _, byte[]? streamData) in extras)
{
payloads.Add(streamData);
}
List<byte[]> parts = new();
byte[] header = Encoding.ASCII.GetBytes(string.Concat("%PDF-1.4", NL()));
parts.Add(header);
List<long> offsets = new();
long position = header.Length;
for (int i = 0; i < bodies.Count; i++)
{
int number = i + 1;
byte[]? streamData = payloads[i];
byte[] open = Encoding.ASCII.GetBytes(string.Concat(number.ToString(), SP(), "0 obj", NL(), bodies[i], NL()));
if (streamData is null)
{
byte[] close = Encoding.ASCII.GetBytes(string.Concat("endobj", NL()));
offsets.Add(position);
position += open.Length + close.Length;
parts.Add(open);
parts.Add(close);
continue;
}
byte[] streamOpen = Encoding.ASCII.GetBytes(string.Concat("stream", NL()));
byte[] streamClose = Encoding.ASCII.GetBytes(string.Concat(NL(), "endstream", NL(), "endobj", NL()));
offsets.Add(position);
position += open.Length + streamOpen.Length + streamData.Length + streamClose.Length;
parts.Add(open);
parts.Add(streamOpen);
parts.Add(streamData);
parts.Add(streamClose);
}
int size = bodies.Count + 1;
StringBuilder xref = new();
xref.Append(string.Concat("xref", NL(), "0 ", size.ToString(), NL()));
xref.Append(string.Concat("0000000000 65535 f ", NL()));
foreach (long offset in offsets)
{
xref.Append(string.Concat(offset.ToString("D10"), " 00000 n ", NL()));
}
xref.Append(string.Concat("trailer", NL(), "<< /Size ", size.ToString(), " /Root 1 0 R >>", NL()));
xref.Append(string.Concat("startxref", NL(), position.ToString(), NL(), "%%EOF", NL()));
parts.Add(Encoding.ASCII.GetBytes(xref.ToString()));
int total = 0;
foreach (byte[] part in parts)
{
total += part.Length;
}
byte[] output = new byte[total];
int cursor = 0;
foreach (byte[] part in parts)
{
Buffer.BlockCopy(part, 0, output, cursor, part.Length);
cursor += part.Length;
}
return output;
}
private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
{
InMemoryQpdfHost host = new();
host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
return host;
}
private static async Task<(QpdfExecutionResult Result, byte[] Pdf)> RunAsync(InMemoryQpdfHost host, params string[] flags)
{
List<string> argv = new() { "inputs/a.pdf" };
argv.AddRange(flags);
argv.AddRange(["--", "outputs/a.pdf"]);
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
StringBuilder failure = new();
failure.Append(result.Status.ToString());
foreach (QpdfDiagnostic diagnostic in result.Diagnostics)
{
failure.Append((char)124);
failure.Append(diagnostic.Code.ToString());
}
Assert.True(result.Status == QpdfExecutionStatus.Completed, failure.ToString());
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
return (result, pdf);
}

private static async Task<byte[]> PageContentAsync(byte[] pdf)
{
InMemoryQpdfHost host = new();
host.AddInput(QpdfPath.From("inputs/r.pdf"), pdf, QpdfInputOwnership.Copy);
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/r.pdf", "--json", "--json-stream-data=inline", "--", "outputs/r.json"]), CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/r.json"), out byte[] json));
using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(json));
JsonElement objects = document.RootElement.GetProperty("qpdf")[1];
JsonElement page = document.RootElement.GetProperty("pages")[0];
List<byte> joined = new();
foreach (JsonElement content in page.GetProperty("contents").EnumerateArray())
{
string reference = content.GetString() ?? string.Empty;
string[] parts = reference.Split((char)32);
string key = string.Concat("obj:", parts[0], SP(), parts[1], " R");
string data = objects.GetProperty(key).GetProperty("stream").GetProperty("data").GetString() ?? string.Empty;
joined.AddRange(Convert.FromBase64String(data));
}
return [.. joined];
}
private static async Task<JsonDocument> InspectAsync(byte[] pdf, params string[] flags)
{
InMemoryQpdfHost host = new();
host.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
List<string> argv = new() { "inputs/a.pdf" };
argv.AddRange(flags);
argv.AddRange(["--", "outputs/a.json"]);
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] json));
return JsonDocument.Parse(Encoding.UTF8.GetString(json));
}
private static string PageObjectKey(JsonElement page)
{
string reference = page.GetProperty("object").GetString() ?? string.Empty;
string[] parts = reference.Split((char)32);
return string.Concat("obj:", parts[0], SP(), parts[1], " R");
}
private static async Task<byte[]> FormContentAsync(byte[] pdf, string formName)
{
using JsonDocument pages = await InspectAsync(pdf, "--json", "--json-key=pages");
JsonElement page = pages.RootElement.GetProperty("pages")[0];
string pageKey = PageObjectKey(page);
using JsonDocument full = await InspectAsync(pdf, "--json", "--json-key=qpdf", "--json-stream-data=inline");
JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
JsonElement resources = objects.GetProperty(pageKey).GetProperty("value").GetProperty("/Resources");
if (resources.ValueKind == JsonValueKind.String)
{
string[] linked = (resources.GetString() ?? string.Empty).Split((char)32);
resources = objects.GetProperty(string.Concat("obj:", linked[0], SP(), linked[1], " R")).GetProperty("value");
}
JsonElement xobjects = resources.GetProperty("/XObject");
string formReference = xobjects.GetProperty(formName).GetString() ?? string.Empty;
string[] formParts = formReference.Split((char)32);
string formKey = string.Concat("obj:", formParts[0], SP(), formParts[1], " R");
string data = objects.GetProperty(formKey).GetProperty("stream").GetProperty("data").GetString() ?? string.Empty;
return Convert.FromBase64String(data);
}
private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
{
Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
Assert.Equal(QpdfExitCode.Error, result.ExitCode);
return Assert.Single(result.Diagnostics);
}
private static byte[] TextFixture(string pageExtra, List<(string Dict, byte[]? StreamData)> extras)
{
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
List<(string Dict, byte[]? StreamData)> all = new()
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
};
all.AddRange(extras);
return BuildPdf(string.Empty, string.Concat(" /Contents 4 0 R", pageExtra), all);
}

[Fact]
public async Task StampTextAnnotationOnTop()
{
byte[] appearance = Encoding.ASCII.GetBytes("BT (Note) Tj ET");
byte[] fixture = TextFixture(" /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
("<< /Type /Annot /Subtype /Text /Rect [0 0 100 50] /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 100 50] /Length ", appearance.Length.ToString(), " >>"), appearance),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-annotations=all")).Pdf;
byte[] content = await PageContentAsync(pdf);
string text = Encoding.ASCII.GetString(content);
Assert.Contains("(Hi)", text);
byte[] stamp = await FormContentAsync(pdf, "/Fxo1");
Assert.Contains("(Note)", Encoding.ASCII.GetString(stamp));
using JsonDocument pages = await InspectAsync(pdf, "--json", "--json-key=pages");
using JsonDocument full = await InspectAsync(pdf, "--json", "--json-key=qpdf");
JsonElement value = full.RootElement.GetProperty("qpdf")[1].GetProperty(PageObjectKey(pages.RootElement.GetProperty("pages")[0])).GetProperty("value");
Assert.False(value.TryGetProperty("/Annots", out JsonElement _));
}
[Fact]
public async Task AppearanceWithoutSubtypeBecomesForm()
{
byte[] appearance = Encoding.ASCII.GetBytes("BT (Note) Tj ET");
byte[] fixture = TextFixture(" /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
("<< /Type /Annot /Subtype /Text /Rect [0 0 100 50] /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /BBox [0 0 100 50] /Length ", appearance.Length.ToString(), " >>"), appearance),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-annotations=all")).Pdf;
using JsonDocument pages = await InspectAsync(pdf, "--json", "--json-key=pages");
using JsonDocument full = await InspectAsync(pdf, "--json", "--json-key=qpdf");
JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
JsonElement resources = objects.GetProperty(PageObjectKey(pages.RootElement.GetProperty("pages")[0])).GetProperty("value").GetProperty("/Resources");
string reference = resources.GetProperty("/XObject").GetProperty("/Fxo1").GetString() ?? string.Empty;
string[] parts = reference.Split((char)32);
string formKey = string.Concat("obj:", parts[0], SP(), parts[1], " R");
JsonElement form = objects.GetProperty(formKey).GetProperty("stream").GetProperty("dict");
Assert.Equal("/Form", form.GetProperty("/Subtype").GetString());
}
[Theory]
[InlineData("--flatten-annotations=print", 4, true)]
[InlineData("--flatten-annotations=print", 0, false)]
[InlineData("--flatten-annotations=all", 0, true)]
public async Task PrintModeFiltersOnFlag(string flag, int bits, bool stamped)
{
byte[] appearance = Encoding.ASCII.GetBytes("BT (Note) Tj ET");
string annot = bits == 0
? "<< /Type /Annot /Subtype /Text /Rect [0 0 100 50] /AP << /N 6 0 R >> >>"
: string.Concat("<< /Type /Annot /Subtype /Text /Rect [0 0 100 50] /F ", bits.ToString(), " /AP << /N 6 0 R >> >>");
byte[] fixture = TextFixture(" /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
(annot, null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 100 50] /Length ", appearance.Length.ToString(), " >>"), appearance),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", flag)).Pdf;
byte[] content = await PageContentAsync(pdf);
if (stamped)
{
Assert.Contains("/Fxo1 Do", Encoding.ASCII.GetString(content));
}
else
{
Assert.DoesNotContain("/Fxo1 Do", Encoding.ASCII.GetString(content));
}
}
[Theory]
[InlineData("--flatten-annotations=screen", 32, false)]
[InlineData("--flatten-annotations=screen", 0, true)]
[InlineData("--flatten-annotations=all", 34, true)]
public async Task ScreenModeSkipsNoView(string flag, int bits, bool stamped)
{
byte[] appearance = Encoding.ASCII.GetBytes("BT (Note) Tj ET");
string annot = bits == 0
? "<< /Type /Annot /Subtype /Text /Rect [0 0 100 50] /AP << /N 6 0 R >> >>"
: string.Concat("<< /Type /Annot /Subtype /Text /Rect [0 0 100 50] /F ", bits.ToString(), " /AP << /N 6 0 R >> >>");
byte[] fixture = TextFixture(" /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
(annot, null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 100 50] /Length ", appearance.Length.ToString(), " >>"), appearance),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", flag)).Pdf;
byte[] content = await PageContentAsync(pdf);
if (stamped)
{
Assert.Contains("/Fxo1 Do", Encoding.ASCII.GetString(content));
}
else
{
Assert.DoesNotContain("/Fxo1 Do", Encoding.ASCII.GetString(content));
}
}

[Fact]
public async Task LinkWithoutAppearanceIsKept()
{
byte[] fixture = TextFixture(" /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
("<< /Type /Annot /Subtype /Link /Rect [0 0 100 50] /A << /S /URI /URI (https://example.test) >> >>", null),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-annotations=all")).Pdf;
using JsonDocument pages = await InspectAsync(pdf, "--json", "--json-key=pages");
using JsonDocument full = await InspectAsync(pdf, "--json", "--json-key=qpdf");
JsonElement value = full.RootElement.GetProperty("qpdf")[1].GetProperty(PageObjectKey(pages.RootElement.GetProperty("pages")[0])).GetProperty("value");
Assert.True(value.TryGetProperty("/Annots", out JsonElement _));
byte[] content = await PageContentAsync(pdf);
Assert.DoesNotContain("/Fxo1 Do", Encoding.ASCII.GetString(content));
}
[Fact]
public async Task UncheckedCheckboxIsDropped()
{
byte[] off = Encoding.ASCII.GetBytes("0 w 0 0 10 10 re S");
byte[] fixture = TextFixture(" /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /FT /Btn /AS /Yes /AP << /N << /Off 6 0 R >> >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length ", off.Length.ToString(), " >>"), off),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-annotations=all")).Pdf;
using JsonDocument pages = await InspectAsync(pdf, "--json", "--json-key=pages");
using JsonDocument full = await InspectAsync(pdf, "--json", "--json-key=qpdf");
JsonElement value = full.RootElement.GetProperty("qpdf")[1].GetProperty(PageObjectKey(pages.RootElement.GetProperty("pages")[0])).GetProperty("value");
Assert.False(value.TryGetProperty("/Annots", out JsonElement _));
byte[] content = await PageContentAsync(pdf);
Assert.DoesNotContain("/Fxo1 Do", Encoding.ASCII.GetString(content));
}
[Fact]
public async Task WidgetSkippedWhenAppearancesNeeded()
{
byte[] on = Encoding.ASCII.GetBytes("BT (On) Tj ET");
byte[] fixture = BuildPdf(" /AcroForm << /NeedAppearances true >>", " /Contents 4 0 R /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", Encoding.ASCII.GetBytes("BT (Hi) Tj ET").Length.ToString(), " >>"), Encoding.ASCII.GetBytes("BT (Hi) Tj ET")),
("<< /Type /Annot /Subtype /Widget /Rect [0 0 100 50] /FT /Tx /V (Hello) /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 100 50] /Length ", on.Length.ToString(), " >>"), on),
});
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--flatten-annotations=all", "--", "outputs/a.pdf" };
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == QpdfDiagnosticCode.UnsupportedFeature);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
using JsonDocument pages = await InspectAsync(pdf, "--json", "--json-key=pages");
using JsonDocument full = await InspectAsync(pdf, "--json", "--json-key=qpdf");
JsonElement value = full.RootElement.GetProperty("qpdf")[1].GetProperty(PageObjectKey(pages.RootElement.GetProperty("pages")[0])).GetProperty("value");
Assert.True(value.TryGetProperty("/Annots", out JsonElement _));
}
[Fact]
public async Task EmptyAnnotsArrayLeavesPageAlone()
{
byte[] fixture = TextFixture(" /Annots []", new List<(string Dict, byte[]? StreamData)>());
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-annotations=all")).Pdf;
byte[] content = await PageContentAsync(pdf);
Assert.DoesNotContain("/Fxo1 Do", Encoding.ASCII.GetString(content));
Assert.Contains("(Hi)", Encoding.ASCII.GetString(content));
}

[Fact]
public async Task NoRotateFlagCounterRotatesPlacement()
{
byte[] appearance = Encoding.ASCII.GetBytes("BT (Note) Tj ET");
byte[] plain = TextFixture(" /Rotate 90 /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
("<< /Type /Annot /Subtype /Text /Rect [0 0 100 50] /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 100 50] /Length ", appearance.Length.ToString(), " >>"), appearance),
});
byte[] rotated = TextFixture(" /Rotate 90 /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
("<< /Type /Annot /Subtype /Text /Rect [0 0 100 50] /F 16 /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 100 50] /Length ", appearance.Length.ToString(), " >>"), appearance),
});
byte[] plainPdf = (await RunAsync(HostWith("inputs/a.pdf", plain), "--pages", ".", "1", "--flatten-annotations=all")).Pdf;
byte[] rotatedPdf = (await RunAsync(HostWith("inputs/a.pdf", rotated), "--pages", ".", "1", "--flatten-annotations=all")).Pdf;
string plainText = Encoding.ASCII.GetString(await PageContentAsync(plainPdf));
string rotatedText = Encoding.ASCII.GetString(await PageContentAsync(rotatedPdf));
Assert.Contains("/Fxo1 Do", plainText);
Assert.Contains("/Fxo1 Do", rotatedText);
Assert.NotEqual(plainText, rotatedText);
}
[Fact]
public async Task MissingResourcesAreCreated()
{
byte[] appearance = Encoding.ASCII.GetBytes("BT (Note) Tj ET");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /Annot /Subtype /Text /Rect [0 0 100 50] /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 100 50] /Length ", appearance.Length.ToString(), " >>"), appearance),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-annotations=all")).Pdf;
byte[] output = await PageContentAsync(pdf);
Assert.Contains("/Fxo1 Do", Encoding.ASCII.GetString(output));
}
[Theory]
[InlineData("--flatten-annotations", "--flatten-annotations=all")]
[InlineData("--flatten-annotations=x")]
[InlineData("--flatten-annotations=")]
public async Task MalformedFlattenFlagsFail(params string[] flags)
{
byte[] fixture = TextFixture(string.Empty, new List<(string Dict, byte[]? StreamData)>());
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
List<string> argv = new() { "inputs/a.pdf" };
argv.AddRange(flags);
argv.AddRange(["--", "outputs/a.pdf"]);
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
}
[Fact]
public async Task FlattenRejectedWithInspection()
{
byte[] fixture = TextFixture(string.Empty, new List<(string Dict, byte[]? StreamData)>());
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--flatten-annotations=all"]), CancellationToken.None);
Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
}
[Fact]
public async Task JobRoundTripMatchesArgv()
{
byte[] appearance = Encoding.ASCII.GetBytes("BT (Note) Tj ET");
byte[] fixture = TextFixture(" /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
("<< /Type /Annot /Subtype /Text /Rect [0 0 100 50] /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 100 50] /Length ", appearance.Length.ToString(), " >>"), appearance),
});
byte[] a = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-annotations=print")).Pdf;
InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", fixture);
string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "flattenAnnotations": "print"}""";
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(jobHost, job, CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
Assert.Equal(a, b);
}
[Theory]
[InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "flattenAnnotations": "sometimes"}""")]
[InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "flattenAnnotations": true}""")]
[InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "flattenAnnotations": "all", "flattenAnnotations": "all"}""")]
public async Task JobMalformedFails(string jobJson)
{
byte[] fixture = TextFixture(string.Empty, new List<(string Dict, byte[]? StreamData)>());
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(HostWith("inputs/a.pdf", fixture), jobJson, CancellationToken.None);
Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
}
[Fact]
public async Task WholeFileJobFlattens()
{
byte[] appearance = Encoding.ASCII.GetBytes("BT (Note) Tj ET");
byte[] fixture = TextFixture(" /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
("<< /Type /Annot /Subtype /Text /Rect [0 0 100 50] /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 100 50] /Length ", appearance.Length.ToString(), " >>"), appearance),
});
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "flattenAnnotations": "screen"}""";
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
Assert.Contains("/Fxo1 Do", Encoding.ASCII.GetString(await PageContentAsync(pdf)));
}
[Fact]
public async Task MergeThenFlattenAppliesToFinalPages()
{
byte[] appearance = Encoding.ASCII.GetBytes("BT (Note) Tj ET");
byte[] fixture = TextFixture(" /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
("<< /Type /Annot /Subtype /Text /Rect [0 0 100 50] /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 100 50] /Length ", appearance.Length.ToString(), " >>"), appearance),
});
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
host.AddInput(QpdfPath.From("inputs/b.pdf"), fixture, QpdfInputOwnership.Copy);
List<string> argv = new() { "inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--flatten-annotations=all", "--", "outputs/a.pdf" };
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
using JsonDocument pages = await InspectAsync(pdf, "--json", "--json-key=pages");
Assert.Equal(2, pages.RootElement.GetProperty("pages").GetArrayLength());
byte[] content = await PageContentAsync(pdf);
Assert.Contains("/Fxo1 Do", Encoding.ASCII.GetString(content));
}
}

