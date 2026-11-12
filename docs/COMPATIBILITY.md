# Compatibility

Lokad.qpdf targets **qpdf 12.4.2** structural operations, with AES R4/R6 password-aware document opening.
The [supported profile](SUPPORTED-PROFILE.md) defines the current contract;
the [option inventory](../tests/Compatibility/options.json) records forms, JSON
mappings, remaining gaps and executable evidence. An accepted option alone does
not establish implemented behavior. Development changes are in the
[changelog](../CHANGELOG.md).

## Limitations that affect callers

| Boundary | Contract |
| --- | --- |
| Password security | `QpdfDocument.OpenAsync` accepts AES-128 R4 and AES-256 R6 with user/owner text or raw credentials, including empty passwords. RC4 inputs, R5, encrypted transformations, output encryption and argv/job password options remain unsupported. Signing and signature validation are excluded. |
| Signed inputs | Selection, rewrite, placement and JSON publication retain stored appearances but disable active signature field attributes and catalog certification restrictions. Blank signature fields are disabled too. Rewriting never preserves signature validity; read-only inspection keeps original values. |
| JPEG | Decoding/recompression needs a caller-supplied codec. Gray/RGB is supported; CMYK/YCCK stays opaque and is ineligible for optimization. |
| Zopfli | Unavailable; requests fail with exit 2. Ordinary Flate works. `QPDF_ZOPFLI` is treated as unset. |
| Rotation baking | Numeric XYZ/FitR destinations follow the baked frame; Fit/FitB stays unchanged and 180-degree axis fits project their coordinate. Affected nullable positions and quarter-turn H/V/BH/BV fits fail UnsupportedFeature before publication because their general viewer/window state cannot be preserved. |
| Host effects | Logical paths, inputs, descriptors, environment and publication require host capabilities. Fileless results replace ambient stdout. |
| Resource limits | Host ceilings remain binding under `--no-default-limits`. Memory admission bounds an estimated working set, not CLR heap or process RSS. See [ownership](MEMORY.md). |
| Scope | Rendering, OCR, text extraction/layout, general authoring and PDF/A/X/UA conformance are outside the target. |

The inventory has 140 flags in 157 context entries. It separates implemented
behavior from planned requirements, deferred legacy-output options and explicit
exclusions. Security rejection tests establish current refusal only; they do not
establish password-security support. `currentRejection` tracks that refusal
independently of scope. Incomplete entries retain concrete gaps; exclusions have
reasons. Companion fix-qdf/zlib-flate operations are additional contracts.

## Entry-point defaults

| Behavior | Typed | Argv / job JSON |
| --- | --- | --- |
| Reading | Strict | Bounded repair by default; `--suppress-recovery` is strict |
| Stream copying | Opaque encoded bytes | Generalized decoding and compression |
| Rotation requests | Supplied list order | Last value per exact range text, then ordinal key order |
| Generic catalog data | Selection defaults may omit extras | Ordinary rewrites preserve supported primary extras; split has a narrower policy |

Argv setters apply at their position. Job keys use native sorted-key setter order,
including nested files; duplicate JSON properties keep their last value. Compression
and decode setters follow that order; QDF fills unset defaults. Managed extensions
include separate job-file/JSON/split values and bare compression. Unknown options,
invalid combinations and excluded crypto fail explicitly.

## Known differences from native qpdf

These are intentional current policies; exact object numbers or compressed bytes
are generally not compatibility guarantees.

