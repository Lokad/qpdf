# Compatibility target and scope

Reference: qpdf **12.4.2**, commit
`4eba95899886e851cc41d76886483b347612f2a8`, from
https://github.com/qpdf/qpdf. This target must not move silently.

**Implemented PDF operations: the audited non-cryptographic subset.** 154 inventory entries
are complete and 3 stay partial: the no-Zopfli build profile and four-component CMYK
image samples (accepted as tested compatibility exceptions with truthful partial labeling),
plus `--parser-max-errors`, whose invocation values, zero/default behavior, `--no-default-limits`
scope, host-ceiling enforcement and per-object repair are implemented for the verified
damage matrix, including full-range integers and numeric conversion failures. Remaining long real tokens,
lexical damage, damaged-container transitions, orphan-key/error interactions and full
nested-load evidence still keep that entry partial; excluded crypto
operations carry
negative rejection evidence. The scaffold, runner, and codec-probe tests establish
inventory consistency, fixture/independent-reader contracts, codec feasibility, and
package boundaries; operation coverage is carried by the per-entry unit, budget,
and oracle-differential evidence reconciled below.

## Inventory

`tests/Compatibility/options.json` records 140 distinct documented flags in
157 option/context entries. It includes public argument forms, contexts,
choices, JSON property mappings, scope, completion milestones, review status,
remaining gaps and evidence slots. Explicit `--json-key=encrypt` is excluded
without excluding other JSON keys.

The generator reads public metadata from both `job.yml` and `manual/cli.rst`.
Defaults, aliases, positional grammar, precedence, nested context transitions
and interaction behavior still need operation-specific review and fixtures.
An inventory row or an enumerated choice does not implement that behavior.
The `fix-qdf` and `zlib-flate` companion contracts are additional work, separate
from the main command's flag count.

Supported behavior should match documented qpdf semantics. Stable inspection
data, page sequence, sharing, lossless stream bytes and exit classes need
appropriate comparisons. Compressed output need not be byte-identical.
JPEG component/color invariants and narrowly defined sample tolerances are established
against the approved codec: governed 1- and 3-component DCT decoding plus shrink-only
optimization shipped in M8; four-component CMYK/YCCK samples stay opaque by decision
while codec allocation/cancellation auditing remains open (see below).

## Validation runners and fixtures

`tools/QpdfProbe` is the development-only differential runner. It invokes the
pinned qpdf 12.4.2 executable (`--version`, `--show-npages`, `--check`) and the
pinned independent reader (PdfPig `[0.1.16]`, never a runtime dependency).
`tests/Fixtures/manifest.json` records 47 synthetic fixtures (single- and multi-page
selections, outlines, thumbnails, images, overlays, labels, and orphans, plus truncated
negative fixtures) with SHA-256, byte counts and expected page counts. Ordinary tests use these
fixtures and a fake oracle; they never require `external/` or a real qpdf
binary. The explicit reference lane (`tools/bootstrap-qpdf.ps1`,
`tools/run-reference-checks.ps1`) requires `QPDF_EXE` and fails when it is
missing.

Pinned oracle (bootstrapped 2026-10-01): official `qpdf-12.4.2-msvc64.zip`
(SHA-256 `db87077e683630c1217e0e8f9a20a9749d952ab676e881c3689187763a5de25d`,
matching the release `qpdf-12.4.2.sha256`), extracted under the ignored
`external/oracle/` directory. `bin/qpdf.exe` (SHA-256
`1bdaf878f37eaffea1ea3c30591214c77636a0f2fc7432427516b6e22e36c0ff`)
reports `qpdf version 12.4.2`. Build features: libjpeg statically linked
(`libjpeg` present in `qpdf30.dll`, no separate JPEG DLL); Zopfli absent
(`--zopfli` reports `zopfli support is not enabled`, exit 2; the remaining
`zopfli` strings in the binary are the disabled-build messages). Set
`QPDF_EXE` to `external/oracle/msvc64/qpdf-12.4.2-msvc64/bin/qpdf.exe`
to run the reference and differential lanes.

`tools/run-differential-checks.ps1` (via `QpdfProbe emit`, public API only)
compares managed and oracle outputs operation by operation under ignored
`external/diff/`: oracle `--check` must pass on both outputs, oracle and
independent page counts must agree, plus per-case assertions. Current
coverage, all passing with no engine discrepancies (inventory diff labels may cite finer sub-assertions inside these coarser cases): select (first- and last-page trios), merge,
uncompress (decoded stream payloads identical), normalize (normalized
content payloads identical), coalesce (decoded payloads identical), compress (recompressed payloads identical), flatten-rotation
(swapped MediaBox, dropped /Rotate, byte-identical wrap streams), prune (unreferenced
/F2 dropped under page operations with decoded payloads identical), JSON structural
page-descriptor agreement (version, object and contents references, positions), json-modes (trailer filter, inline payloads, minimal-output, and prefix sidecars each agreeing with the oracle), json-import (shared oracle JSON built by both writers with trios green and sorted decoded-payload identity), json-update (shared mutated JSON applied by both writers with trios green and `/Custom` in both outputs), writer-controls (negotiated/forced/extension headers and catalogs, static test-constant trailers, deterministic/default byte identity, LF-before-endstream shapes), attachments (add/copy/remove trios with cross-extracted bytes, key-set agreement, empty sets),
job-file (shared subset file run by both sides with trio green), recovery (warning/failure exit parity with page-count agreement after repair), metadata (info/metadata removal trios with per-flag assertions), labels (identical labeled/unlabeled catalogs), catalog-extras (primary-merge, subset, whole-file and split catalogs agree), error exit-class parity (out-of-range page, missing input, and unknown flag all exit 2
on both sides), warning exit parity (3/3, 0/0, 3/3), show-npages page-count agreement, show-pages/show-object/show-xref line-for-line dumps, show-pages-with-images listings (clean and dimension-less fixtures, warning exits 3/3), ignore-xref-streams reading (hybrid/classic/stream-only exit parity plus a hybrid selection trio), remove-acroform/remove-structure invariants (explicit removal drops even when ordinary would preserve), forms-preserve (field registration, values, parent links and default resources agree on full selection, subset pruning and merge trios, plus JSON fullname/value/needAppearances agreement), outlines-preserve (ordinary single-page outline titles agree on both sides while contained preview drops them), structure-preserve (ordinary single-page structure keys agree on both sides while contained preview drops them; damaged structure drops leniently), inline (externalized /IIm1 content and kept-inline thresholds agree on both sides), forms (generated
appearance bytes and `/Fxo1` stamping match, including the `1 5.2 Td`
placement), generate-unreachable (selection drops the emptied form and skips silently with 0/0 exits while whole-file warns and regenerates with 3/3 exits), forms-check (--check exits warn 3/3 on unreachable widgets and direct field objects while clean forms stay 0/0), overlay, overlay-annots (copied subtypes, contents, transformed rectangles, appearance matrices and nulled page-boundary destinations agree on both sides), overlay-widgets (imported field registration, values, parent links and default resources agree), overlay-widgets-dr (collision renames and DR conflict rewrites agree), overlay-widgets-repeat (repeated placements rename agree), qdf (editable view with generated object streams: trio plus QDF/ObjStm/XRef/member markers on both outputs and an upstream fix-qdf round trip), qdf-noids (suppressed original-ID comments with the editable view intact on both sides), fixqdf (managed repair versus oracle fix-qdf byte-identical on edited classic and streamed files, trios green), zlibflate (both directions with corrupt-input parity and level ordering), linearized (oracle-linearized files plus page-count and hint-length mutations on both sides), linearize (managed writer versus oracle writer on one- and two-page outputs, both validating under oracle and managed checks with independent page-count agreement), linearize-streams (managed versus oracle writers with generated object streams on one-, two-page, and outlined selections, both outputs validating under both checkers), check-linearization (managed versus oracle inspection exits and messages on valid, plain, and hint-damaged files), show-linearization (managed versus oracle dumps compared line for line), linearize-pass1 (zeroed-entry shape and shorter-than-final on both sides), linearize-outlines (managed and oracle outputs validate with matching show dumps on both files), and linearize-thumbs (thumbnail part-9 placement with matching show dumps). Confirmed intentional differences: our default
output stays uncompressed where upstream compresses, and our `--overlay`
block requires a second `--` terminator before the output file while
upstream takes the output directly after the block terminator. Merged forms
concatenate per-source field registrations with per-source-snapshot `+N` collision renaming
for named field nodes (including direct-dictionary nodes renamed in place with upward rebuilds), keep the first default-resources
dictionary, and embed the combined form directly in the catalog where
upstream writes an indirect object. Direct-dictionary kids stay embedded where the reference promotes them to indirect objects (rename outcomes agree on surviving shapes), and field-like direct objects met while traversing now warn and stay ignored under `--check` on both sides (exit 3, forms-check lane case); page-unreachable form trees drop on explicit selection and merge paths exactly as the reference drops them, while whole-file rewrites keep them verbatim on both sides. `--check` additionally warns with exit 3 on page widgets no field registration reaches and on kids whose /Parent back-link does not name the traversed node, matching the reference; the copy path keeps preserving those shapes verbatim (tested difference). Damaged structure with unresolvable references drops leniently instead of failing, while the reference keeps the damaged tree; well-formed outlines/structure preserve with dangling destinations pruned to null where the reference keeps references to null objects. Catalog extras that reference page-tree or catalog nodes drop their key where the reference remaps them onto the rebuilt nodes.
The `progressive-jpeg` cases compare four authored grayscale/RGB image rewrites
with exact sample hashes, filter removal and oracle/independent-reader trios.
Binary payload/text helpers search PDF delimiters with ordinal comparisons;
culture-sensitive matching can stall on large control-byte sample payloads.

