using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
namespace Lokad.Qpdf.Tests.Content;
public sealed class GenerateAppearancesTests
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
private static async Task<byte[]> AppearanceBytesAsync(byte[] pdf, int annotIndex)
{
using JsonDocument pages = await InspectAsync(pdf, "--json", "--json-key=pages");
using JsonDocument full = await InspectAsync(pdf, "--json", "--json-key=qpdf", "--json-stream-data=inline");
JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
JsonElement page = pages.RootElement.GetProperty("pages")[0];
JsonElement annots = objects.GetProperty(PageObjectKey(page)).GetProperty("value").GetProperty("/Annots");
if (annots.ValueKind == JsonValueKind.String)
{
string[] linked = (annots.GetString() ?? string.Empty).Split((char)32);
annots = objects.GetProperty(string.Concat("obj:", linked[0], SP(), linked[1], " R")).GetProperty("value");
}
JsonElement entry = annots.EnumerateArray().Skip(annotIndex).First();
JsonElement annot = entry.ValueKind == JsonValueKind.String
? objects.GetProperty(StreamKey(entry.GetString() ?? string.Empty)).GetProperty("value")
: entry;
JsonElement ap = annot.GetProperty("/AP");
if (ap.ValueKind == JsonValueKind.String)
{
string[] linked = (ap.GetString() ?? string.Empty).Split((char)32);
ap = objects.GetProperty(string.Concat("obj:", linked[0], SP(), linked[1], " R")).GetProperty("value");
}
JsonElement normal = ap.GetProperty("/N");
JsonElement stream = normal.ValueKind == JsonValueKind.String
? objects.GetProperty(StreamKey(normal.GetString() ?? string.Empty))
: normal;
string data = stream.GetProperty("stream").GetProperty("data").GetString() ?? string.Empty;
return Convert.FromBase64String(data);
}
private static string StreamKey(string reference)
{
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
public async Task TextFieldAppearanceRegeneratesValue()
{
byte[] stale = Encoding.ASCII.GetBytes("q BT ET Q");
byte[] appearance = stale;
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(" /AcroForm << /NeedAppearances true >>", " /Contents 4 0 R /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /V (Hello) /DA (/Helv 12 Tf) /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /Helv 7 0 R >> >> /Length ", appearance.Length.ToString(), " >>"), appearance),
("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--generate-appearances")).Pdf;
byte[] bytes = await AppearanceBytesAsync(pdf, 0);
string text = Encoding.ASCII.GetString(bytes);
Assert.Contains("/Helv 12 Tf", text);
Assert.Contains("(Hello) Tj", text);
Assert.Contains("1 5.2 Td", text);
}

[Fact]
public async Task MissingAppearanceIsCreated()
{
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(" /AcroForm << /NeedAppearances true >>", " /Contents 4 0 R /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /V (Hello) /DA (/Helv 12 Tf) >>", null),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--generate-appearances")).Pdf;
byte[] bytes = await AppearanceBytesAsync(pdf, 0);
Assert.Contains("(Hello) Tj", Encoding.ASCII.GetString(bytes));
}
[Theory]
[InlineData(false, "/Off", "/Off")]
[InlineData(true, "/Yes", "/Off")]
public async Task CheckboxSynchronizesState(bool @checked, string value, string initial)
{
byte[] off = Encoding.ASCII.GetBytes("0 w 0 0 10 10 re S");
byte[] on = Encoding.ASCII.GetBytes("0 w 0 0 10 10 re f");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
string state = @checked ? "/Yes" : "/Off";
byte[] fixture = BuildPdf(" /AcroForm << /NeedAppearances true >>", " /Contents 4 0 R /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
(string.Concat("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /FT /Btn /V ", value, " /AS ", initial, " /AP << /N << /Off 6 0 R /Yes 7 0 R >> >> >>"), null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length ", off.Length.ToString(), " >>"), off),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length ", on.Length.ToString(), " >>"), on),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--generate-appearances")).Pdf;
using JsonDocument pages = await InspectAsync(pdf, "--json", "--json-key=pages");
using JsonDocument full = await InspectAsync(pdf, "--json", "--json-key=qpdf");
JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
JsonElement annots = objects.GetProperty(PageObjectKey(pages.RootElement.GetProperty("pages")[0])).GetProperty("value").GetProperty("/Annots");
JsonElement entry = annots.EnumerateArray().First();
JsonElement annot = entry.ValueKind == JsonValueKind.String
? objects.GetProperty(StreamKey(entry.GetString() ?? string.Empty)).GetProperty("value")
: entry;
string actual = annot.GetProperty("/AS").GetString() ?? string.Empty;
Assert.Equal(@checked ? "/Yes" : "/Off", actual);
}

