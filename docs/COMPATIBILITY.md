# Compatibility

Lokad.qpdf targets qpdf **12.4.2** for non-cryptographic PDF structural operations
through typed, argv and job-JSON entry points. Completion means delivering the
agreed [supported profile](SUPPORTED-PROFILE.md), including the limitations below.
Other required compatibility mismatches need a correction or an explicit scope decision.

## Limitations that affect callers

JSON text import and export share PDFDocEncoding conversion. Unicode controls
that occupy repurposed PDF byte slots now use UTF-16 rather than changing into
accent marks. Ordinary Latin-1 text can use PDFDocEncoding and export as `u:`
when the existing binary heuristic permits it; undefined bytes remain `b:`.
No public signatures change.

AcroForm JSON funds decoded names, choice text and ancestor scratch before
materialization, and refunds scratch after each field is copied into the JSON
buffer. Large metadata can therefore refuse earlier. A separate widget reports
its immediate terminal field even when `/FT` is inherited from a group; a merged
widget with field entries reports itself. Parent links, full/partial names,
inherited values and bounded lenient handling of broken parents remain visible.

Page JSON expands an indirect `/Contents` array into its entries, preserving
stream references and order like a direct array. Non-array and malformed entries
remain visible for inspection. Contents iteration borrows array storage. Image
inspection admits descriptor capacity and compares borrowed name bytes using the
existing ordinal display order, without creating strings for each comparison.
Invalid image-resource overrides retain their existing inheritance policy.

| Boundary | Current behavior |
| --- | --- |
| JPEG processing | Grayscale/RGB decoding and recompression require a caller-supplied codec. Existing images can pass through unchanged without one. |
| Four-component JPEG | CMYK/YCCK stays opaque; decoding requests warn, and image optimization leaves it unchanged. This is an accepted limitation of the supported JPEG contract. |
| Zopfli | `--zopfli` reports unavailable and exits 2. Ordinary Flate compression is supported; `QPDF_ZOPFLI` is treated as unset. |
| Crypto | Passwords, encryption/decryption, permission/restriction operations, signing and signature validation are excluded. Crypto options and encrypted inputs, including empty-password inputs, reject without publishing output. |
| Host effects | Inputs, descriptors, environment and publication require host capabilities. Fileless text/binary results replace ambient stdout; companions use explicit host paths. |
| Resource limits | Host ceilings remain binding under `--no-default-limits`. `MaxMemoryBytes` bounds a charged working-set estimate; an optional shared host budget can also refuse admission. Neither guarantees CLR-heap or process-RSS bounds. See [memory ownership and migration scope](MEMORY.md). |
| PDF scope | Rendering, OCR, text extraction/layout and general authoring are outside the target. Rewriting does not establish PDF/A, PDF/X or PDF/UA conformance. |

Typed opening/selection is strict; argv/job loading repairs boundedly by default,
with `--suppress-recovery` selecting strict rejection. Argv/job rewriting defaults
to generalized decoding and compression; typed selection preserves opaque stream
encodings. Compression/decode setters apply in configuration order; QDF fills
unset defaults. These distinctions are part of the supported contract.

## Resource API and behavior changes

Page selection finishes copying catalog labels and form defaults before allocating
added attachments, and coordinates later copies with overlay/appearance objects.
Cold font and default-program references therefore keep their original meaning
when these operations are combined. Output object numbers can change.
QDF page/content comment preparation borrows array entries instead of copying
them; comment numbering and shared-stream first-use behavior are unchanged.

Overlay form reachability admits traversal and reached-set capacities before
allocation. Repeated field references no longer queue a full pending array;
large distinct field sets can refuse earlier. Cancellation interrupts traversal,
including field-array consumption. No public signatures change.
Imported fields retain unchanged headers. Resource unions, changed defaults,
parent repairs and direct-child arrays receive admission before allocation, so
large fixups can refuse earlier while preserving transactional output. Existing
parent/cycle repair and raw default-program behavior remain unchanged.

