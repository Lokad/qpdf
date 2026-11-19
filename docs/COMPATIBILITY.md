# Compatibility

Lokad.qpdf targets **qpdf 12.4.2** structural operations, with R2–R6 password-aware document opening.
The [supported profile](SUPPORTED-PROFILE.md) defines the current contract;
the [option inventory](../tests/Compatibility/options.json) records forms, JSON
mappings, remaining gaps and executable evidence. An accepted option alone does
not establish implemented behavior. Development changes are in the
[changelog](../CHANGELOG.md).

## Limitations that affect callers

| Boundary | Contract |
| --- | --- |
| Password security | Read/preserve/remove R2–R6; create AES-256 R6 or copy an authenticated template. R4 output normalizes to AES-128. New weak encryption remains deferred. The [password profile](SUPPORTED-PROFILE.md#password-security) defines credentials, source roles and output policies. |
| Signed inputs | [Stored appearances, signature invalidation and inspection](SUPPORTED-PROFILE.md#signed-inputs-and-exclusions) |
| Password encoding recovery | The [authentication profile](SUPPORTED-PROFILE.md#authentication) defines representations and retries. Malformed UTF-16 is rejected, while native recovery can discard its bytes. WinAnsi 0x98 follows [the standard U+02DC mapping](https://www.unicode.org/Public/MAPPINGS/VENDORS/MICSFT/WINDOWS/CP1252.TXT); pinned native recovery maps it to U+0303. |
| Legacy security | R3 accepts both full-digest and key-length owner hashing for short keys. Preservation retains the original password entries; native qpdf and Poppler reject full-digest owners on input and output, while the managed reader accepts them. V2 without Length uses the specified 40-bit default; native qpdf guesses 128 bits. |
| Encryption predicates | `is-encrypted` uses 0/2 for encrypted/plain input; `requires-password` uses 0/2/3 for credentials needed/plain input/credentials matched. Completed query codes are independent of warning presentation. Unsupported handlers fail explicitly. |
| Crypto implementation | Standalone `--show-crypto` reports `managed`. Names describe the implementation in this build; native qpdf can list different providers. |
| Encryption reporting | `show-encryption` reports non-secret declared settings and password-match status. Wrong credentials permit reporting but skip combined content inspections. Reports omit passwords/keys and include per-filter cipher names only for V4/V5 handlers; explicit output paths are rejected. |
| Encrypted JSON | Authenticated exports use plaintext values and selected stream payloads. Password and key fields are null. Import and update use the [JSON protection policies](SUPPORTED-PROFILE.md#authenticated-json-updates-and-creation). |
| JPEG | Decoding and recompression use a caller-supplied codec. Output quality can vary across codecs. [Supported JPEG capabilities](SUPPORTED-PROFILE.md#jpeg-codec-capabilities) |
| Zopfli | Unavailable; requests fail with exit 2. Ordinary Flate works. `QPDF_ZOPFLI` is treated as unset. |
| Rotation baking | Numeric XYZ/FitR destinations follow the baked frame; Fit/FitB stays unchanged and 180-degree axis fits project their coordinate. Affected nullable positions and quarter-turn H/V/BH/BV fits fail UnsupportedFeature before publication because their general viewer/window state cannot be preserved. |
| Host effects | Hosts map logical paths, including descriptor keys, to input leases and staged outputs. Fileless results replace ambient stdout. |
| Resource limits | Host ceilings remain binding under `--no-default-limits`. PDF container and nesting ceilings also apply to JSON imports, where native can ignore them. [Memory admission](MEMORY.md) |
| Scope | Rendering, OCR, text extraction/layout, general authoring and PDF/A/X/UA conformance are outside the target. |

The inventory covers 140 flags in 157 context entries, plus the fix-qdf and zlib-flate
companions. Entries record status, executable evidence, remaining gaps and
exclusion reasons. `cryptoSyntax` identifies option names for parser diagnostics.

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
| First-pass capture | Protect document/attachment inputs, the effective encryption template, read password files and consumed job/response files. Native can overwrite auxiliary inputs after reading them. Inactive settings create no capture. |
| Primary document | Its attachment collection survives selection even if none of its pages is selected. Secondary collections need explicit import; page-reachable files still copy. Explicit empty primary differs from a real zero-page input. |
| Split | Retain final document attachments and explicitly preserved unreferenced roots. Clear active destinations to omitted pages and keep live action continuations in order. Bound splits filter named destination indexes. Other foreign page references become null. Native split omits the attachment collection and outlines. Explicit/derived labels follow assembled output order before splitting. |
| Detached JSON objects | Commands drop detached objects by default. Explicit preservation keeps eligible authored and source objects, including in split parts. Native qpdf drops detached roots in JSON splits and can retain retired protection and layout frames after a preserved update. Existing typed imports retain detached declarations. |
| Graph identity | Preserve same-role sharing/cycles while isolating role-specific replacements from private aliases. Equal contents are not identity. Duplicate pages/widgets have independent headers; distinct pages can retain shared field branches. |
| Catalog and forms | Rebuild page ancestry and required Parent links. Some malformed/unresolvable extras or structure drop under bounded recovery. Combined form dictionaries/children can remain direct. General malformed-field parity is not claimed. |
| Source context | Bind retained secondary/placement URI bases, named destinations and static View optional-content defaults before source cleanup, including interpreted OFF bases. Combined defaults normalize to ON; same-named layers remain distinct. Unsupported compound layer contexts refuse before output. |
| Navigation | First registered repeated page is canonical. Rotation baking corrects stale target coordinates that native can retain; remote/private roles and unchanged/omitted targets keep their treatment. |
| Annotations | Stored geometry and supported appearances follow page frames, units and NoRotate anchors. Static placement clips to the source page. Popup/reply links stay occurrence-local. This is not a general interactive appearance generator; damaged optional paths can warn/skip. |
| Placement forms | Retain primary calculation order; imported fields do not add CO entries. Source resources/default appearance programs bind independently of target resources. |
| Labels | Interpreted malformed trees fail. Unchanged whole-file trees stay raw. Prefix-only styles ignore inactive counters; equal resolved styles/raw prefixes can compact. Label removal applies to split outputs; native split can retain primary-document labels despite `--remove-page-labels`. |
| Attachments | Preserve raw keys/filespec graphs; listing is a projection. Request matching uses decoded ordinal text, emitted keys sort by encoded bytes. Full-collection operations reject duplicate logical keys; selective extraction keeps first-match lookup. Typed and argv/job extraction authenticate R2–R6 inputs and publish plaintext payloads, including to explicit host output paths. Dates are omitted unless supplied; CreationDate/ModDate inspect independently. |
| PDF JSON | Object tables classify live pairs using source xrefs. Native can materialize dead trailer pairs as explicit null objects and alias declared generations that share an object number. Managed page-source export preserves the assembled labels and source version floor. Native JSON can retain primary label positions and version. [Export](SUPPORTED-PROFILE.md#json-export) · [Import and update](SUPPORTED-PROFILE.md#json-import-and-update) |
| Resource pruning | Conservatively retain effective Type 3 and soft-mask dependencies that native can discard. Independent private/metadata consumers keep their original resource graph. |
| Inline images | Externalized Indexed color-space aliases become full XObject names; palette/sample treatment is unchanged. |
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
decoded JSON payloads. Reference jobs fail when tools are missing.
[JPEG fixtures and visual evidence](https://github.com/Lokad/qpdf/blob/master/tests/Fixtures/JPEG.md)

Run `tools/run-reference-checks.ps1 -QpdfExe $oracle -WorkRoot artifacts/reference-check`
from PowerShell with the pinned executable and a fresh workspace. The default
is a new directory under `artifacts/reference-checks`. The workspace contains
stage logs, `results.json`, and the reader, content, differential and parser
evidence. The runner builds the net11.0 probe with a locked restore. Pass
`-ProbeDll` to use an existing build.
`tools/bootstrap-qpdf.ps1` checks the supplied executable's version and
records its SHA-256. The log identifies the official Windows executable when
its digest matches and records the target source commit separately.

Run the visual lane in a fresh PowerShell process on .NET 10 or later. Supply
the pinned qpdf and `pdftoppm` executables, the Release probe DLL and a test
assembly with its dependencies and copied fixtures. Use a fresh workspace.
The receipt records the checkout and the hashes of the supplied binaries and
PDF inputs. Negative controls must reach pixel comparison and exceed tolerance.

```powershell
pwsh -File tools/run-visual-checks.ps1 -QpdfExe $oracle -RendererExe $renderer `
  -ProbeDll tools/QpdfProbe/bin/Release/net11.0/QpdfProbe.dll `
  -FixtureAssembly tests/Lokad.Qpdf.Tests/bin/Release/net10.0/Lokad.Qpdf.Tests.dll `
  -WorkRoot artifacts/visual-check
```

For qualification on Windows, run `tools/install-reference-qpdf.ps1` with a
fresh destination. It verifies both the archive and executable digests.
For source builds, record the clean checkout and the resulting executable
digest. [Reference pins](https://github.com/Lokad/qpdf/blob/master/tools/reference-pin.json)

Inventory hashes normalize CRLF to LF. The reference runner stops and awaits
timed-out processes. Native qpdf, renderers, PdfPig and development codecs
stay in the development tools and tests.