[Fact]
public async Task ParentHeldFieldIsSkipped()
{
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] first = Encoding.ASCII.GetBytes("0 w 0 0 10 10 re S");
byte[] second = Encoding.ASCII.GetBytes("0 w 0 0 10 10 re f");
byte[] fixture = BuildPdf(" /AcroForm << /NeedAppearances true >>", " /Contents 4 0 R /Annots [6 0 R 7 0 R]", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /FT /Btn /Ff 32768 /V /Choice2 /Kids [6 0 R 7 0 R] >>", null),
("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /Parent 5 0 R /AS /Off /AP << /N << /Off 8 0 R /Choice1 8 0 R >> >> >>", null),
("<< /Type /Annot /Subtype /Widget /Rect [0 20 10 30] /Parent 5 0 R /AS /Off /AP << /N << /Off 8 0 R /Choice2 9 0 R >> >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length ", first.Length.ToString(), " >>"), first),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length ", second.Length.ToString(), " >>"), second),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--generate-appearances")).Pdf;
using JsonDocument pages = await InspectAsync(pdf, "--json", "--json-key=pages");
using JsonDocument full = await InspectAsync(pdf, "--json", "--json-key=qpdf");
JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
JsonElement annots = objects.GetProperty(PageObjectKey(pages.RootElement.GetProperty("pages")[0])).GetProperty("value").GetProperty("/Annots");
List<string> states = new();
foreach (JsonElement entry in annots.EnumerateArray())
{
JsonElement annot = entry.ValueKind == JsonValueKind.String
? objects.GetProperty(StreamKey(entry.GetString() ?? string.Empty)).GetProperty("value")
: entry;
states.Add(annot.GetProperty("/AS").GetString() ?? string.Empty);
}
Assert.Equal(new List<string> { "/Off", "/Off" }, states);
}
[Fact]
public async Task RadioButtonIsSkipped()
{
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] appearance = Encoding.ASCII.GetBytes("0 w 0 0 10 10 re S");
byte[] fixture = BuildPdf(" /AcroForm << /Fields [5 0 R] /NeedAppearances true >>", " /Contents 4 0 R /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /FT /Btn /Ff 32768 /T (R) /V /Choice1 /AS /Off /AP << /N << /Off 6 0 R /Choice1 6 0 R >> >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length ", appearance.Length.ToString(), " >>"), appearance),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--generate-appearances")).Pdf;
using JsonDocument pages = await InspectAsync(pdf, "--json", "--json-key=pages");
using JsonDocument full = await InspectAsync(pdf, "--json", "--json-key=qpdf");
JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
JsonElement annots = objects.GetProperty(PageObjectKey(pages.RootElement.GetProperty("pages")[0])).GetProperty("value").GetProperty("/Annots");
JsonElement entry = annots.EnumerateArray().First();
JsonElement annot = entry.ValueKind == JsonValueKind.String
? objects.GetProperty(StreamKey(entry.GetString() ?? string.Empty)).GetProperty("value")
: entry;
Assert.Equal("/Off", annot.GetProperty("/AS").GetString());
Assert.Equal("/Choice1", annot.GetProperty("/V").GetString());
}
[Fact]
public async Task NonAsciiFallsBackToQuestionMark()
{
byte[] stale = Encoding.ASCII.GetBytes("q BT ET Q");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] value = new byte[] { 254, 255, 0, 99, 0, 97, 0, 102, 0, 233 };
string hex = BitConverter.ToString(value).Replace("-", string.Empty);
string annotDict = string.Concat("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /V <", hex, "> /DA (/Helv 12 Tf) /AP << /N 6 0 R >> >>");
byte[] fixture = BuildPdf(" /AcroForm << /NeedAppearances true >>", " /Contents 4 0 R /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
(annotDict, null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /Helv 7 0 R >> >> /Length ", stale.Length.ToString(), " >>"), stale),
("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--generate-appearances")).Pdf;
byte[] bytes = await AppearanceBytesAsync(pdf, 0);
Assert.Contains("(caf?) Tj", Encoding.ASCII.GetString(bytes));
}