Appearance text replacement matches the first `/Tx` marked-content block with
bounded `BMC`/`BDC` nesting, preserving unrelated bytes and inline image data.
Malformed programs warn and keep their original appearances; nesting, memory,
decompression and cancellation limits remain fatal. A missing text block appends
a separated `/Tx` block after the unchanged original program.
Generation borrows the copied form's default `/DA` program when no field ancestor
supplies one, including indirect defaults. A selected font from AcroForm `/DR`
is added to absent, empty or indirect appearance resources; local bindings win.
Text without a usable default program warns and preserves its original appearance
instead of emitting text without a font selection. No public signatures change.

Appearance generation preserves skipped buttons in a page's annotation array
when other widgets on that page are updated. Radio buttons, pushbuttons and
buttons without a usable name value keep their existing state and warnings.
Separate and merged widgets update their own terminal field when `/FT` is
inherited, without changing a shared ancestor or unrelated sibling. Indirect
`/FT`, `/V`, `/DA` and `/Ff` values resolve consistently with direct values.
Parent traversal keeps bounded lenient `/Kids` fallback, using one admitted
reverse index per copied graph. Damaged multiple-parent graphs choose the first
indexed parent deterministically. Missing or broken `/Parent` links can use that
fallback; cycles and links without a usable fallback stop traversal.
Indirect widget updates preserve the original annotation array. Direct widgets
use one admitted replacement array, preserving skipped entries and order.
Annotation flattening shares content-entry collection with checking and rotation;
large metadata can refuse before allocation. Appearance-state lookup compares
raw name bytes without decoding all state names.
Overlay and underlay placements now use sequential `/Fx1`, `/Fx2`, ... names
in the fresh page resource dictionary. Names in the original page's resources
remain inside the `/Fx0` Form and do not reserve names in the outer namespace.
This can change generated resource names while preserving the original content
and its resource references. Placement resource storage receives admission.

JSON names use strict UTF-8 scalar validation. Surrogate encodings and values
above Unicode's range now export with `n:` byte escaping, preserving raw name
bytes through import/update instead of replacing them with replacement text.
Checkbox synchronization preserves the raw appearance-state name in `/AS` and
`/V`, including escaped and non-ASCII bytes. Merged widgets update both entries
together; replacement dictionaries receive admission before graph mutation.
Rotation and page-content checking share content-entry collection and accept
indirect arrays. Malformed instructions behind those arrays now fail content
checking just as direct arrays do. Rotation keeps existing unreadable-content
warnings; growing content/annotation containers and traversal sets receive
admission before allocation, so large rotations can refuse earlier.
Page JSON identifies an uncompressed content array from its first value token;
ordinary stream references no longer require materializing their payloads.
Array contents, compressed members, cached values and generation checks retain
the ordinary reader's parsing and ownership rules.

JSON outline inspection rejects cycles through sibling links as well as child
links, with cancellation and nesting bounds independent of cycle detection.
Decoded titles, outline collections and destination indexes receive memory
admission before allocation. Destination indexes are built only for a nonempty
requested outline tree; filtered JSON requests avoid this unused work.
Attachment keys, names, descriptions, dates, checksums and MIME names now receive
admission before decoding or copying. Large metadata may therefore refuse earlier.
Listing results retain independent decoded text after their input closes;
temporary metadata and traversal storage refund on failure or cancellation.
Attachment JSON reads modification dates from `/ModDate`, independently of
`/CreationDate`; a missing field remains null. Full PDF timestamps with signed
time-zone offsets now retain that offset. Date formatting checks bounded syntax
before allocating. Named streams share metadata reading with typed listings.
Page JSON includes nested bookmarks with explicit page destinations in tree
order. An admitted sparse lookup avoids traversing the outline tree per page.
Sequential JSON pages advance through sorted label breakpoints once, preserving
the last duplicate index and arbitrary label values without repeated prefix scans.