`tools/verify-package-consumer.ps1` runs the public consumer against exactly the
supplied packed artifact in a new consumer/feed/cache directory. Only the local
artifact's hash changes in a copied lock; all reviewed versions, constraints
and external hashes remain locked from the first restore. The consumed DLL
must match the packaged DLL. `tools/test-package-consumer.ps1` proves wrong
artifact versions and external hash mismatches reject before smoke execution;
the tracked consumer lock remains unchanged.

`tools/run-parser-checks.ps1` is included after the full differential suite in
the explicit reference runner. It creates unique per-run authored inputs and
records exact object values, warning exits and output validation. Job numeric
limits accept managed numeric values; oracle job files require decimal strings,
so reference job probes must use the pinned schema's representation.

Integer parsing accepts the complete signed 64-bit range and long leading-zero
tokens under governed scan work and memory admission. Original text remains
available for writing; inspection normalizes integer spelling like qpdf.
In repair mode, integer overflow or an indirect-reference component outside the
signed 32-bit range replaces the whole object with null and warns, independently
of the semantic error threshold. Invalid references within that range instead
consume one error and become a null value. Strict parsing retains its failure
policy. Long real conversion and other lexical/container damage remain open.

## Codec feasibility probes (work item 03)

`QpdfProbe probe-jpeg` exercises the pinned ImageSharp `3.1.12` JPEG codec
through public APIs with per-call encoders, checked dimensions, bounded
streams and prompt disposal; global defaults are never mutated.

- Grayscale (`L8`) and RGB (`Rgb24`) 8x8 round-trips preserve dimensions;
  encoded sizes vary with quality as expected. Feasible for M8 DCT work.
- `JpegEncoder.Quality` accepts `1..100` (default `75`) and rejects `0` and
  `101` with `ArgumentException`. qpdf `--jpeg-quality` accepts `0..100`
  (`QPDFJob_config.cc`); the adapter mapping is `0 -> 1`, identity on
  `1..100`, rejection outside. This clamping is the documented intentional
  difference.
- `JpegEncodingColor.Cmyk`/`Ycck` labels exist, but decoding yields
  color-converted RGB pixels; raw four-component samples are not preserved
  through the public pixel API. Displaying a JPEG or selecting a CMYK enum
  does not prove PDF component preservation. This gap stays open for M8:
  DCT decode must preserve component count, color interpretation, dimensions
  and sample ordering, with explicit narrow per-sample tolerances only for
  rounding/upsampling.
- Allocation is bounded by checked `width * height` against a caller
  pixel budget before any image is allocated; codec-internal buffers remain
  ImageSharp-owned and must be budgeted conservatively in M8.
  Cancellation flows through `LoadAsync`/`SaveAsync` overloads.
Decision (work item 133): four-component opacity is final, not held open.
Probed against the pinned 3.1.12 assembly itself, the public API offers no
raw-sample path: no CMYK pixel type exists (`Cmyk32` does not compile), the
decoder exposes no component selector or no-conversion option, and the
`Cmyk`/`Ycck` encoder labels only tag RGB-derived output. A hand-built baseline
4-component JPEG with known DC-only samples pins the exact boundary from the
oracle side: plain input decodes to exactly 128 + DC per component (exit 0),
Adobe YCCK input converts to CMYK samples (exit 0), and both shapes fail
explicitly before any pixel work on the managed side. The alternatives all
violate standing rules: another JPEG package or native libjpeg binding (banned
dependencies), a hand-written DCT decoder (banned second codec), and lossy RGB
reduction (banned silent fidelity loss). Four-component DCT therefore stays opaque
with an explicit warning on every path: `--check` skips these streams with exit 3
where the reference exits 0, and `--optimize-images` keeps its partial entry for
the missing encode path. The boundary is pinned by the `cmyk` differential
(recorded oracle samples, opaque managed output, 0/3 exits) and unit tests over
genuine four-component inputs. JPEG decoding now uses a per-job managed,
non-pooling ImageSharp allocator: pixel, coefficient and stream buffer owners
reserve their typed capacities before allocation, including a conservative
array/owner allowance, and release them at disposal. Returned samples have a
separate reservation, so simultaneously live pixel and sample frames both count.
Metadata decoding is disabled. Tight-budget failures release codec owners and
preserve an existing output target. The fixed 64 KiB allowance covers other
codec/configuration state; it does not replace the allocator's buffer charges.
Encoding also uses the per-job allocator and a seekable governed output stream.
Every growing capacity and returned copy is admitted while its previous owner
is still charged; failures dispose the working owners. Encoded bytes are not
counted as decoded stream bytes. Both codec directions receive the caller's
cancellation token through the pinned token-bearing APIs, with row copying and
stream writes checking it as well. Aggregate decoded bytes are admitted from
the frame header before another pixel/sample frame is allocated.
Four authored progressive grayscale/RGB fixtures exercise separate DC/AC scans,
non-multiple-of-eight edges, and larger coefficient owners. All sample bytes
are exactly 128; the pinned oracle and managed public rewrites agree byte-for-
byte, and tight coefficient budgets dispose owners. These fixtures are rebuilt
by `tools/build-progressive-fixtures.py` and indexed in the fixture manifest;
they do not establish a general tolerance for textured/progressive images.
The remaining codec-state allowance, independent retention and measured
cancellation latency still need auditing.

`QpdfProbe probe-flate` exercises the governed BCL Flate pipeline.

- `ZLibStream` handles the zlib-wrapped (RFC1950) form used by PDF
  `FlateDecode`; `DeflateStream` handles raw deflate.
- qpdf `--compression-level` accepts `1..9`; BCL exposes `Fastest`,
  `Optimal`, `SmallestSize`, `NoCompression`. The mapping is `1 -> Fastest`,
  `2..8 -> Optimal`, `9 -> SmallestSize`, rejection outside. Numeric levels
  are buckets, not 1:1 zlib levels; record this intentional difference for
  M6 writer controls.
- Corrupted input is rejected with `InvalidDataException`. Half-truncated
  input may decode to short output without throwing (observed `20` vs `3072`
  bytes); callers must validate expected stream length rather than assuming
  the codec rejects every truncation. Expansion limits are enforced by a
  bounded copy loop (8 KiB buffer, checked total) because the output cap does
  not itself cap codec allocations. Codec input/output chunks check cancellation.

Flate encoding and inflation now reserve codec state before construction and
use the governed output stream for admitted capacities, growth overlap and
returned copies. Inflation charges its 8 KiB scratch array separately, validates
Adler-32 over the working span before allocating the returned copy, and releases
working owners on failure. A borrowed input stream caps each refill at 8 KiB
and charges work even for compressed blocks that produce no output; encoding
also supplies at most 8 KiB per codec call. Empty compression validates levels
and admits its eight-byte framing before allocation.

