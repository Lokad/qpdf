using System.Text.Json;
using Lokad.Qpdf.Commands;

namespace Lokad.Qpdf.Jobs;

/// <summary>
/// Narrow job-JSON parser for the initial single-page profile. Accepts exactly
/// `{inputFile, outputFile, pages:[{file, range}]}` with upstream field names;
/// `encrypt` and entry `password` keys reject as crypto, everything else
/// outside the subset fails explicitly. Full job semantics arrive in M4.
/// </summary>
internal static class JobJsonParser
{
    private const int MaxDepth = 32;

    internal static SelectionPlan Parse(string jobJson, QpdfLimits limits)
    {
        if (jobJson is null)
        {
            throw new ArgumentNullException(nameof(jobJson));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        long byteCount = System.Text.Encoding.UTF8.GetByteCount(jobJson);
        if (byteCount > limits.MaxInputBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), byteCount, limits.MaxInputBytes);
        }

        JsonDocumentOptions options = new()
        {
            MaxDepth = MaxDepth,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        };
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(jobJson, options);
        }
        catch (JsonException ex)
        {
            throw new QpdfParseException($"Invalid job JSON: {ex.Message}.", ex.BytePositionInLine, null, ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new QpdfParseException("Job JSON must be an object.", 0L, null);
            }

            string? input = null;
            bool seenInput = false;
            bool seenOutput = false;
            bool seenPages = false;
            string? output = null;
            JsonElement pages = default;
            bool hasPages = false;
            List<OutputRotation> rotations = new();
            bool seenRotate = false;
            List<PageLabelSpec> labels = new();
            bool seenLabels = false;
            bool seenRemoveLabels = false;
            bool removeLabels = false;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "inputFile":
                        if (seenInput)
                        {
                            throw new QpdfParseException("Duplicate job key 'inputFile'.", 0L, null);
                        }

                        seenInput = true;
                        input = RequireString(property.Value, "inputFile");
                        break;
                    case "outputFile":
                        if (seenOutput)
                        {
                            throw new QpdfParseException("Duplicate job key 'outputFile'.", 0L, null);
                        }

                        seenOutput = true;
                        output = RequireString(property.Value, "outputFile");
                        break;
                    case "pages":
                        if (seenPages)
                        {
                            throw new QpdfParseException("Duplicate job key 'pages'.", 0L, null);
                        }

                        seenPages = true;
                        pages = property.Value;
                        hasPages = true;
                        break;
                    case "rotate":
                        if (seenRotate)
                        {
                            throw new QpdfParseException("Duplicate job key 'rotate'.", 0L, null);
                        }

                        seenRotate = true;
                        rotations.AddRange(ParseRotateArray(property.Value));
                        break;
                    case "setPageLabels":
                        if (seenLabels)
                        {
                            throw new QpdfParseException("Duplicate job key 'setPageLabels'.", 0L, null);
                        }

                        seenLabels = true;
                        labels.AddRange(ParseLabelArray(property.Value));
                        break;
                    case "removePageLabels":
                        if (seenRemoveLabels)
                        {
                            throw new QpdfParseException("Duplicate job key 'removePageLabels'.", 0L, null);
                        }

                        seenRemoveLabels = true;
                        if (property.Value.ValueKind != JsonValueKind.True && property.Value.ValueKind != JsonValueKind.False)
                        {
                            throw new QpdfParseException("Job \"removePageLabels\" must be a boolean.", 0L, null);
                        }

                        removeLabels = property.Value.ValueKind == JsonValueKind.True;
                        break;
                    case "encrypt":
                    case "decrypt":
                        throw new QpdfCryptoException(property.Name);
                    default:
                        throw new QpdfParseException($"Unsupported job key '{property.Name}' in the narrow selection profile.", 0L, null);
                }
            }

            if (input == "-")
            {
                throw new QpdfParseException("Standard input is not a seekable PDF input.", 0L, null);
            }

            if (output == "-")
            {
                throw new QpdfParseException("Stdout output requires an explicit streaming contract.", 0L, null);
            }

            if (input is null)
            {
                throw new QpdfParseException("Job JSON requires \"inputFile\".", 0L, null);
            }

            if (output is null)
            {
                throw new QpdfParseException("Job JSON requires \"outputFile\".", 0L, null);
            }

            if (!hasPages)
            {
                throw new QpdfParseException("Job JSON requires \"pages\" with one entry.", 0L, null);
            }

            if (removeLabels && labels.Count > 0)
            {
                throw new QpdfParseException("Job \"setPageLabels\" cannot be combined with \"removePageLabels\".", 0L, null);
            }

            (string file, string range) = ParsePagesEntry(pages);
            string resolved = ResolveFile(file, input);
            int page = SelectionPlan.ParsePageNumber(range);
            return new SelectionPlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(output), page, QpdfRecoveryMode.BoundedRepair) with
            {
                Rotations = rotations,
                Labels = labels,
            };
        }
    }

    private static List<OutputRotation> ParseRotateArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job \"rotate\" must be an array of rotation specifications.", 0L, null);
        }

        List<OutputRotation> rotations = new();
        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                throw new QpdfParseException("Job \"rotate\" entries must be strings.", 0L, null);
            }

            string? specification = entry.GetString();
            if (specification is null)
            {
                throw new QpdfParseException("Job \"rotate\" entries must be strings.", 0L, null);
            }

            rotations.Add(OutputRotation.Parse(specification, 0L));
        }

        return rotations;
    }

    private static List<PageLabelSpec> ParseLabelArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job \"setPageLabels\" must be an array of label specifications.", 0L, null);
        }

        List<PageLabelSpec> labels = new();
        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                throw new QpdfParseException("Job \"setPageLabels\" entries must be strings.", 0L, null);
            }

            string? specification = entry.GetString();
            if (specification is null)
            {
                throw new QpdfParseException("Job \"setPageLabels\" entries must be strings.", 0L, null);
            }

            labels.Add(PageLabelSpec.Parse(specification, 0L));
        }

        return labels;
    }

    private static (string File, string Range) ParsePagesEntry(JsonElement pages)
    {
        if (pages.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job \"pages\" must be an array with one entry.", 0L, null);
        }

        int count = pages.GetArrayLength();
        if (count != 1)
        {
            throw new QpdfParseException($"Job \"pages\" must hold exactly one entry in this profile, not {count}.", 0L, null);
        }

        JsonElement entry = pages[0];
        if (entry.ValueKind != JsonValueKind.Object)
        {
            throw new QpdfParseException("Job \"pages\" entries must be objects.", 0L, null);
        }

        string? file = null;
        string? range = null;
        foreach (JsonProperty property in entry.EnumerateObject())
        {
            switch (property.Name)
            {
                case "file":
                    if (file is not null)
                    {
                        throw new QpdfParseException("Duplicate pages key 'file'.", 0L, null);
                    }
                    file = RequireString(property.Value, "pages[].file");
                    break;
                case "range":
                    if (range is not null)
                    {
                        throw new QpdfParseException("Duplicate pages key 'range'.", 0L, null);
                    }
                    range = RequireString(property.Value, "pages[].range");
                    break;
                case "password":
                    throw new QpdfCryptoException("password");
                default:
                    throw new QpdfParseException($"Unsupported pages key '{property.Name}' in the narrow selection profile.", 0L, null);
            }
        }

        if (file is null)
        {
            throw new QpdfParseException("Job pages entries require \"file\".", 0L, null);
        }

        if (range is null)
        {
            throw new QpdfParseException("Job pages entries require \"range\" in this profile.", 0L, null);
        }

        return (file, range);
    }

    private static string RequireString(JsonElement element, string role)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new QpdfParseException($"Job \"{role}\" must be a string.", 0L, null);
        }

        string? value = element.GetString();
        if (value is null)
        {
            throw new QpdfParseException($"Job \"{role}\" must be a string.", 0L, null);
        }

        return value;
    }

    private static string ResolveFile(string file, string input)
    {
        if (file == ".")
        {
            return input;
        }

        if (!string.Equals(file, input, StringComparison.Ordinal))
        {
            throw new QpdfParseException("Multiple page sources are outside the narrow selection profile.", 0L, null);
        }

        return file;
    }
}
