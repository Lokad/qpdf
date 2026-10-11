using System.Text.Json;
using Lokad.Qpdf.Jobs;

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

    [Fact]
    public void EvidenceNamesResolveToRealTests()
    {
        var discovered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in typeof(CompatibilityInventoryTests).Assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
            {
                bool isTest = false;
                foreach (var attribute in method.GetCustomAttributes(false))
                {
                    string? fullName = attribute.GetType().FullName;
                    if (string.Equals(fullName, "Xunit.FactAttribute", StringComparison.Ordinal) ||
                        string.Equals(fullName, "Xunit.TheoryAttribute", StringComparison.Ordinal))
                    {
                        isTest = true;
                        break;
                    }
                }

                if (isTest && method.DeclaringType is not null)
                {
                    discovered.Add(method.DeclaringType.Name + "." + method.Name);
                }
            }
        }

        var missing = new List<string>();
        using var inventory = ReadInventory();
        foreach (var entry in inventory.RootElement.GetProperty("options").EnumerateArray())
        {
            var evidence = entry.GetProperty("evidence");
            foreach (var slot in new[] { "positive", "negative", "interaction", "resource" })
            {
                foreach (var name in evidence.GetProperty(slot).EnumerateArray())
                {
                    string? test = name.GetString();
                    if (!string.IsNullOrEmpty(test) && !discovered.Contains(test))
                    {
                        missing.Add(entry.GetProperty("context").GetString() + "/" +
                            entry.GetProperty("flag").GetString() + " " + slot + ": " + test);
                    }
                }
            }
        }

        Assert.Empty(missing);
    }
    [Fact]
    public void PartialEntriesKeepVisibleGaps()
    {
        using var inventory = ReadInventory();
        foreach (var entry in inventory.RootElement.GetProperty("options").EnumerateArray())
        {
            if (entry.GetProperty("status").GetString() != "partial")
                continue;

            Assert.NotEmpty(entry.GetProperty("remainingGaps").EnumerateArray());
        }
    }

    [Fact]
    public void JobJsonPathsUseCamelCaseKeys()
    {
        using var inventory = ReadInventory();
        var bad = new List<string>();
        foreach (var entry in inventory.RootElement.GetProperty("options").EnumerateArray())
        {
            foreach (var path in entry.GetProperty("jobJsonPaths").EnumerateArray())
            {
                string? value = path.GetString();
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                foreach (string segment in value.Split('.'))
                {
                    if (segment.Contains('-', StringComparison.Ordinal))
                    {
                        bad.Add(entry.GetProperty("context").GetString() + "/" + entry.GetProperty("flag").GetString() + ": " + value);
                    }
                }
            }
        }

        Assert.True(bad.Count == 0, "Non-camelCase job keys: " + string.Join("; ", bad));
    }

    [Fact]
    public void JobJsonPathsMatchParserKeys()
    {
        string? directory = AppContext.BaseDirectory;
        string parser = string.Empty;
        for (int i = 0; i < 8 && directory is not null; i++)
        {
            string candidate = Path.Combine(directory, "src", "Lokad.Qpdf", "Jobs", "JobJsonParser.cs");
            if (File.Exists(candidate))
            {
                parser = File.ReadAllText(candidate);
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotEqual(string.Empty, parser);
        using var inventory = ReadInventory();
        var bad = new List<string>();
        foreach (var entry in inventory.RootElement.GetProperty("options").EnumerateArray())
        {
            foreach (var path in entry.GetProperty("jobJsonPaths").EnumerateArray())
            {
                string? value = path.GetString();
                if (string.IsNullOrEmpty(value) || value.StartsWith("encrypt.", StringComparison.Ordinal) || value.Contains("[]", StringComparison.Ordinal))
                {
                    continue;
                }

                string leaf = value.Contains('.', StringComparison.Ordinal) ? value.Substring(value.LastIndexOf('.') + 1) : value;
                if (!parser.Contains("case \"" + leaf + "\":", StringComparison.Ordinal)
                    && !JobJsonParser.IsCryptoKey(leaf) && leaf != JobJsonLoader.FileKey)
                {
                    bad.Add(entry.GetProperty("context").GetString() + "/" + entry.GetProperty("flag").GetString() + ": " + value);
                }
            }
        }

        Assert.True(bad.Count == 0, "Job paths without a parser key: " + string.Join("; ", bad));
    }

    [Fact]
    public void CryptoOptionsMatchInventory()
    {
        string? directory = AppContext.BaseDirectory;
        string parser = string.Empty;
        for (int i = 0; i < 8 && directory is not null; i++)
        {
            string candidate = Path.Combine(directory, "src", "Lokad.Qpdf", "Commands", "ArgvParser.cs");
            if (File.Exists(candidate))
            {
                parser = File.ReadAllText(candidate);
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotEqual(string.Empty, parser);
        var names = new List<string>();
        var set = System.Text.RegularExpressions.Regex.Match(parser, "CryptoOptions = new\\(StringComparer.Ordinal\\)\\s*\\{([^}]*)\\}", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(set.Success, "CryptoOptions set not found in ArgvParser.cs.");
        foreach (System.Text.RegularExpressions.Match name in System.Text.RegularExpressions.Regex.Matches(set.Groups[1].Value, "\"([^\"]+)\""))
        {
            names.Add(name.Groups[1].Value);
        }

        using var inventory = ReadInventory();
        var flagged = new List<string>();
        foreach (var entry in inventory.RootElement.GetProperty("options").EnumerateArray())
        {
            if (entry.GetProperty("scope").GetString() != "excluded-crypto")
            {
                continue;
            }

            string? flag = entry.GetProperty("flag").GetString();
            if (!string.IsNullOrEmpty(flag) && flag.StartsWith("--", StringComparison.Ordinal))
            {
                flagged.Add(flag.Substring(2));
            }
        }

        Assert.Equal(names.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal), flagged.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal));
    }

    private static JsonDocument ReadInventory() => JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Compatibility", "options.json")));
}
