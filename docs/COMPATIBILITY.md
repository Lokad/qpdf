# Compatibility

Lokad.qpdf targets qpdf **12.4.2** for non-cryptographic PDF structural operations
through typed, argv and job-JSON entry points. The [supported profile](SUPPORTED-PROFILE.md)
defines operation combinations and limitations; the [option inventory](../tests/Compatibility/options.json)
records their evidence. A roadmap entry or accepted option does not establish
implemented behavior. Completed correction history is in the [changelog](../CHANGELOG.md).

## Limitations that affect callers

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

Page-copy operations, including overlay/underlay imports, reject reachable
signature dictionaries (`/Type /Sig`) and populated signature fields in widget
or retained AcroForm contexts. Field types may be inherited or indirect, and the
signature value need not have a type tag. Typed calls throw `QpdfCryptoException`
with `/Sig`; argv/job JSON report `UnsupportedCryptoOperation`. Blank fields
(absent/null values) and read-only inspection remain supported; no signature is
verified. Native qpdf 12.4.2 retains the reduced signed fixture's metadata; managed
copying deliberately rejects it. Private dictionaries using `V` and `Parent` do
not establish field context. Source catalog/page-tree containment and omitted-page
pruning apply independently.

DCT decoding accepts absent/null `DecodeParms` only. Parameterized DCT, including
empty or indirect parameter dictionaries, stays opaque with warnings in rewriting
and optimization; direct decode requests fail explicitly. This PDF policy is
independent of the supplied codec's wider capabilities.

A single failed output commit preserves its prior target. Multiple outputs commit
sequentially; a later failure or post-commit close fault does not undo earlier
publications. JSON main output and stream sidecars follow the same rule. Prepared
PDF and JSON bytes pass their known lengths through an optional host capacity
hint. Hosts retain their own admission and publication authority.

The built-in memory host stores registered inputs separately from published
outputs, including equal-path companion calls and `--replace-input`. Opening an
input reads its registration until the caller exports and registers the new bytes.

## Resource API and behavior changes

The **0.1.0-alpha.2** host integration deliberately changes these alpha APIs:

- `IQpdfInput.ReadAsync` fills caller-supplied `Memory<byte>` and returns a byte
  count. Use nonallocating `ReadExactlyAsync` with already admitted storage, or
  explicitly budgeted `ReadExactlyToArrayAsync` when an owned array is needed.
  Keep the input identity and backing stable through disposal; finish each read
  before reusing its destination, starting another read or disposing the lease.
- `QpdfJpegCodecContext.MemoryBudget` replaces `ReserveMemory`/`ResizeMemory`.
  Codecs create child scopes and reserve/resize through the shared interfaces.
  Growth receives admission before allocation; refusal preserves the previous
  estimate. Shrink and disposal remain available after cancellation. All workers,
  private storage and borrowed aliases must finish before the codec returns.
- The read-only JPEG decode stream exposes its already admitted input segment
  through `MemoryStream.TryGetBuffer`. Borrow only the offset/count, keep those
  bytes immutable, and release aliases before returning. Other adapter input
  streams still use an admitted copy. Codec signatures are unchanged.
- `QpdfLimits.MaxWork`, `QpdfExecutionCounters.TotalWork` and
  `QpdfJpegCodecContext.ChargeWork` are removed without compatibility overloads.
  The counter constructor takes seven values: live memory, peak memory, decoded
  bytes, output bytes, host operations, open handles and diagnostic count.
  Use cooperative cancellation, including `CheckCancellation` inside codec work;
  hosts supply deadline tokens instead of cumulative CPU fuel.

Local `QpdfLimits` and an optional host shared allowance remain independent
admission controls. Significant storage and growth overlap receive admission;
refusal can precede malformed data discovered later in a decode stage. Estimates
are not measurements of the CLR heap or process RSS. Host buffers, concurrent
jobs and caller-retained values require host ownership policy. See [memory and
host ownership](MEMORY.md) for durable versus invocation owners and refunds.

`MaxNestingDepth` accepts **1..499**, default **256**, for public and invocation
settings. Parent-field checks, widget imports and parser containers retain their
structural checks and cycle guards; cached ancestors do not bypass the configured
ceiling. Expanded page/object selections use `MaxContainerSize` for cardinality.
Host ceilings remain binding when invocation defaults are lifted.

`MaxOutputBytes` is cumulative across a job's outputs, including prepared bytes
abandoned on failure. Typed domain-result operations throw on cancellation;
execution entry points return a canceled result. Cleanup completes pending I/O
before releasing backing or leases. Returned immutable values and independently
exported copies stay charged under their caller owners after document disposal.

