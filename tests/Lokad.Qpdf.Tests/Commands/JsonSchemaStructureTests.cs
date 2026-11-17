using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Operations;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class JsonSchemaStructureTests
{
    public static IEnumerable<object[]> Shapes()
    {
        const string nested = """{"rows":[{"value":"any PDF value"}]}""";
        yield return [nested, """{"rows":[]}""", true, ""];
        yield return [nested, """{"rows":{"value":42}}""", true, ""];
        yield return [nested, """{"rows":[{"value":null},{"value":{"private":[true,{}]}}]}""", true, ""];
        yield return [nested, """{"rows":0}""", false, "$.rows[]: expected a dictionary"];
        yield return [nested, """{"rows":[{}]}""", false, "missing key 'value'"];
        yield return [nested, """{"rows":[{"value":null,"extra":0}]}""", false, "unknown key 'extra'"];
        yield return [nested, """{}""", false, "missing key 'rows'"];
        yield return [nested, """{"rows":[],"extra":null}""", false, "$: unknown key 'extra'"];
        yield return [nested, """{"\u0072ows":[{"\u0076alue":false}]}""", true, ""];
        const string map = """{"map":{"<arbitrary key>":{"required":"description"}}}""";
        yield return [map, """{"map":{}}""", true, ""];
        yield return [map, """{"map":{"obj:4 7 R":{"required":null},"trailer":{"required":[0,{}]}}}""", true, ""];
        yield return [map, """{"map":{"key":{}}}""", false, "missing key 'required'"];
        yield return [map, """{"map":{"key":false}}""", false, "expected a dictionary"];
        yield return [map, """{"map":{"key":{"required":1,"extra":2}}}""", false, "unknown key 'extra'"];
        const string fixedArray = """{"pair":[{"first":"description"},{"<object>":"PDF value"}]}""";
        yield return [fixedArray, """{"pair":[{"first":1},{}]}""", true, ""];
        yield return [fixedArray, """{"pair":[{"first":{}},{"arbitrary":[0,null]}]}""", true, ""];
        yield return [fixedArray, """{"pair":[{"first":1}]}""", false, "array length differs"];
        yield return [fixedArray, """{"pair":[{"first":1},{},{}]}""", false, "array length differs"];
        yield return [fixedArray, """{"pair":{}}""", false, "expected an array"];
        yield return [fixedArray, """{"pair":[{},{}]}""", false, "$.pair[0]: missing key 'first'"];
        yield return ["""{"empty":[]}""", """{"empty":[]}""", true, ""];
        yield return ["""{"empty":[]}""", """{"empty":[null]}""", false, "array length differs"];
        yield return ["""{"empty":[]}""", """{"empty":null}""", false, "expected an array"];
        const string any = """{"values":["unconstrained"]}""";
        yield return [any, """{"values":[null,true,42,"text",[],{}]}""", true, ""];
        yield return [any, """{"values":"5 7 R"}""", true, ""];
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void DescriptionContainersOwnFormatRules(string schema, string json, bool valid, string expected)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        JsonFormatValidator.Check(Encoding.UTF8.GetBytes(json), schema, 2, [], true, governor, diagnostics, CancellationToken.None);
        QpdfDiagnostic result = Assert.Single(diagnostics.Entries);
        Assert.Equal(valid ? QpdfDiagnosticCode.JsonSchemaCheck : QpdfDiagnosticCode.CheckFailed, result.Code);
        Assert.Equal(valid ? QpdfDiagnosticSeverity.Info : QpdfDiagnosticSeverity.Error, result.Severity);
        if (!valid) { Assert.Contains(expected, result.Message, StringComparison.Ordinal); }
    }

    private static JsonObject Page() => new()
    {
        ["object"] = "3 0 R", ["images"] = new JsonArray(), ["contents"] = new JsonArray(),
        ["label"] = null, ["outlines"] = new JsonArray(), ["pageposfrom1"] = 1
    };

    [Theory]
    [InlineData("missing", "missing key 'contents'")]
    [InlineData("unknown", "unknown key 'private'")]
    [InlineData("image", "$.pages[].images[]: expected a dictionary")]
    public void DescribedPageAndImageFieldsRejectNestedDrift(string kind, string expected)
    {
        JsonObject page = Page();
        if (kind == "missing") { page.Remove("contents"); }
        else if (kind == "unknown") { page["private"] = 42; }
        else { page["images"] = new JsonArray(42); }
        QpdfDiagnostic result = Check(new JsonObject { ["pages"] = new JsonArray(page) }.ToJsonString(), 2, ["pages"], true);
        Assert.Equal(QpdfDiagnosticCode.CheckFailed, result.Code); Assert.Contains(expected, result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void VersionedMetadataAndWildcardObjectsFollowTheirDescription(int version)
    {
        string json = version == 1 ? """{"objects":{"4 7 R":[{},null],"trailer":{"/Private":true}},"objectinfo":{"4 7 R":{"stream":{"is":false,"length":null,"filter":null}}}}"""
            : """{"qpdf":[{"jsonversion":2,"pdfversion":"1.7","maxobjectid":4,"pushedinheritedpageresources":false,"calledgetallpages":false},{"obj:4 7 R":{"value":[{},null]},"trailer":{"value":{"/Private":true}}}]}""";
        string[] keys = version == 1 ? ["objects", "objectinfo"] : ["qpdf"];
        Assert.Equal(QpdfDiagnosticCode.JsonSchemaCheck, Check(json, version, keys, true).Code);
        JsonObject root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException();
        if (version == 1) { root["objectinfo"]?["4 7 R"]?["stream"]?.AsObject().Remove("is"); }
        else { root["qpdf"]?[0]?.AsObject().Remove("calledgetallpages"); }
        QpdfDiagnostic failure = Check(root.ToJsonString(), version, keys, true);
        Assert.Equal(QpdfDiagnosticCode.CheckFailed, failure.Code); Assert.Contains("missing key", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedKeysAndMinimalOutputControlRequiredRootFields(bool minimal)
    {
        JsonObject root = new() { ["pages"] = new JsonArray() };
        if (!minimal) { root["version"] = 2; root["parameters"] = new JsonObject { ["decodelevel"] = "generalized" }; }
        Assert.Equal(QpdfDiagnosticCode.JsonSchemaCheck, Check(root.ToJsonString(), 2, ["pages"], minimal).Code);
        root["attachments"] = new JsonObject();
        QpdfDiagnostic extra = Check(root.ToJsonString(), 2, ["pages"], minimal);
        Assert.Equal(QpdfDiagnosticCode.CheckFailed, extra.Code); Assert.Contains("unknown key 'attachments'", extra.Message, StringComparison.Ordinal);
        root.Remove("attachments"); root.Remove(minimal ? "pages" : "parameters");
        Assert.Contains("missing key '" + (minimal ? "pages" : "parameters") + "'", Check(root.ToJsonString(), 2, ["pages"], minimal).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageArrayReferencesAndArbitraryPdfLeavesStayUnconstrained()
    {
        JsonObject image = new()
        {
            ["bitspercomponent"] = 8, ["colorspace"] = new JsonArray("/ICCBased", "7 0 R"),
            ["decodeparms"] = "5 7 R", ["filter"] = "6 2 R", ["filterable"] = false,
            ["height"] = 1, ["width"] = 2, ["name"] = "/Im", ["object"] = "4 0 R"
        };
        JsonObject page = Page(); page["images"] = new JsonArray(image);
        page["label"] = new JsonObject { ["/Private"] = new JsonArray(null, true, 42) };
        Assert.Equal(QpdfDiagnosticCode.JsonSchemaCheck, Check(new JsonObject { ["pages"] = new JsonArray(page) }.ToJsonString(), 2, ["pages"], true).Code);
    }

    private static QpdfDiagnostic Check(string json, int version, string[] keys, bool minimal)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        JsonExecution.CheckJsonSchema(json, Encoding.UTF8.GetByteCount(json), version, keys, minimal, governor, diagnostics, CancellationToken.None);
        return Assert.Single(diagnostics.Entries);
    }
}
