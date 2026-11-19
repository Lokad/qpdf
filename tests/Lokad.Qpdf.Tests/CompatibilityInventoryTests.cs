using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Jobs;
using Lokad.Qpdf.Runtime;

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
                new[] { "required", "deferred", "excluded", "documented-host/build-difference" });
            Assert.Contains(entry.GetProperty("status").GetString(),
                new[] { "not-started", "partial", "complete", "excluded" });
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
    public void RequiredSecurityOperationsAreNotCountedAsImplementedRejections(string flag)
    {
        using var inventory = ReadInventory();
        var entries = inventory.RootElement.GetProperty("options").EnumerateArray()
            .Where(entry => entry.GetProperty("flag").GetString() == flag).ToArray();
        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            Assert.Equal("required", entry.GetProperty("scope").GetString());
            if (entry.GetProperty("status").GetString() == "complete")
            {
                Assert.NotEmpty(entry.GetProperty("evidence").GetProperty("positive").EnumerateArray());
                Assert.NotEmpty(entry.GetProperty("evidence").GetProperty("oracle").EnumerateArray());
            }
            else { Assert.NotEmpty(entry.GetProperty("remainingGaps").EnumerateArray()); }
        });
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
    public void EncryptionJsonKeyRequiresAuthenticatedBehaviorAndNativeEvidence()
    {
        using var inventory = ReadInventory();
        var entry = Assert.Single(inventory.RootElement.GetProperty("options").EnumerateArray(),
            option => option.GetProperty("flag").GetString() == "--json-key");
        Assert.Equal("required", entry.GetProperty("scope").GetString());
        Assert.Equal("required", entry.GetProperty("choiceScopes").GetProperty("encrypt").GetString());
        Assert.Equal("complete", entry.GetProperty("status").GetString());
        Assert.Contains(entry.GetProperty("evidence").GetProperty("positive").EnumerateArray(),
            evidence => evidence.GetString() == "EncryptedJsonTests.TypedArgvAndJobExportAuthenticatedFacts");
        Assert.Contains(entry.GetProperty("evidence").GetProperty("oracle").EnumerateArray(),
            evidence => evidence.GetString() == "diff:security-json");
    }

    [Fact]
    public void EvidenceIdentifiersAreUniqueWithinEachSlot()
    {
        using var inventory = ReadInventory();
        List<string> duplicate = [];
        foreach (JsonElement entry in inventory.RootElement.GetProperty("options").EnumerateArray())
        {
            string entryName = $"{entry.GetProperty("context").GetString()}/{entry.GetProperty("flag").GetString()}";
            foreach (JsonProperty slot in entry.GetProperty("evidence").EnumerateObject())
            {
                HashSet<string?> identifiers = new(StringComparer.Ordinal);
                foreach (JsonElement item in slot.Value.EnumerateArray())
                {
                    string? id = item.GetString();
                    if (!identifiers.Add(id)) { duplicate.Add($"{entryName} repeats {id} in {slot.Name}."); }
                }
            }
        }
        Assert.Empty(duplicate);
    }

    [Fact]
    public void OracleEvidenceNamesResolveToRegisteredOutcomes()
    {
        using JsonDocument registry = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Compatibility", "oracle-evidence.json")));
        HashSet<string> registered = new(StringComparer.Ordinal);
        foreach (JsonProperty stage in registry.RootElement.EnumerateObject())
        {
            Assert.Contains(stage.Name, new[] { "differential", "parser" });
            foreach (JsonProperty entry in stage.Value.EnumerateObject())
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.Value.GetString()), entry.Name);
                Assert.True(registered.Add(entry.Name), entry.Name);
            }
        }
        using JsonDocument inventory = ReadInventory();
        HashSet<string> referenced = new(StringComparer.Ordinal);
        foreach (JsonElement option in inventory.RootElement.GetProperty("options").EnumerateArray())
        {
            foreach (JsonElement entry in option.GetProperty("evidence").GetProperty("oracle").EnumerateArray())
            {
                string id = Assert.IsType<string>(entry.GetString());
                Assert.Contains(id, registered);
                referenced.Add(id);
            }
        }
        Assert.Empty(registered.Except(referenced));
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
    public void IncompleteEntriesKeepVisibleGapsAndExclusionsHaveReasons()
    {
        using var inventory = ReadInventory();
        foreach (var entry in inventory.RootElement.GetProperty("options").EnumerateArray())
        {
            string? status = entry.GetProperty("status").GetString();
            if (status is "partial" or "not-started")
            { Assert.NotEmpty(entry.GetProperty("remainingGaps").EnumerateArray()); }
            if (entry.GetProperty("scope").GetString() == "excluded")
            {
                Assert.Equal("excluded", status);
                Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("exclusionReason").GetString()));
                Assert.NotEmpty(entry.GetProperty("evidence").GetProperty("negative").EnumerateArray());
            }
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
    public async Task JobJsonPathsMatchParserKeys()
    {
        using var inventory = ReadInventory();
        HashSet<string> checkedPaths = new(StringComparer.Ordinal);
        HashSet<string> legacyBlocks = new(StringComparer.Ordinal);
        foreach (var entry in inventory.RootElement.GetProperty("options").EnumerateArray())
        {
            foreach (var item in entry.GetProperty("jobJsonPaths").EnumerateArray())
            {
                string path = Assert.IsType<string>(item.GetString());
                string[] segments = path.Split('.');
                if (segments is ["encrypt", "40bit" or "128bit", _])
                {
                    // Legacy creation rejects the entire enclosing block before reading any fields.
                    Assert.Equal("deferred", entry.GetProperty("scope").GetString());
                    Assert.Equal(segments[1][..^3] + "-bit-encryption", entry.GetProperty("context").GetString());
                    if (legacyBlocks.Add(segments[1]))
                    {
                        QpdfCryptoException rejected = Assert.IsType<QpdfCryptoException>(await ProbeJobPathAsync(segments[..2]));
                        Assert.Equal("encrypt", rejected.Option);
                    }
                    continue;
                }

                if (checkedPaths.Add(path))
                {
                    QpdfException? error = await ProbeJobPathAsync(segments);
                    Assert.True(RecognizesJobPath(segments, error), $"Job path {path} did not reach its setting: {error?.Message}");
                }
            }
        }
    }

    [Theory]
    [InlineData("wrongContainer.parserMaxNesting")]
    [InlineData("global.password")]
    [InlineData("pages[].unknownNestedKey")]
    [InlineData("pages.file")]
    [InlineData("pages[].range.extra")]
    [InlineData("overlay[].mimetype")]
    [InlineData("addAttachment[].repeat")]
    [InlineData("copyAttachmentsFrom[].filename")]
    [InlineData("encrypt.accessibility")]
    [InlineData("encrypt.256bit.parserMaxNesting")]
    public async Task JobPathProbeRejectsKeysOutsideTheirParserContext(string path)
    {
        string[] segments = path.Split('.');
        QpdfException? error = await ProbeJobPathAsync(segments);
        Assert.IsType<QpdfParseException>(error);
        Assert.False(RecognizesJobPath(segments, error), path);
    }

    private static bool RecognizesJobPath(string[] segments, QpdfException? error)
    {
        string path = string.Join('.', segments);
        if (error is null) { return segments.Length == 1 && path is "encrypt" or "global" or "overlay" or "underlay"; }
        if (error is QpdfCryptoException crypto) { return crypto.Option == path; }
        if (error is not QpdfParseException) { return false; }

        string leaf = segments[^1];
        // Diagnostic names may include the enclosing setting or use the argv spelling.
        string[] names = [leaf, path, path.Replace("[].", ".", StringComparison.Ordinal), path.Replace("[].", "-", StringComparison.Ordinal)];
        return names.Any(name => error.Message.StartsWith($"Job {name} must ", StringComparison.Ordinal)
            || error.Message.StartsWith($"Job \"{name}\" must ", StringComparison.Ordinal));
    }

    private static async Task<QpdfException?> ProbeJobPathAsync(string[] segments)
    {
        static JsonObject Encryption() => new() { ["256bit"] = new JsonObject(), ["ownerPassword"] = "inventory" };
        static JsonObject Placement() => new() { ["file"] = "overlay.pdf" };
        JsonObject job = new() { ["inputFile"] = "input.pdf", ["outputFile"] = "output.pdf" };
        JsonObject target = job;
        foreach (string container in segments.AsSpan(0, segments.Length - 1))
        {
            JsonObject child = container == "encrypt" ? Encryption() : new JsonObject();
            if (container.EndsWith("[]", StringComparison.Ordinal))
            {
                child["file"] = container == "pages[]" ? "." : "auxiliary.pdf";
                target[container[..^2]] = new JsonArray(child);
            }
            else { target[container] = child; }
            target = child;
        }
        string leaf = segments[^1];
        // An object in a scalar or array setting triggers validation at that key.
        target[leaf] = segments switch
        {
            ["encrypt"] => Encryption(),
            ["global"] => new JsonObject(),
            ["overlay" or "underlay"] => new JsonArray(Placement()),
            _ => new JsonObject { ["grammarProbe"] = true },
        };

        using InMemoryQpdfHost host = new();
        QpdfLimits limits = QpdfLimits.Default;
        ResourceGovernor governor = new(limits, CancellationToken.None, host);
        try
        {
            using JobJsonLoader.LoadedJob loaded = await JobJsonLoader.ParseAsync(host, job.ToJsonString(), limits,
                governor, CancellationToken.None);
            return null;
        }
        catch (QpdfException ex) { return ex; }
        finally { governor.CompleteStorage(); }
    }

    [Fact]
    public void CryptoSyntaxMatchesParserClassification()
    {
        using var inventory = ReadInventory();
        foreach (var entry in inventory.RootElement.GetProperty("options").EnumerateArray())
        {
            string? flag = entry.GetProperty("flag").GetString();
            if (!string.IsNullOrEmpty(flag) && flag.StartsWith("--", StringComparison.Ordinal))
            {
                bool cryptoSyntax = entry.TryGetProperty("cryptoSyntax", out JsonElement classification) && classification.GetBoolean();
                Assert.Equal(cryptoSyntax, ArgvParser.IsCryptoOption(flag.AsSpan(2)));
            }
        }
        Assert.False(ArgvParser.IsCryptoOption("passwordx"));
        Assert.False(ArgvParser.IsCryptoOption("Password"));
        Assert.False(ArgvParser.IsCryptoOption(string.Empty));
    }

    private static JsonDocument ReadInventory() => JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Compatibility", "options.json")));
}
