using System.Security.Cryptography;
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
        Assert.Equal(37, fixtures.GetArrayLength());

        foreach (JsonElement entry in fixtures.EnumerateArray())
        {
            string? fileOrNull = entry.GetProperty("file").GetString();
            string? shaOrNull = entry.GetProperty("sha256").GetString();
            if (fileOrNull is null || shaOrNull is null)
            {
                throw new Xunit.Sdk.XunitException("Fixture manifest entry is missing file or sha256.");
            }

            string file = fileOrNull;
            string expectedSha = shaOrNull;
            long expectedBytes = entry.GetProperty("bytes").GetInt64();
            string path = Path.Combine(FixtureDir(), file);
            Assert.True(File.Exists(path), $"Fixture file not found: {path}.");

            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(expectedBytes, bytes.Length);
            string actualSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.Equal(expectedSha.ToLowerInvariant(), actualSha);
        }
    }

    [Theory]
    [InlineData("minimal-1page.pdf", 1)]
    [InlineData("minimal-2page.pdf", 2)]
    [InlineData("rotated-1page.pdf", 1)]
    [InlineData("unref-1page.pdf", 1)]
    [InlineData("meta-1page.pdf", 1)]
    [InlineData("struct-1page.pdf", 1)]
    [InlineData("struct-preserve-1page.pdf", 1)]
    [InlineData("lin-objstm-1page.pdf", 1)]
    [InlineData("lin-objstm-2page.pdf", 2)]
    [InlineData("forms-1page.pdf", 1)]
    [InlineData("forms-2page.pdf", 2)]
    [InlineData("inline-1page.pdf", 1)]
    [InlineData("rotated-ap-1page.pdf", 1)]
    [InlineData("orphans-1page.pdf", 1)]
    [InlineData("photo-1page.pdf", 1)]
    [InlineData("images-smask-1page.pdf", 1)]
    [InlineData("flatten-dr-1page.pdf", 1)]
    [InlineData("overlay-annot-1page.pdf", 1)]
    [InlineData("labels-2page.pdf", 2)]
    [InlineData("catalog-extras-1page.pdf", 1)]
    [InlineData("dr-encoding-1page.pdf", 1)]
    [InlineData("dr-merge-1page.pdf", 1)]
    [InlineData("parent-held-1page.pdf", 1)]
    [InlineData("dc-a-1page.pdf", 1)]
    [InlineData("dc-b-1page.pdf", 1)]
    [InlineData("outlined-2page.pdf", 2)]
    [InlineData("outlined-use-2page.pdf", 2)]
    [InlineData("thumb-1page.pdf", 1)]
    [InlineData("linearized-1page.pdf", 1)]
    [InlineData("linearized-2page.pdf", 2)]
    [InlineData("images-1page.pdf", 1)]
    [InlineData("images-nodim-1page.pdf", 1)]
    [InlineData("images-filtered-1page.pdf", 1)]
    [InlineData("overlay-widget-1page.pdf", 1)]
    [InlineData("overlay-widget-dest-1page.pdf", 1)]
    [InlineData("overlay-widget-dr-1page.pdf", 1)]
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

    [Fact]
    public void ValidFixturesStartWithPdfHeader()
    {
        foreach (string file in new[] { "minimal-1page.pdf", "minimal-2page.pdf", "forms-1page.pdf" })
        {
            string path = Path.Combine(FixtureDir(), file);
            byte[] prefix = new byte[8];
            using (FileStream stream = File.OpenRead(path))
            {
                Assert.Equal(8, stream.Read(prefix, 0, prefix.Length));
            }

            Assert.Equal("%PDF-1.4", System.Text.Encoding.ASCII.GetString(prefix));
        }
    }
}