[Fact]
public async Task WinAnsiEncodingKeepsAccents()
{
byte[] stale = Encoding.ASCII.GetBytes("q BT ET Q");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
string hex = "FEFF00630061006600E9";
string annotDict = string.Concat("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /V <", hex, "> /DA (/Helv 12 Tf) /AP << /N 6 0 R >> >>");
byte[] fixture = BuildPdf(" /AcroForm << /NeedAppearances true >>", " /Contents 4 0 R /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
(annotDict, null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >> >> >> /Length ", stale.Length.ToString(), " >>"), stale),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--generate-appearances")).Pdf;
byte[] bytes = await AppearanceBytesAsync(pdf, 0);
Assert.Contains(((char)233).ToString(), System.Text.Encoding.GetEncoding(28591).GetString(bytes));
}
[Fact]
public async Task AcroFormDefaultResourcesEncodingKeepsAccents()
{
byte[] stale = Encoding.ASCII.GetBytes("q BT ET Q");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
string annotDict = string.Concat("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /V <96> /DA (/F1 12 Tf) /AP << /N 6 0 R >> >>");
byte[] fixture = BuildPdf(" /AcroForm << /NeedAppearances true /DR << /Font << /F1 7 0 R >> >> >>", " /Contents 4 0 R /Annots [5 0 R] /Resources << /Font << /Helv 8 0 R >> >>", new List<(string Dict, byte[]? StreamData)>()
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
(annotDict, null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Length ", stale.Length.ToString(), " >>"), stale),
("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", null),
("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--generate-appearances")).Pdf;
byte[] bytes = await AppearanceBytesAsync(pdf, 0);
Assert.Contains(((char)140).ToString(), System.Text.Encoding.GetEncoding(28591).GetString(bytes));
}
[Fact]
public async Task FieldDefaultResourcesIsIgnored()
{
byte[] stale = Encoding.ASCII.GetBytes("q BT ET Q");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
string annotDict = string.Concat("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /V <96> /DA (/F1 12 Tf) /DR << /Font << /F1 7 0 R >> >> /AP << /N 6 0 R >> >>");
byte[] fixture = BuildPdf(" /AcroForm << /NeedAppearances true >>", " /Contents 4 0 R /Annots [5 0 R] /Resources << /Font << /Helv 8 0 R >> >>", new List<(string Dict, byte[]? StreamData)>()
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
(annotDict, null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Length ", stale.Length.ToString(), " >>"), stale),
("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", null),
("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--generate-appearances")).Pdf;
byte[] bytes = await AppearanceBytesAsync(pdf, 0);
// The reference ignores field-level /DR for encoding (AcroForm /DR only),
// so a WinAnsi font reachable only through the field falls back to ASCII.
Assert.Contains("(?) Tj", Encoding.ASCII.GetString(bytes));
}
[Fact]
public async Task MergesAcroFormFontIntoAppearanceResources()
{
byte[] stale = Encoding.ASCII.GetBytes("q BT ET Q");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
string annotDict = string.Concat("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /V <96> /DA (/F1 12 Tf) /AP << /N 6 0 R >> >>");
byte[] fixture = BuildPdf(" /AcroForm << /NeedAppearances true /DR << /Font << /F1 7 0 R >> >> >>", " /Contents 4 0 R /Annots [5 0 R] /Resources << /Font << /Helv 8 0 R >> >>", new List<(string Dict, byte[]? StreamData)>()
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
(annotDict, null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /Helv 8 0 R >> >> /Length ", stale.Length.ToString(), " >>"), stale),
("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", null),
("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--generate-appearances")).Pdf;
byte[] bytes = await AppearanceBytesAsync(pdf, 0);
Assert.Contains(((char)140).ToString(), System.Text.Encoding.GetEncoding(28591).GetString(bytes));
using JsonDocument pages = await InspectAsync(pdf, "--json", "--json-key=pages");
using JsonDocument full = await InspectAsync(pdf, "--json", "--json-key=qpdf");
JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
JsonElement page = pages.RootElement.GetProperty("pages")[0];
JsonElement annots = objects.GetProperty(PageObjectKey(page)).GetProperty("value").GetProperty("/Annots");
if (annots.ValueKind == JsonValueKind.String)
{
string[] linked = (annots.GetString() ?? string.Empty).Split((char)32);
annots = objects.GetProperty(string.Concat("obj:", linked[0], SP(), linked[1], " R")).GetProperty("value");
}
JsonElement entry = annots.EnumerateArray().First();
JsonElement annot = entry.ValueKind == JsonValueKind.String
? objects.GetProperty(StreamKey(entry.GetString() ?? string.Empty)).GetProperty("value")
: entry;
JsonElement ap = annot.GetProperty("/AP");
if (ap.ValueKind == JsonValueKind.String)
{
string[] linked = (ap.GetString() ?? string.Empty).Split((char)32);
ap = objects.GetProperty(string.Concat("obj:", linked[0], SP(), linked[1], " R")).GetProperty("value");
}
JsonElement normal = ap.GetProperty("/N");
JsonElement appearanceStream = normal.ValueKind == JsonValueKind.String
? objects.GetProperty(StreamKey(normal.GetString() ?? string.Empty))
: normal;
JsonElement appearanceDict = appearanceStream.TryGetProperty("stream", out JsonElement streamInfo)
? streamInfo.GetProperty("dict")
: appearanceStream;
JsonElement fontDict = appearanceDict.GetProperty("/Resources").GetProperty("/Font");
Assert.True(fontDict.TryGetProperty("/F1", out _));
Assert.True(fontDict.TryGetProperty("/Helv", out _));
}
[Fact]
public async Task ParentHeldTextFieldGeneratesAppearance()
{
byte[] stale = Encoding.ASCII.GetBytes("q BT ET Q");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(" /AcroForm << /Fields [5 0 R] /NeedAppearances true >>", " /Contents 4 0 R /Annots [6 0 R] /Resources << /Font << /Helv 8 0 R >> >>", new List<(string Dict, byte[]? StreamData)>()
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /FT /Tx /T (Name) /V (Hello) /DA (/Helv 12 Tf) >>", null),
("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /Parent 5 0 R /AP << /N 7 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Length ", stale.Length.ToString(), " >>"), stale),
("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--generate-appearances")).Pdf;
byte[] bytes = await AppearanceBytesAsync(pdf, 0);
string text = Encoding.ASCII.GetString(bytes);
Assert.Contains("(Hello) Tj", text);
Assert.Contains("1 5.2 Td", text);
}
[Fact]
public async Task MergeUsesPrimaryDefaultResourcesEncoding()
{
byte[] stale = Encoding.ASCII.GetBytes("q BT ET Q");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
string annotDict = string.Concat("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /V <96> /DA (/F1 12 Tf) /AP << /N 6 0 R >> >>");
byte[] formed = BuildPdf(" /AcroForm << /NeedAppearances true /DR << /Font << /F1 7 0 R >> >> >>", " /Contents 4 0 R /Annots [5 0 R] /Resources << /Font << /Helv 8 0 R >> >>", new List<(string Dict, byte[]? StreamData)>()
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
(annotDict, null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Length ", stale.Length.ToString(), " >>"), stale),
("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", null),
("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
});
byte[] plain = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[]? StreamData)>()
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
});
InMemoryQpdfHost host = HostWith("inputs/a.pdf", formed);
host.AddInput(QpdfPath.From("inputs/b.pdf"), plain, QpdfInputOwnership.Copy);
QpdfExecutionResult result = await Qpdf.ExecuteAsync(
host, new QpdfCommandInvocation(["--empty", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--generate-appearances", "--", "outputs/m.pdf"]), CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] pdf));
byte[] bytes = await AppearanceBytesAsync(pdf, 0);
Assert.Contains(((char)140).ToString(), System.Text.Encoding.GetEncoding(28591).GetString(bytes));
}

[Fact]
public async Task ListBoxHighlightsSelectedRow()
{
byte[] stale = Encoding.ASCII.GetBytes("q BT ET Q");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(" /AcroForm << /NeedAppearances true >>", " /Contents 4 0 R /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /Annot /Subtype /Widget /Rect [100 500 300 620] /FT /Ch /V (B) /DA (/Helv 12 Tf) /Opt [(A) (B) (C)] /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 200 120] /Length ", stale.Length.ToString(), " >>"), stale),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--generate-appearances")).Pdf;
byte[] listBytes = await AppearanceBytesAsync(pdf, 0);
string text = Encoding.ASCII.GetString(listBytes);
Assert.Contains("0.85 0.85 0.85 rg", text);
Assert.Contains("(A) Tj", text);
Assert.Contains("(B) Tj", text);
Assert.Contains("(C) Tj", text);
}
[Fact]
public async Task SignatureFieldIsSkipped()
{
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(" /AcroForm << /NeedAppearances true >>", " /Contents 4 0 R /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /Annot /Subtype /Widget /Rect [0 0 100 50] /FT /Sig /AP << /N 6 0 R >> >>", null),
("<< /Type /XObject /Subtype /Form /BBox [0 0 100 50] /Length 0 >>", new byte[0]),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--generate-appearances")).Pdf;
byte[] output = await PageContentAsync(pdf);
Assert.DoesNotContain("EMC", Encoding.ASCII.GetString(output));
}
[Fact]
public async Task PushbuttonWarnsAndKeeps()
{
byte[] on = Encoding.ASCII.GetBytes("0 w 0 0 10 10 re f");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(" /AcroForm << /NeedAppearances true >>", " /Contents 4 0 R /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /FT /Btn /Ff 65536 /V /Off /AP << /N << /Off 6 0 R >> >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length ", on.Length.ToString(), " >>"), on),
});
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--generate-appearances", "--", "outputs/a.pdf" };
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == QpdfDiagnosticCode.UnsupportedFeature);
}

