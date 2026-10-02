using System.Reflection;

namespace Lokad.Qpdf.Commands;

/// <summary>Help and version text for the narrow supported subset.</summary>
internal static class HelpText
{
    internal const string Help = """
        Lokad.qpdf: managed non-cryptographic PDF structural processor.
        Usage:
          qpdf input --pages [--file=]file [--range=]n -- output
          qpdf --empty --pages file n -- output (merge from an empty base)
          qpdf input --check
          qpdf --job-json-file file
        Options:
          --suppress-recovery   fail on damaged cross-references instead of bounded repair
          --ignore-xref-streams         use classic tables rather than streams
          --warning-exit-0      exit 0 despite warnings
          --no-warn             suppress warning diagnostics
          @file                 read arguments from a host file, one per line
          --rotate=[+|-]angle[:range] rotate output pages (n, n-m, comma lists, z)
          --split-pages[=n]         write page groups to separate files
          --replace-input             overwrite the single input in place
          --collate[=n[,m]]         interleave multi-file page groups
          --set-page-labels specs  number output pages explicitly
          --min-version=version     raise output version when lower
          --force-version=version   set output version exactly
          --deterministic-id        stable content-hashed document ID
          --static-id               fixed test document ID
          --newline-before-endstream  extra newline before endstream
          --object-streams=mode     disable, preserve (default), or generate
          --preserve-unreferenced       keep unreferenced input objects
          --stream-data=mode            compress, preserve, or uncompress streams
          --compress-streams=[y|n]      compress uncompressed streams
          --decode-level=level          none, generalized, specialized, or all
          --max-stream-filters=n          cap filters decoded per stream (default 25)
          --parser-max-container-size=n   cap objects per container (default 4294967295)
          --parser-max-container-size-damaged=n  cap objects per container during repair (default 5000)
          --parser-max-nesting=n          cap object nesting depth (default host budgets, max 499)
          --no-default-limits             refused: limits cannot be disabled in this build
          --recompress-flate            recompress already-flate streams
          --compression-level=1..9      flate effort for compressed streams
          --remove-info               drop info except modification date
          --remove-metadata           drop the document metadata stream
          --coalesce-contents         combine page content streams
          --normalize-content=[y|n]     normalize newlines in content streams
          --remove-unreferenced-resources=parameter  drop unused page resources
          --externalize-inline-images       lift large inline images to XObjects
          --ii-min-bytes=size-in-bytes      inline-image threshold (default 1024)
          --overlay file [--to=range] [--from=range] [--repeat=range] --  stamp pages on top
          --underlay file [--to=range] [--from=range] [--repeat=range] --  stamp pages below
          --flatten-rotation                  bake page rotation into content
          --flatten-annotations=all|print|screen  push annotations into content
          --generate-appearances              regenerate widget appearances
          --remove-acroform                 remove the interactive form dictionary
          --remove-structure                remove the structure tree
          --qdf                             editable QDF view of the PDF
          --no-original-object-ids          omit original object IDs in QDF
          --linearize                       generate linearized web-optimized output
          --linearize-pass1=file          capture the first linearization pass (debug)
          --optimize-images                   recompress images as JPEG when smaller
          --oi-min-width=n                  minimum width for image optimization
          --oi-min-height=n                 minimum height for image optimization
          --oi-min-area=n                   minimum area for image optimization
          --keep-inline-images              exclude inline images from optimization
          --jpeg-quality=n                  JPEG quality 0..100 for optimized images
          --list-attachments          list embedded files
          --show-attachment=key         write one attachment to the output file
          --add-attachment file [options] --  embed host files
          --remove-attachment=key       drop an embedded file
          --copy-attachments-from file [--prefix=] --  copy embedded files
          --json[=1|2|latest] [--json-key=key] --  write document JSON
          --json-help[=1|2|latest]  show JSON format
          --test-json-schema            validate JSON output against its format description
          --report-memory-usage         report peak memory usage
          --verbose                     summarize the operation
          --keep-files-open=y|n          hint at keeping files open (host-managed)
          --keep-files-open-threshold=n  file-count threshold for the hint (default 200)
          --json-object={trailer|obj[,gen]}  limit JSON objects
          --json-stream-data={none|inline|file}  stream payloads in JSON
          --json-stream-prefix=file-prefix  prefix for JSON stream files
          --json-output[=2|latest]  minimal JSON for serialization
          --json-input  build a PDF from qpdf JSON
          --update-from-json=file  update a PDF from qpdf JSON
          --check-linearization       check linearization tables only
          --show-npages                 show number of pages
          --show-pages                  show page dictionaries and contents
          --with-images                 include image details with --show-pages
          --show-object={trailer|obj[,gen]}  show one object definition
          --show-xref                   show cross-reference entries
          --show-linearization          display linearization hint tables
          --remove-page-labels      drop explicit page numbers (accepted no-op)
          --help                show this help
          --version             show version and compatibility target
          --copyright           show copyright and license notice
          --job-json-help       show job JSON format
          --completion-bash     output a bash completion command
          --completion-zsh      output a zsh completion command
        """;