Coalescing, normalization and inline-image interpretation now require unfiltered
or successfully decoded content. Unsupported/damaged encodings and filter-count
ceilings preserve the affected content and headers; page operations warn rather
than interpreting opaque bytes as instructions. Memory/decompression limits and
cancellation remain fatal. No public signatures change.
These operations share content-entry collection and accept indirect content
arrays. Stream identities and shared array references survive normalization;
coalescing/externalization remove replaced streams only when no remaining copied
object refers to them. Unreferenced-object preservation remains explicit.
Normalization processes each shared indirect stream version once per phase,
including skipped streams, avoiding repeated buffers and repeated warnings.
Per-page resource operations still run; a replaced stream version is reconsidered.
Form merge name decoding, qualified names, collision storage and renamed bytes
receive admission before materialization. Earlier sources remain the collision
snapshot, including hierarchical and unnamed nodes. Large names may now refuse
before allocating text; PDFDocEncoding/UTF-16 and suffix rules are unchanged.
Resource-array merges borrow unchanged storage. Changed arrays admit their final
capacity before copying, retain existing order/duplicates and append only unseen
supported scalar values; cancellation also interrupts the inner equality search.
Merged page labels use admitted sorted lookup rows and predecessor search;
lookup no longer scans the whole prefix of the tree for each selected page.
Number-tree scratch is admitted and borrows parsed arrays. Selection order,
duplicate breakpoints, reference identity and run compression are unchanged.

Overlay catalog default appearances remain borrowed byte programs through import,
without eager PDF-text decoding or re-encoding. Imported defaults preserve their
original bytes, including non-ASCII names; source ownership transfers before close.
Page-label run comparisons borrow immutable values instead of cloning and
hex-encoding prefixes. Reference number/generation and real-number spelling keep
their existing comparison rules; byte-string syntax does not affect equality.
Overlay sources and destinations share content-entry collection and accept
references to content arrays. Damaged entries still warn and skip stamping;
filter/decompression policy and transactional output are unchanged.
Overlay planning admits sparse destination placements and prepares only the source
pages they use. Unplaced content is neither decoded nor warned about, including
no-op ranges. Source opens and explicit range validation still run; range order,
repeats and duplicate placements are preserved. Public signatures are unchanged.

Overlay annotation rectangles normalize reversed endpoints without losing their
extent. Shared appearance streams receive the placement matrix once per copied
stream; separate placements retain independent copies, including widget forms.
Ordinary indirect annotations share the placement's identity memo, so Popup,
Parent and reply links identify the actual transformed page annotations instead
of additional untransformed clones. This can change output object numbers.
Empty or entirely skipped overlay annotations leave the destination annotation
array unchanged, including an absent `/Annots` entry. Annotation metadata, append
arrays and page/rectangle header rebuilds receive admission before allocation;
large placements may refuse earlier without publishing output.
Unsupported or damaged content encodings now warn and skip the affected overlay;
an unreadable destination remains unchanged. Encoded bytes are never relabeled as
unfiltered Form instructions. Opaque stream copying and raw inspection remain available.
Overlay header changes and annotation clones borrow immutable payloads through
the existing source-to-job ownership transfer. Joining, slice adapters and decoded
scratch receive admission before allocation; temporary decode storage retires
after joining. Large jobs may therefore refuse earlier without publishing output.
Annotation flattening now builds each page's XObject resources once, with admitted
capacity growth and content buffers. Invalid page content is checked before
appearance promotion, so a skipped page keeps its appearances and emits no unused
wrapper streams. Indirect content arrays are accepted. Public signatures are unchanged.
Overlay ranges use the shared grammar, including relative indexes, odd/even
selection and exclusions. Duplicate job JSON keys retain their last value,
including nested overlay keys; invalid effective values reject before output.

Overlay font-conflict rewriting compares decoded name bytes and emits escaped
replacement names. Unchanged names, comments and whitespace retain their original
bytes; an escaped font spelling therefore follows the imported font after a
collision. Rewriting admits bounded storage before allocation and preserves a
malformed appearance unchanged when tokenization fails.

Missing appearances emit only the generated Form stream, without an unreachable
initial placeholder. Field values and choices now receive memory admission before
text/byte materialization; large jobs can refuse earlier while preserving the
same PDFDoc/UTF-16 and ASCII/WinAnsi/MacRoman conversion behavior.

