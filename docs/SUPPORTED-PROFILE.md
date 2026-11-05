# Supported profile

This is the contract of the current source revision, targeting qpdf 12.4.2's
non-cryptographic structural operations. See [compatibility](COMPATIBILITY.md)
for intentional differences and [option evidence](../tests/Compatibility/options.json)
for exact forms, choices and remaining gaps. The core has no runtime NuGet
dependencies. Rendering, OCR, text extraction/layout, general authoring and
PDF/A/X/UA conformance are outside scope.

## Entry paths

Typed C# operations, `Qpdf.ExecuteAsync` argv and `Qpdf.ExecuteJobAsync` job JSON
share the reader, object graph, transforms and writer. Typed methods include
selection/merge, checking, attachments, JSON and fix-QDF/zlib-flate companions.
Argv/job additionally expose qpdf-style blocks and writer/content controls.
No route uses ambient stdout, filesystem, processes, environment or network.

- Typed input is strict; argv/job repairs boundedly unless `--suppress-recovery`.
  Typed selection preserves opaque stream encoding; argv/job defaults to
  generalized decoding and compression. Setters apply in configuration order;
  QDF fills unset defaults.
- Job files are partial configurations in the host's path context. Sorted-key
  order, nested include cycles, duplicate declarations and aggregate input limits
  validate. Response files contain one argument per line. Malformed UTF-16 rejects.
- Fileless inspection returns `OutputText` or `OutputBytes`; an empty binary
  payload differs from no payload. File outputs use staged host transactions.
  Execution reports exit 0/2/3 with explicit status/diagnostics.
- Unsupported options/combinations and crypto requests reject explicitly;
  passwords are neither retained nor echoed.

## Input profile

| Area | Supported contract |
| --- | --- |
| PDF versions | Header at offset zero; versions 1.0–1.7 and 2.0. Effective version is the higher header/catalog Version; malformed non-null overrides reject. |
| Xrefs | Classic, streamed, hybrid and incremental chains, latest revision wins; generations, extents, cycles and section budgets validate. |
| Object streams | Validated N/First/header extents, including ordinary indirect header values. Extends remains opaque. Xref bootstrap restrictions still apply. |
| Parsing/recovery | Byte-oriented PDF values, signed 64-bit integers and preserved numeric/string/name bytes. Strict malformed input rejects; bounded repair warns. Bare-reference indirect object bodies reject strictly or repair to null. |
| Streams | Valid declared lengths preserve binary extents. Repair scans within the physical object under limits; strict reading rejects invalid lengths. External-file descriptors remain opaque and are never opened. |
| Filters | Flate/LZW with predictors, ASCIIHex/ASCII85 and RunLength; DCT Gray/RGB requires a supplied codec at decode level all. Filter names/parameters may resolve indirectly. Unavailable/corrupt optional decode paths warn and retain encoded bytes. |
| Excluded data | Any effective Encrypt declaration rejects, including empty-password PDFs. Populated signature copying rejects before publication; blank fields and read-only inspection remain available. |

Configured nesting/container exhaustion is a resource failure, never permission
to drop retained metadata/attachments and publish success. Cycle detection,
malformed-data recovery and cancellation remain separate.

## Selection semantics

- Select ordered 1-based pages, duplicates, FirstAndLast windows or sorted unique
  Normalized sets. Explicit empty/out-of-range requests reject. Range exclusions
  affect only their preceding positive group; odd/even filters apply afterward.
- Merge/collate by source, rotate output positions, apply page labels and split
  into groups. Repeated logical sources share one reader/copier and selected-root
  union; different paths stay independent. Zero-contribution groups still validate.
  Repeated pages/widgets receive independent headers while payloads stay shared.
- The primary document owns global metadata/attachments independently of which
  pages are selected. Typed merge uses its first source; argv/job names the primary
  separately. Secondary global collections need explicit import, while page-reachable
  data copies normally. Empty-base assembly and a real zero-page input differ.
- Fresh catalog/page-tree roots contain only selected pages; each page gets its
  required Parent backlink. Shared non-page identities/cycles remain shared;
  omitted/foreign page targets become null, including within split/unreferenced data.