    internal const string Copyright = """
        Lokad.qpdf: managed non-cryptographic PDF structural processor.
        Copyright (c) 2026 Lokad (MIT License).
        Compatibility target: qpdf 12.4.2, non-cryptographic subset only.
        """;

    internal static bool TryMatchCopyright(IReadOnlyList<string> argv, out string text)
    {
        foreach (string token in argv)
        {
            if (token == "--copyright")
            {
                text = Copyright;
                return true;
            }
        }

        text = string.Empty;
        return false;
    }

    internal static string Version()
    {
        string version = typeof(HelpText).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        return $"Lokad.qpdf {version} (qpdf 12.4.2 compatibility target, non-cryptographic subset)";
    }

    internal static bool TryMatchHelp(IReadOnlyList<string> argv, out string text)
    {
        foreach (string token in argv)
        {
            if (token == "--help")
            {
                text = Help;
                return true;
            }
        }

        text = string.Empty;
        return false;
    }

    internal static bool TryMatchVersion(IReadOnlyList<string> argv, out string text)
    {
        foreach (string token in argv)
        {
            if (token == "--version")
            {
                text = Version();
                return true;
            }
        }

        text = string.Empty;
        return false;
    }

    internal const string JobJsonHelp = """
        Lokad.qpdf job JSON: supported subset of the QPDFJob format (qpdf 12.4.2, non-cryptographic only).
        Usage:
          qpdf --job-json-file file
        Top-level keys:
          inputFile, outputFile, pages:[{file, range}, ...], rotate, setPageLabels,
          removePageLabels, minVersion, forceVersion, deterministicId, staticId,
          newlineBeforeEndstream, objectStreams, preserveUnreferenced, streamData,
          compressStreams, decodeLevel, recompressFlate, compressionLevel, removeInfo,
          removeMetadata, coalesceContents, normalizeContent, externalizeInlineImages,
          iiMinBytes, removeUnreferencedResources, overlay, underlay, flattenRotation,
          flattenAnnotations, generateAppearances, qdf, linearize, linearizePass1,
          checkLinearization, showLinearization, showNpages, showPages, withImages, showObject,
          showXref, removeAcroform, removeStructure, noOriginalObjectIds, optimizeImages,
          oiMinWidth, oiMinHeight, oiMinArea, keepInlineImages, jpegQuality,
          listAttachments, showAttachment, addAttachment, removeAttachment,
          copyAttachmentsFrom, empty, json, jsonKey, jsonObject, jsonStreamData,
          jsonStreamPrefix, jsonOutput, jsonInput, updateFromJson, warningExit0, noWarn,
          suppressRecovery, ignoreXrefStreams, reportMemoryUsage, testJsonSchema, verbose, keepFilesOpen, keepFilesOpenThreshold
        Nested "global" object: maxStreamFilters (non-negative filter count, default 25),
        parserMaxNesting (within 499), parserMaxContainerSize and parserMaxContainerSizeDamaged
        (non-negative object counts, defaults 4294967295 and 5000). Other global keys fail;
        parserMaxNesting (non-negative nesting depth within 499).
        Crypto keys (encrypt, decrypt, password and related options) are rejected with (encrypt, decrypt, password and related options) are rejected with
        UnsupportedCryptoOperation. See --help for the argv surface.
        """;

    internal static bool TryMatchJobJsonHelp(IReadOnlyList<string> argv, out string text)
    {
        foreach (string token in argv)
        {
            if (token == "--job-json-help")
            {
                text = JobJsonHelp;
                return true;
            }
        }

        text = string.Empty;
        return false;
    }

