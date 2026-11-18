using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;

namespace Lokad.Qpdf.Tests;

public sealed class FixtureManifestTests
{
    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    private static JsonDocument ReadManifest()
    {
        string path = Path.Combine(FixtureDir(), "manifest.json");
        Assert.True(File.Exists(path), $"Fixture manifest not found: {path}.");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void ManifestListsExpectedFixturesWithMatchingChecksums()
    {
        using JsonDocument manifest = ReadManifest();
        JsonElement fixtures = manifest.RootElement.GetProperty("fixtures");
        Assert.NotEmpty(fixtures.EnumerateArray());
        HashSet<string> names = new(StringComparer.Ordinal);
        Span<byte> header = stackalloc byte[8];
        int independentlyQualified = 0;

        foreach (JsonElement entry in fixtures.EnumerateArray())
        {
            string file = FixtureName(entry);
            Assert.DoesNotContain('/', file);
            Assert.DoesNotContain('\\', file);
            Assert.DoesNotContain(':', file);
            Assert.EndsWith(".pdf", file, StringComparison.Ordinal);
            Assert.True(names.Add(file), $"Duplicate fixture entry: {file}.");

            bool valid = entry.GetProperty("valid").GetBoolean();
            JsonElement pages = entry.GetProperty("expectedNpages");
            if (pages.ValueKind == JsonValueKind.Null) { Assert.False(valid); }
            else { Assert.True(pages.GetInt32() >= 0, $"Negative page count for {file}."); }
            if (HasIndependentPageCount(entry))
            {
                Assert.True(valid, $"Independent page-count cases must be valid: {file}.");
                independentlyQualified++;
            }
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("description").GetString()));

            string path = Path.Combine(FixtureDir(), file);
            Assert.True(File.Exists(path), $"Fixture file not found: {path}.");
            using FileStream stream = File.OpenRead(path);
            Assert.Equal(entry.GetProperty("bytes").GetInt64(), stream.Length);
            stream.ReadExactly(header);
            Assert.Equal("%PDF-" + entry.GetProperty("headerVersion").GetString(), Encoding.ASCII.GetString(header));
            stream.Position = 0;
            string actualSha = Convert.ToHexString(SHA256.HashData(stream));
            Assert.Equal(entry.GetProperty("sha256").GetString(), actualSha, ignoreCase: true);
        }

        Assert.True(independentlyQualified > 0);
        IEnumerable<string> actualNames = Directory.EnumerateFiles(FixtureDir(), "*.pdf")
            .Select(path => Path.GetFileName(path.AsSpan()).ToString()).Order(StringComparer.Ordinal);
        Assert.Equal(names.Order(StringComparer.Ordinal), actualNames);
    }

    private static string FixtureName(JsonElement entry) => Assert.IsType<string>(entry.GetProperty("file").GetString());

    private static bool HasIndependentPageCount(JsonElement entry)
        => entry.TryGetProperty("independentPageCount", out JsonElement qualified) && qualified.GetBoolean();

    public static IEnumerable<object[]> IndependentReaderCases()
    {
        using JsonDocument manifest = ReadManifest();
        foreach (JsonElement entry in manifest.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            if (HasIndependentPageCount(entry))
            {
                yield return [FixtureName(entry), entry.GetProperty("expectedNpages").GetInt32()];
            }
        }
    }

    [Theory]
    [MemberData(nameof(IndependentReaderCases))]
    public void IndependentReaderReportsExpectedPageCounts(string file, int expectedNpages)
    {
        string path = Path.Combine(FixtureDir(), file);
        using PdfDocument document = PdfDocument.Open(path);
        Assert.Equal(expectedNpages, document.NumberOfPages);
    }

    [Fact]
    public void TruncatedFixtureFailsIndependentReading()
    {
        string path = Path.Combine(FixtureDir(), "truncated-1page.pdf");
        Assert.True(File.Exists(path), $"Negative fixture not found: {path}.");
        Assert.ThrowsAny<Exception>(() =>
        {
            using PdfDocument document = PdfDocument.Open(path);
            _ = document.NumberOfPages;
        });
    }
}
