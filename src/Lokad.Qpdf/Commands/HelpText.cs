using System.Reflection;

namespace Lokad.Qpdf.Commands;

/// <summary>Help and version text for the narrow supported subset.</summary>
internal static class HelpText
{
    internal const string Help = """
        Lokad.qpdf: managed PDF structural processor with AES R4/R6 reading.
        Usage:
          qpdf input --pages [--file=]file [--password=text] [--range=]n -- output
          qpdf --empty --pages file n -- output (merge from an empty base)
          qpdf input --check
          qpdf --job-json-file file
        Options:
          --suppress-recovery   fail on damaged cross-references instead of bounded repair
          --password=text       primary-input password
          --password-file=path  first password line from a host file
          --password-mode=mode  auto, bytes, unicode, or hex-bytes for passwords
          --decrypt             remove protection after authenticating the input
          --copy-encryption=file  copy protection from an independently authenticated template
          --encryption-file-password=text  template password (absent tries empty)
          --encrypt user owner 256 [options] --  create fresh AES-256 R6 protection
            --user-password=text --owner-password=text --bits=256  alternative named form
            --print=none|low|full --modify=none|assembly|form|annotate|all
            --extract=y|n --annotate=y|n --form=y|n --assemble=y|n --modify-other=y|n
            --cleartext-metadata  keep document-level metadata unencrypted
          --suppress-password-recovery  use only the supplied password encoding
          --is-encrypted        query encryption: exit 0 encrypted, 2 unencrypted
          --requires-password   query password: exit 0 needed, 2 unencrypted, 3 matched
          --show-encryption     report encryption settings and password match, without secrets
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
          --parser-max-errors=n           cap errors while parsing an object (default 15, 0 removes)
          --no-default-limits             lift optional parser defaults within host ceilings
          --global [options] --            set global limits inside the block (bare limit flags also accepted)
          --recompress-flate            recompress already-flate streams
          --compression-level=1..9      flate effort for compressed streams
          --remove-info               drop info except modification date
          --remove-metadata           drop the document metadata stream
          --coalesce-contents         combine page content streams
          --normalize-content=[y|n]     normalize newlines in content streams
          --remove-unreferenced-resources=parameter  drop unused page resources
          --preserve-unreferenced-resources   keep page resources (same as --remove-unreferenced-resources=no)
          --externalize-inline-images       lift large inline images to XObjects
          --ii-min-bytes=size-in-bytes      inline-image threshold (default 1024)
          --overlay file [--password=text] [--to=range] [--from=range] [--repeat=range] --  stamp pages on top
          --underlay file [--password=text] [--to=range] [--from=range] [--repeat=range] --  stamp pages below
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
          --show-attachment=key         extract attachment bytes (optional output path)
          --add-attachment file [options] --  embed host files
          --remove-attachment=key       drop an embedded file
          --copy-attachments-from file [--password=text] [--prefix=] --  copy embedded files
          --json[=1|2|latest] [--json-key=key] --  write document JSON
          --json-help[=1|2|latest]  show JSON format
          --test-json-schema            validate JSON output against its format description
          --report-memory-usage         report peak memory usage
          --verbose                     summarize the operation
          --progress                    report write progress (diagnostics only)
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
          --raw-stream-data               extract raw stream bytes (optional output path)
          --filtered-stream-data          extract decoded stream bytes (optional output path)
          --show-xref                   show cross-reference entries
          --show-linearization          display linearization hint tables
          --remove-page-labels      drop merged page labels
          --help                show this help
          --version             show version and compatibility target
          --copyright           show copyright and license notice
          --job-json-help       show job JSON format
          --completion-bash     output a bash completion command
          --completion-zsh      output a zsh completion command
        Binary inspection without an output path returns QpdfExecutionResult.OutputBytes.
        Explicit output paths use the host's staged transactions.
        Derived PDFs keep stored signature marks but disable signature fields and certification restrictions.
        Inspections and PDF transformations accept primary AES-128 R4 and AES-256 R6 passwords, including empty passwords.
        Encryption predicates are mutually exclusive, return no ordinary output, and ignore other inspections.
        Their completed query codes 2 and 3 are not errors or warnings; --warning-exit-0 does not change them.
        PDF transformations preserve primary protection unless --decrypt or --encrypt is supplied.
        JSON updates accept primary credentials and output encryption policies.
        JSON handler declarations require authenticated preservation or an explicit output policy; password descriptors remain unsupported.
        Signing and signature validation are unsupported.
        """;

