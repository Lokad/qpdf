using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
namespace Lokad.Qpdf.Tests.Content;
public sealed class InlineImagesTests
{
private static string NL()
{
return new string((char)10, 1);
}
private static string SP()
{
return new string((char)32, 1);
}
private static byte[] BuildPdf(string catalogExtra, string pageExtra, List<(string Dict, byte[] StreamData)> extras)
{
List<string> bodies = new()
{
string.Concat("<< /Type /Catalog /Pages 2 0 R", catalogExtra, " >>"),
string.Concat("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
string.Concat("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]", pageExtra, " >>"),
};
foreach ((string dict, byte[] _) in extras)
{
bodies.Add(dict);
}
List<byte[]?> payloads = new() { null, null, null };
foreach ((string _, byte[] streamData) in extras)
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
private static async Task<byte[]> TransformAsync(InMemoryQpdfHost host, params string[] flags)
{
List<string> argv = new() { "inputs/a.pdf" };
argv.AddRange(flags);
argv.AddRange(["--", "outputs/a.pdf"]);
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
return pdf;
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
private static async Task<JsonDocument> InspectAsync(InMemoryQpdfHost host, params string[] flags)
{
List<string> argv = new() { "inputs/a.pdf" };
argv.AddRange(flags);
argv.AddRange(["--", "outputs/a.json"]);
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] json));
return JsonDocument.Parse(Encoding.UTF8.GetString(json));
}
private static async Task<HashSet<string>> ResourceKeysAsync(byte[] pdf)
{
using JsonDocument document = await InspectAsync(HostWith("inputs/a.pdf", pdf), "--json", "--json-key=pages");
JsonElement page = document.RootElement.GetProperty("pages")[0];
string reference = page.GetProperty("object").GetString() ?? string.Empty;
string[] parts = reference.Split((char)32);
string key = string.Concat("obj:", parts[0], SP(), parts[1], " R");
using JsonDocument full = await InspectAsync(HostWith("inputs/a.pdf", pdf), "--json", "--json-key=qpdf");
JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
JsonElement resources = objects.GetProperty(key).GetProperty("value").GetProperty("/Resources");
if (resources.ValueKind == JsonValueKind.String)
{
string[] linked = (resources.GetString() ?? string.Empty).Split((char)32);
resources = objects.GetProperty(string.Concat("obj:", linked[0], SP(), linked[1], " R")).GetProperty("value");
}
HashSet<string> keys = new(StringComparer.Ordinal);
if (resources.TryGetProperty("/Font", out JsonElement fonts))
{
foreach (JsonProperty property in fonts.EnumerateObject())
{
keys.Add(string.Concat("Font:", property.Name));
}
}
if (resources.TryGetProperty("/XObject", out JsonElement xobjects))
{
foreach (JsonProperty property in xobjects.EnumerateObject())
{
keys.Add(string.Concat("XObject:", property.Name));
}
}
return keys;
}
private static byte[] InlineContent(string dict, byte[] image)
{
byte[] head = Encoding.ASCII.GetBytes(string.Concat("BT ", SP(), "BI", SP(), dict, SP(), "ID", SP()));
byte[] tail = Encoding.ASCII.GetBytes(string.Concat(SP(), "EI", SP(), "ET"));
byte[] output = new byte[head.Length + image.Length + tail.Length];
Buffer.BlockCopy(head, 0, output, 0, head.Length);
Buffer.BlockCopy(image, 0, output, head.Length, image.Length);
Buffer.BlockCopy(tail, 0, output, head.Length + image.Length, tail.Length);
return output;
}
private static byte[] Filled(int count, byte value)
{
byte[] output = new byte[count];
for (int i = 0; i < count; i++)
{
output[i] = value;
}
return output;
}
private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
{
Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
Assert.Equal(QpdfExitCode.Error, result.ExitCode);
return Assert.Single(result.Diagnostics);
}
[Fact]
public async Task ExternalizeAboveThreshold()
{
byte[] image = Filled(2000, 65);
byte[] content = InlineContent("/W 1 /H 1 /CS /G /BPC 1", image);
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[] StreamData)> { (string.Concat("<< /Length ", content.Length.ToString(), " >>"), content) });
byte[] pdf = await TransformAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--externalize-inline-images");
byte[] output = await PageContentAsync(pdf);
string text = Encoding.ASCII.GetString(output);
Assert.Contains("/IIm1 Do", text);
Assert.DoesNotContain("BI", text);
HashSet<string> keys = await ResourceKeysAsync(pdf);
Assert.Contains("XObject:/IIm1", keys);
}
[Fact]
public async Task KeepBelowThreshold()
{
byte[] image = Filled(10, 65);
byte[] content = InlineContent("/W 1 /H 1 /CS /G /BPC 1", image);
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[] StreamData)> { (string.Concat("<< /Length ", content.Length.ToString(), " >>"), content) });
byte[] pdf = await TransformAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--externalize-inline-images");
byte[] output = await PageContentAsync(pdf);
string text = Encoding.ASCII.GetString(output);
Assert.Contains("BI", text);
Assert.DoesNotContain("/IIm1 Do", text);
}
[Fact]
public async Task CustomThresholdKeepsLarge()
{
byte[] image = Filled(2000, 65);
byte[] content = InlineContent("/W 1 /H 1 /CS /G /BPC 1", image);
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[] StreamData)> { (string.Concat("<< /Length ", content.Length.ToString(), " >>"), content) });
byte[] pdf = await TransformAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--externalize-inline-images", "--ii-min-bytes=3000");
byte[] output = await PageContentAsync(pdf);
Assert.Contains("BI", Encoding.ASCII.GetString(output));
}
[Fact]
public async Task CustomThresholdExternalizesSmall()
{
byte[] image = Filled(10, 65);
byte[] content = InlineContent("/W 1 /H 1 /CS /G /BPC 1", image);
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[] StreamData)> { (string.Concat("<< /Length ", content.Length.ToString(), " >>"), content) });
byte[] pdf = await TransformAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--externalize-inline-images", "--ii-min-bytes=5");
byte[] output = await PageContentAsync(pdf);
Assert.Contains("/IIm1 Do", Encoding.ASCII.GetString(output));
}
[Fact]
public async Task ZeroThresholdExternalizesSingleByte()
{
byte[] image = Filled(1, 65);
byte[] content = InlineContent("/W 1 /H 1 /CS /G /BPC 1", image);
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[] StreamData)> { (string.Concat("<< /Length ", content.Length.ToString(), " >>"), content) });
byte[] pdf = await TransformAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--externalize-inline-images", "--ii-min-bytes=0");
byte[] output = await PageContentAsync(pdf);
Assert.Contains("/IIm1 Do", Encoding.ASCII.GetString(output));
}
[Fact]
public async Task AbbreviationsExpandInXObject()
{
byte[] image = Filled(1500, 66);
byte[] content = InlineContent("/W 2 /H 2 /CS /G /BPC 1", image);
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[] StreamData)> { (string.Concat("<< /Length ", content.Length.ToString(), " >>"), content) });
byte[] pdf = await TransformAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--externalize-inline-images");
using JsonDocument full = await InspectAsync(HostWith("inputs/a.pdf", pdf), "--json", "--json-key=qpdf");
string abbrDump = full.RootElement.GetRawText();
Assert.Contains("Width", abbrDump);
byte[] output = await PageContentAsync(pdf);
Assert.Contains("/IIm1 Do", Encoding.ASCII.GetString(output));
}
[Fact]
public async Task FilterAbbreviationExpands()
{
byte[] image = Filled(1500, 67);
byte[] content = InlineContent("/W 1 /H 1 /CS /G /BPC 1 /F /Fl", image);
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[] StreamData)> { (string.Concat("<< /Length ", content.Length.ToString(), " >>"), content) });
byte[] pdf = await TransformAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--externalize-inline-images");
using JsonDocument full = await InspectAsync(HostWith("inputs/a.pdf", pdf), "--json", "--json-key=qpdf");
string dump = full.RootElement.GetRawText();
Assert.Contains("FlateDecode", dump);
}
[Fact]
public async Task ColorspaceLookupResolves()
{
byte[] image = Filled(1500, 68);
byte[] content = InlineContent("/W 1 /H 1 /CS /MyCS /BPC 1", image);
string resources = " /Resources << /ColorSpace << /MyCS [/Indexed /DeviceRGB 1 <00FF>] >> >>";
byte[] fixture = BuildPdf(string.Empty, string.Concat(" /Contents 4 0 R", resources), new List<(string Dict, byte[] StreamData)> { (string.Concat("<< /Length ", content.Length.ToString(), " >>"), content) });
byte[] pdf = await TransformAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--externalize-inline-images");
byte[] output = await PageContentAsync(pdf);
Assert.Contains("/IIm1 Do", Encoding.ASCII.GetString(output));
}
[Fact]
public async Task ColorspaceMissWarnsAndKeepsName()
{
byte[] image = Filled(1500, 69);
byte[] content = InlineContent("/W 1 /H 1 /CS /Nope /BPC 1", image);
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[] StreamData)> { (string.Concat("<< /Length ", content.Length.ToString(), " >>"), content) });
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--externalize-inline-images", "--", "outputs/a.pdf" };
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.True(result.ExitCode == QpdfExitCode.Success || result.ExitCode == QpdfExitCode.Warning);
Assert.Contains(result.Diagnostics, static d => d.Code == QpdfDiagnosticCode.UnsupportedFeature);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
byte[] output = await PageContentAsync(pdf);
Assert.Contains("/IIm1 Do", Encoding.ASCII.GetString(output));
}
[Fact]
public async Task UniqueNamingSkipsTaken()
{
byte[] image = Filled(1500, 70);
byte[] content = InlineContent("/W 1 /H 1 /CS /G /BPC 1", image);
byte[] tiny = Filled(1, 80);
List<(string Dict, byte[] StreamData)> extras = new()
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 1 /Length 1 >>", tiny),
};
string pageExtra = " /Contents 4 0 R /Resources << /XObject << /IIm1 5 0 R >> >>";
byte[] fixture = BuildPdf(string.Empty, pageExtra, extras);
byte[] pdf = await TransformAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--externalize-inline-images");
HashSet<string> keys = await ResourceKeysAsync(pdf);
Assert.Contains("XObject:/IIm2", keys);
byte[] output = await PageContentAsync(pdf);
Assert.Contains("/IIm2 Do", Encoding.ASCII.GetString(output));
}
[Fact]
public async Task KeptSmallIsByteIdentical()
{
byte[] image = Filled(20, 71);
byte[] content = InlineContent("/W 1 /H 1 /CS /G /BPC 1", image);
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[] StreamData)> { (string.Concat("<< /Length ", content.Length.ToString(), " >>"), content) });
byte[] pdf = await TransformAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--externalize-inline-images");
byte[] output = await PageContentAsync(pdf);
Assert.Equal(content, output);
}
[Fact]
public async Task FlateContentExternalizes()
{
byte[] image = Filled(1500, 72);
byte[] content = InlineContent("/W 1 /H 1 /CS /G /BPC 1", image);
byte[] encoded = Lokad.Qpdf.Filters.FlateFilter.Encode(content, null, QpdfLimits.Default, new Lokad.Qpdf.Runtime.ResourceGovernor(QpdfLimits.Default, CancellationToken.None));
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[] StreamData)> { (string.Concat("<< /Length ", encoded.Length.ToString(), " /Filter /FlateDecode >>"), encoded) });
byte[] pdf = await TransformAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--externalize-inline-images");
byte[] output = await PageContentAsync(pdf);
Assert.Contains("/IIm1 Do", Encoding.ASCII.GetString(output));
}
[Fact]
public async Task FormXObjectExternalizes()
{
byte[] image = Filled(1500, 73);
byte[] formContent = InlineContent("/W 1 /H 1 /CS /G /BPC 1", image);
byte[] pageContent = Encoding.ASCII.GetBytes("BT /F1 12 Tf ET /Fm1 Do");
List<(string Dict, byte[] StreamData)> extras = new()
{
(string.Concat("<< /Length ", pageContent.Length.ToString(), " >>"), pageContent),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length ", formContent.Length.ToString(), " >>"), formContent),
};
string pageExtra = " /Contents 4 0 R /Resources << /XObject << /Fm1 5 0 R >> >>";
byte[] fixture = BuildPdf(string.Empty, pageExtra, extras);
byte[] pdf = await TransformAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--externalize-inline-images");
using JsonDocument full = await InspectAsync(HostWith("inputs/a.pdf", pdf), "--json", "--json-key=qpdf");
string dump = full.RootElement.GetRawText();
Assert.Contains("IIm1", dump);
}
[Fact]
public async Task DamagedContentWarnsAndKeeps()
{
byte[] garbage = Encoding.ASCII.GetBytes("(a");
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[] StreamData)> { (string.Concat("<< /Length ", garbage.Length.ToString(), " /Filter /FlateDecode >>"), garbage) });
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--externalize-inline-images", "--", "outputs/a.pdf" };
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.True(result.ExitCode == QpdfExitCode.Success || result.ExitCode == QpdfExitCode.Warning);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
}
[Theory]
[InlineData("--externalize-inline-images", "--externalize-inline-images")]
[InlineData("--externalize-inline-images=1")]
[InlineData("--ii-min-bytes", "--ii-min-bytes", "--externalize-inline-images")]
[InlineData("--ii-min-bytes=x", "--externalize-inline-images")]
[InlineData("--ii-min-bytes=-1", "--externalize-inline-images")]
public async Task MalformedExternalizeFlagsFail(params string[] flags)
{
byte[] content = InlineContent("/W 1 /H 1 /CS /G /BPC 1", Filled(10, 65));
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[] StreamData)> { (string.Concat("<< /Length ", content.Length.ToString(), " >>"), content) });
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
List<string> argv = new() { "inputs/a.pdf" };
argv.AddRange(flags);
argv.AddRange(["--", "outputs/a.pdf"]);
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
}
[Fact]
public async Task JobRoundTripMatchesArgv()
{
byte[] image = Filled(1500, 74);
byte[] content = InlineContent("/W 1 /H 1 /CS /G /BPC 1", image);
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[] StreamData)> { (string.Concat("<< /Length ", content.Length.ToString(), " >>"), content) });
byte[] a = await TransformAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--externalize-inline-images", "--ii-min-bytes=512");
InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", fixture);
string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "externalizeInlineImages": true, "iiMinBytes": 512}""";
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(jobHost, job, CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
Assert.Equal(a, b);
}
[Theory]
[InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "externalizeInlineImages": "yes"}""")]
[InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "iiMinBytes": -1}""")]
[InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "iiMinBytes": "huge"}""")]
[InlineData("""{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.pdf","pages":[{"file":".","range":"1"}],"externalizeInlineImages":true,"externalizeInlineImages":[]}""")]
public async Task JobMalformedFails(string jobJson)
{
byte[] content = InlineContent("/W 1 /H 1 /CS /G /BPC 1", Filled(10, 65));
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[] StreamData)> { (string.Concat("<< /Length ", content.Length.ToString(), " >>"), content) });
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(HostWith("inputs/a.pdf", fixture), jobJson, CancellationToken.None);
Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
}
[Fact]
public async Task WholeFileJobExternalizes()
{
byte[] image = Filled(1500, 75);
byte[] content = InlineContent("/W 1 /H 1 /CS /G /BPC 1", image);
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[] StreamData)> { (string.Concat("<< /Length ", content.Length.ToString(), " >>"), content) });
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "externalizeInlineImages": true}""";
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
byte[] output = await PageContentAsync(pdf);
Assert.Contains("/IIm1 Do", Encoding.ASCII.GetString(output));
}
[Fact]
public async Task CombinesWithOtherContentOps()
{
byte[] image = Filled(1500, 76);
byte[] first = Encoding.ASCII.GetBytes("BT /F1 12 Tf");
byte[] second = InlineContent("/W 1 /H 1 /CS /G /BPC 1", image);
List<(string Dict, byte[] StreamData)> extras = new()
{
(string.Concat("<< /Length ", first.Length.ToString(), " >>"), first),
(string.Concat("<< /Length ", second.Length.ToString(), " >>"), second),
};
byte[] fixture = BuildPdf(string.Empty, " /Contents [4 0 R 5 0 R]", extras);
byte[] pdf = await TransformAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--externalize-inline-images", "--coalesce-contents", "--normalize-content=y", "--remove-unreferenced-resources=yes");
byte[] output = await PageContentAsync(pdf);
Assert.Contains("/IIm1 Do", Encoding.ASCII.GetString(output));
}
}