[Theory]
[InlineData("--generate-appearances", "--generate-appearances")]
[InlineData("--generate-appearances=x")]
public async Task MalformedGenerateFlagsFail(params string[] flags)
{
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[]? StreamData)>
{
("<< /Length 15 >>", Encoding.ASCII.GetBytes("BT (Page) Tj ET")),
});
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
List<string> argv = new() { "inputs/a.pdf" };
argv.AddRange(flags);
argv.AddRange(["--", "outputs/a.pdf"]);
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
}
[Fact]
public async Task GenerateRejectedWithInspection()
{
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[]? StreamData)>
{
("<< /Length 15 >>", Encoding.ASCII.GetBytes("BT (Page) Tj ET")),
});
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--generate-appearances"]), CancellationToken.None);
Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
}
[Fact]
public async Task JobRoundTripMatchesArgv()
{
byte[] stale = Encoding.ASCII.GetBytes("q BT ET Q");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(" /AcroForm << /NeedAppearances true >>", " /Contents 4 0 R /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /V (Hello) /DA (/Helv 12 Tf) /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /Helv 7 0 R >> >> /Length ", stale.Length.ToString(), " >>"), stale),
("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
});
byte[] a = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--generate-appearances")).Pdf;
InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", fixture);
string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "generateAppearances": true}""";
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(jobHost, job, CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
Assert.Equal(a, b);
}
[Theory]
[InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "generateAppearances": "yes"}""")]
[InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "generateAppearances": true, "generateAppearances": true}""")]
public async Task JobMalformedFails(string jobJson)
{
byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[]? StreamData)>
{
("<< /Length 15 >>", Encoding.ASCII.GetBytes("BT (Page) Tj ET")),
});
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(HostWith("inputs/a.pdf", fixture), jobJson, CancellationToken.None);
Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
}
[Fact]
public async Task WholeFileJobGenerates()
{
byte[] stale = Encoding.ASCII.GetBytes("q BT ET Q");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(" /AcroForm << /NeedAppearances true >>", " /Contents 4 0 R /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /V (Hello) /DA (/Helv 12 Tf) /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Length ", stale.Length.ToString(), " >>"), stale),
});
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "generateAppearances": true}""";
QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
byte[] bytes = await AppearanceBytesAsync(pdf, 0);
Assert.Contains("(Hello) Tj", Encoding.ASCII.GetString(bytes));
}
[Fact]
public async Task MergeThenGenerateAppliesToFinalPages()
{
byte[] stale = Encoding.ASCII.GetBytes("q BT ET Q");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(" /AcroForm << /NeedAppearances true >>", " /Contents 4 0 R /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /V (Hello) /DA (/Helv 12 Tf) /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Length ", stale.Length.ToString(), " >>"), stale),
});
InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
host.AddInput(QpdfPath.From("inputs/b.pdf"), fixture, QpdfInputOwnership.Copy);
List<string> argv = new() { "inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--generate-appearances", "--", "outputs/a.pdf" };
QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
Assert.Equal(QpdfExitCode.Success, result.ExitCode);
Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
using JsonDocument pages = await InspectAsync(pdf, "--json", "--json-key=pages");
Assert.Equal(2, pages.RootElement.GetProperty("pages").GetArrayLength());
byte[] bytes = await AppearanceBytesAsync(pdf, 0);
Assert.Contains("(Hello) Tj", Encoding.ASCII.GetString(bytes));
}
[Fact]
public async Task GenerateThenFlattenStampsFreshAppearance()
{
byte[] stale = Encoding.ASCII.GetBytes("q BT ET Q");
byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
byte[] fixture = BuildPdf(" /AcroForm << /NeedAppearances true >>", " /Contents 4 0 R /Annots [5 0 R]", new List<(string Dict, byte[]? StreamData)>
{
(string.Concat("<< /Length ", content.Length.ToString(), " >>"), content),
("<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /V (Hello) /DA (/Helv 12 Tf) /AP << /N 6 0 R >> >>", null),
(string.Concat("<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Length ", stale.Length.ToString(), " >>"), stale),
});
byte[] pdf = (await RunAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--generate-appearances", "--flatten-annotations=all")).Pdf;
byte[] output = await PageContentAsync(pdf);
string text = Encoding.ASCII.GetString(output);
Assert.Contains("/Fxo1 Do", text);
byte[] stamp = await FormContentForGenerateAsync(pdf);
Assert.Contains("(Hello) Tj", Encoding.ASCII.GetString(stamp));
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
private static async Task<byte[]> FormContentForGenerateAsync(byte[] pdf)
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
string formReference = xobjects.GetProperty("/Fxo1").GetString() ?? string.Empty;
string[] formParts = formReference.Split((char)32);
string formKey = string.Concat("obj:", formParts[0], SP(), formParts[1], " R");
string data = objects.GetProperty(formKey).GetProperty("stream").GetProperty("data").GetString() ?? string.Empty;
return Convert.FromBase64String(data);
}
}