Appearance generation resolves escaped font names by decoded bytes: `/F1` and
`/F#31` select and merge the same resource, including its WinAnsi/MacRoman
encoding. Original appearance spelling, comments and whitespace are preserved
apart from the existing font-size correction. Generated appearance stream bytes
are bounded by `MaxDecodedBytes`; large copies and JPEG candidates can now fail
memory admission before allocation. These corrections add no public signatures.

The resource simplification intentionally removes `QpdfLimits.MaxWork`,
`QpdfExecutionCounters.TotalWork` and `QpdfJpegCodecContext.ChargeWork` without
compatibility overloads. The counter constructor now takes seven values in this
order: live memory, peak memory, decoded bytes, output bytes, host operations,
open handles and diagnostic count. Codec providers use `CheckCancellation` and
`ReserveMemory`; hosts supply deadline cancellation rather than deterministic CPU
fuel. Nesting/container/revision bounds, cycle detection, decoded-byte limits,
memory admission and transactional output remain enforced. Expanded page and
object selections use `MaxContainerSize` for their cardinality bound.

Diagnostic quotas cover actual stored records and UTF-8 message bytes, including
requested summaries. Overflow fails explicitly; it preserves the accepted prefix
and adds one bounded terminal failure outside that quota. Summary records already
accepted before a later failure remain in the cumulative diagnostic counter even
when the returned failure omits those success summaries. `--no-warn` avoids
storing or charging suppressed warnings, while their warning exit status remains
unless `--warning-exit-0` applies. Equal message strings have no identity guarantee.

Verbose/memory summaries capture the processing peak before presentation storage
is allocated. The final `PeakMemoryBytes` counter also includes that storage and
can therefore be higher. Summaries are formatted and admitted before output
publication; completion reuses them without a new allocation or cancellation
check that could turn a successful commit into failure. Each host transaction
retains its existing commit/discard guarantees; multiple targets are not a group
transaction.

## Known differences from native qpdf

These are documented behavior differences, not new waivers. The two accepted
codec/build limitations above remain visible in the inventory. The detailed
[supported profile](SUPPORTED-PROFILE.md) defines operation combinations and
failure behavior; the tables below highlight differences important to callers.

| Area | Difference |
| --- | --- |
| Invocation | Managed syntax also accepts a separate `--job-json-file file`, collation inside a pages block and bare `--no-default-limits`. Job numeric limits accept numbers; native job files use decimal strings. |
| Inspection/output | Diagnostics and payloads are separate results. Failed/canceled jobs discard staged binary output; native stdout may already contain a prefix. Progress uses counted-byte milestones, and diagnostic wording is implementation-defined. Keep-open options are validated hints; host lease policy governs actual handles. |
| Parser limits | Default nesting is 256 versus native 499; invocation overrides remain within the native maximum and host ceilings. Tight container limits fail with exit 2 where native recovery may warn and skip an object. |
| Filters | Partial predictor rows, unknown PNG row selectors, mismatched decode-parameter arrays and preset zlib dictionaries reject instead of using native's lenient handling. Unknown/above-level encodings remain opaque according to the operation's decode policy. |
| Page graphs | Duplicate pages are independent page objects; content-stream sharing can differ. Only the four PDF inheritable page attributes materialize. Omitted-page references become null; page-tree/catalog references are rejected by contained copying. Typed preview drops outlines/structure; ordinary copies preserve them. |
| Catalog/structure | Unresolvable extras drop; extras referring to rebuilt catalog/page-tree nodes may drop instead of remapping. Damaged structure drops leniently. Dangling outline destinations become null; named destinations do not resolve in JSON inspection. |
| Forms | Combined forms can be direct dictionaries, with direct children remaining embedded rather than promoted to indirect objects. Merge default resources are first-source wins. Malformed parent back-links remain on copying paths where native may repair and warn; checking warns on both. General parent-field/default-resource parity is not claimed. |
| Overlay/widgets | Inherited `/UserUnit` is not inverted. Direct-dictionary field parents may remain plain clones where native fails; cyclic parents warn with a valid imported structure. Appearance-local resources preserve rendering while broader remapping remains limited. |
| Appearances/content | Parent-held radios may skip; signature fields skip and pushbuttons warn. Wider/conflicting resource lookup is limited. Damaged content, boxes, appearances or indirect annotations warn and preserve originals or skip transformations rather than emitting native's partial result. |
| Labels/attachments | Malformed label trees fail instead of warning. Attachment dates are omitted unless supplied; names use ASCII/UTF-16 encoding and trees use flat leaves chunked after 64 entries. Empty attachment-copy prefixes reject instead of copying unchanged. |
| PDF JSON | Import/update accepts generation 0 only, rejects pushed inherited attributes, and excludes stream/metadata writer combinations. Version 1 ignores stream payloads; `--json-output=1` rejects. Creation/update renumbers output to classic files. |
| Unreferenced objects | Copy order and container handling can differ; object/xref-stream members are preserved through rebuilt containers. Unloadable objects skip under one aggregated warning instead of failing the job. |
| Companions | `FixQdfAsync` rejects carriage returns, empty/truncated structures and nonsequential numbering. `RunZlibFlateAsync` rejects truncation instead of returning partial output, and accepts compression levels 1–9 only. |
| Linearization checking | Physical defects fail with exit 2 where native may warn with exit 3. Show-linearization retains parsed raw fields on physical failure. |