Diagnostics store actual records and UTF-8 message bytes under their quotas,
including requested summaries. Overflow preserves the accepted prefix and adds
one bounded terminal failure outside the quota. `--no-warn` suppresses warning
storage while retaining warning exit status unless `--warning-exit-0` applies.
Message string identity is unspecified. Accepted success summaries remain in the
cumulative diagnostic counter even when a later failure omits them from its list.

Verbose/memory summaries capture the processing peak before presentation storage.
The final `PeakMemoryBytes` can include a higher presentation peak. Summaries and
result storage are admitted before publication and reused after commit, so result
construction cannot newly refuse a successfully committed output.

## Known differences from native qpdf

These are documented behavior differences, not new waivers. The two accepted
codec/build limitations above remain visible in the inventory. The detailed
[supported profile](SUPPORTED-PROFILE.md) defines operation combinations and
failure behavior; the tables below highlight differences important to callers.

| Area | Difference |
| --- | --- |
| Invocation | Managed syntax also accepts a separate `--job-json-file file`, collation inside a pages block and bare `--no-default-limits`. Job numeric limits accept numbers; native job files use decimal strings. |
| Inspection/output | Diagnostics and payloads are separate results. Failed/canceled jobs discard staged binary output; native stdout may already contain a prefix. Progress uses counted-byte milestones, and diagnostic wording is implementation-defined. Keep-open options are validated hints; host lease policy governs actual handles. |
| Parser limits | Managed nesting settings accept 1..499 (default 256), within host ceilings. Native defaults to 499 and accepts zero as a depth cap; managed zero overrides reject as `InvalidArguments`. Tight container limits fail with exit 2 where native recovery may warn and skip an object. |
| Filters | Partial predictor rows, unknown PNG row selectors, mismatched decode-parameter arrays and preset zlib dictionaries reject instead of using native's lenient handling. Unknown/above-level encodings remain opaque according to the operation's decode policy. |
| Image identities | Managed optimization preserves shared image references and attempts each image once. Native qpdf may replace resource aliases with separate image copies. JPEG generations, work and byte charges therefore follow managed object identities. |
| Page graphs | Duplicate pages are independent page objects; content-stream sharing can differ. Only the four PDF inheritable page attributes materialize. Omitted-page references become null; page-tree/catalog references are rejected by contained copying. Typed preview drops outlines/structure; ordinary copies preserve them. |
| Catalog/structure | Unresolvable extras drop; extras referring to rebuilt catalog/page-tree nodes may drop instead of remapping. Damaged structure drops leniently. Dangling outline destinations become null; named destinations do not resolve in JSON inspection. |
| Forms | Combined forms can be direct dictionaries, with direct children remaining embedded rather than promoted to indirect objects. Merge default resources are first-source wins. Malformed parent back-links remain on copying paths where native may repair and warn; checking warns on both. General parent-field/default-resource parity is not claimed. |
| Overlay/widgets | Inherited `/UserUnit` is not inverted. Direct-dictionary field parents may remain plain clones where native fails; cyclic parents warn with a valid imported structure. Appearance-local resources preserve rendering while broader remapping remains limited. |
| Appearances/content | Parent-held radios may skip; empty signature fields skip and pushbuttons warn. Populated signature copying rejects explicitly. Wider/conflicting resource lookup is limited. Damaged content, boxes, appearances or indirect annotations warn and preserve originals or skip transformations rather than emitting native's partial result. |
| Labels/attachments | Malformed label trees fail instead of warning. Attachment dates are omitted unless supplied; names use ASCII/UTF-16 encoding and rebuilt trees use one flat name array. Empty attachment-copy prefixes reject instead of copying unchanged. |
| PDF JSON | Import/update accepts generation 0 only, rejects pushed inherited attributes, and excludes stream/metadata writer combinations. Version 1 ignores stream payloads; `--json-output=1` rejects. Creation/update renumbers output to classic files. Unlike native qpdf 12.4.2, malformed, mistyped and unsupported catalog `/Version` values reject before output opens, including indirect values; this avoids publishing PDFs the managed reader cannot open. Dangling imported references still normalize to null. Valid catalog overrides remain intact even when `--force-version` lowers the JSON output header. |
| Unreferenced objects | Copy order and container handling can differ; object/xref-stream members are preserved through rebuilt containers. Unresolvable roots and contained-copy failures skip under one aggregated warning; strict load errors remain fatal. Managed split output retains preserved roots that native split omits. |
| Companions | `FixQdfAsync` requires LF outer syntax and rejects empty/truncated structures and nonsequential numbering; binary stream/xref carriage returns are preserved. `RunZlibFlateAsync` rejects truncation instead of returning partial output, and accepts compression levels 1–9 only. |
| Linearization checking | Physical defects fail with exit 2 where native may warn with exit 3. Show-linearization retains parsed raw fields on physical failure. |