- Explicit labels override source labels before splitting in assembled order.
  Unchanged whole-file trees stay raw; interpreted trees validate. Prefix-only
  styles ignore inactive counters. Output mapping/count metadata reflects selection.
- Retained source URI bases and named destinations bind before source cleanup
  when context would otherwise be lost. First repeated page stays canonical.
  Static View optional-content registrations/default OFF states combine by identity;
  same names do not merge layers. A sole complex context stays intact; unsupported
  automatic/alternate/non-View composition rejects before publication.
- Source-local action binding follows recognized page/annotation/field/outline
  roles and cycles. Remote actions and private dictionary extras remain opaque.
  Replacement headers preserve independent original aliases and field/CO identity.
- Ordinary copies preserve supported primary catalog extras/outlines/structure;
  typed previews and split use narrower policies. Unresolvable malformed optional
  graphs can drop; resource, profile, cancellation and host failures remain fatal.

## Page content profile

Supported transforms include coalescing, normalization, conservative resource
pruning, inline-image externalization, overlay/underlay, appearance generation,
annotation flattening and rotation baking. Checking/normalization treats a page's
Contents array as one logical program, including tokens/inline packets spanning
streams. If required fragments are unavailable, normalization keeps the program
and checking reports skipped coverage. Opaque inline payloads remain intact.

Placement/rotation composes source frames, leaf UserUnit and appearance matrices;
supported rectangles, absolute points, border dimensions, insets, line distances
and NoRotate anchors follow that frame. Headers are immutable role-specific copies;
private aliases and payloads retain originals. Static imported appearances clip to
the source page. Invalid optional appearances can warn/skip; required unrepresentable
transforms fail. Popup/reply links remain occurrence-local. General interactive
appearance behavior and malformed-field parity are not claimed.

Widget generation binds source DA/Q/default resources, inherited field values and
supported text/choice/button states. Missing usable fonts preserve appearances,
warn and prevent unsupported flattening. Imported fields register with collision-safe
names/resources; placements retain primary calculation order. Empty signature fields
skip; pushbuttons and unsupported generation paths warn.

Rotation baking updates retained XYZ/FitR, OpenAction, local GoTo, link/outline,
named-index and field-event destinations through the actual baked matrix. Fit/FitB
stays unchanged; 180-degree H/V/BH/BV keeps its mode with the projected coordinate.
XYZ zero/null zoom retains its meaning. Affected nullable positions and quarter-turn
axis fits cannot generally encode arbitrary prior viewer/window state: reject
UnsupportedFeature before publication. Unbaked/omitted targets, discarded catalog
extras, remote actions and private data retain their treatment.

Image optimization attempts eligible 8-bit DeviceGray/DeviceRGB XObjects once per
identity across pages/forms/flattened appearances; keep a JPEG only if smaller.
It requires a codec for eligible work. Explicit quality enables DCT recompression.
CMYK/YCCK and color-key masks remain opaque; valid soft masks retain their separate
policy. Filter ceilings precede codec/sample admission. Large inline images can
externalize first; Indexed inline aliases expand to valid XObject names.

## Inspection profile

- Checking declares header, xref, trailer, page tree, object graph, stream decoding,
  page content, forms and linearization stages. Skipped coverage carries a reason;
  failed checking stops later inspection. A check is not a conformance/rendering
  assertion. Writer settings do not all transform inspection-only graphs.
- Object/xref/page/image/attachment/outline listings use the shared resolver and
  retain raw identities where their format requires them. Selected/placed inspection
  uses the assembled graph; pruning is a serialization policy. Form inheritance
  is bounded and is not a strict malformed-form repair pass.
- PDF JSON versions 1/2 expose the documented qpdf structures. V1 references use
  full `n g R` keys; V2 uses `obj:n g R` and byte-reversible strings (`b:` whenever
  Unicode would change bytes). Names preserve raw bytes with escaped presentation
  where needed. Named outline destinations remain unresolved in inspection.
- V2 stream data can be absent, inline base64 or host sidecars, with raw fallback
  for unavailable optional decoding. V1 ignores stream payloads. Sidecars/main
  output must have distinct logical paths.