The state allowances are conservative admission models: 512 KiB for deflate
and 128 KiB for inflate, including the BCL pooled buffer and wrapper state.
[zlib's memory model](https://zlib.net/zlib_tech.html) gives approximately 268 KiB
for default compression and 40 KiB for inflation;
[the .NET 10 stream implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.IO.Compression/src/System/IO/Compression/DeflateZLib/DeflateStream.cs)
uses an additional 8 KiB pooled buffer. The allowances add headroom; their
adequacy across supported runtime/platform implementations still needs
independent measurement. Returned pool buffers can remain retained by the BCL
after a job. Measured cancellation/retention and state-allowance adequacy remain
audit work; these changes do not establish a
process-memory ceiling.

ImageSharp stays behind the JPEG adapter and out of the public API;
no Drawing, SkiaSharp, native libjpeg or alternative codec is introduced.
The pin remains exactly `3.1.12` in central versions, lock files and the
package dependency group.

## Strict inspection (work item 05b)

`QpdfDocument` strictly inspects single-section classic-xref PDFs: `%PDF-` header
at offset 0 with versions 1.0-1.7 and 2.0, tail `startxref` that must match the
actual table position, mandatory `%%EOF`, fixed-width entries validated before
reading, exact `[0, /Size)` coverage with a free object 0, and header/xref
consistency per object. XRef streams, hybrid/incremental structures, and recovery
are explicit later work and fail as strict parse errors today.

Page counting traverses `/Kids` with a cycle set and the nesting budget;
traversal wins over `/Count`, which only warns. Absent `/Type` tolerates
`/Kids`-bearing nodes as page branches and others as leaves; present but
mismatched `/Type` fails. Trailer `/Encrypt` rejects structurally as
`EncryptedInput` (including empty-password forms), without password fields,
prompts, or value echo. The effective version is the header version overridden by a
valid catalog /Version name (direct or indirect); malformed, mistyped, unresolvable,
or unsupported catalog versions fail. Stream /Length resolves direct integers and
indirect integers with a load-cycle guard; missing, negative, non-integer,
unresolvable, and hugely over-declared lengths fail; stream bytes come from /Length,
so delimiter-like bytes inside the data are preserved.

## Governed Flate and predictors (work item 08)

The internal filter pipeline resolves `/Filter` names and arrays with
canonical abbreviations (`Fl`, `LZW`, `A85`, `AHx`, `RL`, `CCF`, `DCT`)
and aligns `/DecodeParms` as upstream does: a single dictionary applies
to every stage, an array must match the filter count, and empty chains
ignore parms. FlateDecode, LZWDecode (both EarlyChange values), ASCIIHexDecode,
ASCII85Decode, and RunLengthDecode execute; anything else fails explicitly as
unsupported, never silently. DCTDecode executes at decode level all (work item 32).

Flate uses BCL inflate inside strict zlib framing: method/window/header
checks, preset-dictionary rejection, per-chunk output budgets, and an
Adler-32 trailer verification that catches truncations BCL alone would
decode short without error. Corrupt bodies and checksum mismatches fail
as truncated input; decompression bombs fail on budgets.

Flate now owns the decoded-byte charge for direct, filter-chain, companion and
linearization-hint calls. Each bounded inflate chunk checks the aggregate
prospective sample count before output growth or copying; successful stages
commit their charge once. PNG working buffers admit the additional row
selectors, which do not count as returned sample bytes. Shared predictors also
check aggregate admission before allocating transformed output. Failed budget
checks release working owners while preserving prior returned results. Other
filters' capacity/ownership and failed/canceled public counters remain part of
the broader containment audit.

Shared TIFF/PNG predictors now admit returned output before allocation, charge
work before copying/transforming, and check cancellation within wide rows.
Previous samples come from the output itself, so empty inputs allocate no row
scratch even with huge declared columns. Failed transformations release their
output reservation; Flate releases its inflated intermediate after prediction.
Packed TIFF samples start each row at its byte boundary, including padding;
sixteen-bit samples retain their component and row boundaries.

Predictors mirror upstream parameters (`/Predictor` 1, 2, or 10-15 with
defaults 1/1/1/8 for predictor/columns/colors/bits, unknown keys
ignored): TIFF horizontal differencing per sample with packed rows, PNG
per-row Sub/Up/Average/Paeth at byte granularity. Strict deltas: partial
rows are rejected (upstream pads them), unknown PNG row bytes are
rejected (upstream ignores them), parms-length mismatches are rejected
(upstream warns and skips decoding), and preset dictionaries are
rejected. Structural decoding consumes this pipeline starting with xref
and object streams in work item 09.

## LZW, ASCIIHex, ASCII85, and RunLength (work item 11)

LZW mirrors the reference bit packing (MSB-first), table rules (clear 256, end 257,
entries 258-4095), both EarlyChange settings (default 1, explicit 0/1 only), KwKwK
handling, width growth at 511/1023/2047 with the EarlyChange delta, and table-full
rejection. Codes after end-of-data and trailing partial codes are ignored like
upstream. LZW predictors reuse the Flate predictor stage with the same strict deltas.

ASCIIHex ignores ASCII whitespace, ends at `>`, pads a trailing odd digit with zero,
tolerates a missing end marker, and ignores later bytes; bad digits fail. ASCII85
ignores ASCII whitespace, allows `z` only as a full group, ends at `~>`, pads partial
final groups with `u`, wraps group overflow like the reference, and ignores later
bytes; misplaced `z`, broken end sequences, and out-of-range bytes fail. RunLength
copies below 128, repeats above 128, continues past 128 markers like upstream, and
drops truncated tails instead of failing. All four stay budget-bounded per emitted
chunk. ASCIIHex, ASCII85, and RunLength reject DecodeParms; mixed chains decode in
filter order. Structural decoding accepts LZW xref streams through the same pipeline.

## Xref and object streams (work item 09)

`startxref` may point at a classic table or a Flate- or LZW-compressed xref
stream (`/Type /XRef`, `/Size`, `/W`, optional `/Index`, decoded under
output budgets with exact data sizing). Entry types are free,
uncompressed with pre-read offset validation, and compressed with
generation forced to zero; unknown types fail. Object-0 stream entries
are ignored like the reference reader while object 0 still resolves as
free. Coverage of `[1, /Size)` is exact; free-head rules match classic.
Object streams (`/Type /ObjStm`, `/N`, `/First`) decode once with
ascending, in-bounds offsets and per-entry extents, caching only what
the xref table points at the stream. Strict deltas versus warn-and-continue:
corrupt headers, self-containment,
low object numbers, non-ascending offsets, and trailing entry bytes all
fail, where the reference warns and continues. `/Prev` and `/XRefStm`
fail explicitly as work-item-10 scope, replacing silent staleness with
errors. `/Extends` on object streams is read per the reference (whose reader
carries no Extends handling): members resolve through the header pairs of the extending stream itself, so
even the self-extending objstm-extends.pdf fixture returns
its font object on both sides. Selection output normalizes to classic xref;
object-stream preservation arrives with writer controls (M6).

## Incremental and hybrid chains (work item 10)

`/Prev` chains across classic and stream sections resolve with
latest-wins precedence; hybrid `/XRefStm` links merge without
overriding their own revision and their own `/Prev` stays ignored like
upstream. Cycles terminate explicitly, section counts run against a
revision budget, and link targets must be integers inside the input.
Deleted entries (latest free) resolve as null; replaced entries and
generations resolve transparently, including compressed objects whose
streams were superseded. Union coverage of the effective `/Size` is
exact with absent-or-free object 0, and over-range entries fail rather
than being ignored. Every chained trailer is checked for encryption
declarations. Recovery of damaged structures stays M4 work and fails
explicitly here. `--ignore-xref-streams` (plus job key `ignoreXrefStreams`)
skips cross-reference streams and hybrid `/XRefStm` links, reading classic
tables only: hybrid files fall back to their classic revision, bogus stream
links never load, and stream-only files fail strictly into bounded recovery.
The flag rides every plan that opens a document (plus the host budgets) with
exit-for-exit oracle parity on hybrid, classic, and stream-only fixtures.

## Byte, version, and indirect-length semantics (work item 12)

`QpdfDocument.Version` is the effective version: the `%PDF-` header (offset 0,
1.0-1.7 and 2.0) overridden by a valid catalog `/Version` name, direct or
indirect. Null or absent `/Version` keeps the header; malformed, mistyped,
unresolvable, or unsupported values fail. Selection output echoes the effective
version in its header. Object loads carry a reentrancy guard so a
self-referential indirect `/Length` fails as a parse error instead of recursing.
Over-declared lengths fail before allocating; stream data is sliced strictly by
`/Length`, preserving delimiter-like bytes.

## Ordered multi-page selection (work item 13)

The typed selection accepts explicit ordered pages (duplicates and order
preserved), `FirstAndLast(N)` windows, and sorted/deduplicated `Normalized`
sets. `FirstAndLast(N)` returns the first N and last N pages in document order,
or every page once when the windows meet or overlap. Results carry the source
page count and the exact per-output-page source mapping. Each occurrence copies
to an independent page object while immutable resources stay shared; selecting
every page still rewrites the graph rather than echoing input bytes. The reference
instead shares content streams across duplicate occurrences (probed: a ranged collate
repeating pages yields shared streams there, independent copies here, with identical
page order on both sides). Oversized
selections fail on work budgets and out-of-range pages fail explicitly.

## Catalog, inheritance, and widget policy (work item 14)

Ordinary selection and rewrite paths carry a fresh catalog with the input form dictionary preserved through the shared copy engine (work item 80) and with outlines and structure preserved for ordinary paths while contained preview drops them: field registration, values, parent links and default resources round-trip across typed, argv and job paths, with top-level fields pruned when every widget in their subtree falls outside the kept pages. `--remove-acroform` is the explicit form prune switch threaded through every publication path; `--remove-structure` is the explicit structure prune switch (previously a no-op, now drops `/StructTreeRoot`/`/MarkInfo` even when ordinary would preserve); flatten-all drops the form with its annotations. Typed preview (`SelectPageAsync`/`SelectPagesAsync`) keeps the contained policy of dropping `/Outlines`/`/StructTreeRoot`/`/MarkInfo` while ordinary argv/job/whole-file/merge preserve them via the shared copier (dangling outline destinations prune to null; damaged structure drops leniently instead of failing). Page-tree (`/Pages`) and catalog references stay structural errors; references to already-copied objects remap, and references to any other page prune to null under the ordinary selection policy so omitted pages can never resurrect. Field and annotation `/Parent` links remap through the shared memo table while page-tree ancestry stays dropped and materialized. Parent-held field lookup and default-resource merging stay explicitly open. Unknown catalog keys (`/NeedsRendering`, `/PageLayout`, `/ViewerPreferences`, `/Lang`, `/OpenAction`) copy through the owning source engine on ordinary selection, whole-file rewrite, and primary-source merge (split groups and empty bases drop them); unresolvable values drop keys silently like the reference.
Widget annotations travel with the page with appearance bytes preserved. Only the four inheritable attributes materialize,
with explicit null treated as absent (ancestor fallback, else `/MediaBox` failure);
page-only boxes such as `/BleedBox` and `/UserUnit` never inherit.

## Inspection breadth (work item 16)

`qpdf input --show-npages` (plus job key `showNpages`) reports the traversed page count
as an info diagnostic with exit 0; valued forms, output files, page selections, and other
operations fail explicitly, and encrypted or exhausted-budget inputs fail without output.
The differential lane compares managed and oracle counts on one- and two-page fixtures.

`qpdf input --show-pages` (plus job key `showPages`) lists each page dictionary with its
indirect content references in oracle-identical line layout across argv and job paths, with
the same explicit combination failures; the differential lane compares dumps line for line.

`qpdf input --show-object={trailer|obj[,gen]}` (plus job key `showObject`) renders one object
definition in canonical single-line layout (sorted keys, oracle-identical spacing) across argv
and job paths, resolving compressed members and reporting `null` for missing objects like the
reference; streams show their dictionary only. Object-generation selectors resolve exact
pairs against the newest entry per number exactly like the reference: bare selectors
address generation 0, freed or never-defined pairs display `null` with a success exit,
and superseded same-generation revisions stay unaddressable on both sides.
`--show-object={trailer|obj[,gen]}` with `--raw-stream-data` (plus job key `rawStreamData`)
or `--filtered-stream-data` (plus job key `filteredStreamData`, decoded as `--decode-level=all`)
publishes exact stream bytes to a required output file (byte-identical with the reference,
filtered winning when both are given); non-stream resolutions keep the diagnostics view and
an output file is then rejected, while valued, duplicate, misplaced, and `--normalize-content`
combinations fail explicitly (where upstream silently ignores the modifier outside `--show-object`
and allows the normalization combination). The differential lane compares payloads byte for
byte on plain and compressed streams plus line-for-line trailer and missing dumps; the plain
show-object lane still compares definitions line for line on plain, stream, trailer, missing,
and compressed objects, plus same-generation, absent-generation, freed, and live-pair selectors over incremental fixtures.

`qpdf input --show-xref` (plus job key `showXref`) lists every live cross-reference entry
(`n/gen: uncompressed; offset`, `n/0: compressed; stream, index`, free entries skipped)
in oracle-identical line layout across argv and job paths; the differential lane compares
dumps line for line on classic and object-stream files.

`Qpdf.CheckAsync` runs a declared partial check: header, cross-reference,
trailer, page tree, full object-graph traversal, decode-level-gated stream
decoding (`none`, `generalized`, `specialized`, `all`), page content, and
linearization. Skipped stages stay visible with explicit reasons and never
pass silently; failures carry sanitized per-object detail with caps. Streams
above the selected level are caller-scoped; streams with milestone-unavailable
filters skip with a warning. Content tokenizing (M7) and hint validation (M10)
report skipped; non-linearized files pass linearization trivially. `qpdf input
--check` maps to the same engine with exit 0/2/3 from the diagnostics; job-JSON
check has no upstream schema basis yet and fails explicitly.

## Cross-document import identity (work item 19)

`Qpdf.SelectPagesAsync` merges ordered pages from several inputs with per-source
object identity: separate copiers share one destination numbering sequence and one
governor, so equal object numbers across documents never collide while widgets
keep their within-source field links. Every page keeps its own resources, so
same-named resources and fields stay inert without content rewriting. Merged forms concatenate per-source field registrations with per-source-snapshot `+N` collision renaming for named field nodes, keep the first default-resources dictionary, OR the NeedAppearances flags, and embed the combined form directly in the catalog where upstream writes an indirect object. Output takes the highest source
version and carries per-page source mapping plus per-source page counts. Direct-node promotion and first-wins resources stay explicitly open; four-component CMYK/YCCK decoding stays opaque by decision (see below). Generation handling is complete: selectors and reference resolution match the reference pair-exact behavior above.

## Split publication and replacement (work item 21)

`qpdf --split-pages[=n]` partitions the selected pages into groups sharing one
object pool with per-group reachability pruning and renumbering, upstream `%d`
and `-NN[-MM]` naming, sliced page labels, and upfront collision validation;
partial outputs can remain if a later group fails. The job-JSON `splitPages` key
(string, `""` for bare) rides the same engine across merge, single-page and
whole-file plans with argv job-parity, and an empty valued form matches bare upstream.
`--replace-input` rewrites
the single distinct input in place with no backup files and no output operand,
through argv and job JSON (`replaceInput` as `""` or boolean, conflicting with
`outputFile`, `pages`-less plans, and `empty`); failed publications leave the
target untouched, matching the reference, which likewise creates no backups.
Both reject contradictory combinations explicitly; typed callers split through `PdfPageMergeOptions.SplitPages` (pages per group) on the shared merge engine, rejecting non-positive sizes with `ArgumentOutOfRangeException`. The differential lane replaces a shared two-page file
on both sides with trio validation and no backup files created.

## Collation and page labels (work item 20, part 2)

`PdfPageMergeOptions` carries collation (absent concatenates; one group size
applies uniformly, otherwise one per source with round-robin exhaustion),
rotations, and explicit labels. Source page-label trees merge through the
shared copy engine: per-output-page resolution over each source number tree in
output order concatenates per-source entries with page-index offsets, unlabeled
pages contribute fabricated continuation entries, covering starts are adjusted
with explicit starts on every computed entry, and runs compress against the
previous entry; indirect label dictionaries resolve through the owning source
engine. `--set-page-labels` (`first-page:[type][/start[/prefix]]` with `n`,
`rN`, `z`) and job-JSON `setPageLabels` replace merged labels with a fresh
`/PageLabels` number tree (first entry at page 1, strictly increasing), while
`--remove-page-labels` and job-JSON `removePageLabels: true` drop merged labels
everywhere including whole-file rewrites, and are contradicted only by
combining them with set. Bare whole-file rewrites carry the source tree
verbatim; split groups slice merged labels with restart behavior and drop
explicit specifications, matching the reference. Malformed trees fail
explicitly instead of warning. Prefixes encode as upstream does (ASCII literals,
PDFDocEncoding hex, otherwise UTF-16BE with byte-order mark); default start 1
stays omitted from explicit specifications.

## Multi-file merge (work item 20, part 3)

`qpdf --pages` takes repeated file/range groups (positional or `--file=`/`--range=`
forms, `.` for the primary input) with repeatable `--collate[=n[,m,...]]`
(uniform, per-source, or bare single-group); `--collate` is accepted at the main level
as upstream places it, while the inside-`--pages` form keeps working as leniency and both
forms share one grammar with identical bytes. Single groups without collation keep
the exact legacy single-file behavior. Job JSON accepts multi-entry `pages`
arrays concatenating in order plus a `collate` key (`""` for bare, otherwise `n[,m,...]`)
with argv job-parity. Group counts other than one value or one per source fail explicitly,
as do out-of-range pages, without publication. Ranged groups interleave in identical page
order on both sides (see the sharing note above).
Page selection carries complete option evidence (ordered/duplicate/first-last coverage,
explicit range failures, work-budget enforcement, select/merge differentials).

Bare `--empty` writes a zero-page document (fresh catalog with an empty page
tree) and every inspection accepts the empty input through argv and job JSON:
`--show-npages` reports 0, `--show-pages` lists nothing, `--show-object`,
`--show-xref`, `--json`, `--list-attachments`, and linearization inspections
read the synthetic catalog, while `--check` fails explicitly on the page-less
file, matching the reference exit classes throughout. Dot selections against
the empty input fail explicitly here while the reference crashes; no crash
parity is claimed. The differential lane covers bare rewrites (zero pages on
both sides with failing checks) plus show-npages/check exit parity.

## Ordinary rotation (work item 20, part 1)

`PdfPageRotation` (`Set`/`Add`/`Subtract` over output positions, any multiple of
90 normalized modulo 360) rides the multi-source selection and the single-page
typed selection (`SelectPageAsync` rotation overloads with default and explicit
budgets, proven by the package consumer); `qpdf --rotate` and
job-JSON `rotate` share the `[+|-]angle[:range]` specification with the full shared
page-range grammar (spans, descending ranges, `rN`/`z`, exclusions, `:odd`/`:even`,
duplicates preserved and applied in order, matching the probed reference). `--rotate`
is accepted at the main level as upstream places it, with the inside-`--pages` form kept
as leniency and byte-identical results; repeated specifications accumulate with later
specifications winning per page, and positions span merged outputs including
`--file=`/`--range=` groups. Typed single-page rotations are byte-identical with
the matching argv and job-JSON rotations, which transitively ties them to the
`rotate-ranges` oracle agreement. Out-of-range positions
fail explicitly. Rotation folds over the inherited
base (explicit page values beat ancestry, null counts as absent); pages without
applicable specifications keep their bytes untouched. Page labels
stay open behind source-tree merging.

## Invocation breadth (work item 18)

Response files expand once through the host with one argument per line, UTF-8
checked, budgeted per line, and leases closed; `@-` has no stdin descriptor and
fails explicitly, missing files stay literal like the reference parser, and
nested `@` tokens never re-expand. Response payloads are admitted against the memory
budget before any byte is fetched. `--job-json-file` reads the identical job
schema through the host with plan parity. Job-JSON parsing itself runs under the job
governor: input bytes are work-charged and the transient document model is
memory-admitted before construction and released with the plan. `--warning-exit-0` maps warnings to
exit 0, `--no-warn` drops warning messages while keeping exit 3 (both mirror the
reference manual), and both ride the shared plan across argv and job JSON (`warningExit0`,
`noWarn`, plus `suppressRecovery` mapping to strict recovery). The differential lane checks
warning exit parity with the oracle (plain 3/3, flagged 0/0, suppressed-message 3/3).
`--help` and `--version` return identity text as info diagnostics with exit 0, as does the
new `--copyright` notice (Lokad/MIT/12.4.2-target); valued identity forms fail explicitly, and
identity requests bypass exhausted output budgets. `--completion-bash` and `--completion-zsh` emit static subset completion scripts (no shell execution or environment lookup), `--job-json-help` prints the supported job-key subset, and `--json-help[=1|2|latest]` describes the matching inspection version; the differential lane checks exit-0 identity parity with shared markers on both sides. `--keep-files-open=y|n` and `--keep-files-open-threshold=n` are validated performance hints with identical outputs (host lease policy stays authoritative); the differential lane runs hinted merges with trios green on both sides. All other operations stay
explicitly unsupported. The differential lane checks error exit-class parity with the oracle: out-of-range page ranges, missing inputs, and unknown flags all exit 2 on both sides. `--global [options] --` limit blocks behave identically on both sides; bare limit flags outside the block are accepted here as a narrow-profile leniency while the reference rejects them, and the differential lane runs blocked selections (filters, nesting at 100 and at the upstream default 499, and loose container caps) with trios green on both sides. Over-limit filter chains stay opaque with a warning (exit 3) on both sides. Invocation parser knobs only tighten within embedding host hard ceilings: a value above an explicitly supplied host ceiling fails with BudgetExceeded, while default-constructed options leave invocation values (within their absolute bounds) in charge; value equality with a library default never counts as untouched (agreement at 499 for nesting under default options). `--no-default-limits` (bare argv, `--global` block, job `noDefaultLimits`) mirrors upstream `disable_defaults`, but only under default-constructed options: it lifts the error budget to 0 and the damaged container and filter caps to 4294967295. Explicit values, explicitly supplied host ceilings, parser nesting, and the undamaged container cap always stand, and valued, duplicate, and import combinations fail explicitly. The differential lane decodes a 30-filter chain identically on both sides under `--no-default-limits` (oracle inside its `--global` block, managed bare by narrow-profile leniency). `--parser-max-errors=n` has an independent error budget for each indirect object and compressed member (default 15; 0 removes the numeric threshold). Reaching a finite threshold returns that object as null with a warning (exit 3), rather than failing the whole job solely for the threshold. Six nearby errors still abort an object at zero; four intervening good tokens break the proximity cluster without resetting the total count. Actual loads use repair for valid xrefs as well as rebuilt xrefs: unknown words become byte strings with valid xrefs, but null elements after reconstruction, matching the reference's sanity policy. Duplicate keys consume one budgeted error and keep the last value when allowed; missing dictionary keys receive collision-free fake names after the dictionary is read, while a missing last value is null. Warnings and budgeted errors are distinct. Strict typed opening and `--suppress-recovery` retain the documented strict profile. Invocation controls now propagate into argv and job inspection plans too. An explicit finite host error ceiling rejects zero or larger invocation values; an explicitly unlimited host allows finite thresholds. Host work/memory/diagnostic failures and cancellation propagate, and scan effort no longer shares the semantic error count. Recovery warnings summarize trial damage; actual object loads report their repairs. The explicit parser lane compares 306 object values/exits against qpdf across finite/zero limits and valid/rebuilt xrefs, and 18 rewrites retain the repaired/null values with clean oracle checks and independent page counts. The large-storm differential now agrees under default and finite budgets as well as zero and `--no-default-limits`. The entry stays partial pending long real tokens, lexical damage, complete damaged-container transitions, orphan-key/error interactions, nested-load evidence and trial-allocation lifetime review. The default nesting cap is tighter (256 vs 499) for stack safety with agreement at 499; tight container caps fail fast (exit 2) while the oracle warns and skips the oversized object during recovery (exit 3), with agreement at loose caps.

Output-neutral diagnostics (work item 81): `--progress` reports counted-byte
milestones (25/50/75/100%) while the reference streams object-based
percentages during the write; `--verbose` appends an operation summary;
`--report-memory-usage` reports the exact governor peak while the reference
reports a best-effort figure; `--test-json-schema` validates generated
top-level keys against the matching `--json-help` description and fails
explicitly on drift. Report text stays implementation-defined on both sides
with no stable cross-implementation contract. All four flags are exit-neutral
and output-neutral, and the differential lane checks exit-0 parity, green
trios, and byte-identical outputs with and without each flag (plus matching
top-level JSON keys for the schema check).

## Bounded repair (work item 17)

Strict parsing runs first; only its failure engages bounded repair, which scans
for object headers, trial-parses each candidate, keeps the last valid
occurrence, aborts after MaxParserErrors failed trial parses (default 15; 0 removes
the threshold), and fills
unscanned gaps as free. Streams with indirect lengths are
skipped by the scanner. The tail trailer wins when its root resolves, otherwise
the last scanned catalog wins; files without a catalog fail. Repaired output is
rewritten, never the original bytes. The CLI repairs by default and emits a
recovery warning (exit 3); `--suppress-recovery` keeps strict failure. Typed
selection and inspection stay strict. `--ignore-xref-streams` classic-table
reading is covered under incremental and hybrid chains above.

## Selector audit (work item 15)

Indirect `/Filter` and `/DecodeParms` resolve through the document engine,
including indirect predictor integers and `/EarlyChange`; unresolvable or
mistyped indirect specs fail explicitly. Cross-reference stream filter specs
stay direct-only. Empty documents resolve `FirstAndLast` to an empty selection
with a zero-page rewrite; cyclic resource graphs terminate through memoized
copies; escaped names and strings round-trip value-faithfully with canonical
re-escapes. The clean package consumer validates single- and multi-page typed,
argv, and job paths with source-mapping checks.

## Single-page selection (work item 06a)

Typed `Qpdf.SelectPageAsync` copies one page by 1-based index through the
same reader, graph copier, and classic-xref writer. Reachable shared objects
are copied once with destination identities registered before descending;
`/Parent` links are dropped and the four inheritable attributes are
materialized from ancestry (missing `/MediaBox` fails). Streams travel
opaque with recomputed `/Length`; output carries a fresh catalog, no `/ID`,
and generation 0 throughout. Every serialization runs a counting pass and
an emitting pass whose lengths must agree (the M1 replay canary); repeated
runs are byte-identical. Page-tree references fail explicitly; page references across
pages follow the item-13 multi-page and item-14 prune policies above: annotations,
forms, and multi-page surgery stay open for M3/M5.

## Command adapters (work item 06b)

`Qpdf.ExecuteAsync` (argv) and `Qpdf.ExecuteJobAsync` (job JSON) parse a
narrow single-page subset into one shared selection plan executed by the
typed path. The argv subset follows upstream spelling (`--pages`, `--file`,
`--range`, `.` for the primary input, the terminating `--`); the job subset
uses upstream field names (`inputFile`, `outputFile`, `pages[].file`,
`pages[].range`). An omitted range selects all pages; ranges,
collation, multiple sources, `--empty`, `--replace-input`, stdout `-`,
response files, flexible operand order, and unknown options fail
explicitly. All documented password/encryption flags reject with the
stable `UnsupportedCryptoOperation` diagnostic and exit 2 without retaining
or echoing values; encrypted inputs map to the same code. Adapter outcomes
reuse the execution-result mapping, including the warning exit. No inventory
entry changes: parsing a name never completes its behavior contract.

## Writer versions, IDs and stream endings (work item 22a)

`--min-version` raises the output header when the greatest input version is
lower; `--force-version` sets it exactly even when lower. Both accept
`major.minor[.extension-level]` for the supported headers 1.0-1.7 and 2.0;
other values fail as invalid arguments. A positive extension level emits a
direct catalog `/Extensions << /ADBE << /BaseVersion /M.m /ExtensionLevel N >> >>`.
Forced versions below 1.5 yield classic xref tables with object streams disabled first, per upstream (work item 22b);
no other feature downgrading is claimed.

Trailer `/ID` is always emitted. The first word preserves the source trailer
identifier when present, otherwise it repeats the generated second word. The
default second word is a deterministic MD5 content hash (placeholder zeros
hashed, then replaced by the digest, preserving the counting-pass length),
so repeated runs over the same inputs and options are byte-identical. This
differs intentionally from upstream time-based random IDs: the library has no
ambient clock, and the contained profile favors reproducibility. `--static-id`
selects the upstream test constant `<31415926535897932384626433832795>`
(static wins when both ID flags are given). `--deterministic-id` is accepted
and matches the default deterministic behavior.

Stream `endstream` handling follows upstream: by default the serializer
writes `data` + `endstream` with no extra newline; `--newline-before-endstream`
always writes `data` + `\\nendstream`, doubling a trailing newline when present.
`/Length` stays recomputed and direct. All paths (typed, argv, job JSON with
`minVersion`, `forceVersion`, `deterministicId`, `staticId`,
`newlineBeforeEndstream`) share one negotiation, identifier, and serializer
implementation with counting-pass replay checks and output budgets.

## Writer object-stream modes (work item 22b)

`--object-streams` accepts `disable`, `preserve` (the default, matching
upstream), and `generate` through argv (`--object-streams=mode` or
`--object-streams mode`) and job JSON (`objectStreams`), with invalid modes,
bare flags, and duplicates rejected as invalid arguments. `disable` always
writes classic xref tables; `preserve` re-compresses exactly the objects that
arrived compressed (inputs without object streams stay classic) without
reproducing exact original stream groupings; `generate` packs every eligible
non-stream object (catalog, page tree nodes, streams, and signed byte-range
dictionaries stay uncompressed) into new object streams of at most 100
members, distributed evenly. Any object streams imply a cross-reference
stream (`/Type /XRef`, `/Size`, `/Root`, `/ID`, `/W`, dense coverage, no
`/Index`) instead of a classic table, with `startxref` pointing at the xref
stream object. Generation raises the header to at least 1.5; a forced version
below 1.5 disables object streams first, per upstream. Payloads and xref
entries are emitted uncompressed with no `/Filter` or predictor: valid PDF
that our reader and the independent reader accept, but larger than upstream
Flate-packed output. Flate packing arrives with the compression controls
(work item 23). Typed callers default to `preserve` like the CLI.

## Preserve-unreferenced objects (work item 22c)

`--preserve-unreferenced` (bare; job JSON `preserveUnreferenced` as `""` or
boolean) keeps every input object the page selection did not reach, in
ascending original object-number order per source, appended after the
reachable graph with shared references remapped through the same memoized
copier. Preservation covers the primary input on merges (other sources keep
only their selected pages) and is suspended under `--empty`, matching the
reference on both counts. Detached pages and detached trees survive as loose
objects while rebuilt roots (the input catalog plus the selected page-tree
chain) are superseded, and preserved metadata stays linked from the rebuilt
catalog with its bytes intact. Fresh catalog/pages stay 1/2. Preserved
objects participate in `--object-streams` packing (compressed-ness or
generation eligibility apply to them like any selected object) and are
included in every split group via the reachability closure, so no dangling
references are emitted. Intentional differences from upstream: preserved
objects keep ascending copy order rather than original-ID order (identical on
single-source selections; multi-source pools renumber per source);
object/xref-stream containers are not carried over while their members are
preserved decoded as direct objects; objects that fail to load or copy are
skipped under one aggregated `UnsupportedFeature` warning instead of failing
the job. Follow-up closed: unknown catalog keys now ride the shared copy engine on ordinary paths (see the catalog policy above).

## Stream compression controls (work item 23a)

`--stream-data` (`compress`/`preserve`/`uncompress`), `--compress-streams`
(`y`/`n`, bare means `y`), `--decode-level` (`none`/`generalized`/
`specialized`/`all`), `--recompress-flate`, and `--compression-level`
(`1..9`) are accepted through argv (both `=` and space forms) and job JSON
(`streamData`, `compressStreams`, `decodeLevel`, `recompressFlate`,
`compressionLevel`), with invalid, bare-required, and duplicate forms
rejected. Resolution matches upstream mappings: `compress` implies Flate
output plus generalized decoding, `uncompress` implies generalized decoding
without output compression, `preserve` implies neither; explicit
`--compress-streams`/`--decode-level` override the `--stream-data` mapping
(upstream applies these in argument order instead). Already-Flate streams are
left untouched unless `--recompress-flate` is combined with a covering decode
level; empty results stay uncompressed; indirect filter specs resolve through
the document engine. Decode coverage mirrors inspection; `all` additionally decodes 1- and 3-component DCTDecode streams (work item 32) and warns per undecodable stream, while filters above the requested level stay opaque silently. Four-component CMYK/YCCK JPEGs stay opaque with a warning because the approved codec cannot preserve their raw samples. Corrupt streams and
bad specs fall back to opaque bytes under a warning; `/Crypt` is never
decoded or stripped. Levels bucket to BCL Flate effort (`1` fastest, `2..8`
optimal, `9` smallest size; default optimal) with governed bounded encoding
whose zlib output round-trips through the strict decoder. Intentional
difference: the default profile preserves stream encodings opaquely instead
of upstream compress defaults, so existing previews stay byte-stable without
flags. `--zopfli` reports that Zopfli is not enabled in this build and exits
with an error; `QPDF_ZOPFLI` has no host environment capability yet and is
treated as unset (matching upstream unset behavior). Generated object/xref
streams Flate-encode whenever the resolved compression request enables compression (work item 23b); otherwise they stay raw.

## Container Flate packing (work item 23b)

Generated object-stream payloads and cross-reference entries are
Flate-encoded with plain `/FlateDecode` (no PNG predictor) whenever the
resolved stream-compression request enables compression, honoring
`--compression-level` buckets through the same governed BCL pipeline as
regular streams (deterministic bytes, so counting and emitting passes still
agree). Otherwise containers stay raw. No predictor is emitted, unlike
upstream xref streams: readers must not require decode parameters, and the
strict decoder accepts plain Flate.

## Document info and metadata (work item 24a)

The output trailer carries the input `/Info` dictionary and the output
catalog carries the input `/Metadata` stream by default (copied through the
shared memoized copier, so streams honor the compression knobs and indirect
values remap; multi-source output takes the primary input, single-source and
split groups likewise). `--remove-metadata` omits the catalog stream while
keeping info; `--remove-info` reduces `/Info` to a lone `/ModDate` entry (or
omits `/Info` when absent) and also omits the metadata stream, matching
upstream. Both flags accept argv bare and job-JSON empty-string/boolean
forms. Unpreservable extras are skipped under one aggregated warning instead
of failing the job; old catalog structure is never relinked.

## Attachment list and extract (work item 24b)

`--list-attachments` prints one `key -> obj,gen` line per embedded file
plus filename, description, MIME type, dates, checksum, and size detail
lines; `--show-attachment=key` (repeatable, last wins) extracts fully
decoded bytes, falling back to raw bytes with a warning where decoding is
unavailable. Both share the typed `ListAttachmentsAsync` /
`ExtractAttachmentAsync` engine over name-tree traversal with cycle, depth,
and work budgets: malformed tree skeletons fail explicitly while bad entries
are skipped with warnings, and unknown keys fail without echoing key bytes.
Intentional differences: extraction publishes to the declared output file
instead of stdout (no stdout binary contract exists); listing detail lines
use this profile's stable diagnostic format rather than `--verbose` gating;
keys compare as decoded text with exact ordinal match. Selection output still
drops `/Names`; attachment add/copy/remove follow separately.

## Attachment add and remove (work item 25a)

`--add-attachment` blocks (file plus `= `/space nested options, repeatable,
`--`-terminated) and repeatable `--remove-attachment=key` combine with page
selection, replacement, and split groups (every group carries the full set)
through one shared engine: removals first (unknown keys fail), then sequential
adds with per-add `--replace` handling and collected collision errors.
Existing attachments are preserved from the primary input and merged with
additions into a rebuilt sorted tree; output uses `/PageMode
/UseAttachments` on insertions. Intentional differences: unspecified dates
are omitted rather than stamped (no ambient clock reads); keys and names
encode as ASCII literals or UTF-16 hex; trees use flat leaves chunked past
64 entries without balancing.

## Attachment copy and typed editing surface (work item 25b)

Repeatable `--copy-attachments-from FILE [--prefix=] --` blocks copy
entries from other files through the same engine after removals and
additions: copied filespecs are rechained per source with dense numbering,
prefixed keys colliding as a group per source file, empty `--prefix=` values failing explicitly where upstream copies unchanged, and unknown sources
failing without echoing key bytes. Nested `--password` (argv) and job
`password` keys reject as crypto operations. The typed surface
(`QpdfAttachmentSpec`, `QpdfAttachmentCopy`, `Add/Remove/CopyAttachmentsAsync`)
shares the engine and reports the final attachment count without reopening
the output. Intentional differences: no ambient clock reads; trees stay
flat-chunked past 64 entries.

## PDF JSON inspection (work item 26a)

`--json[=1|2|latest]` with repeatable `--json-key` (plus job `json`/`jsonKey`
and typed `GetDocumentJsonAsync`) emits the upstream top-level layout with
stream data omitted: canonical v1/v2 name and string encodings (u:/b:/n:
forms, arbitrary-precision numbers with upstream real normalization),
sorted dictionary keys, per-key selection with version rules, page entries
with contents/labels/per-page outlines, number-tree page labels, recursive
outlines with page positions, attachment details with ISO-8601 dates, and a
constant unencrypted report for the encrypt key. Page entries list direct image
XObjects with dimensions, bits, colorspace, filter names, aligned decode parms
and level-gated filterable flags, matching the reference on raw, dimension-less,
photo and filtered fixtures (both decode levels). Intentional differences:
JSON requires a declared output file (no stdout contract);
named outline destinations do not resolve. The differential lane compares the pages key structurally against the oracle (version, object and contents references, page positions).

## PDF JSON stream modes and serialization output (work item 26b)

Repeatable `--json-object` selectors (trailer or obj[,gen], canonicalized)
filter the objects, objectinfo, and qpdf dictionaries across versions.
`--json-stream-data=inline` embeds base64 payloads (decoded per the decode
level with raw fallback and slimmed dictionaries); `=file` publishes sidecar
outputs named after the output file or `--json-stream-prefix` and records
`datafile` paths. `--json-output[=2|latest]` implies JSON with inline
payloads, decode level none, the qpdf key by default, and no version or
parameters keys. The `acroform` key reports field type inheritance, qualified
names, values, choices, and widget annotations. Intentional differences: JSON
requires a declared output file (no stdout contract); version 1 ignores
stream payloads; `--json-output` with version 1 fails.

## PDF JSON import (work item 27a)

`--json-input` (plus job `jsonInput` and typed `CreatePdfFromJsonAsync`)
converts qpdf JSON version 2 back to PDF: the header version sets the output
version (with min/force overrides), stream dictionaries keep their semantics
with recomputed lengths, and objects are renumbered densely with dangling
references nulled. Import validates structure strictly (required qpdf pair,
versions, object keys, value/stream exclusivity, payload presence, base64 and
hex grammars, name escapes) while ignoring unknown keys for forward
compatibility. Inline base64 payloads are memory-admitted against the text-length
bound before decoding. Intentional differences: JSON input requires a declared output
file; object generations must be 0; stream and metadata writer options cannot
combine; PDF update from JSON is a supported companion path (work item 27b).

## PDF JSON update (work item 27b)

`--update-from-json` (plus job `updateFromJson` and typed
`UpdatePdfFromJsonAsync`) merges qpdf JSON into an existing PDF: every
existing object stays addressable while JSON entries replace or add objects
by number with the same value grammar as creation. Existing streams keep
their bytes when neither data nor datafile is given; new streams still
require exactly one. The trailer is replaced only when the JSON carries one,
the JSON pdfversion overrides the output version when present, and the input
document identifier is preserved. A `pushedinheritedpageresources` value of
true is rejected. Intentional differences mirror creation: generations must
be 0, stream and metadata writer options cannot combine, and outputs are
renumbered classic files.

## Content tokenizing (work item 28)

An internal content-stream tokenizer covers operators, numbers, strings,
names, arrays, dictionaries, and inline images with upstream-compatible
boundary rules: image data ends at a delimiter-surrounded EI validated by a
ten-token lookahead, falling back to the last candidate, with the separator
before EI included in the payload. No command surface changes yet; page
content checks, image reporting, and appearance work build on this next.

## Page content operations (work item 29)

`--coalesce-contents`, `--normalize-content=y/n`, and
`--remove-unreferenced-resources=auto/yes/no` (plus job keys) share one
engine over copied pages: coalescing joins decoded streams, normalization
rewrites newlines and recodes strings/names with warnings on damage, and
pruning drops unused font and XObject entries with form recursion and
unresolved-name guards. Intentional differences: content flags combine only
with page operations and whole-file output, never with inspection; damaged
content keeps original bytes where upstream writes partial output. The differential lane compares
coalesced bytes exactly and prunes an unreferenced /F2 under page operations with byte-identical
decoded payloads; pruning engages under page operations (including --split-pages) while whole-file
output without page selection keeps resources, matching the upstream page-spec gating. Bare `--preserve-unreferenced-resources` (plus job key) is a synonym for `--remove-unreferenced-resources=no` with last-wins precedence on conflict and no valued form.

## Inline image externalization (work item 30a)

--externalize-inline-images with --ii-min-bytes (default 1024, plus job keys) mirrors the upstream tracker: threshold on image bytes, abbreviation expansion, device color mapping with resource lookup and warn-and-keep on miss, unique /IIm naming, byte-identical kept images, and Flate-aware page and indirect-form rewriting before other content ops. Replaced content and resource identities drop from the pool when nothing references them anymore (shared streams and dictionaries stay), and the writers emit free cross-reference entries for the resulting holes. The differential lane externalizes a shared inline-image fixture on both sides with matching /IIm1 content. Intentional differences: direct form XObjects stay untouched, content flags combine only with page operations and whole-file output, and damaged content keeps original bytes with a warning.

## Overlay and underlay (work item 30b)

Repeatable --overlay and --underlay blocks (--file=file or positional file, --to=, --from=, --repeat= with empty allowed, plus job keys) mirror upstream stacking: underlays in order, then the original page as /Fx0, then overlays in order, each placed from TrimBox fallbacks with centered shrink-to-fit matrices that invert destination rotation and scale. Page ranges use the full shared grammar (spans, descending ranges, `rN`/`z`, exclusions, `:odd`/`:even`, duplicates preserved), with unused overlay pages left unembedded on both sides. Block options require the `=` form like the reference (space-separated values fail on both sides with exit 2), and empty ranges select nothing on both sides. Each applied overlay page also contributes fresh per-placement annotation clones appended to the destination `/Annots`: clone rectangles move with the same placement matrix (missing or malformed rectangles behave as all-zeros), appearance-stream matrices concatenate with it (missing matrices stay absent under identity placements; malformed matrices behave as all-zeros), page-boundary references prune to null, and non-dictionary entries are skipped with a warning, all matching the reference. Widget annotations additionally import their top-level fields through the shared form machinery (work item 121): parent-link top lookup with indirect field identity, one fresh field copy per placement shared across radio siblings, per-placement default-resource copying with destination-wins conflict renaming, document default-appearance and quadding overrides, field default-resource reset with conflict-map appearance rewriting, merge collision renaming, and NeedAppearances propagation, all matching the reference. Widgets without an importable field (direct annotations, formless sources, bare tops) fall back to plain clones, and lookup notes stay informational so exits stay 0, both matching the reference. Intentional differences: inherited /UserUnit on output pages is not inverted; damaged overlay content warns and skips instead of emitting partial forms; a direct-dictionary field parent that the reference fails on (exit 2) stays a plain clone with a note (exit 0); cyclic field parents import with a warning and a valid structure where the reference emits degenerate names; appearance-stream resource remapping stays deferred with rendering preserved through appearance-local resources. The differential lane overlays widget fixtures on both sides with trio validation, matching field registration, values, page placement, parent links, NeedAppearances, fallback notes, collision renames, and DR conflict rewrites, plus a repeated-placement rename case.

## Rotation flattening and page-content checks (work item 31a)

--flatten-rotation (plus job key) mirrors upstream page baking: boxes rotate about the media box per 90/180/270 degrees, a counter-transform wraps the content streams, /Rotate is dropped, and annotation rectangles move with the page. Appearance form XObjects referenced from annotation /AP dictionaries (including /N, /R, /D state dictionaries) gain the same rotation matrix composed over any existing matrix, while direct-stream appearances stay untouched as upstream leaves them. Direct /UserUnit entries survive on flattened pages while inherited units stay uninherited on both sides. Pages without actionable rotation stay byte-identical. The page-content check now tokenizes page content streams (honoring the check decode level) instead of reporting skipped. Intentional differences: damaged pages warn and stay as is, and form-field restructuring is left to later appearance work. The differential lane flattens a checked-in /Rotate 90 page on both sides with trio validation, a swapped MediaBox, a dropped /Rotate, and byte-identical decoded wrap streams, plus an appearance-bearing rotated page with matching appearance matrices and kept user units on both sides.

## Annotation flattening (work item 31b)

--flatten-annotations with ll, print, or screen (plus job key) mirrors upstream stamping: appearance streams become /Fxo form XObjects placed by annotation-derived matrices with flag filtering and page-rotation counter-rotation, and flattened annotations leave /Annots. Widgets defer to generated appearances when the document needs them, and appearance-less annotations drop only when they carry an appearance dictionary. Appearances referencing AcroForm default-resource fonts stamp verbatim with the DR font intact, matching the reference (flatten-dr lane case). Intentional differences: indirect damage keeps originals with a warning instead of failing.

## Appearance generation (work item 31c)

--generate-appearances (plus job key generateAppearances) mirrors upstream generation order (generate, then flatten-annotations, then flatten-rotation): when the catalog needs appearances it regenerates text/list appearance streams from field values and default appearance (ASCII fallback, WinAnsi/MacRoman encodings resolved through appearance resources, then AcroForm default resources (field-level /DR ignored like the reference; page /Resources never consulted; missing DR fonts merged into kept appearance resources), BMC/EMC value rewrite, list highlight) and synchronizes checkbox /V and /AS state (radio buttons keep both states with a warning, matching the reference exit 3; quadding is ignored in generated placement on both sides, matching the reference), then clears the condition so a later --flatten-annotations stamps fresh appearances. `--remove-acroform` (plus job key `removeAcroform`) is the explicit form prune switch threaded through every publication path; the differential lane checks
both sides drop the dictionary with trios green. `--remove-structure` (plus job key
`removeStructure`) is the explicit structure prune switch with the same coverage. Appearances are rewritten raw (no /Filter). Intentional differences: field context resolves through preserved /Parent links with subtree search as fallback and parent-held radios degrade to skip; widgets unreachable from the traversed form generate only when the output form keeps field registrations, with whole-file paths warning per unreached widget (exit 3) and selection paths skipping silently while dropping the emptied form entirely (generate-unreachable lane case); registration-free forms stay registration-free on every path, and the generated flag clears once generation has run; malformed parent links (a /Kids entry without a matching /Parent back-link) stay preserved silently on copy paths where the reference corrects and warns (exit 3, tested difference), while `--check` warns on both sides with exit 3 (unit evidence plus the forms-check lane case); wider resource lookup (page resources, conflicting dictionaries) stays explicitly open; signature fields are skipped and pushbuttons warn; damaged appearance streams warn and keep the field.

## Governed JPEG decoding (work item 32)

DCTDecode executes at decode level `all` through a governed ImageSharp 3.1.12 adapter: frame dimensions and component counts are read from the JPEG header before any pixel buffer is allocated, work and memory budgets are charged up front, and 1-component (grayscale) and 3-component images decode to raw sample bytes with the filter removed. Intentional differences: four-component CMYK/YCCK JPEGs stay opaque with an `UnsupportedFeature` warning (final decision recorded under codec feasibility above: the pinned assembly offers no CMYK pixel type or raw-component decoder surface; the `cmyk` differential pins oracle-0/managed-3 exits with recorded oracle samples); lower decode levels keep DCT streams opaque silently; corrupt JPEG data warns and stays opaque. Content and overlay paths still gate on `specialized` and never decode images implicitly.

## Image optimization (work item 33)

--optimize-images (plus job key optimizeImages) recompresses eligible image XObjects with DCT exactly when the JPEG is strictly smaller: 8 bits per component with /DeviceGray or /DeviceRGB, width, height, and area strictly above --oi-min-width/--oi-min-height/--oi-min-area (defaults 128/128/16384, 0 disables), and already-DCT images left alone unless --jpeg-quality (0..100, mapped 0 to 1, default 75) forces recompression. Replacement keeps every dictionary key except /Filter//DecodeParms, which become /DCTDecode, so /SMask references and /Mask arrays survive recompression. Recursion follows form XObjects with a cycle guard and shared images stay consistent. Intentional differences: four-component CMYK has no raw-sample encode path and stays as is; 1-bit image masks and colorspaces outside /DeviceGray//DeviceRGB (including /Indexed, /CalGray, and /Separation) are skipped; every skip is silent; large inline images externalize first (unless --keep-inline-images) and join the same eligibility rules, with the conventional pre-EI separator trimmed before sample validation. The differential lane optimizes a shared noisy photo on both sides (matching DCT dimensions and colorspaces at an explicit quality), proves byte-identical kept payloads under disabling thresholds, and keeps inline images inline with `--keep-inline-images` on both sides, plus a masked-image case proving DCT recompression with the /SMask reference intact and the mask stream left flate on both sides.

## Inline optimization and codec hardening (work item 34)

Image optimization covers externalized inline images under the same shrink-only rules, and the filter layer gained adversarial cases: truncated JPEGs and two-component frames stay opaque with a warning, chained Flate+DCT decodes with --recompress-flate, dimension mismatches skip silently, and encode budgets fail explicitly. Attachment extraction now decodes DCT streams, and the streams check passes valid JPEGs at decode level all. Authored progressive JPEG fixtures now verify decoded samples and allocation ownership; the pinned encoder still does not emit progressive JPEG. Four-component CMYK/YCCK stays opaque by the recorded decision (genuine four-component inputs covered by unit tests and the `cmyk` differential).

## QDF editable view (work item 35)

--qdf (plus job key qdf) with --no-original-object-ids (plus job key
noOriginalObjectIds) emits the editable QDF view: a binary marker plus
%QDF-1.0, per-object original-ID and page/contents comments, sorted keys with
indented values, uncompressed streams framed with indirect lengths in the
following object and ignore_newline tolerance, and a classic cross-reference
table. QDF implies generalized decoding with content normalization unless
explicitly overridden (--normalize-content=n keeps newlines,
--compress-streams=y restores Flate, --decode-level=none still normalizes).
--object-streams=generate packs eligible objects into unfiltered object
streams with per-member comments, upstream-style /First and offsets landing
on values, and an unfiltered cross-reference stream (/W [ 1 f1 f2 ]).
Intentional differences: generated grouping follows writer packing rules
(catalog/pages stay direct) rather than upstream packing; page /Parent links
stay dropped per catalog policy; objects without a source carry no original-ID
comment. The differential lane covers a two-page QDF object-stream case
(oracle trio, marker agreement, upstream fix-qdf round trip green). The
fix-qdf and zlib-flate companions remain later work items.

## Managed fix-qdf repair (work item 36)

The typed `FixQdfAsync` companion repairs edited QDF through the shared governor and staged host publication. Like upstream fix-qdf it recomputes object-stream `/N`, `/W` and `/First` with renumbered member tables, every stream length (honoring `ignore_newline`), classic cross-reference tables with trailer `/Size`, unfiltered cross-reference streams, and startxref offsets, while enforcing sequential object numbering. Classic QDF stream lengths now pair through renumbering so multi-stream files keep their length objects. Intentional differences: both paths are explicit host paths with no standard input or output default; carriage returns, empty inputs, truncated structures, and non-sequential objects fail explicitly instead of echoing degenerate output. The fix-qdf and zlib-flate companions stay outside the main flag inventory.

## Managed zlib-flate facade (work item 37)

The typed `RunZlibFlateAsync` companion compresses raw bytes or uncompresses zlib-wrapped bytes between explicit host paths through the governed Flate pipeline: strict header, framing, and Adler-32 checks on inflate with per-chunk memory budgets, and level buckets 1..9 (1 fastest, 9 smallest, default optimal) on deflate. Modes and out-of-range levels fail explicitly without publishing output. Intentional differences: truncated input fails instead of exiting 3 with partial output; compression level 0 and values above 9 fail instead of passing through to zlib; there is no Zopfli support and no host capability supplies `QPDF_ZOPFLI`, which stays treated as unset. The differential lane round-trips both directions against oracle zlib-flate, checks corrupt-input exit parity, and asserts level ordering. The companions stay outside the main flag inventory.

## Linearization checking (work item 38)

The linearization check detects the parameter dictionary in the first bytes, validates its keys (/H bounds, integer /O /E /N /T with /N matching the traversed pages, optional integer /P), resolves hint streams by cross-reference offset, decodes raw or Flate-encoded hint data, and parses the page-offset, shared-object, and outline tables with a governed bit reader under file-bounded counts. A file whose /L disagrees with its size reads as not linearized, matching upstream. Intentional differences: defects fail explicitly (exit 2) where upstream warns (exit 3); managed linearized outputs validate through this check on both sides (work item 39). `--show-linearization` shares the engine and likewise fails explicitly on unreadable hint tables where upstream prints a partial dump. The differential lane checks oracle-linearized files plus page-count and hint-length mutations on both sides.

## Linearized writing (work item 39)

`--linearize` (plus job key `linearize`) emits linearized web-optimized output through a two-pass writer over the shared selection engine: objects are classified into upstream-style parts (catalog in part 4, first page with its private then shared objects in part 6, later page-plus-private runs in part 7, remaining shared objects in part 8, pages tree plus unreached objects in part 9), renumbered so the second half sorts low, counted with placeholder dictionaries and zeroed cross-references, then emitted with generated page-offset/shared-object hint tables and padded reservation fields (200-space linearization dictionary, 21-wide `/Prev`) under a replay check that the emitting pass matches the counting pass plus the hint length. Hints Flate-encode only when the resolved stream-compression request enables compression (`--stream-data=compress`); `/ID` defaults to a deterministic content hash with `--static-id` selecting the upstream test constant. `--object-streams=generate` (and `preserve` over compressed inputs) publishes streamed linearized output: eligible objects pack into Flate object streams (pages and the catalog stay direct), members resolve to their container during part classification with page-shared containers winning placement, renumbering sorts uncompressed units low per half with container members numbered after their half, and raw cross-reference streams replace both classic tables under the same two-pass replay check. QDF is silently disabled, as do `--json`, JSON import, attachment inspection and `--check` combinations fail explicitly. Intentional differences from the oracle layout: cross-reference streams stay unfiltered (matching the QDF xref-stream profile) where upstream Flate-encodes with a PNG predictor; the outline hint table is omitted whenever outline objects compress (as observed upstream); member numbering follows container encounter order rather than upstream packing. Outlines ride the selection into the output catalog (first page when the catalog requests outline mode, otherwise closing part 9) with a generated outline hint table validated by offset cross-checks; outline destinations into non-primary merge inputs prune to null under the per-source copy policy. `--linearize-pass1` captures the counting pass, and `--check-linearization` and `--show-linearization` inspect through the shared engine. The differential lane compares managed and oracle linearized outputs on one- and two-page selections with oracle trios green on both sides plus independent page-count agreement. Parent-field/default-resource handling, CMYK/YCCK raw samples, and codec allocation/cancellation accounting stay explicitly open.

## Exclusions and differences

Passwords, encryption/decryption, encryption inspection, cryptographic options,
permission enforcement, signing and signature validation are out of scope.
Reject encrypted input structurally; do not prompt for or read passwords.
`--remove-restrictions` is excluded. Every password, permission, inspection, and
restriction flag fails explicitly with `UnsupportedCryptoOperation` (exit 2) across argv,
job JSON (top-level, pages, overlay/underlay, attachment-copy, and encrypt-block keys),
and encrypted file inputs, publishing no output; all 49 exclusion entries carry negative
evidence. Ordinary document IDs, deterministic IDs
and non-secret file checksums remain part of structural processing.

Rendering, OCR, text extraction/layout and general document authoring are not
project goals. Do not claim PDF/A, PDF/X or PDF/UA conformance from a rewrite.

There is no vendored code, qpdf/libqpdf binding or upstream runtime dependency.
The differential runner invokes the pinned qpdf executable as a development
oracle only. It remains separate from production and ordinary tests.

The target reference build has JPEG support and no optional Zopfli support.
`--zopfli` will follow that build profile's explicit unavailable behavior;
ordinary Flate compression remains required. Host limits remain enforceable
even when a caller disables qpdf-compatible default parser limits. A shared budget-enforcement suite proves every public entry path
(selection, inspection, content transforms, overlays, linearization, QDF, split, rotation,
appearances) fails explicitly with `BudgetExceeded` and publishes nothing under exhausted budgets.
Every public entry path constructs its governor with the caller's cancellation token,
so interval checks and codec entry/row checks observe cancellation instead of running blind.
Every input open and staged-output creation charges one host operation and one open handle
before touching the host, so per-job handle budgets bind concurrent leases (including
overlay second sources and linearize pass-one outputs) even against permissive hosts;
reads through a governed lease charge one host operation per underlying host call, so
one-byte short reads cannot bypass the budget; admission runs before acquisition and
ownership starts at acquisition, so budget failures never leak leases; response inputs
are admitted against the memory budget before any byte is fetched; duplicate opens of
one path keep the publication conflict until the last lease closes.

## Progression and dependency gates

| Phase | Intended result |
| --- | --- |
| M0 | Build/package baseline, inventory, reference runner and dependency probes. Scaffold, runners, fixtures and JPEG/Flate probes are present; a real-executable smoke remains. |
| M1 | Minimal strict classic-xref read/select/write path, shared typed/argv/job entry points and a real package consumer. |
| M2 | Strict reader breadth: structural filters, xref/object streams and incremental/hybrid inputs. |
| M3 | Complete bounded selector: first/last and explicit ranges, source mapping, duplicates, inheritance and retained page structures. |
| M4 | Inspection, bounded repair and invocation infrastructure. |
| M5 | Cross-document import, merge/collate/split, rotation/labels and output transactions. |
| M6 | Writer controls, metadata/attachments and PDF JSON inspection/import/update. |
| M7 | Content parsing, resource analysis, overlays and limited appearance/flattening operations. |
| M8 | Governed ImageSharp JPEG processing and image optimization. |
| M9 | QDF and managed companion facades. |
| M10 | Linearization inspection/checking, then two-pass writing. |
| M11 | Resolve remaining gaps and complete the compatibility/release audit. |

Early CLI page jobs use explicit strict input, supported writer settings and
disabled resource pruning. Defaults are not silently changed. General imports
depend on form/widget identity; default page-resource pruning depends on
content analysis. Full `--check` also depends on content, JPEG and linearization
checks. Keep those entries partial until their dependencies are validated.

Each implementation step includes limits, cancellation and failure behavior.
Milestones need reproducible evidence, not just accepted option names. A known
gap can permit independent later work, but cannot be declared complete.
