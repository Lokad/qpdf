using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
namespace Lokad.Qpdf.Tests.Content;
public sealed class OverlayTests
{
private static string NL()
{
return new string((char)10, 1);
}
private static string SP()
{
return new string((char)32, 1);
}
private static byte[] BuildPdf(List<string> pageTexts)
{
int n = pageTexts.Count;
List<byte[]> contentBytes = new();
foreach (string text in pageTexts)
{
contentBytes.Add(Encoding.ASCII.GetBytes(text));
}
List<string> bodies = new()
{
"<< /Type /Catalog /Pages 2 0 R >>",
"PAGESPLACEHOLDER",
};
List<string> kids = new();
for (int i = 0; i < n; i++)
{
int pageNumber = i + 3;
int contentNumber = pageNumber + n;
kids.Add(pageNumber.ToString() + " 0 R");
bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents " + contentNumber.ToString() + " 0 R >>");
}
for (int i = 0; i < n; i++)
{
bodies.Add("<< /Length " + contentBytes[i].Length.ToString() + " >>");
}
bodies[1] = "<< /Type /Pages /Kids [" + string.Join(SP(), kids) + "] /Count " + n.ToString() + " >>";
List<byte[]?> payloads = new() { null, null };
for (int i = 0; i < n; i++)
{
payloads.Add(null);
}
foreach (byte[] bytes in contentBytes)
{
payloads.Add(bytes);
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
private static InMemoryQpdfHost HostWithTwo(byte[] primary, byte[] overlay)
{
InMemoryQpdfHost host = new();
host.AddInput(QpdfPath.From("inputs/a.pdf"), primary, QpdfInputOwnership.Copy);
host.AddInput(QpdfPath.From("inputs/o.pdf"), overlay, QpdfInputOwnership.Copy);
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
failure.Append((char)124);
failure.Append(diagnostic.Message);
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
private static async Task<byte[]> FormContentAsync(byte[] pdf, string formName)
{
using JsonDocument pages = await InspectAsync(pdf, "--json", "--json-key=pages");
JsonElement page = pages.RootElement.GetProperty("pages")[0];
string reference = page.GetProperty("object").GetString() ?? string.Empty;
string[] parts = reference.Split((char)32);
string pageKey = string.Concat("obj:", parts[0], SP(), parts[1], " R");
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
private static async Task<HashSet<string>> XObjectNamesAsync(byte[] pdf)
{
using JsonDocument pages = await InspectAsync(pdf, "--json", "--json-key=pages");
JsonElement page = pages.RootElement.GetProperty("pages")[0];
string reference = page.GetProperty("object").GetString() ?? string.Empty;
string[] parts = reference.Split((char)32);
string pageKey = string.Concat("obj:", parts[0], SP(), parts[1], " R");
using JsonDocument full = await InspectAsync(pdf, "--json", "--json-key=qpdf");
JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
JsonElement resources = objects.GetProperty(pageKey).GetProperty("value").GetProperty("/Resources");
if (resources.ValueKind == JsonValueKind.String)
{
string[] linked = (resources.GetString() ?? string.Empty).Split((char)32);
resources = objects.GetProperty(string.Concat("obj:", linked[0], SP(), linked[1], " R")).GetProperty("value");
}
HashSet<string> names = new(StringComparer.Ordinal);
JsonElement xobjects = resources.GetProperty("/XObject");
foreach (JsonProperty property in xobjects.EnumerateObject())
{
names.Add(property.Name);
}
return names;
}
private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
{
Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
Assert.Equal(QpdfExitCode.Error, result.ExitCode);
return Assert.Single(result.Diagnostics);
}

[Fact]
public async Task OverlayStacksStampOnTop()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Stamp) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
byte[] content = await PageContentAsync(pdf);
string text = Encoding.ASCII.GetString(content);
Assert.Contains("/Fx0 Do", text);
Assert.Contains("/Fx1 Do", text);
Assert.True(text.IndexOf("/Fx0 Do", StringComparison.Ordinal) < text.IndexOf("/Fx1 Do", StringComparison.Ordinal));
byte[] inner = await FormContentAsync(pdf, "/Fx0");
Assert.Contains("Primary", Encoding.ASCII.GetString(inner));
byte[] stamp = await FormContentAsync(pdf, "/Fx1");
Assert.Contains("Stamp", Encoding.ASCII.GetString(stamp));
}
[Fact]
public async Task UnderlayGoesBelowOriginal()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Base) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--underlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
byte[] content = await PageContentAsync(pdf);
string text = Encoding.ASCII.GetString(content);
Assert.True(text.IndexOf("/Fx1 Do", StringComparison.Ordinal) < text.IndexOf("/Fx0 Do", StringComparison.Ordinal));
}
[Fact]
public async Task DefaultMappingPairsPagesInOrder()
{
byte[] primary = BuildPdf(new List<string> { "BT (One) Tj ET", "BT (Two) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (A) Tj ET", "BT (B) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
HashSet<string> names = await XObjectNamesAsync(pdf);
Assert.Contains("/Fx0", names);
Assert.Contains("/Fx1", names);
}
[Fact]
public async Task ToRestrictsDestinationPages()
{
byte[] primary = BuildPdf(new List<string> { "BT (One) Tj ET", "BT (Two) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (A) Tj ET", "BT (B) Tj ET" });
InMemoryQpdfHost host = HostWithTwo(primary, overlay);
List<string> argv = new() { "inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "inputs/a.pdf", "2", "--", "--overlay", "inputs/o.pdf", "--to=2", "--", "outputs/a.pdf" };
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
using JsonDocument pages = await InspectAsync(pdf, "--json", "--json-key=pages");
Assert.Equal(2, pages.RootElement.GetProperty("pages").GetArrayLength());
}
[Fact]
public async Task FromExhaustionLeavesRemainingPagesAlone()
{
byte[] primary = BuildPdf(new List<string> { "BT (One) Tj ET", "BT (Two) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Only) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--from=1", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
byte[] content = await PageContentAsync(pdf);
string text = Encoding.ASCII.GetString(content);
Assert.Contains("/Fx1 Do", text);
}
[Fact]
public async Task RepeatCyclesAfterFromPages()
{
byte[] primary = BuildPdf(new List<string> { "BT (One) Tj ET", "BT (Two) Tj ET", "BT (Three) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (F) Tj ET", "BT (R) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--from=1", "--repeat=2", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
byte[] content = await PageContentAsync(pdf);
string text = Encoding.ASCII.GetString(content);
Assert.Contains("/Fx1 Do", text);
}
[Fact]
public async Task EmptyFromWithRepeatCoversAllPages()
{
byte[] primary = BuildPdf(new List<string> { "BT (One) Tj ET", "BT (Two) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (R1) Tj ET", "BT (R2) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--underlay", "inputs/o.pdf", "--from=", "--repeat=1,2", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
byte[] content = await PageContentAsync(pdf);
Assert.Contains("/Fx1 Do", Encoding.ASCII.GetString(content));
}

[Fact]
public async Task SelfOverlayUsesPrimaryInput()
{
byte[] primary = BuildPdf(new List<string> { "BT (Self) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, primary), "--overlay", ".", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
byte[] content = await PageContentAsync(pdf);
Assert.Contains("/Fx1 Do", Encoding.ASCII.GetString(content));
}
[Fact]
public async Task MultipleOverlaysStackInOrder()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] first = BuildPdf(new List<string> { "BT (First) Tj ET" });
byte[] second = BuildPdf(new List<string> { "BT (Second) Tj ET" });
InMemoryQpdfHost host = HostWithTwo(primary, first);
host.AddInput(QpdfPath.From("inputs/p.pdf"), second, QpdfInputOwnership.Copy);
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(host, "--overlay", "inputs/o.pdf", "--", "--overlay", "inputs/p.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
byte[] content = await PageContentAsync(pdf);
string text = Encoding.ASCII.GetString(content);
Assert.True(text.IndexOf("/Fx1 Do", StringComparison.Ordinal) < text.IndexOf("/Fx2 Do", StringComparison.Ordinal));
byte[] stamp = await FormContentAsync(pdf, "/Fx2");
Assert.Contains("Second", Encoding.ASCII.GetString(stamp));
}
[Fact]
public async Task UnderlayAndOverlayCombineAroundOriginal()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Stamp) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--underlay", "inputs/o.pdf", "--", "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
byte[] content = await PageContentAsync(pdf);
string text = Encoding.ASCII.GetString(content);
int under = text.IndexOf("/Fx1 Do", StringComparison.Ordinal);
int orig = text.IndexOf("/Fx0 Do", StringComparison.Ordinal);
int over = text.IndexOf("/Fx2 Do", StringComparison.Ordinal);
Assert.True(under >= 0 && orig > under && over > orig);
}
[Fact]
public async Task PagesSelectionThenOverlay()
{
byte[] primary = BuildPdf(new List<string> { "BT (One) Tj ET", "BT (Two) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Stamp) Tj ET" });
InMemoryQpdfHost host = HostWithTwo(primary, overlay);
List<string> argv = new() { "inputs/a.pdf", "--pages", "inputs/a.pdf", "2", "--", "--overlay", "inputs/o.pdf", "--", "outputs/a.pdf" };
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
byte[] inner = await FormContentAsync(pdf, "/Fx0");
Assert.Contains("Two", Encoding.ASCII.GetString(inner));
}
[Fact]
public async Task MergeThenOverlayAppliesToFinalPages()
{
byte[] primary = BuildPdf(new List<string> { "BT (One) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Stamp) Tj ET" });
InMemoryQpdfHost host = HostWithTwo(primary, overlay);
host.AddInput(QpdfPath.From("inputs/b.pdf"), primary, QpdfInputOwnership.Copy);
List<string> argv = new() { "inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--", "--overlay", "inputs/o.pdf", "--", "outputs/a.pdf" };
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
using JsonDocument pages = await InspectAsync(pdf, "--json", "--json-key=pages");
Assert.Equal(2, pages.RootElement.GetProperty("pages").GetArrayLength());
byte[] content = await PageContentAsync(pdf);
Assert.Contains("/Fx1 Do", Encoding.ASCII.GetString(content));
}
[Fact]
public async Task SameSizeOverlayUsesIdentityPlacement()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Stamp) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
byte[] content = await PageContentAsync(pdf);
Assert.Contains("1 0 0 1 0 0 cm", Encoding.ASCII.GetString(content));
}

[Theory]
[InlineData("--overlay")]
[InlineData("--overlay", "inputs/o.pdf", "--to=1", "--to=2")]
[InlineData("--overlay=o.pdf")]
[InlineData("--overlay", "inputs/o.pdf", "--to=r1")]
[InlineData("--overlay", "inputs/o.pdf", "--to=1:odd")]
[InlineData("--overlay", "inputs/o.pdf", "--to=x1")]
[InlineData("--overlay", "inputs/o.pdf", "--from=z")]
[InlineData("--overlay", "inputs/o.pdf", "--repeat=0")]
[InlineData("--overlay", "inputs/o.pdf", "--bogus=1")]
public async Task MalformedOverlayFlagsFail(params string[] flags)
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Stamp) Tj ET" });
InMemoryQpdfHost host = HostWithTwo(primary, overlay);
List<string> argv = new() { "inputs/a.pdf" };
argv.AddRange(flags);
argv.AddRange(["--", "outputs/a.pdf"]);
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
if (result.Status == QpdfExecutionStatus.Completed)
{
return;
}

Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
}
[Fact]
public async Task OverlayRejectedWithInspection()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Stamp) Tj ET" });
InMemoryQpdfHost host = HostWithTwo(primary, overlay);
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--overlay", "inputs/o.pdf", "--", "outputs/a.pdf"]), CancellationToken.None);
Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
}
[Fact]
public async Task JobRoundTripMatchesArgv()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Stamp) Tj ET" });
byte[] a = new byte[0];
InMemoryQpdfHost argvHost = HostWithTwo(primary, overlay);
{
(QpdfExecutionResult r, byte[] pdf) = await RunAsync(argvHost, "--overlay", "inputs/o.pdf", "--to=1", "--");
a = pdf;
Assert.Equal(QpdfExitCode.Success, r.ExitCode);
}
InMemoryQpdfHost jobHost = HostWithTwo(primary, overlay);
string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "overlay": [{"file": "inputs/o.pdf", "to": "1"}]}""";
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(jobHost, job, CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
Assert.Equal(a, b);
}
[Theory]
[InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "overlay": [{"to": "1"}]}""")]
[InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "overlay": [{"file": "inputs/o.pdf", "to": "r1"}]}""")]
[InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "overlay": [{"file": "inputs/o.pdf", "from": "1", "from": "1"}]}""")]
[InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "underlay": "yes"}""")]
public async Task JobMalformedFails(string jobJson)
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Stamp) Tj ET" });
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(HostWithTwo(primary, overlay), jobJson, CancellationToken.None);
if (result.Status == QpdfExecutionStatus.Completed)
{
return;
}

Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
}
[Fact]
public async Task WholeFileJobOverlay()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Stamp) Tj ET" });
InMemoryQpdfHost host = HostWithTwo(primary, overlay);
string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "underlay": [{"file": "inputs/o.pdf"}]}""";
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
byte[] content = await PageContentAsync(pdf);
Assert.Contains("/Fx1 Do", Encoding.ASCII.GetString(content));
}

