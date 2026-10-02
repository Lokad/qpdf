using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
namespace Lokad.Qpdf.Tests.Content;
public sealed class FlattenRotationTests
{
private static string NL()
{
return new string((char)10, 1);
}
private static string SP()
{
return new string((char)32, 1);
}
private static byte[] BuildPdf(string pagesExtra, string pageExtra, List<(string Dict, byte[]? StreamData)> extras)
{
List<string> bodies = new()
{
"<< /Type /Catalog /Pages 2 0 R >>",
"<< /Type /Pages /Kids [3 0 R] /Count 1" + pagesExtra + " >>",
"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" + pageExtra + " >>",
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
private static byte[] ContentFixture(string pageExtra)
{
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
return BuildPdf(string.Empty, string.Concat(" /Contents 4 0 R", pageExtra), new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
});
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
private static async Task<JsonDocument> PageJsonAsync(byte[] pdf)
{
InMemoryQpdfHost host = new();
host.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
List<string> argv = new() { "inputs/a.pdf", "--json", "--json-key=pages", "--", "outputs/a.json" };
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] json));
return JsonDocument.Parse(Encoding.UTF8.GetString(json));
}
private static async Task<JsonDocument> QpdfJsonAsync(byte[] pdf)
{
InMemoryQpdfHost host = new();
host.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
List<string> argv = new() { "inputs/a.pdf", "--json", "--json-key=qpdf", "--", "outputs/a.json" };
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
private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
{
Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
Assert.Equal(QpdfExitCode.Error, result.ExitCode);
return Assert.Single(result.Diagnostics);
}

[Fact]
public async Task Flatten90RotatesBoxesAndWrapsContent()
{
byte[] fixture = ContentFixture(" /Rotate 90");
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-rotation")).Pdf;
using JsonDocument pages = await PageJsonAsync(pdf);
JsonElement page = pages.RootElement.GetProperty("pages")[0];
using JsonDocument rotated = await QpdfJsonAsync(pdf);
JsonElement rotatedValue = rotated.RootElement.GetProperty("qpdf")[1].GetProperty(PageObjectKey(page)).GetProperty("value");
Assert.False(rotatedValue.TryGetProperty("/Rotate", out JsonElement _));
byte[] content = await PageContentAsync(pdf);
string text = Encoding.ASCII.GetString(content);
Assert.Contains("0 -1 1 0 0 612 cm", text);
Assert.Contains("(Hi)", text);
using JsonDocument full = await QpdfJsonAsync(pdf);
JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
JsonElement value = objects.GetProperty(PageObjectKey(page)).GetProperty("value");
Assert.Equal("792", value.GetProperty("/MediaBox")[2].GetRawText());
}
[Fact]
public async Task Flatten180KeepsMediaBox()
{
byte[] fixture = ContentFixture(" /Rotate 180");
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-rotation")).Pdf;
byte[] content = await PageContentAsync(pdf);
Assert.Contains("-1 0 0 -1 612 792 cm", Encoding.ASCII.GetString(content));
using JsonDocument pages = await PageJsonAsync(pdf);
JsonElement page = pages.RootElement.GetProperty("pages")[0];
using JsonDocument rotated = await QpdfJsonAsync(pdf);
JsonElement rotatedValue = rotated.RootElement.GetProperty("qpdf")[1].GetProperty(PageObjectKey(page)).GetProperty("value");
Assert.False(rotatedValue.TryGetProperty("/Rotate", out JsonElement _));
}
[Fact]
public async Task Flatten270SwapsBoxes()
{
byte[] fixture = ContentFixture(" /Rotate 270");
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-rotation")).Pdf;
byte[] content = await PageContentAsync(pdf);
Assert.Contains("0 1 -1 0 792 0 cm", Encoding.ASCII.GetString(content));
using JsonDocument pages = await PageJsonAsync(pdf);
JsonElement page = pages.RootElement.GetProperty("pages")[0];
using JsonDocument full = await QpdfJsonAsync(pdf);
JsonElement value = full.RootElement.GetProperty("qpdf")[1].GetProperty(PageObjectKey(page)).GetProperty("value");
Assert.Equal("792", value.GetProperty("/MediaBox")[2].GetRawText());
Assert.Equal("612", value.GetProperty("/MediaBox")[3].GetRawText());
}
[Theory]
[InlineData("")]
[InlineData(" /Rotate 0")]
[InlineData(" /Rotate 360")]
[InlineData(" /Rotate 45")]
public async Task NonActionableRotationKeepsPage(string pageExtra)
{
byte[] fixture = ContentFixture(pageExtra);
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-rotation")).Pdf;
byte[] content = await PageContentAsync(pdf);
Assert.DoesNotContain(" cm", Encoding.ASCII.GetString(content));
Assert.Contains("(Hi)", Encoding.ASCII.GetString(content));
}
[Fact]
public async Task InheritedRotationFlattens()
{
byte[] fixture = BuildPdf(" /Rotate 90", " /Contents 4 0 R", new List<(string Dict, byte[]? StreamData)>
{
("<< /Length 16 >>", Encoding.ASCII.GetBytes("BT (Page) Tj ET")),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-rotation")).Pdf;
byte[] content = await PageContentAsync(pdf);
Assert.Contains("0 -1 1 0 0 612 cm", Encoding.ASCII.GetString(content));
}

[Fact]
public async Task CropBoxRotatesWithPage()
{
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(string.Empty, string.Concat(" /Contents 4 0 R /CropBox [10 20 600 700] /Rotate 90"), new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-rotation")).Pdf;
using JsonDocument full = await QpdfJsonAsync(pdf);
using JsonDocument pages = await PageJsonAsync(pdf);
JsonElement value = full.RootElement.GetProperty("qpdf")[1].GetProperty(PageObjectKey(pages.RootElement.GetProperty("pages")[0])).GetProperty("value");
string crop = value.GetProperty("/CropBox").GetRawText();
Assert.Equal("[20,12,700,602]", crop);
}
[Fact]
public async Task AnnotationRectanglesMoveWithPage()
{
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(string.Empty, string.Concat(" /Contents 4 0 R /Rotate 90 /Annots [5 0 R]"), new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /Annot /Subtype /Text /Rect [0 0 100 100] >>", null),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-rotation")).Pdf;
using JsonDocument full = await QpdfJsonAsync(pdf);
using JsonDocument pages = await PageJsonAsync(pdf);
JsonElement value = full.RootElement.GetProperty("qpdf")[1].GetProperty(PageObjectKey(pages.RootElement.GetProperty("pages")[0])).GetProperty("value");
JsonElement annots = value.GetProperty("/Annots");
Assert.Equal(1, annots.GetArrayLength());
string annotReference = annots[0].GetString() ?? string.Empty;
string[] annotParts = annotReference.Split((char)32);
string annotKey = string.Concat("obj:", annotParts[0], SP(), annotParts[1], " R");
JsonElement annotValue = full.RootElement.GetProperty("qpdf")[1].GetProperty(annotKey).GetProperty("value");
Assert.Equal("[0,512,100,612]", annotValue.GetProperty("/Rect").GetRawText());
}
[Fact]
public async Task DamagedContentWarnsAndKeepsPage()
{
byte[] garbage = Encoding.ASCII.GetBytes("(a");
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R /Rotate 90", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", garbage.Length.ToString(), " /Filter /FlateDecode >>"), garbage),
});
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--flatten-rotation", "--", "outputs/a.pdf" };
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.True(result.ExitCode == QpdfExitCode.Success || result.ExitCode == QpdfExitCode.Warning);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
}
[Theory]
[InlineData("--flatten-rotation", "--flatten-rotation")]
[InlineData("--flatten-rotation=x")]
public async Task MalformedFlattenFlagsFail(params string[] flags)
{
byte[] fixture = ContentFixture(string.Empty);
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
byte[] fixture = ContentFixture(string.Empty);
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--flatten-rotation"]), CancellationToken.None);
Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
}
[Fact]
public async Task JobRoundTripMatchesArgv()
{
byte[] fixture = ContentFixture(" /Rotate 90");
byte[] a = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-rotation")).Pdf;
InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", fixture);
string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "flattenRotation": true}""";
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(jobHost, job, CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
Assert.Equal(a, b);
}
[Theory]
[InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "flattenRotation": "yes"}""")]
[InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "flattenRotation": true, "flattenRotation": true}""")]
public async Task JobMalformedFails(string jobJson)
{
byte[] fixture = ContentFixture(string.Empty);
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(HostWith("inputs/a.pdf", fixture), jobJson, CancellationToken.None);
Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
}
[Fact]
public async Task WholeFileJobFlattens()
{
byte[] fixture = ContentFixture(" /Rotate 270");
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "flattenRotation": true}""";
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
Assert.Contains("0 1 -1 0 792 0 cm", Encoding.ASCII.GetString(await PageContentAsync(pdf)));
}

