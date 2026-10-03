using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
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
List<string> argv = new() { "inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "inputs/a.pdf", "2", "--overlay", "inputs/o.pdf", "--to=2", "--", "--", "outputs/a.pdf" };
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
List<string> argv = new() { "inputs/a.pdf", "--pages", "inputs/a.pdf", "2", "--overlay", "inputs/o.pdf", "--", "--", "outputs/a.pdf" };
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
List<string> argv = new() { "inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--overlay", "inputs/o.pdf", "--", "--", "outputs/a.pdf" };
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
[InlineData("--overlay", "inputs/o.pdf", "--to=")]
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
}