[Fact]
public async Task MissingOverlayFileFails()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Stamp) Tj ET" });
InMemoryQpdfHost host = HostWithTwo(primary, overlay);
List<string> argv = new() { "inputs/a.pdf", "--overlay", "inputs/missing.pdf", "--", "--", "outputs/a.pdf" };
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfDiagnosticCode.HostOperationFailed, SingleError(result).Code);
}
[Fact]
public async Task OverlayPasswordIsCrypto()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Stamp) Tj ET" });
InMemoryQpdfHost host = HostWithTwo(primary, overlay);
List<string> argv = new() { "inputs/a.pdf", "--overlay", "inputs/o.pdf", "--password=secret", "--", "--", "outputs/a.pdf" };
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, SingleError(result).Code);
}

[Fact]
public async Task JobOverlayPasswordIsCrypto()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Stamp) Tj ET" });
string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "overlay": [{"file": "inputs/o.pdf", "password": "x"}]}""";
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(HostWithTwo(primary, overlay), job, CancellationToken.None);
Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, SingleError(result).Code);
}

[Fact]
public async Task FromDescendingMapsInOrder()
{
byte[] primary = BuildPdf(new List<string> { "BT (One) Tj ET", "BT (Two) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (A) Tj ET", "BT (B) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--from=2-1", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.Equal(new HashSet<string>(["B"]), await OverlayMarkersOnPageAsync(pdf, 0));
Assert.Equal(new HashSet<string>(["A"]), await OverlayMarkersOnPageAsync(pdf, 1));
}
[Fact]
public async Task FromRelativeSelectsLastOverlayPage()
{
byte[] primary = BuildPdf(new List<string> { "BT (One) Tj ET", "BT (Two) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (A) Tj ET", "BT (B) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--from=r1", "--to=1", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.Equal(new HashSet<string>(["B"]), await OverlayMarkersOnPageAsync(pdf, 0));
Assert.Equal(new HashSet<string>(), await OverlayMarkersOnPageAsync(pdf, 1));
Assert.DoesNotContain("(A)", Encoding.ASCII.GetString(pdf));
}
[Fact]
public async Task FromExclusionSkipsOverlayPage()
{
byte[] primary = BuildPdf(new List<string> { "BT (One) Tj ET", "BT (Two) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (A) Tj ET", "BT (B) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--from=1-2,x2", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.Equal(new HashSet<string>(["A"]), await OverlayMarkersOnPageAsync(pdf, 0));
Assert.Equal(new HashSet<string>(), await OverlayMarkersOnPageAsync(pdf, 1));
}
[Fact]
public async Task FromOddPositionsMapInOrder()
{
byte[] primary = BuildPdf(new List<string> { "BT (One) Tj ET", "BT (Two) Tj ET", "BT (Three) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (A) Tj ET", "BT (B) Tj ET", "BT (C) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--from=1-3:odd", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.Equal(new HashSet<string>(["A"]), await OverlayMarkersOnPageAsync(pdf, 0));
Assert.Equal(new HashSet<string>(["C"]), await OverlayMarkersOnPageAsync(pdf, 1));
Assert.Equal(new HashSet<string>(), await OverlayMarkersOnPageAsync(pdf, 2));
}
[Fact]
public async Task ToOddRestrictsDestinations()
{
byte[] primary = BuildPdf(new List<string> { "BT (One) Tj ET", "BT (Two) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (A) Tj ET", "BT (B) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--to=1-2:odd", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.Equal(new HashSet<string>(["A"]), await OverlayMarkersOnPageAsync(pdf, 0));
Assert.Equal(new HashSet<string>(), await OverlayMarkersOnPageAsync(pdf, 1));
Assert.DoesNotContain("(B)", Encoding.ASCII.GetString(pdf));
}
[Fact]
public async Task RepeatRangesCycleAfterFromPages()
{
byte[] primary = BuildPdf(new List<string> { "BT (One) Tj ET", "BT (Two) Tj ET", "BT (Three) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (F) Tj ET", "BT (R) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--from=1", "--repeat=r1", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.Equal(new HashSet<string>(["F"]), await OverlayMarkersOnPageAsync(pdf, 0));
Assert.Equal(new HashSet<string>(["R"]), await OverlayMarkersOnPageAsync(pdf, 1));
Assert.Equal(new HashSet<string>(["R"]), await OverlayMarkersOnPageAsync(pdf, 2));
}
[Fact]
public async Task UnderlayRangesShareGrammar()
{
byte[] primary = BuildPdf(new List<string> { "BT (One) Tj ET", "BT (Two) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (A) Tj ET", "BT (B) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--underlay", "inputs/o.pdf", "--from=2-1", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.Equal(new HashSet<string>(["B"]), await OverlayMarkersOnPageAsync(pdf, 0));
Assert.Equal(new HashSet<string>(["A"]), await OverlayMarkersOnPageAsync(pdf, 1));
}
[Fact]
public async Task JobFromRangeMatchesArgv()
{
byte[] primary = BuildPdf(new List<string> { "BT (One) Tj ET", "BT (Two) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (A) Tj ET", "BT (B) Tj ET" });
byte[] a = new byte[0];
InMemoryQpdfHost argvHost = HostWithTwo(primary, overlay);
{
(QpdfExecutionResult r, byte[] pdf) = await RunAsync(argvHost, "--overlay", "inputs/o.pdf", "--from=2-1", "--");
a = pdf;
Assert.Equal(QpdfExitCode.Success, r.ExitCode);
}
InMemoryQpdfHost jobHost = HostWithTwo(primary, overlay);
string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "overlay": [{"file": "inputs/o.pdf", "from": "2-1"}]}""";
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(jobHost, job, CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
Assert.Equal(a, b);
}
[Theory]
[InlineData("--overlay", "inputs/o.pdf", "--from=x1")]
[InlineData("--overlay", "inputs/o.pdf", "--from=9")]
[InlineData("--overlay", "inputs/o.pdf", "--to=1-2:even:odd")]
public async Task RangedOverlayFlagsFail(params string[] flags)
{
byte[] primary = BuildPdf(new List<string> { "BT (One) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Stamp) Tj ET" });
InMemoryQpdfHost host = HostWithTwo(primary, overlay);
List<string> argv = new() { "inputs/a.pdf" };
argv.AddRange(flags);
argv.AddRange(["--", "outputs/a.pdf"]);
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
}
[Theory]
[InlineData("--overlay", "--file", "inputs/o.pdf")]
[InlineData("--overlay", "inputs/o.pdf", "--file")]
[InlineData("--overlay", "inputs/o.pdf", "--to", "1")]
[InlineData("--overlay", "inputs/o.pdf", "--from", "1")]
[InlineData("--overlay", "inputs/o.pdf", "--from")]
[InlineData("--overlay", "inputs/o.pdf", "--repeat", "1")]
[InlineData("--underlay", "--file", "inputs/o.pdf")]
[InlineData("--underlay", "inputs/o.pdf", "--to", "1")]
public async Task OverlayBlockOptionsRequireEqualsForm(params string[] flags)
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Stamp) Tj ET" });
InMemoryQpdfHost host = HostWithTwo(primary, overlay);
List<string> argv = new() { "inputs/a.pdf" };
argv.AddRange(flags);
argv.AddRange(["--", "outputs/a.pdf"]);
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
}
[Fact]
public async Task EmptyToRangeAppliesOverlayNowhere()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Stamp) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--to=", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
byte[] content = await PageContentAsync(pdf);
string text = Encoding.ASCII.GetString(content);
Assert.Contains("Primary", text);
Assert.DoesNotContain("/Fx1 Do", text);
}
[Fact]
public async Task OverlayFileLongFormMatchesPositional()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildPdf(new List<string> { "BT (Stamp) Tj ET" });
byte[] positional;
{
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
positional = pdf;
}
byte[] longForm;
{
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "--file=inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
longForm = pdf;
}
Assert.Equal(positional, longForm);
}
private static async Task<HashSet<string>> OverlayMarkersOnPageAsync(byte[] pdf, int pageIndex)
{
using JsonDocument pages = await InspectAsync(pdf, "--json", "--json-key=pages");
JsonElement page = pages.RootElement.GetProperty("pages")[pageIndex];
string reference = page.GetProperty("object").GetString() ?? string.Empty;
string[] parts = reference.Split((char)32);
string pageKey = string.Concat("obj:", parts[0], SP(), parts[1], " R");
using JsonDocument full = await InspectAsync(pdf, "--json", "--json-key=qpdf", "--json-stream-data=inline");
JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
JsonElement pageValue = objects.GetProperty(pageKey).GetProperty("value");
JsonElement resources;
if (!pageValue.TryGetProperty("/Resources", out resources))
{
return new HashSet<string>(StringComparer.Ordinal);
}
if (resources.ValueKind == JsonValueKind.String)
{
string[] linked = (resources.GetString() ?? string.Empty).Split((char)32);
resources = objects.GetProperty(string.Concat("obj:", linked[0], SP(), linked[1], " R")).GetProperty("value");
}
HashSet<string> markers = new(StringComparer.Ordinal);
JsonElement xobjects;
if (!resources.TryGetProperty("/XObject", out xobjects))
{
return markers;
}
foreach (JsonProperty property in xobjects.EnumerateObject())
{
if (property.Name == "/Fx0")
{
continue;
}
string formReference = property.Value.GetString() ?? string.Empty;
string[] formParts = formReference.Split((char)32);
if (formParts.Length < 2)
{
continue;
}
string formKey = string.Concat("obj:", formParts[0], SP(), formParts[1], " R");
JsonElement form;
if (!objects.TryGetProperty(formKey, out form))
{
continue;
}
JsonElement stream;
if (!form.TryGetProperty("stream", out stream))
{
continue;
}
JsonElement data;
if (!stream.TryGetProperty("data", out data))
{
continue;
}
string encoded = data.GetString() ?? string.Empty;
byte[] bytes;
try
{
bytes = Convert.FromBase64String(encoded);
}
catch (FormatException)
{
continue;
}
string text = Encoding.ASCII.GetString(bytes);
foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"\(([A-Za-z0-9]+)\)"))
{
markers.Add(match.Groups[1].Value);
}
}
return markers;
}
[Fact]
public async Task OverlayCopiesTextAnnotation()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildAnnotPdf("[0 0 612 792]", "[<< /Type /Annot /Subtype /Text /Rect [10 10 60 60] /Contents (OverlayNote) >>]", new List<(string Dict, byte[]? StreamData)>(), "BT (Stamp) Tj ET");
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
await using QpdfDocument output = await OpenOutputAsync(pdf);
PdfDictionary annot = Assert.Single(OutputAnnots(output, 0));
Assert.Equal("Text", AnnotName(annot, "Subtype"));
Assert.Equal(new double[4] { 10.0, 10.0, 60.0, 60.0 }, AnnotRect(annot));
Assert.Equal("OverlayNote", AnnotString(annot, "Contents"));
Assert.False(annot.TryGet(PdfName.FromAscii("P"), out QpdfObject? _));
}
[Fact]
public async Task OverlayAnnotationsJobMatchesArgv()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildAnnotPdf("[0 0 612 792]", "[<< /Type /Annot /Subtype /Text /Rect [10 10 60 60] /Contents (OverlayNote) >>]", new List<(string Dict, byte[]? StreamData)>(), "BT (Stamp) Tj ET");
byte[] a = new byte[0];
InMemoryQpdfHost argvHost = HostWithTwo(primary, overlay);
{
(QpdfExecutionResult r, byte[] pdf) = await RunAsync(argvHost, "--overlay", "inputs/o.pdf", "--");
a = pdf;
Assert.Equal(QpdfExitCode.Success, r.ExitCode);
}
InMemoryQpdfHost jobHost = HostWithTwo(primary, overlay);
string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "overlay": [{"file": "inputs/o.pdf"}]}""";
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(jobHost, job, CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
Assert.Equal(a, b);
}
[Fact]
public async Task UnderlayCopiesAnnotations()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildAnnotPdf("[0 0 612 792]", "[<< /Type /Annot /Subtype /Text /Rect [10 10 60 60] /Contents (UnderNote) >>]", new List<(string Dict, byte[]? StreamData)>(), "BT (Stamp) Tj ET");
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--underlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
await using QpdfDocument output = await OpenOutputAsync(pdf);
PdfDictionary annot = Assert.Single(OutputAnnots(output, 0));
Assert.Equal("UnderNote", AnnotString(annot, "Contents"));
}
[Fact]
public async Task OverlayTransformsRectForScaledPlacement()
{
byte[] primary = BuildAnnotPdf("[0 0 100 100]", string.Empty, new List<(string Dict, byte[]? StreamData)>(), "BT (Base) Tj ET");
byte[] overlay = BuildAnnotPdf("[0 0 200 50]", "[<< /Type /Annot /Subtype /Text /Rect [10 10 60 40] /Contents (Scaled) >>]", new List<(string Dict, byte[]? StreamData)>(), "BT (Over) Tj ET");
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
await using QpdfDocument output = await OpenOutputAsync(pdf);
PdfDictionary annot = Assert.Single(OutputAnnots(output, 0));
Assert.Equal(new double[4] { 5.0, 42.5, 30.0, 57.5 }, AnnotRect(annot));
}
[Fact]
public async Task OverlayDropsPageReferenceToNull()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildAnnotPdf("[0 0 612 792]", "[<< /Type /Annot /Subtype /Link /Rect [70 10 120 40] /A << /S /GoTo /D [3 0 R /Fit] >> >>]", new List<(string Dict, byte[]? StreamData)>(), "BT (Stamp) Tj ET");
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
await using QpdfDocument output = await OpenOutputAsync(pdf);
PdfDictionary annot = Assert.Single(OutputAnnots(output, 0));
Assert.True(annot.TryGet(PdfName.FromAscii("A"), out QpdfObject? actionRaw));
PdfDictionary action = Assert.IsType<PdfDictionary>(actionRaw);
Assert.True(action.TryGet(PdfName.FromAscii("D"), out QpdfObject? destRaw));
PdfArray dest = Assert.IsType<PdfArray>(destRaw);
PdfReference pageLink = Assert.IsType<PdfReference>(dest[0]);
Assert.IsType<PdfNull>(output.GetObject(pageLink.ObjectNumber));
}
[Fact]
public async Task OverlayClonesAppearanceWithConcatenatedMatrix()
{
byte[] appearance = Encoding.ASCII.GetBytes("0 0 m 10 10 l S");
byte[] overlay = BuildAnnotPdf("[0 0 200 50]", "[<< /Type /Annot /Subtype /Text /Rect [130 10 180 40] /Contents (NoteAP) /AP << /N 5 0 R >> >>]", new List<(string Dict, byte[]? StreamData)>() { ("<< /Length " + appearance.Length.ToString() + " /Matrix [2 0 0 2 5 5] >>", appearance) }, "BT (Over) Tj ET");
byte[] primary = BuildAnnotPdf("[0 0 100 100]", string.Empty, new List<(string Dict, byte[]? StreamData)>(), "BT (Base) Tj ET");
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
await using QpdfDocument output = await OpenOutputAsync(pdf);
PdfDictionary annot = Assert.Single(OutputAnnots(output, 0));
Assert.True(annot.TryGet(PdfName.FromAscii("AP"), out QpdfObject? apRaw));
PdfDictionary ap = Assert.IsType<PdfDictionary>(apRaw);
Assert.True(ap.TryGet(PdfName.FromAscii("N"), out QpdfObject? streamRaw));
PdfReference streamLink = Assert.IsType<PdfReference>(streamRaw);
PdfStream stream = Assert.IsType<PdfStream>(output.GetObject(streamLink.ObjectNumber));
Assert.Equal(appearance, stream.Data);
Assert.Equal(new double[6] { 1.0, 0.0, 0.0, 1.0, 5.0, 80.0 }, AnnotMatrix(stream.Dictionary));
}
[Fact]
public async Task MalformedAppearanceMatrixBecomesZeros()
{
byte[] appearance = Encoding.ASCII.GetBytes("0 0 m 5 5 l S");
byte[] overlay = BuildAnnotPdf("[0 0 100 100]", "[<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] /Contents (BadM) /AP << /N 5 0 R >> >>]", new List<(string Dict, byte[]? StreamData)>() { ("<< /Length " + appearance.Length.ToString() + " /Matrix [1 2] >>", appearance) }, "BT (Over) Tj ET");
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
await using QpdfDocument output = await OpenOutputAsync(pdf);
PdfDictionary annot = Assert.Single(OutputAnnots(output, 0));
Assert.True(annot.TryGet(PdfName.FromAscii("AP"), out QpdfObject? apRaw));
PdfDictionary ap = Assert.IsType<PdfDictionary>(apRaw);
Assert.True(ap.TryGet(PdfName.FromAscii("N"), out QpdfObject? streamRaw));
PdfReference streamLink = Assert.IsType<PdfReference>(streamRaw);
PdfStream stream = Assert.IsType<PdfStream>(output.GetObject(streamLink.ObjectNumber));
Assert.Equal(new double[6] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }, AnnotMatrix(stream.Dictionary));
}
[Fact]
public async Task MissingRectBecomesTransformedZeros()
{
byte[] primary = BuildAnnotPdf("[0 0 100 100]", string.Empty, new List<(string Dict, byte[]? StreamData)>(), "BT (Base) Tj ET");
byte[] overlay = BuildAnnotPdf("[0 0 200 50]", "[<< /Type /Annot /Subtype /Stamp /Name /Approved >>]", new List<(string Dict, byte[]? StreamData)>(), "BT (Over) Tj ET");
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
await using QpdfDocument output = await OpenOutputAsync(pdf);
PdfDictionary annot = Assert.Single(OutputAnnots(output, 0));
Assert.Equal(new double[4] { 0.0, 37.5, 0.0, 37.5 }, AnnotRect(annot));
}
[Fact]
public async Task DirectAnnotationBecomesIndirect()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildAnnotPdf("[0 0 612 792]", "[<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /Contents (Direct) >>]", new List<(string Dict, byte[]? StreamData)>(), "BT (Stamp) Tj ET");
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
await using QpdfDocument output = await OpenOutputAsync(pdf);
PageLocation location = output.PageReferences[0];
Assert.True(location.Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject? annotsRaw));
PdfArray annots = Assert.IsType<PdfArray>(annotsRaw);
Assert.IsType<PdfReference>(Assert.Single(annots.Items));
}
[Fact]
public async Task NonDictionaryAnnotationSkippedWithWarning()
{
byte[] appearance = Encoding.ASCII.GetBytes("0 0 m 1 1 l S");
byte[] overlay = BuildAnnotPdf("[0 0 612 792]", "[<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /Contents (Kept) >> 5 0 R]", new List<(string Dict, byte[]? StreamData)>() { ("<< /Length " + appearance.Length.ToString() + " >>", appearance) }, "BT (Stamp) Tj ET");
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
InMemoryQpdfHost host = HostWithTwo(primary, overlay);
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(host, "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == QpdfDiagnosticCode.UnsupportedFeature);
await using QpdfDocument output = await OpenOutputAsync(pdf);
PdfDictionary annot = Assert.Single(OutputAnnots(output, 0));
Assert.Equal("Kept", AnnotString(annot, "Contents"));
}
[Fact]
public async Task OverlayWidgetImportsFieldWithValueAndEmptyDr()
{
List<(string Dict, byte[]? StreamData)> extras = new()
{
("<< /Type /Annot /Subtype /Widget /Rect [10 10 100 30] /FT /Tx /T (O) /V (ov) >>", null),
};
byte[] overlay = BuildFormPdf(" /AcroForm << /Fields [5 0 R] >>", "[5 0 R]", extras, "BT (Stamp) Tj ET");
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.Empty(result.Diagnostics);
await using QpdfDocument output = await OpenOutputAsync(pdf);
PdfDictionary field = Assert.Single(OutputFields(output));
Assert.Equal("O", AnnotString(field, "T"));
Assert.Equal("ov", AnnotString(field, "V"));
PdfDictionary annot = Assert.Single(OutputAnnots(output, 0));
Assert.Equal("O", AnnotString(annot, "T"));
Assert.Equal(OutputFieldRefs(output)[0].ObjectNumber, OutputAnnotRefs(output, 0)[0].ObjectNumber);
PdfDictionary resources = OutputFormDR(output, OutputForm(output));
Assert.Empty(resources.Keys);
}
[Fact]
public async Task OverlayWidgetImportsParentTopWithNeedAppearances()
{
List<(string Dict, byte[]? StreamData)> extras = new()
{
("<< /FT /Tx /T (P) /V (pv) /Kids [6 0 R] >>", null),
("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /Parent 5 0 R >>", null),
};
byte[] overlay = BuildFormPdf(" /AcroForm << /Fields [5 0 R] /NeedAppearances true >>", "[6 0 R]", extras, "BT (Stamp) Tj ET");
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.Empty(result.Diagnostics);
await using QpdfDocument output = await OpenOutputAsync(pdf);
PdfDictionary field = Assert.Single(OutputFields(output));
Assert.Equal("P", AnnotString(field, "T"));
Assert.Equal("pv", AnnotString(field, "V"));
PdfDictionary annot = Assert.Single(OutputAnnots(output, 0));
Assert.True(annot.TryGet(PdfName.FromAscii("Parent"), out QpdfObject? parentRaw));
Assert.Equal(OutputFieldRefs(output)[0].ObjectNumber, Assert.IsType<PdfReference>(parentRaw).ObjectNumber);
PdfDictionary form = OutputForm(output);
Assert.True(form.TryGet(PdfName.FromAscii("NeedAppearances"), out QpdfObject? needRaw));
Assert.True(Assert.IsType<PdfBoolean>(needRaw).Value);
}
[Fact]
public async Task OverlayWidgetCollisionRenamesImport()
{
List<(string Dict, byte[]? StreamData)> destExtras = new()
{
("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /FT /Tx /T (O) /V (dest) >>", null),
};
List<(string Dict, byte[]? StreamData)> srcExtras = new()
{
("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /T (O) /V (ov) >>", null),
};
byte[] primary = BuildFormPdf(" /AcroForm << /Fields [5 0 R] >>", "[5 0 R]", destExtras, "BT (Primary) Tj ET");
byte[] overlay = BuildFormPdf(" /AcroForm << /Fields [5 0 R] >>", "[5 0 R]", srcExtras, "BT (Stamp) Tj ET");
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.Empty(result.Diagnostics);
await using QpdfDocument output = await OpenOutputAsync(pdf);
List<PdfDictionary> fields = OutputFields(output);
Assert.Equal(2, fields.Count);
Assert.Equal("O", AnnotString(fields[0], "T"));
Assert.Equal("dest", AnnotString(fields[0], "V"));
Assert.Equal("O+1", AnnotString(fields[1], "T"));
Assert.Equal("ov", AnnotString(fields[1], "V"));
}
[Fact]
public async Task OverlayWidgetDrConflictRenamesFontAndRewritesDa()
{
List<(string Dict, byte[]? StreamData)> destExtras = new()
{
("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /FT /Tx /T (D) /V (dd) /DA (/F1 10 Tf 0 g) >>", null),
("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
};
List<(string Dict, byte[]? StreamData)> srcExtras = new()
{
("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /T (S) /V (sv) /DA (/F1 12 Tf 0 g) >>", null),
("<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", null),
};
byte[] primary = BuildFormPdf(" /AcroForm << /Fields [5 0 R] /DR << /Font << /F1 6 0 R >> >> >>", "[5 0 R]", destExtras, "BT (Primary) Tj ET");
byte[] overlay = BuildFormPdf(" /AcroForm << /Fields [5 0 R] /DR << /Font << /F1 6 0 R >> >> >>", "[5 0 R]", srcExtras, "BT (Stamp) Tj ET");
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.Empty(result.Diagnostics);
await using QpdfDocument output = await OpenOutputAsync(pdf);
List<PdfDictionary> fields = OutputFields(output);
Assert.Equal(2, fields.Count);
Assert.Equal("D", AnnotString(fields[0], "T"));
Assert.Equal("S", AnnotString(fields[1], "T"));
Assert.Equal("/F1 10 Tf 0 g", AnnotString(fields[0], "DA"));
Assert.Equal("/F1_1 12 Tf 0 g", AnnotString(fields[1], "DA"));
PdfDictionary resources = OutputFormDR(output, OutputForm(output));
Assert.True(resources.TryGet(PdfName.FromAscii("Font"), out QpdfObject? fontsRaw));
PdfDictionary fonts = fontsRaw is PdfReference fontsRef ? Assert.IsType<PdfDictionary>(output.GetObject(fontsRef.ObjectNumber)) : Assert.IsType<PdfDictionary>(fontsRaw);
Assert.True(fonts.TryGet(PdfName.FromAscii("F1"), out QpdfObject? f1Raw));
Assert.True(fonts.TryGet(PdfName.FromAscii("F1_1"), out QpdfObject? f1aRaw));
PdfDictionary f1 = Assert.IsType<PdfDictionary>(output.GetObject(Assert.IsType<PdfReference>(f1Raw).ObjectNumber));
PdfDictionary f1a = Assert.IsType<PdfDictionary>(output.GetObject(Assert.IsType<PdfReference>(f1aRaw).ObjectNumber));
Assert.Equal("Helvetica", AnnotName(f1, "BaseFont"));
Assert.Equal("Courier", AnnotName(f1a, "BaseFont"));
}
[Fact]
public async Task OverlayWidgetStampsFormDefaultAppearance()
{
List<(string Dict, byte[]? StreamData)> extras = new()
{
("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /T (O) /V (ov) >>", null),
};
byte[] overlay = BuildFormPdf(" /AcroForm << /Fields [5 0 R] /DA (/F1 12 Tf 0 g) >>", "[5 0 R]", extras, "BT (Stamp) Tj ET");
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.Empty(result.Diagnostics);
await using QpdfDocument output = await OpenOutputAsync(pdf);
PdfDictionary field = Assert.Single(OutputFields(output));
Assert.Equal("/F1 12 Tf 0 g", AnnotString(field, "DA"));
}

[Fact]
public async Task OverlayWidgetRepeatRenamesAcrossPlacements()
{
List<(string Dict, byte[]? StreamData)> extras = new()
{
("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /T (O) /V (ov) >>", null),
};
byte[] overlay = BuildFormPdf(" /AcroForm << /Fields [5 0 R] >>", "[5 0 R]", extras, "BT (Stamp) Tj ET");
byte[] primary = BuildPdf(new List<string> { "BT (One) Tj ET", "BT (Two) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--repeat=1", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.Empty(result.Diagnostics);
await using QpdfDocument output = await OpenOutputAsync(pdf);
Assert.Equal(2, output.PageCount);
List<PdfDictionary> fields = OutputFields(output);
Assert.Equal(2, fields.Count);
Assert.Equal("O", AnnotString(fields[0], "T"));
Assert.Equal("O+1", AnnotString(fields[1], "T"));
Assert.Single(OutputAnnots(output, 0));
Assert.Single(OutputAnnots(output, 1));
}
[Fact]
public async Task OverlayWidgetRadioSiblingsShareParent()
{
List<(string Dict, byte[]? StreamData)> extras = new()
{
("<< /FT /Btn /T (R) /Kids [6 0 R 7 0 R] >>", null),
("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /Parent 5 0 R >>", null),
("<< /Type /Annot /Subtype /Widget /Rect [20 0 30 10] /Parent 5 0 R >>", null),
};
byte[] overlay = BuildFormPdf(" /AcroForm << /Fields [5 0 R] >>", "[6 0 R 7 0 R]", extras, "BT (Stamp) Tj ET");
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.Empty(result.Diagnostics);
await using QpdfDocument output = await OpenOutputAsync(pdf);
PdfDictionary field = Assert.Single(OutputFields(output));
Assert.Equal("R", AnnotString(field, "T"));
List<PdfDictionary> annots = OutputAnnots(output, 0);
Assert.Equal(2, annots.Count);
int fieldNumber = OutputFieldRefs(output)[0].ObjectNumber;
foreach (PdfDictionary annot in annots)
{
Assert.True(annot.TryGet(PdfName.FromAscii("Parent"), out QpdfObject? parentRaw));
Assert.Equal(fieldNumber, Assert.IsType<PdfReference>(parentRaw).ObjectNumber);
}
}
[Fact]
public async Task OverlayWidgetUnreachedImportsWithInfo()
{
List<(string Dict, byte[]? StreamData)> extras = new()
{
("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /FT /Tx /T (F) /V (f) >>", null),
("<< /Type /Annot /Subtype /Widget /Rect [10 10 100 30] /FT /Tx /T (UW) /V (uv) >>", null),
};
byte[] overlay = BuildFormPdf(" /AcroForm << /Fields [5 0 R] >>", "[5 0 R 6 0 R]", extras, "BT (Stamp) Tj ET");
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Severity == QpdfDiagnosticSeverity.Info && diagnostic.Message.Contains("does not reference"));
await using QpdfDocument output = await OpenOutputAsync(pdf);
List<PdfDictionary> fields = OutputFields(output);
Assert.Equal(2, fields.Count);
Assert.Equal("F", AnnotString(fields[0], "T"));
Assert.Equal("UW", AnnotString(fields[1], "T"));
Assert.Equal("uv", AnnotString(fields[1], "V"));
}
[Fact]
public async Task OverlayWidgetDirectWithFormClonesWithInfo()
{
List<(string Dict, byte[]? StreamData)> extras = new()
{
("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /FT /Tx /T (F) /V (f) >>", null),
};
byte[] overlay = BuildFormPdf(" /AcroForm << /Fields [5 0 R] >>", "[5 0 R << /Type /Annot /Subtype /Widget /Rect [10 10 100 30] /FT /Tx /T (DW) /V (dv) >>]", extras, "BT (Stamp) Tj ET");
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Severity == QpdfDiagnosticSeverity.Info && diagnostic.Message.Contains("does not reference"));
Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Severity == QpdfDiagnosticSeverity.Info && diagnostic.Message.Contains("not indirect"));
await using QpdfDocument output = await OpenOutputAsync(pdf);
PdfDictionary field = Assert.Single(OutputFields(output));
Assert.Equal("F", AnnotString(field, "T"));
Assert.Equal(2, OutputAnnots(output, 0).Count);
}
[Fact]
public async Task OverlayWidgetBareTopClonesWithoutField()
{
List<(string Dict, byte[]? StreamData)> extras = new()
{
("<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /Contents (Note) >>", null),
("<< /Type /Annot /Subtype /Widget /Rect [10 10 100 30] >>", null),
};
byte[] overlay = BuildFormPdf(" /AcroForm << /Fields [6 0 R] >>", "[5 0 R 6 0 R]", extras, "BT (Stamp) Tj ET");
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.Empty(result.Diagnostics);
await using QpdfDocument output = await OpenOutputAsync(pdf);
Assert.Equal(2, OutputAnnots(output, 0).Count);
PdfDictionary catalog = Assert.IsType<PdfDictionary>(output.GetObject(1));
Assert.False(catalog.TryGet(PdfName.FromAscii("AcroForm"), out QpdfObject? _));
}

[Fact]
public async Task UnderlayWidgetImportsField()
{
List<(string Dict, byte[]? StreamData)> extras = new()
{
("<< /Type /Annot /Subtype /Widget /Rect [10 10 100 30] /FT /Tx /T (U) /V (uv) >>", null),
};
byte[] overlay = BuildFormPdf(" /AcroForm << /Fields [5 0 R] >>", "[5 0 R]", extras, "BT (Stamp) Tj ET");
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--underlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.Empty(result.Diagnostics);
await using QpdfDocument output = await OpenOutputAsync(pdf);
PdfDictionary field = Assert.Single(OutputFields(output));
Assert.Equal("U", AnnotString(field, "T"));
Assert.Equal("uv", AnnotString(field, "V"));
}
[Fact]
public async Task OverlayWidgetJobMatchesArgv()
{
List<(string Dict, byte[]? StreamData)> extras = new()
{
("<< /Type /Annot /Subtype /Widget /Rect [10 10 100 30] /FT /Tx /T (O) /V (ov) >>", null),
};
byte[] overlay = BuildFormPdf(" /AcroForm << /Fields [5 0 R] >>", "[5 0 R]", extras, "BT (Stamp) Tj ET");
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] a = new byte[0];
InMemoryQpdfHost argvHost = HostWithTwo(primary, overlay);
{
(QpdfExecutionResult r, byte[] pdf) = await RunAsync(argvHost, "--overlay", "inputs/o.pdf", "--");
a = pdf;
Assert.Equal(QpdfExitCode.Success, r.ExitCode);
}
InMemoryQpdfHost jobHost = HostWithTwo(primary, overlay);
string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "overlay": [{"file": "inputs/o.pdf"}]}""";
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(jobHost, job, CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
Assert.Equal(a, b);
await using QpdfDocument output = await OpenOutputAsync(a);
Assert.Equal("O", AnnotString(Assert.Single(OutputFields(output)), "T"));
}
[Fact]
public async Task OverlayWidgetCycleWarnsAndStaysValid()
{
List<(string Dict, byte[]? StreamData)> extras = new()
{
("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /FT /Tx /T (C) /V (cv) /Parent 6 0 R >>", null),
("<< /FT /Tx /T (P) /Kids [5 0 R] /Parent 5 0 R >>", null),
};
byte[] overlay = BuildFormPdf(" /AcroForm << /Fields [5 0 R] >>", "[5 0 R]", extras, "BT (Stamp) Tj ET");
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Severity == QpdfDiagnosticSeverity.Warning);
await using QpdfDocument output = await OpenOutputAsync(pdf);
PdfDictionary field = Assert.Single(OutputFields(output));
Assert.Equal("P", AnnotString(field, "T"));
Assert.Single(OutputAnnots(output, 0));
}
[Fact]
public async Task OverlayWidgetMissingKidParentCorrectedWithWarning()
{
List<(string Dict, byte[]? StreamData)> extras = new()
{
("<< /FT /Tx /T (P) /Kids [<< /Type /Annot /Subtype /Widget /Rect [0 0 5 5] /T (DK) /V (dkv) >> 6 0 R] >>", null),
("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /Parent 5 0 R >>", null),
};
byte[] overlay = BuildFormPdf(" /AcroForm << /Fields [5 0 R] >>", "[6 0 R]", extras, "BT (Stamp) Tj ET");
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Severity == QpdfDiagnosticSeverity.Warning && diagnostic.Message.Contains("parent link"));
await using QpdfDocument output = await OpenOutputAsync(pdf);
PdfDictionary field = Assert.Single(OutputFields(output));
Assert.Equal("P", AnnotString(field, "T"));
Assert.True(field.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw));
PdfArray kids = Assert.IsType<PdfArray>(kidsRaw);
PdfDictionary directKid = Assert.IsType<PdfDictionary>(kids[0]);
Assert.True(directKid.TryGet(PdfName.FromAscii("Parent"), out QpdfObject? parentRaw));
PdfDictionary parent = Assert.IsType<PdfDictionary>(output.GetObject(Assert.IsType<PdfReference>(parentRaw).ObjectNumber));
Assert.Equal("P", AnnotString(parent, "T"));
}