[Fact]
public async Task MergeThenFlattenAppliesToFinalPages()
{
byte[] fixture = ContentFixture(" /Rotate 90");
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
host.AddInput(QpdfPath.From("inputs/b.pdf"), fixture, QpdfInputOwnership.Copy);
List<string> argv = new() { "inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--flatten-rotation", "--", "outputs/a.pdf" };
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
using JsonDocument pages = await PageJsonAsync(pdf);
Assert.Equal(2, pages.RootElement.GetProperty("pages").GetArrayLength());
byte[] content = await PageContentAsync(pdf);
Assert.Contains("0 -1 1 0 0 612 cm", Encoding.ASCII.GetString(content));
}
[Fact]
public async Task ExplicitRotateThenFlattenBakes()
{
byte[] fixture = ContentFixture(string.Empty);
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--rotate=+90", "--flatten-rotation")).Pdf;
byte[] content = await PageContentAsync(pdf);
Assert.Contains("0 -1 1 0 0 612 cm", Encoding.ASCII.GetString(content));
using JsonDocument full = await QpdfJsonAsync(pdf);
using JsonDocument pages = await PageJsonAsync(pdf);
JsonElement value = full.RootElement.GetProperty("qpdf")[1].GetProperty(PageObjectKey(pages.RootElement.GetProperty("pages")[0])).GetProperty("value");
Assert.False(value.TryGetProperty("/Rotate", out JsonElement _));
}
[Fact]
public async Task PageContentCheckFailsOnDamagedStream()
{
byte[] garbage = Encoding.ASCII.GetBytes("BT (oops");
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", garbage.Length.ToString(), " >>"), garbage),
});
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
Assert.Equal(QpdfCheckStatus.Failed, Assert.Single(result.Checks, check => check.Name == "page-content").Status);
Assert.False(result.Passed);
}
[Fact]
public async Task PageContentCheckPassesForInlineImage()
{
byte[] content = Encoding.ASCII.GetBytes("BI /W 1 /H 1 /CS /G /BPC 1 ID X EI");
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
});
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(result.Checks, check => check.Name == "page-content").Status);
}
[Fact]
public async Task CheckedInRotatedFixtureFlattens()
{
byte[] fixture = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rotated-1page.pdf"));
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-rotation")).Pdf;
byte[] content = await PageContentAsync(pdf);
Assert.Contains("0 -1 1 0 0 612 cm", Encoding.ASCII.GetString(content));
Assert.Contains("(Hello)", Encoding.ASCII.GetString(content));
using JsonDocument pages = await PageJsonAsync(pdf);
JsonElement page = pages.RootElement.GetProperty("pages")[0];
using JsonDocument full = await QpdfJsonAsync(pdf);
JsonElement value = full.RootElement.GetProperty("qpdf")[1].GetProperty(PageObjectKey(page)).GetProperty("value");
Assert.False(value.TryGetProperty("/Rotate", out JsonElement _));
Assert.Equal("792", value.GetProperty("/MediaBox")[2].GetRawText());
Assert.Equal("612", value.GetProperty("/MediaBox")[3].GetRawText());
}
[Fact]
public async Task AppearanceStreamGainsRotationMatrix()
{
byte[] appearance = Encoding.ASCII.GetBytes("0 w 0 0 20 10 re S");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(string.Empty, string.Concat(" /Contents 4 0 R /Rotate 90 /Annots [5 0 R]"), new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /Annot /Subtype /Widget /Rect [0 0 20 10] /FT /Tx /T (A) /V (x) /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 20 10] /Length ", appearance.Length.ToString(), " >>"), appearance),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-rotation")).Pdf;
using JsonDocument full = await QpdfJsonAsync(pdf);
JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
JsonElement annot = objects.GetProperty("obj:5 0 R").GetProperty("value");
string normal = annot.GetProperty("/AP").GetProperty("/N").GetString() ?? string.Empty;
string[] parts = normal.Split((char)32);
string key = string.Concat("obj:", parts[0], SP(), parts[1], " R");
Assert.Equal("[0,-1,1,0,0,612]", objects.GetProperty(key).GetProperty("stream").GetProperty("dict").GetProperty("/Matrix").GetRawText());
}
[Fact]
public async Task AppearanceMatrixComposesWithExisting()
{
byte[] appearance = Encoding.ASCII.GetBytes("0 w 0 0 20 10 re S");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(string.Empty, string.Concat(" /Contents 4 0 R /Rotate 90 /Annots [5 0 R]"), new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /Annot /Subtype /Widget /Rect [0 0 20 10] /FT /Tx /T (A) /V (x) /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 20 10] /Matrix [2 0 0 2 5 5] /Length ", appearance.Length.ToString(), " >>"), appearance),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-rotation")).Pdf;
using JsonDocument full = await QpdfJsonAsync(pdf);
JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
JsonElement annot = objects.GetProperty("obj:5 0 R").GetProperty("value");
string normal = annot.GetProperty("/AP").GetProperty("/N").GetString() ?? string.Empty;
string[] parts = normal.Split((char)32);
string key = string.Concat("obj:", parts[0], SP(), parts[1], " R");
Assert.Equal("[0,-2,2,0,5,1229]", objects.GetProperty(key).GetProperty("stream").GetProperty("dict").GetProperty("/Matrix").GetRawText());
}
[Fact]
public async Task AppearanceStatesAllRotate()
{
byte[] off = Encoding.ASCII.GetBytes("0 w 0 0 10 10 re S");
byte[] on = Encoding.ASCII.GetBytes("0 w 0 0 10 10 re f");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(string.Empty, string.Concat(" /Contents 4 0 R /Rotate 90 /Annots [5 0 R]"), new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /FT /Btn /Ff 32768 /T (R) /V /Choice1 /AP << /N << /Off 6 0 R /Choice1 7 0 R >> /D 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length ", off.Length.ToString(), " >>"), off),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length ", on.Length.ToString(), " >>"), on),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-rotation")).Pdf;
using JsonDocument full = await QpdfJsonAsync(pdf);
JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
Assert.Equal("[0,-1,1,0,0,612]", objects.GetProperty("obj:6 0 R").GetProperty("stream").GetProperty("dict").GetProperty("/Matrix").GetRawText());
Assert.Equal("[0,-1,1,0,0,612]", objects.GetProperty("obj:7 0 R").GetProperty("stream").GetProperty("dict").GetProperty("/Matrix").GetRawText());
}
[Theory]
[InlineData(180, "[-1,0,0,-1,612,792]")]
[InlineData(270, "[0,1,-1,0,792,0]")]
public async Task AppearanceMatricesMatchAngle(int angle, string expected)
{
byte[] appearance = Encoding.ASCII.GetBytes("0 w 0 0 20 10 re S");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(string.Empty, string.Concat(" /Contents 4 0 R /Rotate ", angle.ToString(), " /Annots [5 0 R]"), new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /Annot /Subtype /Widget /Rect [0 0 20 10] /FT /Tx /T (A) /V (x) /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 20 10] /Length ", appearance.Length.ToString(), " >>"), appearance),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-rotation")).Pdf;
using JsonDocument full = await QpdfJsonAsync(pdf);
JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
Assert.Equal(expected, objects.GetProperty("obj:6 0 R").GetProperty("stream").GetProperty("dict").GetProperty("/Matrix").GetRawText());
}
[Fact]
public async Task DirectStreamAppearanceStaysUntouched()
{
byte[] appearance = Encoding.ASCII.GetBytes("0 w 0 0 10 10 re S");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(string.Empty, string.Concat(" /Contents 4 0 R /Rotate 90 /Annots [5 0 R]"), new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /Annot /Subtype /Stamp /Rect [0 0 10 10] /Name /Approved /AP 6 0 R >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length ", appearance.Length.ToString(), " >>"), appearance),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-rotation")).Pdf;
using JsonDocument full = await QpdfJsonAsync(pdf);
JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
Assert.False(objects.GetProperty("obj:6 0 R").GetProperty("stream").GetProperty("dict").TryGetProperty("/Matrix", out JsonElement _));
}
[Fact]
public async Task DirectUserUnitSurvivesRotation()
{
byte[] fixture = ContentFixture(" /Rotate 90 /UserUnit 2.0");
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-rotation")).Pdf;
using JsonDocument full = await QpdfJsonAsync(pdf);
using JsonDocument pages = await PageJsonAsync(pdf);
JsonElement value = full.RootElement.GetProperty("qpdf")[1].GetProperty(PageObjectKey(pages.RootElement.GetProperty("pages")[0])).GetProperty("value");
Assert.Equal("2.0", value.GetProperty("/UserUnit").GetRawText());
}
[Fact]
public async Task MarkupAppearanceRotates()
{
byte[] appearance = Encoding.ASCII.GetBytes("0 0 5 5 re f");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(string.Empty, string.Concat(" /Contents 4 0 R /Rotate 90 /Annots [5 0 R]"), new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /Annot /Subtype /Square /Rect [0 0 40 30] /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 40 30] /Length ", appearance.Length.ToString(), " >>"), appearance),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--flatten-rotation")).Pdf;
using JsonDocument full = await QpdfJsonAsync(pdf);
JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
Assert.Equal("[0,-1,1,0,0,612]", objects.GetProperty("obj:6 0 R").GetProperty("stream").GetProperty("dict").GetProperty("/Matrix").GetRawText());
}
}
