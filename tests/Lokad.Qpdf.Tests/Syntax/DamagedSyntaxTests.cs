using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Syntax;

public sealed class DamagedSyntaxTests
{
    // Independently authored byte fixtures and exact stdout observed with qpdf 12.4.2.
    // Latin-1 maps bytes reversibly; expected values came from raw stdout, not UTF-8 logs.
    // Ordinary tests use these snapshots without a reference executable or checkout.
    private sealed record Observation(string Name, string Body, bool Sanity, long[] Budgets, string Value, bool Warns);

    internal static IEnumerable<(string Name, string Body, bool Sanity, long Budget, string Expected, bool Warns)> Cases()
    {
        Observation[] observations =
        [
            new("damage-name-identity", "<< /A#2 1 /A#232 2 >>", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "<< /A#2 1 /A#232 2 >>", true),
            new("damage-name-identity", "<< /A#2 1 /A#232 2 >>", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "<< /A#2 1 /A#232 2 >>", true),
            new("damage-name-multiple-hashes", "/A#Q#G", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "/A#Q#G", true),
            new("damage-name-multiple-hashes", "/A#Q#G", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "/A#Q#G", true),
            new("damage-array-backslash-eof", "[ (abc\\", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-array-backslash-eof", "[ (abc\\", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-array-dict-close", "[ << /A 1 ] 2 >> 3 ]", false, [0L, 2L, 3L, 6L, 15L],
                "[ << /A 1 /QPDFFake2 2 >> 3 ]", true),
            new("damage-array-dict-close", "[ << /A 1 ] 2 >> 3 ]", false, [1L],
                "null", true),
            new("damage-array-dict-close", "[ << /A 1 ] 2 >> 3 ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-array-endobj", "[ 1 endobj 2 ]", false, [0L, 2L, 3L, 6L, 15L],
                "[ 1 (endobj) 2 ]", true),
            new("damage-array-endobj", "[ 1 endobj 2 ]", false, [1L],
                "null", true),
            new("damage-array-endobj", "[ 1 endobj 2 ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-array-endstream", "[ 1 endstream 2 ]", false, [0L, 2L, 3L, 6L, 15L],
                "[ 1 (endstream) 2 ]", true),
            new("damage-array-endstream", "[ 1 endstream 2 ]", false, [1L],
                "null", true),
            new("damage-array-endstream", "[ 1 endstream 2 ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-array-eof", "[ 1 2", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-array-eof", "[ 1 2", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-array-hex-eof", "[ <ABC", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-array-hex-eof", "[ <ABC", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-array-literal-eof", "[ (abc", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-array-literal-eof", "[ (abc", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-array-paren", "[ 1 ) 2 ]", false, [0L, 2L, 3L, 6L, 15L],
                "[ 1 null 2 ]", true),
            new("damage-array-paren", "[ 1 ) 2 ]", false, [1L],
                "null", true),
            new("damage-array-paren", "[ 1 ) 2 ]", true, [0L, 2L, 3L, 6L, 15L],
                "[ 1 null 2 ]", true),
            new("damage-array-paren", "[ 1 ) 2 ]", true, [1L],
                "null", true),
            new("damage-array-stream", "[ 1 stream 2 ]", false, [0L, 2L, 3L, 6L, 15L],
                "[ 1 (stream) 2 ]", true),
            new("damage-array-stream", "[ 1 stream 2 ]", false, [1L],
                "null", true),
            new("damage-array-stream", "[ 1 stream 2 ]", true, [0L, 2L, 3L, 6L, 15L],
                "[ 1 null 2 ]", true),
            new("damage-array-stream", "[ 1 stream 2 ]", true, [1L],
                "null", true),
            new("damage-dict-array-close", "<< /A [ 1 >> /B 2 ] /C 3 >>", false, [0L, 2L, 3L, 6L, 15L],
                "<< /A [ 1 null /B 2 ] /C 3 >>", true),
            new("damage-dict-array-close", "<< /A [ 1 >> /B 2 ] /C 3 >>", false, [1L],
                "null", true),
            new("damage-dict-array-close", "<< /A [ 1 >> /B 2 ] /C 3 >>", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-dict-eof", "<< /A 1", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-dict-eof", "<< /A 1", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-dict-hex-eof", "<< /A <ABC", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-dict-hex-eof", "<< /A <ABC", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-dict-key-close", "<< /A 1 ] /B 2 >>", false, [0L, 2L, 3L, 6L, 15L],
                "<< /A 1 /B 2 >>", true),
            new("damage-dict-key-close", "<< /A 1 ] /B 2 >>", false, [1L],
                "null", true),
            new("damage-dict-key-close", "<< /A 1 ] /B 2 >>", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-dict-literal-eof", "<< /A (abc", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-dict-literal-eof", "<< /A (abc", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-dict-name-short", "<< /A#2 1 /B 2 >>", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "<< /A#2 1 /B 2 >>", true),
            new("damage-dict-name-short", "<< /A#2 1 /B 2 >>", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "<< /A#2 1 /B 2 >>", true),
            new("damage-dot-reals", "[ . +. -. ]", false, [0L, 6L, 15L],
                "[ (.) (+.) (-.) ]", true),
            new("damage-dot-reals", "[ . +. -. ]", false, [1L, 2L, 3L],
                "null", true),
            new("damage-dot-reals", "[ . +. -. ]", true, [0L, 6L, 15L],
                "[ null null null ]", true),
            new("damage-dot-reals", "[ . +. -. ]", true, [1L, 2L, 3L],
                "null", true),
            new("damage-empty-name", "/", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "/", false),
            new("damage-empty-name", "/", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "/", false),
            new("damage-hex-first", "[ <GABC> 2 ]", false, [0L, 6L, 15L],
                "[ null (ABC) null 2 ]", true),
            new("damage-hex-first", "[ <GABC> 2 ]", false, [1L, 2L, 3L],
                "null", true),
            new("damage-hex-first", "[ <GABC> 2 ]", true, [0L, 6L, 15L],
                "[ null null null 2 ]", true),
            new("damage-hex-first", "[ <GABC> 2 ]", true, [1L, 2L, 3L],
                "null", true),
            new("damage-hex-tail", "[ <A G 1> 2 ]", false, [0L, 3L, 6L, 15L],
                "[ null 1 null 2 ]", true),
            new("damage-hex-tail", "[ <A G 1> 2 ]", false, [1L, 2L],
                "null", true),
            new("damage-hex-tail", "[ <A G 1> 2 ]", true, [0L, 3L, 6L, 15L],
                "[ null 1 null 2 ]", true),
            new("damage-hex-tail", "[ <A G 1> 2 ]", true, [1L, 2L],
                "null", true),
            new("damage-invalid-numbers", "[ 1..2 1.2.3 1e3 .5x 1-2 ++1 --1 +-1 ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-invalid-numbers", "[ 1..2 1.2.3 1e3 .5x 1-2 ++1 --1 +-1 ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-literal-byte-range", "[ (\\000\\001\\037\\177\\200\\377) ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ <00011f7f80ff> ]", false),
            new("damage-literal-byte-range", "[ (\\000\\001\\037\\177\\200\\377) ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ <00011f7f80ff> ]", false),
            new("damage-literal-escapes", "[ (a\\q\\8\\777\\12z\\(b\\)) ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ (aq8\u00ff\\nz\\(b\\)) ]", false),
            new("damage-literal-escapes", "[ (a\\q\\8\\777\\12z\\(b\\)) ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ (aq8\u00ff\\nz\\(b\\)) ]", false),
            new("damage-literal-nesting", "[ (a(b)c) (a\\)b) (a\\\\b) ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ (a\\(b\\)c) (a\\)b) (a\\\\b) ]", false),
            new("damage-literal-nesting", "[ (a(b)c) (a\\)b) (a\\\\b) ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ (a\\(b\\)c) (a\\)b) (a\\\\b) ]", false),
            new("damage-literal-raw-high", "[ (a\u0080\u00ffb) ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ <6180ff62> ]", false),
            new("damage-literal-raw-high", "[ (a\u0080\u00ffb) ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ <6180ff62> ]", false),
            new("damage-name-escaped-high", "[ /A#80#FF ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ /A#80#ff ]", false),
            new("damage-name-escaped-high", "[ /A#80#FF ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ /A#80#ff ]", false),
            new("damage-name-incomplete", "[ /A#2 /B# /C#GG 2 ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ /A#2 /B# /C#GG 2 ]", true),
            new("damage-name-incomplete", "[ /A#2 /B# /C#GG 2 ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ /A#2 /B# /C#GG 2 ]", true),
            new("damage-name-raw-high", "[ /A\u0080\u00ff ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ /A#80#ff ]", false),
            new("damage-name-raw-high", "[ /A\u0080\u00ff ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ /A#80#ff ]", false),
            new("damage-octal-widths", "[ (\\7\\11\\112\\1123\\777\\400) ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ <07094a4a33ff00> ]", false),
            new("damage-octal-widths", "[ (\\7\\11\\112\\1123\\777\\400) ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ <07094a4a33ff00> ]", false),
            new("damage-odd-hex", "[ <A> <ABC> <0> ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ <a0> <abc0> <00> ]", false),
            new("damage-odd-hex", "[ <A> <ABC> <0> ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ <a0> <abc0> <00> ]", false),
            new("damage-root-array-close", "]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-root-array-close", "]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-root-backslash-eof", "(abc\\", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-root-backslash-eof", "(abc\\", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-root-dict-close", ">>", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-root-dict-close", ">>", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-root-dot", ".", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "(.)", true),
            new("damage-root-dot", ".", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "(.)", true),
            new("damage-root-hex-eof", "<ABC", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-root-hex-eof", "<ABC", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-root-hex-first", "<GABC>", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-root-hex-first", "<GABC>", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-root-hex-tail", "<A G 1>", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-root-hex-tail", "<A G 1>", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-root-literal-eof", "(abc", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-root-literal-eof", "(abc", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-root-minus-dot", "-.", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "(-.)", true),
            new("damage-root-minus-dot", "-.", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "(-.)", true),
            new("damage-root-minus", "-", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "(-)", true),
            new("damage-root-minus", "-", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "(-)", true),
            new("damage-root-name-hash", "/A#", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "/A#", true),
            new("damage-root-name-hash", "/A#", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "/A#", true),
            new("damage-root-name-invalid", "/A#GG", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "/A#GG", true),
            new("damage-root-name-invalid", "/A#GG", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "/A#GG", true),
            new("damage-root-name-short", "/A#2", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "/A#2", true),
            new("damage-root-name-short", "/A#2", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "/A#2", true),
            new("damage-root-paren", ")", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-root-paren", ")", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-root-plus-dot", "+.", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "(+.)", true),
            new("damage-root-plus-dot", "+.", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "(+.)", true),
            new("damage-root-plus", "+", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "(+)", true),
            new("damage-root-plus", "+", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "(+)", true),
            new("damage-signs", "[ + - . +. -. .. ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("damage-signs", "[ + - . +. -. .. ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "null", true),
            new("display-del-byte", "[ (\\177abcd) ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ (\\177abcd) ]", false),
            new("display-del-byte", "[ (\\177abcd) ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ (\\177abcd) ]", false),
            new("display-empty-hex", "[ <> ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ () ]", false),
            new("display-empty-hex", "[ <> ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ () ]", false),
            new("display-escaped-controls", "[ (\\010\\011\\012\\014\\015) ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ (\\b\\t\\n\\f\\r) ]", false),
            new("display-escaped-controls", "[ (\\010\\011\\012\\014\\015) ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ (\\b\\t\\n\\f\\r) ]", false),
            new("display-hard-control", "[ (\\007abcdefghij) ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ <076162636465666768696a> ]", false),
            new("display-hard-control", "[ (\\007abcdefghij) ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ <076162636465666768696a> ]", false),
            new("display-low-high-byte", "[ (\\030abcd) ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ (\\030abcd) ]", false),
            new("display-low-high-byte", "[ (\\030abcd) ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ (\\030abcd) ]", false),
            new("display-one-fifth", "[ (\\200abcd) ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ (\\200abcd) ]", false),
            new("display-one-fifth", "[ (\\200abcd) ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ (\\200abcd) ]", false),
            new("display-over-one-fifth", "[ (\\200abc) ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ <80616263> ]", false),
            new("display-over-one-fifth", "[ (\\200abc) ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ <80616263> ]", false),
            new("display-printable-hex", "[ <414243> ]", false, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ (ABC) ]", false),
            new("display-printable-hex", "[ <414243> ]", true, [0L, 1L, 2L, 3L, 6L, 15L],
                "[ (ABC) ]", false),
        ];
        foreach (Observation item in observations)
        {
            foreach (long budget in item.Budgets)
            {
                yield return (item.Name, item.Body, item.Sanity, budget, item.Value, item.Warns);
            }
        }
    }

    public static IEnumerable<object[]> OracleCases()
    {
        foreach ((string name, string body, bool sanity, long budget, string expected, bool warns) in Cases())
        {
            yield return [name, body, sanity, budget, expected, warns];
        }
    }

    [Theory]
    [MemberData(nameof(OracleCases))]
    public void DamagedValuesMatchExactByteOracle(string name, string body, bool sanity, long budget, string expected, bool warns)
    {
        Assert.NotEmpty(name);
        QpdfLimits limits = QpdfLimits.Default with { MaxParserErrors = budget };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        PdfParser parser = new(limits, governor, diagnostics, null) { TolerateObjectErrors = true, SanityChecks = sanity };
        using ParserMemoryScope parsedMemory0 = ParserMemoryScope.Create(governor);
        (_, _, QpdfObject value) = parser.ParseIndirectObject(new PdfTokenizer(Encoding.Latin1.GetBytes("5 0 obj\n" + body + "\nendobj\n")), 0, parsedMemory0);
        parsedMemory0.RetainGraph(); parsedMemory0.Dispose();
        DiagnosticBag displayDiagnostics = new(governor);
        CanonicalDisplay.AddDiagnostic(value, limits, governor, null, displayDiagnostics);
        Assert.Equal(expected, Assert.Single(displayDiagnostics.Entries).Message);
        Assert.Equal(warns, diagnostics.Entries.Count != 0);
    }

    public static IEnumerable<object[]> StrictDamageCases()
    {
        foreach (var item in Cases().Where(item => !item.Sanity && item.Warns).DistinctBy(item => item.Name))
        {
            yield return [item.Name, item.Body];
        }
    }

    [Theory]
    [MemberData(nameof(StrictDamageCases))]
    public void StrictParserRejectsDamagedTokensAndContainers(string name, string body)
    {
        Assert.NotEmpty(name);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfParser parser = new(QpdfLimits.Default, governor, new DiagnosticBag(governor), null);
        Assert.Throws<QpdfParseException>(() =>
        {
            using ParserMemoryScope parsedMemory1 = ParserMemoryScope.Create(governor);
            _ = parser.ParseObjectValue(new PdfTokenizer(Encoding.Latin1.GetBytes(body)), 0, parsedMemory1);
        });
    }

    internal static bool HasMalformedName(string name) =>
        name is "damage-name-incomplete" or "damage-root-name-short" or "damage-root-name-hash"
            or "damage-root-name-invalid" or "damage-dict-name-short" or "damage-name-identity"
            or "damage-name-multiple-hashes";

    [Fact]
    public void StrayHashesHaveDistinctRawIdentityAndOneWarningPerToken()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        PdfParser parser = new(QpdfLimits.Default, governor, diagnostics, null) { TolerateObjectErrors = true };
        using ParserMemoryScope parsedMemory2 = ParserMemoryScope.Create(governor);
        PdfName value = Assert.IsType<PdfName>(parser.ParseObjectValue(new PdfTokenizer("/A#Q#G"u8.ToArray()), 0, parsedMemory2));
        parsedMemory2.RetainGraph(); parsedMemory2.Dispose();
        Assert.Equal(new byte[] { 65, 0, 81, 0, 71 }, value.RawBytes);
        Assert.NotEqual(new PdfName("A#Q#G"u8.ToArray()), value);
        Assert.Single(diagnostics.Entries);
    }

    [Theory]
    [InlineData("(\\000\\001\\037\\177\\200\\377)", new byte[] { 0, 1, 31, 127, 128, 255 })]
    [InlineData("(\\7\\11\\112\\1123\\777\\400)", new byte[] { 7, 9, 74, 74, 51, 255, 0 })]
    public void StrictLiteralDecodePreservesOctalBytes(string body, byte[] expected)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfParser parser = new(QpdfLimits.Default, governor, new DiagnosticBag(governor), null);
        using ParserMemoryScope parsedMemory3 = ParserMemoryScope.Create(governor);
        PdfByteString value = Assert.IsType<PdfByteString>(parser.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(body)), 0, parsedMemory3));
        parsedMemory3.RetainGraph(); parsedMemory3.Dispose();
        Assert.False(value.Hex);
        Assert.Equal(expected, value.Value);
    }
}