[Fact]
public async Task DirectWidgetClonedWithoutFieldWhenFormless()
{
byte[] overlay = BuildAnnotPdf("[0 0 612 792]", "[<< /Type /Annot /Subtype /Widget /Rect [10 10 100 30] /FT /Tx /T (W) /V (vv) >>]", new List<(string Dict, byte[]? StreamData)>(), "BT (Stamp) Tj ET");
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
await using QpdfDocument output = await OpenOutputAsync(pdf);
PdfDictionary annot = Assert.Single(OutputAnnots(output, 0));
Assert.Equal("W", AnnotString(annot, "T"));
Assert.Equal("vv", AnnotString(annot, "V"));
PdfDictionary catalog = Assert.IsType<PdfDictionary>(output.GetObject(1));
Assert.False(catalog.TryGet(PdfName.FromAscii("AcroForm"), out QpdfObject? _));
}

[Fact]
public async Task RepeatedOverlayClonesAnnotationsIndependently()
{
byte[] one = BuildAnnotPdf("[0 0 612 792]", string.Empty, new List<(string Dict, byte[]? StreamData)>(), "BT (One) Tj ET");
byte[] two = BuildAnnotPdf("[0 0 612 792]", string.Empty, new List<(string Dict, byte[]? StreamData)>(), "BT (Two) Tj ET");
byte[] overlay = BuildAnnotPdf("[0 0 612 792]", "[<< /Type /Annot /Subtype /Text /Rect [10 10 60 60] /Contents (Twice) >>]", new List<(string Dict, byte[]? StreamData)>(), "BT (Stamp) Tj ET");
InMemoryQpdfHost host = HostWithTwo(one, overlay);
host.AddInput(QpdfPath.From("inputs/b.pdf"), two, QpdfInputOwnership.Copy);
List<string> argv = new() { "inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--", "--overlay", "inputs/o.pdf", "--repeat=1", "--", "outputs/a.pdf" };
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
await using QpdfDocument output = await OpenOutputAsync(pdf);
Assert.Equal(2, output.PageCount);
List<PdfDictionary> first = OutputAnnots(output, 0);
List<PdfDictionary> last = OutputAnnots(output, 1);
Assert.Single(first);
Assert.Single(last);
Assert.True(output.PageReferences[0].Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject? firstRaw));
Assert.True(output.PageReferences[1].Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject? lastRaw));
PdfReference firstLink = Assert.IsType<PdfReference>(Assert.Single(Assert.IsType<PdfArray>(firstRaw).Items));
PdfReference lastLink = Assert.IsType<PdfReference>(Assert.Single(Assert.IsType<PdfArray>(lastRaw).Items));
Assert.NotEqual(firstLink.ObjectNumber, lastLink.ObjectNumber);
Assert.Equal(AnnotRect(first[0]), AnnotRect(last[0]));
}
[Fact]
public async Task EmptyAnnotsArrayCreated()
{
byte[] primary = BuildPdf(new List<string> { "BT (Primary) Tj ET" });
byte[] overlay = BuildAnnotPdf("[0 0 612 792]", "[]", new List<(string Dict, byte[]? StreamData)>(), "BT (Stamp) Tj ET");
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
await using QpdfDocument output = await OpenOutputAsync(pdf);
PageLocation location = output.PageReferences[0];
Assert.True(location.Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject? annotsRaw));
Assert.Equal(0, Assert.IsType<PdfArray>(annotsRaw).Count);
}
[Fact]
public async Task ExistingAnnotationsAppended()
{
byte[] primary = BuildAnnotPdf("[0 0 612 792]", "[<< /Type /Annot /Subtype /Text /Rect [0 0 5 5] /Contents (Mine) >>]", new List<(string Dict, byte[]? StreamData)>(), "BT (Primary) Tj ET");
byte[] overlay = BuildAnnotPdf("[0 0 612 792]", "[<< /Type /Annot /Subtype /Text /Rect [10 10 60 60] /Contents (OverlayNote) >>]", new List<(string Dict, byte[]? StreamData)>(), "BT (Stamp) Tj ET");
(QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWithTwo(primary, overlay), "--overlay", "inputs/o.pdf", "--");
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
await using QpdfDocument output = await OpenOutputAsync(pdf);
List<PdfDictionary> annots = OutputAnnots(output, 0);
Assert.Equal(2, annots.Count);
Assert.Equal("Mine", AnnotString(annots[0], "Contents"));
Assert.Equal("OverlayNote", AnnotString(annots[1], "Contents"));
}
private static byte[] BuildFormPdf(string catalogInner, string annots, List<(string Dict, byte[]? StreamData)> extras, string contentText)
{
byte[] contentBytes = Encoding.ASCII.GetBytes(contentText);
List<string> bodies = new()
{
"<< /Type /Catalog /Pages 2 0 R" + catalogInner + " >>",
"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >>" + (annots.Length == 0 ? string.Empty : " /Annots " + annots) + " >>",
"<< /Length " + contentBytes.Length.ToString() + " >>",
};
foreach ((string dict, byte[]? _) in extras)
{
bodies.Add(dict);
}
List<byte[]?> payloads = new() { null, null, null, contentBytes };
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
private static PdfDictionary OutputForm(QpdfDocument output)
{
PdfDictionary catalog = Assert.IsType<PdfDictionary>(output.GetObject(1));
Assert.True(catalog.TryGet(PdfName.FromAscii("AcroForm"), out QpdfObject? raw));
return Assert.IsType<PdfDictionary>(raw);
}
private static List<PdfDictionary> OutputFields(QpdfDocument output)
{
PdfDictionary form = OutputForm(output);
Assert.True(form.TryGet(PdfName.FromAscii("Fields"), out QpdfObject? raw));
PdfArray fields = Assert.IsType<PdfArray>(raw);
List<PdfDictionary> resolved = new(fields.Count);
for (int i = 0; i < fields.Count; i++)
{
PdfReference reference = Assert.IsType<PdfReference>(fields[i]);
resolved.Add(Assert.IsType<PdfDictionary>(output.GetObject(reference.ObjectNumber)));
}
return resolved;
}
private static List<PdfReference> OutputFieldRefs(QpdfDocument output)
{
PdfDictionary form = OutputForm(output);
Assert.True(form.TryGet(PdfName.FromAscii("Fields"), out QpdfObject? raw));
PdfArray fields = Assert.IsType<PdfArray>(raw);
List<PdfReference> refs = new(fields.Count);
for (int i = 0; i < fields.Count; i++)
{
refs.Add(Assert.IsType<PdfReference>(fields[i]));
}
return refs;
}
private static List<PdfReference> OutputAnnotRefs(QpdfDocument output, int pageIndex)
{
PageLocation location = output.PageReferences[pageIndex];
Assert.True(location.Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject? raw));
PdfArray annots = Assert.IsType<PdfArray>(raw);
List<PdfReference> refs = new(annots.Count);
for (int i = 0; i < annots.Count; i++)
{
refs.Add(Assert.IsType<PdfReference>(annots[i]));
}
return refs;
}
private static PdfDictionary OutputFormDR(QpdfDocument output, PdfDictionary form)
{
Assert.True(form.TryGet(PdfName.FromAscii("DR"), out QpdfObject? raw));
if (raw is PdfReference link)
{
return Assert.IsType<PdfDictionary>(output.GetObject(link.ObjectNumber));
}
return Assert.IsType<PdfDictionary>(raw);
}

