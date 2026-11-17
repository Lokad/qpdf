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
| Signed inputs | Selection, rewrite, placement and JSON publication retain stored appearances but disable active signature field attributes and catalog certification restrictions. Blank signature fields are disabled too. Rewriting never preserves signature validity. `--remove-restrictions`/`removeRestrictions` explicitly selects this default PDF publication policy; combining it with non-predicate inspection or JSON export rejects. Encryption predicates take precedence and ignore this modifier (see [predicate policy](SUPPORTED-PROFILE.md#reports-and-predicates)). Read-only inspection keeps original values. |
| Password encoding recovery | The [authentication profile](SUPPORTED-PROFILE.md#authentication) defines representations and retries. Malformed UTF-16 is rejected, while native recovery can discard its bytes. WinAnsi 0x98 follows [the standard U+02DC mapping](https://www.unicode.org/Public/MAPPINGS/VENDORS/MICSFT/WINDOWS/CP1252.TXT); pinned native recovery maps it to U+0303. |
| Legacy security | R3 accepts both full-digest and key-length owner hashing for short keys. Preservation retains the original password entries; native qpdf and Poppler reject full-digest owners on input and output, while the managed reader accepts them. V2 without Length uses the specified 40-bit default; native qpdf guesses 128 bits. |
| Encryption predicates | `is-encrypted` uses 0/2 for encrypted/plain input; `requires-password` uses 0/2/3 for credentials needed/plain input/credentials matched. Completed query codes are independent of warning presentation. Unsupported handlers fail explicitly. |
| Crypto implementation | Standalone `--show-crypto` reports `managed`. Names describe the implementation in this build; native qpdf can list different providers. |
| Encryption reporting | `show-encryption` reports non-secret declared settings and password-match status. Wrong credentials permit reporting but skip combined content inspections. Reports omit passwords/keys and include per-filter cipher names only for V4/V5 handlers; explicit output paths are rejected. |
| Encrypted JSON | Authenticated JSON exports decrypted values and selected stream payloads. The encryption section keeps password/key fields null. Typed and command updates authenticate the original PDF and apply plaintext object patches using an explicit preserve/remove/create/template output policy. Typed and command creation protects plaintext JSON through a fresh AES-256 or authenticated template policy. Preservation validates a repeated handler against the authenticated PDF. Creation from a JSON `/Encrypt` declaration requires an explicit remove/create/template policy; JSON alone cannot authenticate a key. |
| JPEG | Decoding/recompression needs a caller-supplied codec. Gray/RGB is supported. CMYK/YCCK decoding requires an explicit host capability and yields raw PDF CMYK samples, preserving the PDF color space and Decode array. Four-component recompression remains ineligible. |
| Zopfli | Unavailable; requests fail with exit 2. Ordinary Flate works. `QPDF_ZOPFLI` is treated as unset. |
| Rotation baking | Numeric XYZ/FitR destinations follow the baked frame; Fit/FitB stays unchanged and 180-degree axis fits project their coordinate. Affected nullable positions and quarter-turn H/V/BH/BV fits fail UnsupportedFeature before publication because their general viewer/window state cannot be preserved. |
| Host effects | Logical paths, inputs, descriptors, environment and publication require host capabilities. Fileless results replace ambient stdout. |
| Resource limits | Host ceilings remain binding under `--no-default-limits`. PDF container and nesting ceilings also apply to JSON imports, where native can ignore them. Memory admission bounds an estimated working set, not CLR heap or process RSS. See [ownership](MEMORY.md). |
| Scope | Rendering, OCR, text extraction/layout, general authoring and PDF/A/X/UA conformance are outside the target. |

The inventory has 140 flags in 157 context entries. It separates implemented
behavior from planned requirements, deferred legacy-output options and explicit
exclusions. Security rejection tests establish current refusal only; they do not
establish password-security support. `cryptoSyntax` identifies security option
names used for parser diagnostics;
it does not indicate support or rejection. Completion requires positive behavior,
resource/failure controls and native evidence, independently of that syntax marker.
Incomplete entries retain concrete gaps; exclusions have
reasons. Companion fix-qdf/zlib-flate operations are additional contracts.

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
| Rotation requests | Supplied list order | Last value per exact range text, then ordinal key order |
| Generic catalog data | Selection defaults may omit extras | Ordinary rewrites preserve supported primary extras; split has a narrower policy |

Argv setters apply at their position. Job keys use native sorted-key setter order,
including nested files; duplicate JSON properties keep their last value. Semantic
validation uses effective values; malformed string encoding still rejects even
in a superseded value. Compression
and decode setters follow that order; QDF fills unset defaults. Managed extensions
include separate job-file/JSON/split values and bare compression. Unknown options,
invalid combinations and excluded crypto fail explicitly.

Repeated scalar/bare argv setters are accepted, including warning, recovery,
version and keep-open hints.
Input/output/pages/replace declarations and repeated `--empty` remain exclusive;
main-context parser ceilings also remain exclusive. Stream-filter setters and
`--global` blocks keep setter order. Block-local duplicate rules remain explicit.

Single-input typed extraction and first/last previews can supply
`QpdfRecoveryMode.BoundedRepair` to `SelectPageAsync`; existing overloads stay
strict. The reader uses the same bounded syntax/xref repair as commands, including
rebuilding a table with a zero-entry subsection. `PdfPageSelectionResult.Warnings`
reports recovery. Streams remain opaque, and source bytes, passwords, limits and
transactional publication keep their existing contracts. The full recovery overload
also accepts rotations, an input password and an output-encryption policy.

Typed merging/splitting selects the same policy through
`PdfPageMergeOptions.RecoveryMode`. It defaults to Strict and applies to every
source and encryption template. Repeated paths share one opened document;
credential consistency, source mappings, warnings and output protection retain
their existing contracts. Undefined recovery values reject before host access.

Document opening accepts an explicit recovery policy with credentials/limits;
typed checking and JSON inspection use their options' `RecoveryMode`. Returned
document warnings, check diagnostics and JSON warnings report repair. Typed JSON
can return its report without publishing it; file sidecars still require an
explicit prefix. See the [inspection profile](SUPPORTED-PROFILE.md#inspection-profile).

```csharp
PdfPageSelectionResult preview = await Qpdf.SelectPageAsync(
    host, input, output, PdfPageSelectionOptions.FirstAndLast(2),
    QpdfRecoveryMode.BoundedRepair, cancellationToken);
```

Typed attachment list/extract/add/remove/copy have full overloads accepting
`QpdfRecoveryMode`. The policy applies to primary, copy-source and encryption-template
readers; existing overloads remain strict. Explicit listing returns
`QpdfAttachmentListResult` with immutable metadata, warnings and closed-handle counters.
Typed extraction closes its input before publication, retaining payload backing through commit.

## Known differences from native qpdf

These are intentional current policies; exact object numbers or compressed bytes
are generally not compatibility guarantees.

| Area | Managed behavior |
| --- | --- |
| Page counts | Typed `CountPagesAsync` returns the traversed page count and reader warnings. Commands retain native-style declared `/Count` reporting, including existing malformed-count behavior after complete acquisition. Standalone bounded counting accepts a declaration only after page-tree validation; combined inspections and transforms keep their complete-input path. |
| Writer encryption defaults | Preserve primary protection in every writer. Native qpdf clears default protection in QDF and split modes unless an explicit encryption or template policy reapplies it. |
| Publication | Single-target commit is transactional. Failed/canceled preparation publishes no partial stdout payload. Split/sidecar targets commit sequentially; earlier successes survive a later failure. |
| Split target names | Every expanded name must differ from the real primary and every selected input, including when the primary contributes only catalog/security policy. Alias rejection precedes output creation. |
| First-pass capture | Protect document/attachment inputs, the effective encryption template, read password files and consumed job/response files from capture replacement. Native can overwrite auxiliary inputs after reading them. Inactive captures and superseded templates retain their existing behavior. |
| Primary document | Its attachment collection survives selection even if none of its pages is selected. Secondary collections need explicit import; page-reachable files still copy. Explicit empty primary differs from a real zero-page input. |
| Split | Retain final document attachments and explicitly preserved unreferenced roots; native split output omits the document attachment collection. Foreign page references become null. Explicit/derived labels follow assembled output order before splitting. |
| Graph identity | Preserve same-role sharing/cycles while isolating role-specific replacements from private aliases. Equal contents are not identity. Duplicate pages/widgets have independent headers; distinct pages can retain shared field branches. |
| Catalog and forms | Rebuild page ancestry and required Parent links. Some malformed/unresolvable extras or structure drop under bounded recovery. Combined form dictionaries/children can remain direct. General malformed-field parity is not claimed. |
| Signature fields | Unregister signature fields and turn their widgets into static stamp annotations with the stored appearance (empty when absent). Remove signature types, values, defaults, seed constraints, locks and calculation-order entries, catalog Perms and AcroForm SigFlags. Independent private aliases retain their data; these are not active signatures. |
| Source context | Bind retained secondary/placement URI bases, named destinations and static View optional-content defaults before source cleanup, including interpreted OFF bases. Combined defaults normalize to ON; same-named layers remain distinct. Unsupported compound layer contexts refuse before output. |
| Navigation | First registered repeated page is canonical. Rotation baking corrects stale target coordinates that native can retain; remote/private roles and unchanged/omitted targets keep their treatment. |
| Annotations | Stored geometry and supported appearances follow page frames, units and NoRotate anchors. Static placement clips to the source page. Popup/reply links stay occurrence-local. This is not a general interactive appearance generator; damaged optional paths can warn/skip. |
| Placement forms | Retain primary calculation order; imported fields do not add CO entries. Source resources/default appearance programs bind independently of target resources. |
| Labels | Interpreted malformed trees fail. Unchanged whole-file trees stay raw. Prefix-only styles ignore inactive counters; equal resolved styles/raw prefixes can compact. Label removal applies to split outputs; native split can retain primary-document labels despite `--remove-page-labels`. |
| Attachments | Preserve raw keys/filespec graphs; listing is a projection. Request matching uses decoded ordinal text, emitted keys sort by encoded bytes. Full-collection operations reject duplicate logical keys; selective extraction keeps first-match lookup. Typed and argv/job extraction authenticate R2–R6 inputs and publish plaintext payloads, including to explicit host output paths. Dates are omitted unless supplied; CreationDate/ModDate inspect independently. |
| PDF JSON | V2 uses binary `b:` strings whenever Unicode would change bytes. V1 object keys use full `n g R` references. Object tables classify live pairs using source xrefs; native can materialize dead trailer pairs as explicit null objects. Page assembly preserves source labels and the selected sources' version floor, matching PDF selection; direct native JSON can retain primary label positions and version instead. Import/update matches full number/generation pairs and validates page structure; object streams and linearization share the physical writers, while other layout/content/stream/metadata transforms remain excluded. Dangling references become null; newly declared pairs do not revive stale source edges. Distinct declared generations retain independent values, while native JSON publication can alias generations sharing an object number. |
| Resource pruning | Conservatively retain effective Type 3 and soft-mask dependencies that native can discard. Independent private/metadata consumers keep their original resource graph. |
| Inline images | Externalized Indexed color-space aliases become full XObject names; palette/sample treatment is unchanged. |
| Parser | Integer scalars cover signed 64-bit values. Strict input rejects bare-reference object bodies; repair replaces them with null. Configured nesting/container exhaustion remains a resource failure and cannot silently discard retained metadata. |
| Filters | Flate ends at the first complete zlib frame and rejects truncation/checksum failures. RunLength tolerates truncated packets and missing end markers. Compression write failures abort the destination before codec cleanup, preserving the original error without retrying memory admission. External-file streams remain opaque; no referenced path/URL is opened. |
| Plaintext Crypt stages | Copying consumes valid leading default/Identity stages before content transforms. JSON-authored stages normalize before output encryption. Remaining encodings and private filter aliases survive; unknown or malformed stages cannot be treated as plaintext for encryption. |
| Xref-tagged graph streams | Retained `/Type /XRef` streams use ordinary graph encryption and remain present in QDF; physical xref sections stay clear. Native raw JSON can return their ciphertext, and native QDF can discard them, leaving a bad reference. Independent header/AES controls cover these differences. |
| Checking | Skipped coverage stays visible. Inspection does not apply every writer removal setting or establish rendering/conformance. Physical linearization defects can fail where native warns. |
| Image JSON | Decode-none reports retain raw filter specifications with `filterable:false`, including malformed decoder settings. Enabled decoding retains strict specification validation; native can warn and report the descriptor instead. |
| IDs / compression | Default IDs are deterministic content hashes; static IDs use the native test constant. BCL Flate levels map 1/2..8/9 to fastest/optimal/smallest. JPEG quality is 0..100 (development adapter maps 0 to 1); equal settings across codecs need not produce equivalent quality. Numbering, packing, predictors and hint bytes may differ. |
| Linearized outlines | Outline closures stay outside object streams so hint spans remain contiguous. Pinned native packing can split a root from later outline containers and warn about its own offsets/lengths; the reference controls retain that failure and compare a valid classic equivalent. |
| Companions | Fix-QDF requires LF outer syntax, sequential numbering and consistent stream/length framing; ambiguous marker payloads reject. Zlib-flate rejects truncation rather than returning partial output. |

Rotation destination limits follow the viewer-dependent semantics in
[ISO 32000-1, 12.3.2.2 and Table 151](https://opensource.adobe.com/dc-acrobat-sdk-docs/standards/pdfstandards/pdf/PDF32000_2008.pdf).
A null position retains a prior viewer coordinate; it is not zero. For example,
the tested 90-degree transform maps `y' = 120 - x`. Prior horizontal positions
25 and 75 require new vertical positions 95 and 45. Neither a constant nor a
null retaining the old vertical position expresses that dependency. Numeric
XYZ/FitR values can be projected directly.

Changing an axis-fit destination from FitH to FitV also changes its zoom rule.
For a 200-by-100 page in a 600-by-400 window, width fit gives zoom 3 while
height fit gives zoom 4. A quarter-turn conversion cannot generally preserve
both the requested coordinate and the original fit rule for arbitrary windows.
The existing fixtures therefore retain explicit refusals for these cases,
while covering numeric destinations, Fit/FitB, 180-degree axis fits and
navigation discarded by splitting. Raster checks establish page appearance,
not interactive viewer navigation.

JPEG quality is a codec setting, not a cross-encoder equivalence guarantee.
The development adapter passes quality 1..100 to Lokad.Image 0.2.0, whose color
quantizers differ from native qpdf's libjpeg quantizers. The authored RGB-noise
case at quality 75 has normalized cross-encoder mean error 0.052373, exceeding
the existing 0.04 comparison limit. Both outputs pass the unchanged comparison
against the original image. The visual report records this as
`documented-codec-difference`, not a passing native comparison, and requires
a missing-image negative control to fail those original-image bounds.

## Resource APIs

`CountPagesAsync` provides a typed count without exposing a document or object
resolver. Document opening acquires complete input. The count API and standalone
`--show-npages` / `showNpages` share bounded acquisition for
compact classic-xref PDFs with large unrelated objects. The page tree is traversed;
reading `/Count` alone is insufficient. This validates count metadata, rather than
every unrelated object or content stream.

Small inputs, protected documents, incremental/hybrid/streamed xrefs, oversized
metadata and uncertain required ranges use complete acquisition on the same
opened lease. Declared-count mismatches also require complete input before the
command's existing reporting policy applies. Short reads and path replacement
retain their lease semantics. Memory refusal fails without retrying another
storage strategy. See [memory bounds](MEMORY.md#engine-guarantees-and-limits).

`QpdfExecutionCounters.HostOperations` observes executed
opens, reads and writes; it is not an allowance. Short-read hosts can perform more
calls than complete-read hosts without exhausting a cumulative quota. Publication
admits actual wrapper/staging storage rather than predicting calls, and selective
reading depends on input structure and memory. Use cancellation/deadlines for host
work; input/output, decoded bytes, memory, handles and structural limits still apply.

The input and resource interfaces have these contracts:

- Inputs implement `ReadAsync(offset, Memory<byte>, token)` and return a count;
  the caller owns the admitted destination. Identity, length and bytes stay pinned
  through lease disposal; pending reads finish before buffer reuse or cleanup.
- Hosts expose `IMemoryBudget` through `IQpdfMemoryHost`. Codecs borrow
  `QpdfJpegCodecContext.MemoryBudget`.
  Keep caller scopes alive through escaped results and all asynchronous cleanup.
- Supply deadline/cancellation tokens for work limits; structural, decoded-byte
  and memory ceilings remain independent.
- The counter constructor takes seven values: live/peak memory, decoded bytes,
  output bytes, host operations, open handles and diagnostic count.
- Nesting/container exhaustion throws `QpdfLimitException` (BudgetExceeded).
  `MaxNestingDepth` is 1..499, default 256.
- Required malformed source contexts use `QpdfParseException`/InvalidArguments;
  unsupported binding uses UnsupportedFeature. Host failures use HostOperationFailed.

See [memory ownership](MEMORY.md) for admission, refusal and publication obligations,
and the [changelog](../CHANGELOG.md) for migration history.

## How compatibility is verified

Ordinary tests are offline and require no native PDF engine. They cover behavior,
failure categories, cancellation/refusal, graph identity, escaped lifetimes and
output faults. Locked Release/package/isolated-consumer gates protect the public
surface and dependency boundary. Explicit reference jobs add native differential,
parser and finite raster controls; syntax alone is not a semantic oracle, and
lossy JPEGs need appropriate comparisons. No timing gate or general rendering
equivalence is claimed. The security matrix compares R2–R6 query exits and
classic, object-stream, QDF, linearized and decrypted outputs through argv and
job JSON. It also compares fresh AES-256, independent templates, raw/UTF-8/
PDFDocEncoding passwords, host password files and non-secret reports. Native
checks authenticate both roles and verify policy. Encrypted inline/sidecar JSON
exports cross into native and managed update/creation with changed values and
explicit preserve/remove/new/template policies; decrypted
controls compare page/metadata payloads and independent-reader page counts.
Source cases use distinct encrypted text and credentials, compare independent
ordered page text, and reject wrong secondary passwords before managed publication.
Reference jobs fail when tools are missing.

The pinned source commit is `4eba95899886e851cc41d76886483b347612f2a8`.
Inventory hashes normalize CRLF to LF; reference metadata changes require review.
The official Windows `qpdf-12.4.2-msvc64.zip` SHA-256 is
`db87077e683630c1217e0e8f9a20a9749d952ab676e881c3689187763a5de25d`;
its `bin/qpdf.exe` is
`1bdaf878f37eaffea1ea3c30591214c77636a0f2fc7432427516b6e22e36c0ff`.
Development scripts verify these identities and stop/await timed-out processes.
Native qpdf, renderers, PdfPig and codecs never become core runtime dependencies.
