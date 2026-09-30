using System.Text.Json;

namespace Lokad.Qpdf.Tests;

public sealed class CompatibilityInventoryTests
{
    [Fact]
    public void InventoryCoversThePinnedSurfaceWithoutDuplicateContexts()
    {
        using var inventory = ReadInventory();
        var root = inventory.RootElement;
        Assert.Equal("12.4.2", root.GetProperty("reference").GetProperty("version").GetString());
        Assert.Equal("4eba95899886e851cc41d76886483b347612f2a8",
            root.GetProperty("reference").GetProperty("commit").GetString());

        var entries = root.GetProperty("options").EnumerateArray().ToArray();
        Assert.Equal(140, entries.Select(entry => entry.GetProperty("flag").GetString()).Distinct().Count());
        var identities = entries.Select(entry => (
            entry.GetProperty("context").GetString(), entry.GetProperty("flag").GetString()));
        Assert.Equal(entries.Length, identities.Distinct().Count());
        Assert.All(entries, entry =>
        {
            Assert.Contains(entry.GetProperty("scope").GetString(),
                new[] { "required", "excluded-crypto", "documented-host/build-difference" });
            Assert.Contains(entry.GetProperty("status").GetString(),
                new[] { "not-started", "partial", "complete" });
            Assert.NotEmpty(entry.GetProperty("manualForms").EnumerateArray());
        });
    }

    [Theory]
    [InlineData("--password")]
    [InlineData("--password-file")]
    [InlineData("--encrypt")]
    [InlineData("--decrypt")]
    [InlineData("--copy-encryption")]
    [InlineData("--show-crypto")]
    [InlineData("--show-encryption")]
    [InlineData("--is-encrypted")]
    [InlineData("--requires-password")]
    [InlineData("--remove-restrictions")]
    public void CryptoOperationsAreExcludedInEveryContext(string flag)
    {
        using var inventory = ReadInventory();
        var entries = inventory.RootElement.GetProperty("options").EnumerateArray()
            .Where(entry => entry.GetProperty("flag").GetString() == flag).ToArray();
        Assert.NotEmpty(entries);
        Assert.All(entries, entry => Assert.Equal("excluded-crypto", entry.GetProperty("scope").GetString()));
    }

    [Fact]
    public void CompletedEntriesNeedEvidenceAndNoUnresolvedDependencies()
    {
        using var inventory = ReadInventory();
        foreach (var entry in inventory.RootElement.GetProperty("options").EnumerateArray())
        {
            if (entry.GetProperty("status").GetString() != "complete")
                continue;

            Assert.Empty(entry.GetProperty("remainingGaps").EnumerateArray());
            var evidence = entry.GetProperty("evidence");
            Assert.NotEmpty(evidence.GetProperty("negative").EnumerateArray());
            if (entry.GetProperty("scope").GetString() == "required")
            {
                Assert.NotEmpty(evidence.GetProperty("positive").EnumerateArray());
                Assert.NotEmpty(evidence.GetProperty("resource").EnumerateArray());
                Assert.NotEmpty(evidence.GetProperty("oracle").EnumerateArray());
            }
        }
    }

    [Fact]
    public void EncryptionJsonKeyIsExcludedWithoutExcludingOtherJsonKeys()
    {
        using var inventory = ReadInventory();
        var entry = Assert.Single(inventory.RootElement.GetProperty("options").EnumerateArray(),
            option => option.GetProperty("flag").GetString() == "--json-key");
        Assert.Equal("required", entry.GetProperty("scope").GetString());
        Assert.Equal("excluded-crypto", entry.GetProperty("choiceScopes").GetProperty("encrypt").GetString());
    }

    private static JsonDocument ReadInventory() => JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Compatibility", "options.json")));
}