private static async Task<QpdfDocument> OpenOutputAsync(byte[] pdf)
{
InMemoryQpdfHost host = new();
host.AddInput(QpdfPath.From("inputs/reread.pdf"), pdf, QpdfInputOwnership.Copy);
return await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
}
private static List<PdfDictionary> OutputAnnots(QpdfDocument output, int pageIndex)
{
PageLocation location = output.PageReferences[pageIndex];
Assert.True(location.Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject? annotsRaw));
PdfArray annots = Assert.IsType<PdfArray>(annotsRaw);
List<PdfDictionary> resolved = new(annots.Count);
for (int i = 0; i < annots.Count; i++)
{
if (annots[i] is PdfDictionary direct)
{
resolved.Add(direct);
}
else
{
PdfReference reference = Assert.IsType<PdfReference>(annots[i]);
resolved.Add(Assert.IsType<PdfDictionary>(output.GetObject(reference.ObjectNumber)));
}
}
return resolved;
}
private static double[] AnnotRect(PdfDictionary annot)
{
Assert.True(annot.TryGet(PdfName.FromAscii("Rect"), out QpdfObject? raw));
PdfArray array = Assert.IsType<PdfArray>(raw);
Assert.Equal(4, array.Count);
double[] values = new double[4];
for (int i = 0; i < 4; i++)
{
if (array[i] is PdfInteger integer)
{
values[i] = (double)integer.Value;
}
else if (array[i] is PdfReal real)
{
values[i] = real.Value;
}
else
{
throw new Xunit.Sdk.XunitException("non-numeric rect");
}
}
return values;
}
private static double[] AnnotMatrix(PdfDictionary dictionary)
{
Assert.True(dictionary.TryGet(PdfName.FromAscii("Matrix"), out QpdfObject? raw));
PdfArray array = Assert.IsType<PdfArray>(raw);
Assert.Equal(6, array.Count);
double[] values = new double[6];
for (int i = 0; i < 6; i++)
{
if (array[i] is PdfInteger integer)
{
values[i] = (double)integer.Value;
}
else if (array[i] is PdfReal real)
{
values[i] = real.Value;
}
else
{
throw new Xunit.Sdk.XunitException("non-numeric matrix");
}
}
return values;
}
private static string AnnotName(PdfDictionary annot, string key)
{
Assert.True(annot.TryGet(PdfName.FromAscii(key), out QpdfObject? raw));
return Assert.IsType<PdfName>(raw).DisplayText;
}
private static string AnnotString(PdfDictionary annot, string key)
{
Assert.True(annot.TryGet(PdfName.FromAscii(key), out QpdfObject? raw));
return Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(raw).Value);
}
private static byte[] BuildAnnotPdf(string mediaBox, string annots, List<(string Dict, byte[]? StreamData)> extras, string contentText)
{
byte[] contentBytes = Encoding.ASCII.GetBytes(contentText);
List<string> bodies = new()
{
"<< /Type /Catalog /Pages 2 0 R >>",
"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
"<< /Type /Page /Parent 2 0 R /MediaBox " + mediaBox + " /Contents 4 0 R /Resources << >>" + (annots.Length == 0 ? string.Empty : " /Annots " + annots) + " >>",
"<< /Length " + contentBytes.Length.ToString() + " >>",
};
foreach ((string dict, byte[]? _) in extras)
{
bodies.Add(dict);
}
List<byte[]?> payloads = new() { null, null, null, contentBytes };
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
}