- JSON creation/update reads V2 qpdf data, accepts generation-zero declarations,
  validates the resulting page tree and crypto/signature policy, and writes a
  renumbered classic PDF. Dangling references become null. Page/content/image/layout
  transforms and stream/metadata writer combinations are unsupported in this path.

## Writer profile

The shared writer supports stream compression, object-stream preserve/generate/
disable modes, version/extension negotiation, deterministic/static IDs, metadata
controls, QDF and linearization. Known emitted features and opened page/placement/
attachment sources contribute version floors; explicit force wins without transcoding
opaque features. Object streams require 1.5. Catalog XML metadata is uncompressed.
Compression levels 1/2–8/9 map to BCL fastest/optimal/smallest size.

Final-root reachability retires superseded graphs without deleting independent
private consumers. Preserve-unreferenced adds eligible objects in source-number
order, excluding old catalog/page/container structure. Failed malformed candidates
roll back fresh copies and warn; strict load/profile/resource failures stay fatal.
Split retains these roots, but never adds foreign pages. Output numbering, packing,
compressed bytes and physical hints can differ from native qpdf.

## Attachments profile

Raw name-tree keys and safely copyable filespec graphs are authoritative. Listing
projects preferred filenames, descriptions, MIME type, dates, stored checksum,
declared size and stream number. Optional malformed scalars can be unavailable
without losing a usable payload; required specs/streams/children still validate.
Direct/indirect/null optional containers use the same policy.

- Match request keys by decoded ordinal text; sort emitted keys by encoded bytes.
  Full-collection operations reject duplicate logical keys. Selective extraction
  uses first-match lookup without scanning unrelated branches for duplicates.
- List/extract/add/remove/copy share typed/argv/job implementations. Extraction
  does not require successful listing and uses raw bytes with a warning only where
  embedded decoding is unavailable. External-file placeholders are not payloads.
- Remove entries before additions; imports follow additions. Missing keys,
  repeated removals and collisions without replacement reject. Removed/replaced
  attachment-only graphs drop unless independently rooted or explicitly preserved.
  Failed copied entries still participate in conflict detection.
- New keys/filenames are nonempty, authored text is valid UTF-16 and MIME names
  are ASCII without NUL. Existing empty keys can be removed/extracted. Dates are
  omitted unless supplied; additions record size/checksum and default basename.
- Primary collections and final split attachments survive independently of edits.
  Adding/importing supplies UseAttachments only when the primary PageMode hint is
  absent/null. Named hints retain their bytes; split drops opaque generic extras.

## QDF and linearization profile

QDF supplies editable objects, indirect lengths and original-ID comments unless
suppressed; it defaults to generalized decoding and content normalization.
`FixQdfAsync` repairs consistent LF-framed edited QDF with sequential numbering;
ambiguous marker-shaped payloads reject. `RunZlibFlateAsync` ends at the first
complete frame and rejects truncation/checksum errors. Inputs close before staging.

Linearization uses two passes with page/shared/outline hints and optional object
streams. Check/show validates physical boundaries and available hint contents;
unavailable filters/external hints report skipped coverage. Active first-pass capture
must differ from all input/main/split targets; without linearization it creates no
output. Split capture retains the last group's first pass. QDF is disabled by
linearization. Empty split jobs create no output or capture.

## Governance

All paths apply input/output/decoded-byte, memory, filter, host-operation/handle,
diagnostic, nesting and container limits plus independent cancellation. Nesting
is 1..499 (default 256); filter default is 25. Invocation parser settings cannot
raise host ceilings; `--no-default-limits` lifts only applicable unspecified defaults.
Repair error thresholds are bounded per object/member. Resource exhaustion cannot
be downgraded into optional-root data loss.

Diagnostics have stable codes, bounded actual storage and explicit overflow.
Host failures omit transport exception text/stacks. Cumulative CPU fuel is absent;
hosts can use deadline tokens. Caller-owned results remain funded beyond execution;
pending I/O/workers finish before cleanup/refunds. Single-target replacement is
transactional; earlier multi-target commits survive later failure and a close
fault cannot undo a commit. See [memory/host ownership](MEMORY.md) and
[API changes](COMPATIBILITY.md#resource-api-and-behavior-changes).