Physical output can also differ while preserving PDF meaning:

- Default document IDs are deterministic content hashes, rather than native's
  time-based IDs; `--static-id` uses the native test constant. Unspecified
  attachment timestamps are omitted because runtime code has no ambient clock.
- BCL Flate levels map `1` to fastest, `2–8` to optimal and `9` to smallest size.
  JPEG providers receive quality `0–100`; the development ImageSharp adapter maps
  `0` to `1`. Compressed bytes and lossy samples need their appropriate comparisons.
- Object numbering/grouping follows managed packing rules. Rebuilt catalogs and
  page-tree roots stay direct; QDF/linearized exclusions remain explicit. Ordinary
  compressed xrefs use plain Flate without native's PNG predictor; linearized
  xref streams stay unfiltered. Hint bytes describe each output's physical layout.

## Supported operations and evidence

| Family | Implemented scope | Detailed contract |
| --- | --- | --- |
| Reading | Classic, streamed, hybrid and incremental xrefs; object streams; strict parsing and bounded recovery | [Input profile](SUPPORTED-PROFILE.md#input-profile) |
| Pages | Ordered/duplicate selections, ranges, merge, collation, rotation, labels, split and replacement | [Selection](SUPPORTED-PROFILE.md#selection-semantics) |
| Inspection | Checking, object/xref/page/attachment listings, fileless payloads and PDF JSON inspection/import/update | [Inspection](SUPPORTED-PROFILE.md#inspection-profile) |
| Content/images | Normalization, coalescing, conservative pruning, inline externalization, overlays, appearances, flattening and grayscale/RGB JPEG processing | [Content](SUPPORTED-PROFILE.md#page-content-profile), [selection](SUPPORTED-PROFILE.md#selection-semantics) |
| Writing | Version/ID controls, stream compression, object streams and unreferenced-object policy | [Writer](SUPPORTED-PROFILE.md#writer-profile) |
| Attachments | List, extract, add, remove and copy | [Attachments](SUPPORTED-PROFILE.md#attachments-profile) |
| Companions/linearization | QDF, managed fix-qdf/zlib-flate and linearization check/show/two-pass writing | [QDF and linearization](SUPPORTED-PROFILE.md#qdf-and-linearization-profile) |

The canonical [option inventory](../tests/Compatibility/options.json) records
forms, contexts, choices, JSON mappings, scope, remaining gaps and executable
evidence. It contains 140 flags in 157 option/context entries: 106 required
entries complete, 49 crypto exclusions complete, and two explicit partial entries
(`--optimize-images` for CMYK and `--zopfli`). These counts describe the current
inventory, not test or release quotas. Completion of parsing alone is insufficient.
The fix-qdf/zlib-flate companions are additional contracts outside the flag count.

[CompatibilityInventoryTests](../tests/Lokad.Qpdf.Tests/CompatibilityInventoryTests.cs)
checks inventory identity, scope, evidence and visible partial-entry gaps.
`tools/update-compatibility.py` reads pinned `job.yml` and `manual/cli.rst` metadata
while preserving reviewed status/evidence. Metadata does not prove defaults,
precedence, grammar or interactions; those require behavior fixtures.

## How compatibility is verified

Reference checks compare fresh managed/native outputs under the original input
and budget controls. Stable inspection text, decoded bytes, page order, sharing,
field relationships, error/warning exits and output integrity have mechanism-
appropriate assertions. Native syntax/linearization checks and the independent
PdfPig reader supplement exact parser and payload comparisons. Skipped checker
stages remain explicit; an unavailable check is not a pass.

The original 64 differing hint-payload pairs remain recorded. Their layouts are
validated semantically and against physical object boundaries; differing bytes
alone do not establish a defect. Historical checker false passes retain negative
regressions. Finite visual cases check placement, rotation, appearances and JPEG
optimization with fixed tolerances and deliberate output-defect controls.

Ordinary tests require no native tools. During implementation, run focused public
jobs and affected reference checks. Comprehensive reference, visual and fresh
package-consumer checks belong at integration/release checkpoints; repeat for
changed behavior, failures or a specific unresolved concern. Historical case
identities, counts, exact CLR receipts and prescribed GC rounds add no obligations.
Practical byte/structural/decompression limits, cancellation, cleanup and actual memory
growth remain protected by the small representative workload set.
See [contributor checks](../AGENTS.md) for verification commands and
[benchmarks](BENCHMARKS.md) for performance measurements and artifact identities.

## Codec implementation notes

The core has no runtime package dependencies. JPEG decoding/recompression requires
a caller-supplied `IQpdfJpegCodec`, exposed by the host's `IQpdfJpegCodecHost`
capability. Without it, opaque JPEG copying works, checks report decoding skipped,
and eligible image optimization fails before publication. Frame dimensions and
component counts are checked before sample allocation; the codec context admits
private memory and provides cancellation. Providers must observe the
[borrowed-buffer and cleanup contract](../src/Lokad.Qpdf/Public/IQpdfJpegCodec.cs).

The development-only [ImageSharp 3.1.12 adapter](../tools/Lokad.Qpdf.ImageSharp)
preserves the existing grayscale/RGB behavior. Its pixel API does not preserve raw
CMYK/YCCK components, so RGB conversion is not substituted for PDF sample fidelity.
The five temporary advisory exclusions and reachability assessment remain beside
their declarations in [Directory.Build.props](../Directory.Build.props).

Flate validates zlib framing and Adler-32 because BCL inflate can return short
output for truncated data. Codec calls/refills are bounded in 8 KiB chunks. The
current state allowances are 512 KiB for deflate and 128 KiB for inflate, with
scratch/output growth charged separately. Their rationale combines
[zlib's approximate state model](https://zlib.net/zlib_tech.html) (about 268 KiB
compression/40 KiB inflation) and the
[.NET 10 stream buffer](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.IO.Compression/src/System/IO/Compression/DeflateZLib/DeflateStream.cs)
(8 KiB). These are coarse admission estimates; BCL pools can retain returned
buffers. Unreleased charges and pooled storage do not by themselves prove a leak
or a process-memory bound.

## Pinned reference provenance

The target is [qpdf 12.4.2](https://github.com/qpdf/qpdf), commit
`4eba95899886e851cc41d76886483b347612f2a8`; changing it requires explicit review.
The official Windows `qpdf-12.4.2-msvc64.zip` reports `qpdf version 12.4.2`, has
statically linked libjpeg support and no optional Zopfli support (requesting it
exits 2).

| Artifact | SHA-256 |
| --- | --- |
| `qpdf-12.4.2-msvc64.zip` | `db87077e683630c1217e0e8f9a20a9749d952ab676e881c3689187763a5de25d` |
| `bin/qpdf.exe` | `1bdaf878f37eaffea1ea3c30591214c77636a0f2fc7432427516b6e22e36c0ff` |

The development-only oracle lives under ignored `external/oracle/`; setup and
version/hash validation uses [bootstrap-qpdf.ps1](../tools/bootstrap-qpdf.ps1).
Missing tools fail explicit reference jobs. Native qpdf, PdfPig and renderers
never become production dependencies.
