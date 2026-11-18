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
| Signed inputs | Published PDFs retain stored appearances and invalidate signatures. Read-only inspection retains original values. [Signed-input policy](SUPPORTED-PROFILE.md#signed-inputs-and-exclusions) |
| Password encoding recovery | The [authentication profile](SUPPORTED-PROFILE.md#authentication) defines representations and retries. Malformed UTF-16 is rejected, while native recovery can discard its bytes. WinAnsi 0x98 follows [the standard U+02DC mapping](https://www.unicode.org/Public/MAPPINGS/VENDORS/MICSFT/WINDOWS/CP1252.TXT); pinned native recovery maps it to U+0303. |
| Legacy security | R3 accepts both full-digest and key-length owner hashing for short keys. Preservation retains the original password entries; native qpdf and Poppler reject full-digest owners on input and output, while the managed reader accepts them. V2 without Length uses the specified 40-bit default; native qpdf guesses 128 bits. |
| Encryption predicates | `is-encrypted` uses 0/2 for encrypted/plain input; `requires-password` uses 0/2/3 for credentials needed/plain input/credentials matched. Completed query codes are independent of warning presentation. Unsupported handlers fail explicitly. |
| Crypto implementation | Standalone `--show-crypto` reports `managed`. Names describe the implementation in this build; native qpdf can list different providers. |
| Encryption reporting | `show-encryption` reports non-secret declared settings and password-match status. Wrong credentials permit reporting but skip combined content inspections. Reports omit passwords/keys and include per-filter cipher names only for V4/V5 handlers; explicit output paths are rejected. |
| Encrypted JSON | Authenticated exports use plaintext values and selected stream payloads. Password and key fields are null. Import and update use the [JSON protection policies](SUPPORTED-PROFILE.md#authenticated-json-updates-and-creation). |
| JPEG | Decoding and recompression use a caller-supplied codec. Output quality can vary across codecs. [Supported JPEG capabilities](SUPPORTED-PROFILE.md#jpeg-codec-capabilities) |
| Zopfli | Unavailable; requests fail with exit 2. Ordinary Flate works. `QPDF_ZOPFLI` is treated as unset. |
| Rotation baking | Numeric XYZ/FitR destinations follow the baked frame; Fit/FitB stays unchanged and 180-degree axis fits project their coordinate. Affected nullable positions and quarter-turn H/V/BH/BV fits fail UnsupportedFeature before publication because their general viewer/window state cannot be preserved. |
| Host effects | Logical paths, inputs, descriptors, environment and publication require host capabilities. Fileless results replace ambient stdout. |
| Resource limits | Host ceilings remain binding under `--no-default-limits`. PDF container and nesting ceilings also apply to JSON imports, where native can ignore them. Memory admission bounds an estimated working set, not CLR heap or process RSS. See [ownership](MEMORY.md). |
| Scope | Rendering, OCR, text extraction/layout, general authoring and PDF/A/X/UA conformance are outside the target. |

The inventory covers 140 flags in 157 context entries, plus the fix-qdf and zlib-flate
companions. Entries record status, executable evidence, remaining gaps and
exclusion reasons. `cryptoSyntax` identifies option names for parser diagnostics.

## Entry-point defaults

`QpdfDocument.GetObject(PdfReference)` resolves an exact number/generation pair;
stale, free and missing pairs yield PDF null. `GetObject(int)` looks up the active
object by number and throws for unknown numbers. Both return values that survive
document disposal; exact-reference lookup uses the document's opening token and budgets.

| Behavior | Typed | Argv / job JSON |
| --- | --- | --- |
| Reading | Strict by default; document/check/count/selection/JSON can request bounded repair | Bounded repair by default; `--suppress-recovery` is strict |
| Stream copying | Opaque encoded bytes | Generalized decoding and compression |
| JSON import packing | Existing overloads disable packing; full overloads accept `QpdfJsonImportOptions` | Preserve matching source members by default |
| JSON import compression | Preserve encoded payloads | Generalized decoding and compression, with the ordinary stream setters |
| Rotation requests | Supplied list order | Last value per exact range text, then ordinal key order |
| Generic catalog data | Selection defaults may omit extras | Ordinary rewrites preserve supported primary extras; split has a narrower policy |

Argv setters apply in argument order. Job keys apply in sorted order within each
file, and duplicate JSON properties keep their last value. Semantic validation
uses effective values. Malformed string encoding fails even in a superseded
value. Compression and decode setters follow that order. QDF fills unset defaults.
Managed extensions include separate job-file/JSON values, separate unsigned
decimal split sizes and bare compression. To split into filenames based on a
fully numeric output operand, use `--split-pages -- 2` or `--split-pages=1 2`.
Unknown options,
invalid combinations and excluded crypto fail explicitly.

Repeated scalar/bare argv setters are accepted, including warning, recovery,
version and keep-open hints.
Nonempty input/output declarations, pages/replace declarations and repeated `--empty` remain exclusive;
main-context parser ceilings also remain exclusive. Stream-filter setters and
`--global` blocks keep setter order. Block-local duplicate rules remain explicit.

See the supported profile for typed recovery options and warnings in
[document opening](SUPPORTED-PROFILE.md#entry-paths), [page selection](SUPPORTED-PROFILE.md#selection-semantics),
[inspection](SUPPORTED-PROFILE.md#inspection-profile) and
[attachments](SUPPORTED-PROFILE.md#attachments-profile).

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
| Split | Retain final document attachments and explicitly preserved unreferenced roots; native split output omits the document attachment collection. Foreign page references become null. Explicit/derived labels follow assembled output order before splitting. |
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
| Checking | Skipped coverage stays visible. Inspection does not apply every writer removal setting or establish rendering/conformance. Physical linearization defects can fail where native warns. |
| Image JSON | Decode-none reports retain raw filter specifications with `filterable:false`, including malformed decoder settings. Enabled decoding retains strict specification validation; native can warn and report the descriptor instead. |
| IDs / compression | Default IDs are deterministic content hashes; static IDs use the native test constant. BCL Flate levels map 1/2..8/9 to fastest/optimal/smallest. Numbering, packing, predictors and hint bytes may differ. |
| Linearized outlines | Outline closures stay outside object streams so hint spans remain contiguous. Pinned native packing can split a root from later outline containers and warn about its own offsets/lengths; the reference controls retain that failure and compare a valid classic equivalent. |
| Companions | Fix-QDF requires LF outer syntax, sequential numbering and unambiguous stream/length framing. Zlib-flate reports malformed frames and enforces decoded and output limits during inflation. Once the codec returns a byte beyond the tighter limit, the operation refuses before validating the rest of the frame. An exact fit still requires frame validation. |

Nullable destination positions retain prior viewer coordinates. A quarter turn
can make one coordinate depend on the other. Turning a width fit into a height
fit also changes how the viewer computes zoom. These semantics explain the
rotation refusals above. [ISO 32000-1, 12.3.2.2 and Table 151](https://opensource.adobe.com/dc-acrobat-sdk-docs/standards/pdfstandards/pdf/PDF32000_2008.pdf)

## Resource APIs

The typed count and standalone `--show-npages` / `showNpages` share bounded
acquisition. Their count policies differ as described above.
[Counting](SUPPORTED-PROFILE.md#inspection-profile) · [Acquisition bounds](MEMORY.md#engine-guarantees-and-limits)

`QpdfExecutionCounters.HostOperations` counts actual opens, reads and writes.
Hosts can supply deadline tokens alongside structural, decoded-byte, memory and
handle limits. Nesting or container exhaustion raises `QpdfLimitException`
(`BudgetExceeded`). Required malformed source contexts raise `QpdfParseException`
(`InvalidArguments`), unsupported binding yields `UnsupportedFeature`, and host
failures yield `HostOperationFailed`.
[Input leases](MEMORY.md#input-leases) · [Caller ownership](MEMORY.md#escaped-values-and-results) · [JPEG codecs](MEMORY.md#jpeg-codecs)

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
`tools/bootstrap-qpdf.ps1` checks that the supplied executable reports qpdf
12.4.2 and records its SHA-256.

Run the visual lane separately with a Release solution build, PowerShell on
.NET 10 or later, the pinned qpdf executable and an identified `pdftoppm`.
Use a fresh workspace for the rendered comparisons and their negative controls.

```powershell
pwsh -File tools/run-visual-checks.ps1 -QpdfExe $oracle -RendererExe $renderer -WorkRoot artifacts/visual-check
```

The pinned source commit is `4eba95899886e851cc41d76886483b347612f2a8`.
Inventory hashes normalize CRLF to LF; reference metadata changes require review.
The official Windows `qpdf-12.4.2-msvc64.zip` SHA-256 is
`db87077e683630c1217e0e8f9a20a9749d952ab676e881c3689187763a5de25d`;
its `bin/qpdf.exe` is
`1bdaf878f37eaffea1ea3c30591214c77636a0f2fc7432427516b6e22e36c0ff`.
`tools/install-reference-qpdf.ps1` verifies both Windows hashes. The reference
runner stops and awaits timed-out processes. Native qpdf, renderers, PdfPig and
development codecs stay in the development tools and tests.