    internal const string JsonHelpV2 = """
        {"version": "JSON format version (2)", "parameters": "decode and stream-data parameters for this inspection", "pages": "per-page descriptors with object, contents, label, outlines and pageposfrom1", "pagelabels": "explicit page labels", "attachments": "embedded-file entries", "encrypt": "unencrypted sentinel", "outlines": "explicit-destination outline entries", "acroform": "form fields with inherited types, names, values and annotations", "qpdf": "object table (version 2)"}
        """;

    internal const string JsonHelpV1 = """
        {"version": "JSON format version (1)", "parameters": "decode and stream-data parameters for this inspection", "pages": "per-page descriptors with object, contents, label, outlines and pageposfrom1", "pagelabels": "explicit page labels", "attachments": "embedded-file entries", "encrypt": "unencrypted sentinel", "outlines": "explicit-destination outline entries", "acroform": "form fields with inherited types, names, values and annotations", "objects": "object table (version 1)", "objectinfo": "object metadata (version 1)"}
        """;

    internal static bool TryMatchJsonHelp(IReadOnlyList<string> argv, out string text)
    {
        foreach (string token in argv)
        {
            if (token == "--json-help" || token == "--json-help=2" || token == "--json-help=latest")
            {
                text = JsonHelpV2;
                return true;
            }

            if (token == "--json-help=1")
            {
                text = JsonHelpV1;
                return true;
            }
        }

        text = string.Empty;
        return false;
    }

    internal const string CompletionFlags = "--suppress-recovery --warning-exit-0 --no-warn --rotate --split-pages --replace-input --collate --set-page-labels --min-version --force-version --deterministic-id --static-id --newline-before-endstream --object-streams --preserve-unreferenced --stream-data --compress-streams --decode-level --recompress-flate --compression-level --remove-info --remove-metadata --coalesce-contents --normalize-content --remove-unreferenced-resources --externalize-inline-images --ii-min-bytes --overlay --underlay --flatten-rotation --flatten-annotations --generate-appearances --remove-acroform --remove-structure --qdf --no-original-object-ids --linearize --linearize-pass1 --optimize-images --oi-min-width --oi-min-height --oi-min-area --keep-inline-images --jpeg-quality --list-attachments --show-attachment --add-attachment --remove-attachment --copy-attachments-from --max-stream-filters --parser-max-nesting --parser-max-container-size --parser-max-container-size-damaged --parser-max-errors --no-default-limits --ignore-xref-streams --empty --json --json-help --json-object --json-stream-data --json-stream-prefix --json-output --json-input --update-from-json --check-linearization --show-npages --show-pages --with-images --show-object --show-xref --show-linearization --remove-page-labels --help --version --copyright --job-json-help --completion-bash --completion-zsh --check --pages --job-json-file --json-key --test-json-schema --report-memory-usage --verbose --keep-files-open --keep-files-open-threshold";

    internal static string BashCompletion()
    {
        return "# Lokad.qpdf bash completion (supported subset of qpdf 12.4.2; static script with no executable or environment lookup).\n_qpdf_complete() {\n    local flags=\"" + CompletionFlags + "\"\n    COMPREPLY=($(compgen -W \"$flags\" -- \"${COMP_WORDS[COMP_CWORD]}\"))\n}\ncomplete -F _qpdf_complete qpdf\n";
    }

    internal static string ZshCompletion()
    {
        return "#compdef qpdf\n# Lokad.qpdf zsh completion (supported subset of qpdf 12.4.2; static script with no executable or environment lookup).\n_qpdf_complete() {\n    local -a flags\n    flags=(" + CompletionFlags + ")\n    compadd -a flags\n}\ncompdef _qpdf_complete qpdf\n";
    }

    internal static bool TryMatchCompletionBash(IReadOnlyList<string> argv, out string text)
    {
        foreach (string token in argv)
        {
            if (token == "--completion-bash")
            {
                text = BashCompletion();
                return true;
            }
        }

        text = string.Empty;
        return false;
    }

    internal static bool TryMatchCompletionZsh(IReadOnlyList<string> argv, out string text)
    {
        foreach (string token in argv)
        {
            if (token == "--completion-zsh")
            {
                text = ZshCompletion();
                return true;
            }
        }

        text = string.Empty;
        return false;
    }

    internal static bool TryMatchZopfli(IReadOnlyList<string> argv, out string text)
    {
        foreach (string token in argv)
        {
            if (token == "--zopfli")
            {
                text = "Zopfli support is not enabled in this build.";
                return true;
            }
        }

        text = string.Empty;
        return false;
    }
}