| Area | Managed behavior |
| --- | --- |
| Publication | Single-target commit is transactional. Failed/canceled preparation publishes no partial stdout payload. Split/sidecar targets commit sequentially; earlier successes survive a later failure. |
| Primary document | Its attachment collection survives selection even if none of its pages is selected. Secondary collections need explicit import; page-reachable files still copy. Explicit empty primary differs from a real zero-page input. |
| Split | Retain final document attachments and explicitly preserved unreferenced roots. Foreign page references become null. Explicit/derived labels follow assembled output order before splitting. |
| Graph identity | Preserve same-role sharing/cycles while isolating role-specific replacements from private aliases. Equal contents are not identity. Duplicate pages/widgets have independent headers; distinct pages can retain shared field branches. |
| Catalog and forms | Rebuild page ancestry and required Parent links. Some malformed/unresolvable extras or structure drop under bounded recovery. Combined form dictionaries/children can remain direct. General malformed-field parity is not claimed. |
| Signature fields | Unregister signature fields and turn their widgets into static stamp annotations with the stored appearance (empty when absent). Remove signature types, values, defaults, seed constraints, locks and calculation-order entries, catalog Perms and AcroForm SigFlags. Independent private aliases retain their data; these are not active signatures. |
| Source context | Bind retained secondary/placement URI bases, named destinations and static View optional-content defaults before source cleanup. Same-named layers remain distinct. Unsupported compound layer contexts refuse before output. |
| Navigation | First registered repeated page is canonical. Rotation baking corrects stale target coordinates that native can retain; remote/private roles and unchanged/omitted targets keep their treatment. |
| Annotations | Stored geometry and supported appearances follow page frames, units and NoRotate anchors. Static placement clips to the source page. Popup/reply links stay occurrence-local. This is not a general interactive appearance generator; damaged optional paths can warn/skip. |
| Placement forms | Retain primary calculation order; imported fields do not add CO entries. Source resources/default appearance programs bind independently of target resources. |
| Labels | Interpreted malformed trees fail. Unchanged whole-file trees stay raw. Prefix-only styles ignore inactive counters; equal resolved styles/raw prefixes can compact. |
| Attachments | Preserve raw keys/filespec graphs; listing is a projection. Request matching uses decoded ordinal text, emitted keys sort by encoded bytes. Full-collection operations reject duplicate logical keys; selective extraction keeps first-match lookup. Dates are omitted unless supplied; CreationDate/ModDate inspect independently. |
| PDF JSON | V2 uses binary `b:` strings whenever Unicode would change bytes. V1 object keys use full `n g R` references. Import/update accepts generation zero, validates page structure and excludes PDF-layout/stream/metadata-transform combinations. Dangling imported references become null. |
| Resource pruning | Conservatively retain effective Type 3 and soft-mask dependencies that native can discard. Independent private/metadata consumers keep their original resource graph. |
| Inline images | Externalized Indexed color-space aliases become full XObject names; palette/sample treatment is unchanged. |
| Parser | Integer scalars cover signed 64-bit values. Strict input rejects bare-reference object bodies; repair replaces them with null. Configured nesting/container exhaustion remains a resource failure and cannot silently discard retained metadata. |
| Filters | Flate ends at the first complete zlib frame; RunLength ends at its first end marker. Truncation/checksum failures still reject. Compression write failures abort the destination before codec cleanup, preserving the original error without retrying memory admission. External-file streams remain opaque; no referenced path/URL is opened. |
| Checking | Skipped coverage stays visible. Inspection does not apply every writer removal setting or establish rendering/conformance. Physical linearization defects can fail where native warns. |
| IDs / compression | Default IDs are deterministic content hashes; static IDs use the native test constant. BCL Flate levels map 1/2–8/9 to fastest/optimal/smallest. JPEG quality is 0–100 (development adapter maps 0 to 1). Numbering, packing, predictors and hint bytes may differ. |
| Companions | Fix-QDF requires LF outer syntax, sequential numbering and consistent stream/length framing; ambiguous marker payloads reject. Zlib-flate rejects truncation rather than returning partial output. |

## Resource API and behavior changes

The development API changed before 0.1.0 without compatibility overloads:

- Inputs implement `ReadAsync(offset, Memory<byte>, token)` and return a count;
  the caller owns the admitted destination. Identity, length and bytes stay pinned
  through lease disposal; pending reads finish before buffer reuse or cleanup.
- Hosts expose `IMemoryBudget` through `IQpdfMemoryHost`. Codecs borrow
  `QpdfJpegCodecContext.MemoryBudget` instead of ReserveMemory/ResizeMemory hooks.
  Keep caller scopes alive through escaped results and all asynchronous cleanup.
- `QpdfLimits.MaxWork`, `QpdfExecutionCounters.TotalWork` and
  `QpdfJpegCodecContext.ChargeWork` are removed. Supply deadline/cancellation tokens;
  structural, decoded-byte and memory ceilings remain independent.
- The counter constructor takes seven values: live/peak memory, decoded bytes,
  output bytes, host operations, open handles and diagnostic count.
- Nesting/container exhaustion throws `QpdfLimitException` (BudgetExceeded),
  rather than a parse exception. `MaxNestingDepth` is 1..499, default 256.
- Required malformed source contexts use `QpdfParseException`/InvalidArguments;
  unsupported binding uses UnsupportedFeature. Typed base QpdfException catches
  still work; real host failures retain HostOperationFailed.

See [memory ownership](MEMORY.md) for admission, refusal and publication obligations.

## How compatibility is verified

Ordinary tests are offline and require no native PDF engine. They cover behavior,
failure categories, cancellation/refusal, graph identity, escaped lifetimes and
output faults. Locked Release/package/isolated-consumer gates protect the public
surface and dependency boundary. Explicit reference jobs add native differential,
parser and finite raster controls; syntax alone is not a semantic oracle, and
lossy JPEGs need appropriate comparisons. No timing gate or general rendering
equivalence is claimed. Reference jobs fail when tools are missing.

The pinned source commit is `4eba95899886e851cc41d76886483b347612f2a8`.
Inventory hashes normalize CRLF to LF; reference metadata changes require review.
The official Windows `qpdf-12.4.2-msvc64.zip` SHA-256 is
`db87077e683630c1217e0e8f9a20a9749d952ab676e881c3689187763a5de25d`;
its `bin/qpdf.exe` is
`1bdaf878f37eaffea1ea3c30591214c77636a0f2fc7432427516b6e22e36c0ff`.
Development scripts verify these identities and stop/await timed-out processes.
Native qpdf, renderers, PdfPig and codecs never become core runtime dependencies.
