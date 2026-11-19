using System.Text.RegularExpressions;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class SelectedCatalogInspectionTests
{
    [Fact]
    public async Task CheckedForeignPageAndCatalogShareTheRegisteredWidget()
    {
        using InMemoryQpdfHost host = Host("minimal-1page.pdf", "forms-1page.pdf");
        QpdfExecutionResult original = await Qpdf.ExecuteAsync(host,
            new(["base", "--show-xref"]), CancellationToken.None);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """
            {"inputFile":"base","pages":[{"file":"foreign","range":"1"}],
             "check":"","showObject":"1","showPages":"","showXref":""}
            """, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Null(result.OutputBytes);
        Assert.Equal(0L, result.Counters.TotalOutputBytes);
        Assert.Equal(original.Diagnostics.Where(d => d.Code == QpdfDiagnosticCode.XrefListing),
            result.Diagnostics.Where(d => d.Code == QpdfDiagnosticCode.XrefListing));

        string form = await Show(host, Selector(ObjectListing(result), @"/AcroForm (\d+) (\d+) R"));
        string field = Selector(form, @"/Fields \[ (\d+) (\d+) R \]");
        string pageLine = Assert.Single(result.Diagnostics,
            d => d.Code == QpdfDiagnosticCode.PageListing && d.Message.StartsWith("page 1:", StringComparison.Ordinal)).Message;
        string page = await Show(host, Selector(pageLine, @"^page 1: (\d+) (\d+) R$"));
        Assert.Equal(field, Selector(page, @"/Annots \[ (\d+) (\d+) R \]"));

        string widget = await Show(host, field);
        Assert.Contains("/Subtype /Widget", widget, StringComparison.Ordinal);
        Assert.Contains("/T (Name)", widget, StringComparison.Ordinal);
        Assert.Contains("/V (Hello)", widget, StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SelectedLabelsReplaceTheActiveTreeAndLeaveTheAuditTreeReadable()
    {
        using InMemoryQpdfHost host = Host("json-assembly-a.pdf", "json-assembly-b.pdf");
        string catalog = await Show(host, "1");
        string active = Selector(catalog, @"/PageLabels (\d+) (\d+) R");
        string audit = Selector(catalog, @"/Audit (\d+) (\d+) R");
        Assert.NotEqual(active, audit);
        Assert.Contains("/Nums [ 0 << /P (B1)", await Show(host, active), StringComparison.Ordinal);
        Assert.Contains("/Nums [ 0 << /P (A1) >> 1 << /P (A2) >> ]", await Show(host, audit), StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static InMemoryQpdfHost Host(string primary, string foreign)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("base"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", primary)),
            QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("foreign"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", foreign)),
            QpdfInputOwnership.Transfer);
        return host;
    }

    private static async Task<string> Show(InMemoryQpdfHost host, string selector)
    {
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["base", "--pages", "foreign", "1", "--", "--show-object=" + selector]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        return ObjectListing(result);
    }

    private static string ObjectListing(QpdfExecutionResult result)
        => Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message;

    private static string Selector(string listing, string pattern)
    {
        Match match = Regex.Match(listing, pattern);
        Assert.True(match.Success, listing);
        return match.Groups[1].Value + "," + match.Groups[2].Value;
    }
}