    internal const string Copyright = """
        Lokad.qpdf: managed PDF structural processor with AES R4/R6 reading.
        Copyright (c) 2026 Lokad (MIT License).
        Compatibility target: qpdf 12.4.2 structural subset with AES R4/R6 reading.
        """;

    internal static bool TryMatchCopyright(IReadOnlyList<string> argv, out string text)
    {
        foreach (string token in IdentityArguments(argv))
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
        return $"Lokad.qpdf {version} (qpdf 12.4.2 structural compatibility target, AES R4/R6 reading)";
    }

    internal static bool TryMatchHelp(IReadOnlyList<string> argv, out string text)
    {
        foreach (string token in IdentityArguments(argv))
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
        foreach (string token in IdentityArguments(argv))
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
        Lokad.qpdf job JSON: supported subset of the QPDFJob format (qpdf 12.4.2).
        Usage:
          qpdf --job-json-file file
        Top-level keys:
          inputFile, outputFile, password, passwordFile, decrypt, encrypt, copyEncryption, encryptionFilePassword, passwordMode, suppressPasswordRecovery, isEncrypted, requiresPassword, showEncryption, pages:[{file, range, password}, ...], collate, replaceInput, splitPages, rotate, setPageLabels,
          removePageLabels, minVersion, forceVersion, deterministicId, staticId,
          newlineBeforeEndstream, objectStreams, preserveUnreferenced, streamData,
          compressStreams, decodeLevel, recompressFlate, compressionLevel, removeInfo,
          removeMetadata, coalesceContents, normalizeContent, externalizeInlineImages,
          iiMinBytes, removeUnreferencedResources, preserveUnreferencedResources, overlay, underlay, flattenRotation,
          flattenAnnotations, generateAppearances, qdf, linearize, linearizePass1,
          check, checkLinearization, showLinearization, showNpages, showPages, withImages, showObject,
          rawStreamData, filteredStreamData,
          showXref, removeAcroform, removeStructure, noOriginalObjectIds, optimizeImages,
          oiMinWidth, oiMinHeight, oiMinArea, keepInlineImages, jpegQuality,
          listAttachments, showAttachment, addAttachment, removeAttachment,
          copyAttachmentsFrom, empty, json, jsonKey, jsonObject, jsonStreamData,
          jsonStreamPrefix, jsonOutput, jsonInput, updateFromJson, warningExit0, noWarn,
          suppressRecovery, ignoreXrefStreams, reportMemoryUsage, testJsonSchema, verbose, progress, keepFilesOpen, keepFilesOpenThreshold
        Inspection flags check, rawStreamData and filteredStreamData use "" as their value.
        rawStreamData and filteredStreamData require showObject; without outputFile they return result bytes.
        Nested "global" object: maxStreamFilters (non-negative filter count, default 25),
        parserMaxNesting (within 499), parserMaxContainerSize and parserMaxContainerSizeDamaged
        (non-negative object counts, defaults 4294967295 and 5000), parserMaxErrors
        (non-negative error count, default 15; "0" removes the threshold), and noDefaultLimits
        ("" lifts unspecified parser defaults; explicit invocation and host limits remain binding).
        Other global keys fail.
        password accepts text (including "") for the primary input in inspection, JSON export and PDF transformations.
        passwordFile reads the first line of a host file; empty files retain the previous password.
        passwordMode accepts auto, bytes, unicode or hex-bytes; only hex-bytes changes input byte decoding.
        suppressPasswordRecovery takes "" and disables alternate-encoding attempts.
        decrypt takes "" or a boolean and removes protection from PDF output after authentication.
        encrypt takes {userPassword,ownerPassword,"256bit":{}} for new AES-256 R6 output.
        copyEncryption names a template; encryptionFilePassword independently authenticates it.
        A plaintext template removes protection. Password modes apply; output-free inspections ignore the template.
        The 256bit object accepts print, modify, extract, annotate, form, assemble, modifyOther,
        accessibility and cleartextMetadata. Nonempty user passwords require a nonempty owner password.
        isEncrypted and requiresPassword take "" and return the native query exit codes described by --help.
        showEncryption takes "" and reports declared settings without passwords or keys.
        PDF transformations preserve primary protection unless decrypt/encrypt/copyEncryption selects another policy.
        pages[].password authenticates every occurrence of that secondary file; conflicting credentials reject.
        copyAttachmentsFrom[].password authenticates the attachment source independently.
        overlay[].password and underlay[].password authenticate each placement source independently.
        JSON updates accept primary credentials and output encryption policies.
        JSON handler declarations require authenticated preservation or an explicit output policy; legacy creation and password descriptors remain unsupported.
        Other crypto keys are rejected with UnsupportedCryptoOperation. See --help for the argv surface.
        """;

    internal static bool TryMatchJobJsonHelp(IReadOnlyList<string> argv, out string text)
    {
        foreach (string token in IdentityArguments(argv))
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
        {"version": "JSON format version (2)", "parameters": "decode and stream-data parameters for this inspection", "pages": "per-page descriptors with object, contents, label, outlines, pageposfrom1 and images", "pagelabels": "explicit page labels", "attachments": "embedded-file entries", "encrypt": "non-secret encryption facts; password/key fields stay null", "outlines": "explicit-destination outline entries", "acroform": "form fields with inherited types, names, values and annotations", "qpdf": "object table (version 2)"}
        """;

    internal const string JsonHelpV1 = """
        {"version": "JSON format version (1)", "parameters": "decode and stream-data parameters for this inspection", "pages": "per-page descriptors with object, contents, label, outlines, pageposfrom1 and images", "pagelabels": "explicit page labels", "attachments": "embedded-file entries", "encrypt": "non-secret encryption facts; password/key fields stay null", "outlines": "explicit-destination outline entries", "acroform": "form fields with inherited types, names, values and annotations", "objects": "object table keyed by full references such as 3 0 R (version 1)", "objectinfo": "object metadata keyed by the same full references (version 1)"}
        """;

    internal static bool TryMatchJsonHelp(IReadOnlyList<string> argv, out string text)
    {
        foreach (string token in IdentityArguments(argv))
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

    internal const string CompletionFlags = "--encrypt --decrypt --copy-encryption --encryption-file-password --password-file --password-mode --suppress-password-recovery --show-encryption --is-encrypted --requires-password --password --suppress-recovery --warning-exit-0 --no-warn --rotate --split-pages --replace-input --collate --set-page-labels --min-version --force-version --deterministic-id --static-id --newline-before-endstream --object-streams --preserve-unreferenced --stream-data --compress-streams --decode-level --recompress-flate --compression-level --remove-info --remove-metadata --coalesce-contents --normalize-content --remove-unreferenced-resources --preserve-unreferenced-resources --externalize-inline-images --ii-min-bytes --overlay --underlay --flatten-rotation --flatten-annotations --generate-appearances --remove-acroform --remove-structure --qdf --no-original-object-ids --linearize --linearize-pass1 --optimize-images --oi-min-width --oi-min-height --oi-min-area --keep-inline-images --jpeg-quality --list-attachments --show-attachment --add-attachment --remove-attachment --copy-attachments-from --max-stream-filters --parser-max-nesting --parser-max-container-size --parser-max-container-size-damaged --parser-max-errors --no-default-limits --global --ignore-xref-streams --empty --json --json-help --json-object --json-stream-data --json-stream-prefix --json-output --json-input --update-from-json --check-linearization --show-npages --show-pages --with-images --show-object --raw-stream-data --filtered-stream-data --show-xref --show-linearization --remove-page-labels --help --version --copyright --job-json-help --completion-bash --completion-zsh --check --pages --job-json-file --json-key --test-json-schema --report-memory-usage --verbose --progress --keep-files-open --keep-files-open-threshold";

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
        foreach (string token in IdentityArguments(argv))
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
        foreach (string token in IdentityArguments(argv))
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

    private static IEnumerable<string> IdentityArguments(IReadOnlyList<string> argv)
    {
        for (int index = 0; index < argv.Count; index++)
        {
            if (argv[index] != "--encrypt") { yield return argv[index]; continue; }
            index++;
            if (index < argv.Count && !argv[index].StartsWith("--", StringComparison.Ordinal)) { index += 3; }
            while (index < argv.Count && argv[index] != "--") { index++; }
        }
    }

    internal static bool TryMatchZopfli(IReadOnlyList<string> argv, out string text)
    {
        foreach (string token in IdentityArguments(argv))
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
