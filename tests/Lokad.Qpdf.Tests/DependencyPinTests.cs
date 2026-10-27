using System.Text.Json;
using System.Xml.Linq;

namespace Lokad.Qpdf.Tests;

public sealed class DependencyPinTests
{
    private static string RepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && directory is not null; i++)
        {
            if (File.Exists(Path.Combine(directory, "Directory.Packages.props")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("Repository root with Directory.Packages.props was not found.");
    }

    [Fact]
    public void CentralVersionsPinTheApprovedCodec()
    {
        XDocument versions = XDocument.Load(Path.Combine(RepoRoot(), "Directory.Packages.props"));
        string? codec = versions.Descendants("PackageVersion")
            .Where(element => string.Equals(element.Attribute("Include")?.Value, "Lokad.Image", StringComparison.Ordinal))
            .Select(element => element.Attribute("Version")?.Value)
            .SingleOrDefault();
        Assert.Equal("[0.2.0]", codec);
        Assert.DoesNotContain(versions.Descendants("PackageVersion"), element =>
            string.Equals(element.Attribute("Include")?.Value, "SixLabors.ImageSharp", StringComparison.Ordinal));
        string? reader = versions.Descendants("PackageVersion")
            .Where(element => string.Equals(element.Attribute("Include")?.Value, "PdfPig", StringComparison.Ordinal))
            .Select(element => element.Attribute("Version")?.Value)
            .SingleOrDefault();
        Assert.Equal("[0.1.16]", reader);
    }

    [Fact]
    public void LibraryProjectHasNoRuntimePackageDependencies()
    {
        XDocument project = XDocument.Load(Path.Combine(RepoRoot(), "src", "Lokad.Qpdf", "Lokad.Qpdf.csproj"));
        List<string> runtime = project.Descendants("PackageReference")
            .Where(element => element.Attribute("PrivateAssets") is null)
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToList();
        Assert.Empty(runtime);
        Assert.DoesNotContain(project.Descendants("PackageReference"), element =>
            (element.Attribute("Include")?.Value ?? string.Empty).Contains("Drawing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CentralVersionsPinBenchmarkHarness()
    {
        XDocument versions = XDocument.Load(Path.Combine(RepoRoot(), "Directory.Packages.props"));
        string? harness = versions.Descendants("PackageVersion")
            .Where(element => string.Equals(element.Attribute("Include")?.Value, "BenchmarkDotNet", StringComparison.Ordinal))
            .Select(element => element.Attribute("Version")?.Value)
            .SingleOrDefault();
        Assert.Equal("[0.15.8]", harness);
    }

    [Fact]
    public void LibraryLockContainsOnlyPrivateBuildTooling()
    {
        using JsonDocument inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RepoRoot(), "src", "Lokad.Qpdf", "packages.lock.json")));
        string[] packages = inventory.RootElement.GetProperty("dependencies").GetProperty("net10.0").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "Microsoft.Build.Tasks.Git", "Microsoft.SourceLink.Common", "Microsoft.SourceLink.GitHub" }, packages.OrderBy(name => name, StringComparer.Ordinal));
    }
}
