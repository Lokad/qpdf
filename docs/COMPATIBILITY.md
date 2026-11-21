# Compatibility

Lokad.qpdf targets **qpdf 12.4.2** structural operations, with R2–R6 password-aware document opening.
The supported profile defines the caller contract. The option inventory covers
140 flags in 157 contexts and the fix-QDF and zlib-flate companions. It records
forms, JSON mappings, exclusions, gaps and executable evidence.
The pinned reference lane records the log and line for each registered oracle
outcome.
[Supported profile](SUPPORTED-PROFILE.md) ·
[Option inventory](../tests/Compatibility/options.json) · [Changelog](../CHANGELOG.md)

## Limitations that affect callers

| Boundary | Contract |
| --- | --- |
| Password security | Read/preserve/remove R2–R6; create AES-256 R6 or copy an authenticated template. R4 output normalizes to AES-128. New weak encryption remains deferred. The [password profile](SUPPORTED-PROFILE.md#password-security) defines credentials, source roles and output policies. |
| Signed inputs | [Stored appearances, signature invalidation and inspection](SUPPORTED-PROFILE.md#signed-inputs-and-exclusions) |
| Password encoding recovery | The [authentication profile](SUPPORTED-PROFILE.md#authentication) defines representations and retries. Malformed UTF-16 is rejected, while native recovery can discard its bytes. WinAnsi 0x98 follows [the standard U+02DC mapping](https://www.unicode.org/Public/MAPPINGS/VENDORS/MICSFT/WINDOWS/CP1252.TXT); pinned native recovery maps it to U+0303. |
| Legacy security | R3 accepts both full-digest and key-length owner hashing for short keys. Preservation retains the original password entries; native qpdf and Poppler reject full-digest owners on input and output, while the managed reader accepts them. V2 without Length uses the specified 40-bit default; native qpdf guesses 128 bits. |
| Encryption reporting | Managed reports omit passwords and keys. Native qpdf can print a recovered password and list different crypto providers. [Reports and predicates](SUPPORTED-PROFILE.md#reports-and-predicates) |
| JPEG | Decoding and recompression use a caller-supplied codec. Output quality can vary across codecs. [Image transforms and outputs](SUPPORTED-PROFILE.md#image-transform-outputs) |
| Zopfli | Unavailable; requests fail with exit 2. Ordinary Flate works. `QPDF_ZOPFLI` is treated as unset. |
| Rotation baking | Numeric XYZ/FitR destinations follow the baked frame; Fit/FitB stays unchanged and 180-degree axis fits project their coordinate. Affected nullable positions and quarter-turn H/V/BH/BV fits fail UnsupportedFeature before publication because their general viewer/window state cannot be preserved. |
| Resource limits | Host ceilings remain binding under `--no-default-limits`. PDF container and nesting ceilings also apply to JSON imports, where native can ignore them. [Memory admission](MEMORY.md) |
| Scope | Rendering, OCR, text extraction/layout, general authoring and PDF/A/X/UA conformance are outside the target. |

## Entry-point defaults

The [entry paths](SUPPORTED-PROFILE.md#entry-paths) define reading, stream
copying and setter order. [JSON import output options](SUPPORTED-PROFILE.md#output-options)
define stream encoding and packing defaults.

`QpdfDocument.GetObject(PdfReference)` resolves an exact number/generation pair;
stale, free and missing pairs yield PDF null. `GetObject(int)` looks up the active
object by number and throws for unknown numbers. Both return values that survive
document disposal; exact-reference lookup uses the document's opening token and budgets.

| Behavior | Typed | Argv / job JSON |
| --- | --- | --- |
| Rotation requests | Supplied list order | Last value per exact range text, then ordinal key order |
| Generic catalog data | Selection defaults may omit extras | Ordinary rewrites preserve supported primary extras; split has a narrower policy |

Managed extensions include separate job-file values, recognized JSON version
and compression choices as separate tokens, and separate unsigned decimal
split sizes. Bare JSON and compression flags leave ordinary path operands
available. If a filename matches a recognized choice, add a separator
(`--json-output -- 2`) or specify the value with equals (`--json-output=2 2`).
Use the same separator or equals syntax for numeric split output names.
In job JSON, `"json":""` and `"jsonOutput":""` select the default version (2).
For JSON export, an output of `-` returns the report in the execution result.

## Known differences from native qpdf

These are intentional current policies; exact object numbers or compressed bytes
are generally not compatibility guarantees.

| Area | Managed behavior |
| --- | --- |
| Page counts | Typed `CountPagesAsync` returns the traversed page count and reader warnings. Commands retain native-style declared `/Count` reporting, including existing malformed-count behavior after complete acquisition. Standalone bounded counting accepts a declaration only after page-tree validation; combined inspections and transforms keep their complete-input path. |
| Writer encryption defaults | Preserve primary protection in every writer. Native qpdf clears default protection in QDF and split modes unless an explicit encryption or template policy reapplies it. |
| Publication | Fileless operations expose completed payloads in their result. Stable host identities detect aliases for readable named targets. [Publication rules](SUPPORTED-PROFILE.md#governance) |
| Placement and replacement | An overlay or underlay that resolves to the replacement target is rejected before staging. Native qpdf on Linux allows this combination. [Publication rules](SUPPORTED-PROFILE.md#governance) |
| First-pass capture | Protect document/attachment inputs, the effective encryption template, read password files and consumed job/response files. Native can overwrite auxiliary inputs after reading them. Inactive settings create no capture. |
| Primary document | Its attachment collection survives selection even if none of its pages is selected. Secondary collections need explicit import; page-reachable files still copy. Explicit empty primary differs from a real zero-page input. |
| Split | Retain final document attachments and explicitly preserved unreferenced roots. Clear active destinations to omitted pages and keep live action continuations in order. Bound splits filter named destination indexes. Other foreign page references become null. Native split omits the attachment collection and outlines. Explicit/derived labels follow assembled output order before splitting. |
| Detached JSON objects | Commands drop detached objects by default. Explicit preservation keeps eligible authored and source objects, including in split parts. Native qpdf drops detached roots in JSON splits and can retain retired protection and layout frames after a preserved update. Existing typed imports retain detached declarations. |
| Graph identity | Preserve same-role sharing/cycles while isolating role-specific replacements from private aliases. Equal contents are not identity. Duplicate pages/widgets have independent headers; distinct pages can retain shared field branches. |
| Bound content normalization | Standalone normalization gives active page programs separate stream identities from private aliases. Native qpdf rewrites the shared stream. QDF normalization keeps the shared identity. |
| JSON content normalization | Export can normalize active page programs before reporting. Native qpdf leaves JSON stream bytes unchanged. [JSON export](SUPPORTED-PROFILE.md#json-export) |
| Catalog and forms | Rebuild page ancestry and required Parent links. Some malformed/unresolvable extras or structure drop under bounded recovery. Combined form dictionaries/children can remain direct. General malformed-field parity is not claimed. |
| Print presets | Page selection and bound splits project the active `/PrintPageRange` to the output order. Native selection can retain stale positions. [Selection semantics](SUPPORTED-PROFILE.md#selection-semantics) |
| Articles | Page edits project the primary catalog's article rings and page registrations. Native selection can retain stale bead-page links and native splits omit catalog threads. [Selection semantics](SUPPORTED-PROFILE.md#selection-semantics) |
| Tagged pages | Page edits with a changed page set or sequence omit the active tag roots with a warning. Native selection can retain stale active structure links. [Selection semantics](SUPPORTED-PROFILE.md#selection-semantics) |
| Source context | Bind retained secondary/placement URI bases, named destinations and static View optional-content defaults before source cleanup, including interpreted OFF bases. Combined defaults normalize to ON; same-named layers remain distinct. Unsupported compound layer contexts refuse before output. |
| Navigation | First registered repeated page is canonical. Rotation baking corrects stale target coordinates that native can retain; remote/private roles and unchanged/omitted targets keep their treatment. |
| Viewports | Native rotation baking can leave viewport bounds and measurement origins in the original frame. [Page content profile](SUPPORTED-PROFILE.md#page-content-profile) |
| Placement forms | Retain primary calculation order; imported fields do not add CO entries. Source resources/default appearance programs bind independently of target resources. |
| Labels | Interpreted malformed trees fail. Unchanged whole-file trees stay raw. Prefix-only styles ignore inactive counters; equal resolved styles/raw prefixes can compact. Label removal applies to split outputs; native split can retain primary-document labels despite `--remove-page-labels`. |
| PDF JSON | Object tables classify live pairs using source xrefs. Native can materialize dead trailer pairs as explicit null objects and alias declared generations that share an object number. Managed page-source export preserves the assembled labels and source version floor. Native JSON can retain primary label positions and version. [Export](SUPPORTED-PROFILE.md#json-export) · [Import and update](SUPPORTED-PROFILE.md#json-import-and-update) |
| Form JSON | A widget without local field entries uses the terminal field's identity and local names in the managed field descriptor. Native uses the widget's identity and local names. Managed `annotation.object` retains the widget reference. [JSON export](SUPPORTED-PROFILE.md#json-export) |
| Resource pruning | Conservatively retain effective Type 3 and soft-mask dependencies that native can discard. Independent private/metadata consumers keep their original resource graph. |
| Page resources | Selection and splitting warn about missing or invalid bindings. Managed repair uses an empty dictionary for recognized resource-free programs. Programs with unresolved dependencies keep the source binding. |
| Inline images | Externalized Indexed color-space aliases become full XObject names; palette/sample treatment is unchanged. Bound Form externalization gives active Forms fresh identities and retains private originals. Native can rewrite the shared Form in place. |
| Parser | Integer scalars cover signed 64-bit values. Strict input rejects bare-reference object bodies; repair replaces them with null. Configured nesting/container exhaustion remains a resource failure and cannot silently discard retained metadata. |
| Filters | Flate ends at the first complete zlib frame and rejects truncation/checksum failures. RunLength tolerates truncated packets and missing end markers. Compression write failures abort the destination before codec cleanup, preserving the original error without retrying memory admission. External-file streams remain opaque; no referenced path/URL is opened. |
| Plaintext Crypt stages | Copying consumes valid leading default/Identity stages before content transforms. JSON-authored stages normalize before output encryption. Remaining encodings and private filter aliases survive; unknown or malformed stages cannot be treated as plaintext for encryption. |
| Xref-tagged graph streams | Retained `/Type /XRef` streams use ordinary graph encryption and remain present in QDF; physical xref sections stay clear. Native raw JSON can return their ciphertext, and native QDF can discard them, leaving a bad reference. Independent header/AES controls cover these differences. |
| Checking | Skipped coverage stays visible. Checking verifies structural conditions. Physical linearization defects can fail where native warns. [Inspection profile](SUPPORTED-PROFILE.md#inspection-profile) |
| Image JSON | Decode-none reports retain raw filter specifications with `filterable:false`, including malformed decoder settings. Enabled decoding retains strict specification validation; native can warn and report the descriptor instead. |
| IDs / compression | Default IDs are deterministic content hashes; static IDs use the native test constant. BCL Flate levels map 1/2..8/9 to fastest/optimal/smallest. Numbering, packing, predictors and hint bytes may differ. |
| Linearized outlines | Outline closures stay outside object streams so hint spans remain contiguous. Pinned native packing can split a root from later outline containers and warn about its own offsets/lengths; the reference controls retain that failure and compare a valid classic equivalent. |
| Companions | Fix-QDF requires LF outer syntax, sequential numbering and unambiguous stream/length framing. Zlib-flate reports malformed frames and enforces decoded and output limits during inflation. Once the codec returns a byte beyond the tighter limit, the operation refuses before validating the rest of the frame. An exact fit still requires frame validation. |

Nullable destination positions retain prior viewer coordinates. A quarter turn
can make one coordinate depend on the other. Turning a width fit into a height
fit also changes how the viewer computes zoom. These semantics explain the
rotation refusals above. [ISO 32000-1, 12.3.2.2 and Table 151](https://opensource.adobe.com/dc-acrobat-sdk-docs/standards/pdfstandards/pdf/PDF32000_2008.pdf)

## Resource APIs

Nesting or container exhaustion raises `QpdfLimitException`
(`BudgetExceeded`). Required malformed source contexts raise `QpdfParseException`
(`InvalidArguments`), unsupported binding yields `UnsupportedFeature`, and host
failures yield `HostOperationFailed`.
[Acquisition bounds, counters and cancellation](MEMORY.md#engine-guarantees-and-limits)

## How compatibility is verified

CI runs offline behavior tests, locked builds and isolated package consumers.
The reference job compares native and managed semantics with an independent
reader, including ordered page content, parser recovery, password policies and
decoded JSON payloads. An explicit raster lane measures rendered output.
[Run the reference and visual checks](https://github.com/Lokad/qpdf/blob/master/tests/Lokad.Qpdf.Tests/README.md#reference-and-visual-checks)