Additional intentional data differences are:

- Split labels advance their source numbering runs. Selecting/reordering then
  splitting retains source labels; native's combined selection-and-split command
  instead applies original label positions. Native selection followed by a
  separate split supplies the managed behavior's reference control.
- Attachment name-tree keys sort by emitted bytes, including mixed ASCII/UTF-16
  encodings. Native qpdf 12.4.2 shares the former decoded-order defect. Listing
  and request matching continue to use decoded ordinal comparison.
- Integer scalars retain signed 64-bit values; PDF JSON real values expand into
  PDF decimal syntax, require finite binary64 magnitude and signed 32-bit
  exponents, and preserve decimal text on underflow. Unsupported magnitudes
  reject before publication rather than emitting unreadable numeric tokens.
- RunLength decoding stops at its first end marker, ignoring trailing bytes.
  Native qpdf 12.4.2 also exhibits the former managed trailing-data defect.

Physical output can also differ while preserving PDF meaning:

- Default document IDs are deterministic content hashes, rather than native's
  time-based IDs; `--static-id` uses the native test constant. Unspecified
  attachment timestamps are omitted because runtime code has no ambient clock.
- BCL Flate levels map `1` to fastest, `2–8` to optimal and `9` to smallest size.
  JPEG providers receive quality `0–100`; the development Lokad.Image adapter maps
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
while preserving reviewed status/evidence and supplemental requirements. Its
offline regeneration tests use synthetic metadata and a mocked Git identity;
they preserve review annotations and exercise reference-version guards without
requiring an upstream checkout. Metadata does not prove defaults,
precedence, grammar or interactions; those require behavior fixtures.

## How compatibility is verified

The development validator accepts LF, CRLF and CR header endings. Its bounded
header inspection previously rejected CR-only headers before PdfPig opened the
input. Malformed/truncated rejection and the core input policy are unchanged.

The obsolete development-only `probe-flate` command has been removed. Codec and
companion tests exercise the shared production pipeline; the standalone BCL
feasibility implementation and smoke-only command test are no longer maintained.

Reference checks compare fresh managed/native outputs under the original input
and budget controls. Stable inspection text, decoded bytes, page order, sharing,
field relationships, error/warning exits and output integrity have mechanism-
appropriate assertions. Exact stream comparisons use pinned qpdf's structural
JSON and generalized decoding, retaining encoded bytes for opaque filters.
Signatures include each logical page's ordered content streams as well as the
stream-byte multiset; physical object order and numbering can differ. Matching
signatures also require matching ordered PdfPig page text. All-stream byte/text
views support separate image, font and appearance assertions without claiming
rendering equivalence. Negative controls cover payload words `stream`/`endstream`,
page/content-array swaps and changed font encoding with identical stream bytes.
Native syntax/linearization checks and visual checks supplement this evidence.
Skipped checker stages remain explicit; an unavailable check is not a pass.

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

The development-only [Lokad.Image 0.2.0 adapter](../tools/Lokad.Qpdf.Image)
uses packed Gray/RGB samples, disables orientation changes and forwards private
allocations to the active qpdf governor. Core decode input is borrowed through its visible read-only stream; other
input streams are copied under admission. The ImageSharp adapter, dependency and six
temporary advisory exclusions are removed; ordinary NuGet auditing stays enabled.
CMYK/YCCK and parameterized-DCT images retain their PDF opacity policy, independent
of upstream codec capabilities. The explicit default quality remains 75, with
zero mapped to one; RGB encoding selects YCbCr 4:2:0. JPEG bytes, compression ratios
and decoded lossy samples can change between providers. Equal numeric quality
does not imply identical distortion, and optimization still requires a smaller
encoded packet. Caller codec signatures and the dependency-free core are retained.

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

The development-only oracle can live under ignored `external/oracle/`.
[Installation](../tools/install-reference-qpdf.ps1) verifies the official archive
and executable hashes. [Bootstrap](../tools/bootstrap-qpdf.ps1) and QpdfProbe
require the exact first-line version banner and record the executable identity;
copyright lines are allowed, but lookalike versions and qualified builds reject.
Missing tools fail explicit reference jobs. Native qpdf, PdfPig and renderers
never become production dependencies.
Parser reference calls stop and await a timed-out oracle before closing capture
streams, retaining partial stdout/stderr for diagnosis. Their workspaces are fresh;
`-WorkRoot` selects an explicit destination. CI exercises this failure path with
a small controlled executable, without requiring native qpdf in ordinary tests.
