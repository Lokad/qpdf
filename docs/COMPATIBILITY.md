# Compatibility target and scope

Reference: qpdf **12.4.2**, commit
`4eba95899886e851cc41d76886483b347612f2a8`, from
https://github.com/qpdf/qpdf. This target must not move silently.

**Implemented PDF operations: none.** Every inventory entry is initially
`not-started`, including excluded operations whose future runtime rejection
still needs tests. Passing the scaffold, runner and codec-probe tests
establishes inventory consistency, fixture/independent-reader contracts,
codec feasibility and package boundaries, not PDF compatibility.

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
JPEG component/color invariants and narrowly defined sample tolerances must be
established against the approved codec; the M0 probe bounds feasibility only
(see below) and full JPEG integration remains M8.

## Validation runners and fixtures

`tools/QpdfProbe` is the development-only differential runner. It invokes the
pinned qpdf 12.4.2 executable (`--version`, `--show-npages`, `--check`) and the
pinned independent reader (PdfPig `[0.1.16]`, never a runtime dependency).
`tests/Fixtures/manifest.json` records synthetic classic-xref fixtures
(`minimal-1page.pdf`, `minimal-2page.pdf`) and a truncated negative fixture,
with SHA-256, byte counts and expected page counts. Ordinary tests use these
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
coverage, all passing with no engine discrepancies: select (first- and last-page trios), merge,
uncompress (decoded stream payloads identical), normalize (normalized
content payloads identical), coalesce (decoded payloads identical), flatten-rotation
(swapped MediaBox, dropped /Rotate, byte-identical wrap streams), prune (unreferenced
/F2 dropped under page operations with decoded payloads identical), JSON structural
page-descriptor agreement (version, object and contents references, positions),
error exit-class parity (out-of-range page, missing input, and unknown flag all exit 2
on both sides), warning exit parity (3/3, 0/0, 3/3), show-npages page-count agreement, show-pages/show-object/show-xref line-for-line dumps, show-pages-with-images listings (clean and dimension-less fixtures, warning exits 3/3), ignore-xref-streams reading (hybrid/classic/stream-only exit parity plus a hybrid selection trio), remove-acroform/remove-structure invariants, forms (generated
appearance bytes and `/Fxo1` stamping match, including the `1 5.2 Td`
placement), overlay, qdf (editable view with generated object streams: trio plus QDF/ObjStm/XRef/member markers on both outputs and an upstream fix-qdf round trip), fixqdf (managed repair versus oracle fix-qdf byte-identical on edited classic and streamed files, trios green), zlibflate (both directions with corrupt-input parity and level ordering), linearized (oracle-linearized files plus page-count and hint-length mutations on both sides), linearize (managed writer versus oracle writer on one- and two-page outputs, both validating under oracle and managed checks with independent page-count agreement), linearize-streams (managed versus oracle writers with generated object streams on one-, two-page, and outlined selections, both outputs validating under both checkers), check-linearization (managed versus oracle inspection exits and messages on valid, plain, and hint-damaged files), show-linearization (managed versus oracle dumps compared line for line), linearize-pass1 (zeroed-entry shape and shorter-than-final on both sides), linearize-outlines (managed and oracle outputs validate with matching show dumps on both files), and linearize-thumbs (thumbnail part-9 placement with matching show dumps). Confirmed intentional differences: our default
output stays uncompressed where upstream compresses, and our `--overlay`
block requires a second `--` terminator before the output file while
upstream takes the output directly after the block terminator.
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
  not itself cap codec allocations. Cancellation flows through the async
  read/write overloads.

ImageSharp stays behind the future JPEG adapter and out of the public API;
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
fail, where the reference warns and continues. `/Prev`, `/XRefStm`, and
`/Extends` fail explicitly as work-item-10 scope, replacing silent
staleness with errors. Selection output normalizes to classic xref;
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
every page still rewrites the graph rather than echoing input bytes. Oversized
selections fail on work budgets and out-of-range pages fail explicitly.

## Catalog, inheritance, and widget policy (work item 14)

Preview output carries a fresh catalog with only `/Type` and `/Pages`: outlines,
names, metadata, and `/AcroForm` never transfer. Page-tree (`/Pages`) and catalog
references stay structural errors; references to already-copied objects remap, and
references to any other page prune to null so omitted pages can never resurrect.
Widget annotations travel with the page with appearance bytes preserved; the field
hierarchy is not reconstructed. Only the four inheritable attributes materialize,
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
reference; streams show their dictionary only, and non-zero generations fail explicitly. Raw
and filtered stream-data output stays unimplemented. The differential lane compares dumps line
for line on plain, stream, trailer, missing, and compressed objects.

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
same-named resources and fields stay inert without content rewriting; the output
catalog stays fresh with no merged `/AcroForm`. Output takes the highest source
version and carries per-page source mapping plus per-source page counts.

## Split publication and replacement (work item 21)

`qpdf --split-pages[=n]` partitions the selected pages into groups sharing one
object pool with per-group reachability pruning and renumbering, upstream `%d`
and `-NN[-MM]` naming, sliced page labels, and upfront collision validation;
partial outputs can remain if a later group fails. `--replace-input` rewrites
the single distinct input in place with no backup files and no output operand.
Both reject contradictory combinations explicitly; typed callers split by looping
single-output selections.

## Collation and page labels (work item 20, part 2)

`PdfPageMergeOptions` carries collation (absent concatenates; one group size
applies uniformly, otherwise one per source with round-robin exhaustion),
rotations, and explicit labels. Source page-label trees are dropped, never
merged; `--set-page-labels` (`first-page:[type][/start[/prefix]]` with `n`,
`rN`, `z`) and job-JSON `setPageLabels` emit a fresh `/PageLabels` number tree
(first entry at page 1, strictly increasing), while `--remove-page-labels` and
job-JSON `removePageLabels: true` are accepted no-ops contradicted only by
combining them with set. Prefixes store as UTF-8 bytes. Multi-file `--pages`
forms and `--collate` stay open.

## Multi-file merge (work item 20, part 3)

`qpdf --pages` takes repeated file/range groups (positional or `--file=`/`--range=`
forms, `.` for the primary input) with repeatable `--collate[=n[,m,...]]`
(uniform, per-source, or bare single-group); single groups without collation keep
the exact legacy single-file behavior. Job JSON accepts multi-entry `pages`
arrays concatenating in order; the `collate` key stays explicitly unsupported.
Out-of-range pages map to explicit failures without publication.
Page selection carries complete option evidence (ordered/duplicate/first-last coverage,
explicit range failures, work-budget enforcement, select/merge differentials).

## Ordinary rotation (work item 20, part 1)

`PdfPageRotation` (`Set`/`Add`/`Subtract` over output positions, any multiple of
90 normalized modulo 360) rides the multi-source selection; `qpdf --rotate` and
job-JSON `rotate` share the `[+|-]angle[:range]` specification with `n`, `n-m`,
comma lists, and `z`. Later specifications win per page, duplicates apply once,
and out-of-range positions fail explicitly. Rotation folds over the inherited
base (explicit page values beat ancestry, null counts as absent); pages without
applicable specifications keep their bytes untouched. Collation, page labels,
and multi-file `--pages` forms stay open.

## Invocation breadth (work item 18)

Response files expand once through the host with one argument per line, UTF-8
checked, budgeted per line, and leases closed; `@-` has no stdin descriptor and
fails explicitly, missing files stay literal like the reference parser, and
nested `@` tokens never re-expand. `--job-json-file` reads the identical job
schema through the host with plan parity. `--warning-exit-0` maps warnings to
exit 0, `--no-warn` drops warning messages while keeping exit 3 (both mirror the
reference manual), and both ride the shared plan across argv and job JSON (`warningExit0`,
`noWarn`, plus `suppressRecovery` mapping to strict recovery). The differential lane checks
warning exit parity with the oracle (plain 3/3, flagged 0/0, suppressed-message 3/3).
`--help` and `--version` return identity text as info diagnostics with exit 0, as does the
new `--copyright` notice (Lokad/MIT/12.4.2-target); valued identity forms fail explicitly, and
identity requests bypass exhausted output budgets. Completion and all other operations stay
explicitly unsupported. The differential lane checks error exit-class parity with the oracle: out-of-range page ranges, missing inputs, and unknown flags all exit 2 on both sides.

## Bounded repair (work item 17)

Strict parsing runs first; only its failure engages bounded repair, which scans
for object headers, trial-parses each candidate, keeps the last valid
occurrence, and fills unscanned gaps as free. Streams with indirect lengths are
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
`pages[].range`). Both require an explicit single numeric range; ranges,
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
Forced versions below 1.5 already yield classic xref (object streams stay
disabled until work item 22b); no other feature downgrading is claimed.

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
copier. Fresh catalog/pages stay 1/2. Preserved objects participate in
`--object-streams` packing (compressed-ness or generation eligibility apply
to them like any selected object) and are included in every split group via
the reachability closure, so no dangling references are emitted. Intentional
differences from upstream: reachable objects keep copy order rather than
being re-sorted by original ID; the input catalog, page-tree, page, and
object/xref-stream containers are excluded by contained-preview policy
(omitted pages can never resurrect; stream payloads travel through their
members); preserved metadata stays unlinked (catalog relinking is structural
editing, a later milestone); objects that fail to load or copy are skipped
under one aggregated `UnsupportedFeature` warning instead of failing the job.

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
streams stay uncompressed until work item 23b.

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
prefixed keys colliding as a group per source file, and unknown sources
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
constant unencrypted report for the encrypt key. Intentional differences:
JSON requires a declared output file (no stdout contract); pages report no
images yet; named outline destinations do not resolve; `acroform`,
`--json-object`, `--json-stream-data`, `--json-stream-prefix`,
`--json-output`, `--json-help`, and JSON input stay explicitly unsupported. The differential lane compares the pages key structurally against the oracle (version, object and contents references, page positions).

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
stream payloads; `--json-output` with version 1 fails; `--json-help` and JSON
input stay explicitly unsupported.

## PDF JSON import (work item 27a)

`--json-input` (plus job `jsonInput` and typed `CreatePdfFromJsonAsync`)
converts qpdf JSON version 2 back to PDF: the header version sets the output
version (with min/force overrides), stream dictionaries keep their semantics
with recomputed lengths, and objects are renumbered densely with dangling
references nulled. Import validates structure strictly (required qpdf pair,
versions, object keys, value/stream exclusivity, payload presence, base64 and
hex grammars, name escapes) while ignoring unknown keys for forward
compatibility. Intentional differences: JSON input requires a declared output
file; object generations must be 0; stream and metadata writer options cannot
combine; PDF update from JSON stays explicitly unsupported.

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
output without page selection keeps resources, matching the upstream page-spec gating.

## Inline image externalization (work item 30a)

--externalize-inline-images with --ii-min-bytes (default 1024, plus job keys) mirrors the upstream tracker: threshold on image bytes, abbreviation expansion, device color mapping with resource lookup and warn-and-keep on miss, unique /IIm naming, byte-identical kept images, and Flate-aware page and indirect-form rewriting before other content ops. Intentional differences: direct form XObjects stay untouched, content flags combine only with page operations and whole-file output, and damaged content keeps original bytes with a warning.

## Overlay and underlay (work item 30b)

Repeatable --overlay and --underlay blocks (--file or positional file, --to, --from with empty allowed, --repeat, plus job keys) mirror upstream stacking: underlays in order, then the original page as /Fx0, then overlays in order, each placed from TrimBox fallbacks with centered shrink-to-fit matrices that invert destination rotation and scale. Page ranges accept plain numbers, dash ranges in either direction, and comma lists. Intentional differences: relative numbers, exclusions, and odd/even positions stay unsupported; overlay annotations are not copied; inherited /UserUnit on output pages is not inverted; damaged overlay content warns and skips instead of emitting partial forms.

## Rotation flattening and page-content checks (work item 31a)

--flatten-rotation (plus job key) mirrors upstream page baking: boxes rotate about the media box per 90/180/270 degrees, a counter-transform wraps the content streams, /Rotate is dropped, and annotation rectangles move with the page. Pages without actionable rotation stay byte-identical. The page-content check now tokenizes page content streams (honoring the check decode level) instead of reporting skipped. Intentional differences: annotation appearance streams are untouched, damaged pages warn and stay as is, and form-field restructuring is left to later appearance work. The differential lane flattens a checked-in /Rotate 90 page on both sides with trio validation, a swapped MediaBox, a dropped /Rotate, and byte-identical decoded wrap streams.

## Annotation flattening (work item 31b)

--flatten-annotations with ll, print, or screen (plus job key) mirrors upstream stamping: appearance streams become /Fxo form XObjects placed by annotation-derived matrices with flag filtering and page-rotation counter-rotation, and flattened annotations leave /Annots. Widgets defer to generated appearances when the document needs them, and appearance-less annotations drop only when they carry an appearance dictionary. Intentional differences: field default resources are not merged into appearances, and indirect damage keeps originals with a warning instead of failing.

## Appearance generation (work item 31c)

--generate-appearances (plus job key generateAppearances) mirrors upstream generation order (generate, then flatten-annotations, then flatten-rotation): when the catalog needs appearances it regenerates text/list appearance streams from field values and default appearance (ASCII fallback, WinAnsi/MacRoman encodings, BMC/EMC value rewrite, list highlight) and synchronizes checkbox/radio /V and /AS state, then clears the condition so a later --flatten-annotations stamps fresh appearances. `--remove-acroform` (plus job key `removeAcroform`) is accepted as a
no-op: every publication path rebuilds a fresh catalog without `/AcroForm`, so outputs never
carry the form dictionary while widget annotations stay in place; the differential lane checks
both sides drop the dictionary with trios green. `--remove-structure` (plus job key
`removeStructure`) is likewise accepted as a no-op: fresh catalogs never carry `/StructTreeRoot`
or `/MarkInfo`; a dedicated differential case checks both sides with trios green. Appearances are rewritten raw (no /Filter). Intentional differences: parent-held field nodes never reach output pools so field context resolves by subtree search and parent-held radios degrade to skip; field default resources are not merged; signature fields are skipped and pushbuttons warn; damaged appearance streams warn and keep the field.

## Governed JPEG decoding (work item 32)

DCTDecode executes at decode level `all` through a governed ImageSharp 3.1.12 adapter: frame dimensions and component counts are read from the JPEG header before any pixel buffer is allocated, work and memory budgets are charged up front, and 1-component (grayscale) and 3-component images decode to raw sample bytes with the filter removed. Intentional differences: four-component CMYK/YCCK JPEGs stay opaque with an `UnsupportedFeature` warning because the approved public pixel API only yields color-converted RGB and cannot preserve raw component samples; lower decode levels keep DCT streams opaque silently; corrupt JPEG data warns and stays opaque. Content and overlay paths still gate on `specialized` and never decode images implicitly.

## Image optimization (work item 33)

--optimize-images (plus job key optimizeImages) recompresses eligible image XObjects with DCT exactly when the JPEG is strictly smaller: 8 bits per component with /DeviceGray or /DeviceRGB, width, height, and area strictly above --oi-min-width/--oi-min-height/--oi-min-area (defaults 128/128/16384, 0 disables), and already-DCT images left alone unless --jpeg-quality (0..100, mapped 0 to 1, default 75) forces recompression. Replacement keeps every dictionary key except /Filter//DecodeParms, which become /DCTDecode. Recursion follows form XObjects with a cycle guard and shared images stay consistent. Intentional differences: four-component CMYK has no raw-sample encode path and stays as is; masks and exotic colorspaces are skipped; every skip is silent; large inline images externalize first (unless --keep-inline-images) and join the same eligibility rules, with the conventional pre-EI separator trimmed before sample validation.

## Inline optimization and codec hardening (work item 34)

Image optimization covers externalized inline images under the same shrink-only rules, and the filter layer gained adversarial cases: truncated JPEGs and two-component frames stay opaque with a warning, chained Flate+DCT decodes with --recompress-flate, dimension mismatches skip silently, and encode budgets fail explicitly. Attachment extraction now decodes DCT streams, and the streams check passes valid JPEGs at decode level all. Progressive JPEG has no encoder in the pinned codec so no round-trip fixture exists; four-component CMYK remains the visible decoder and encoder gap.

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

The typed `RunZlibFlateAsync` companion compresses raw bytes or uncompresses zlib-wrapped bytes between explicit host paths through the governed Flate pipeline: strict header, framing, and Adler-32 checks on inflate with per-chunk memory budgets, and level buckets 1..9 (1 fastest, 9 smallest, default optimal) on deflate. Modes and out-of-range levels fail explicitly without publishing output. Intentional differences: truncated input fails instead of exiting 3 with partial output; empty input deflates to empty output (BCL emits no framing for empty input) instead of an 8-byte stream; compression level 0 and values above 9 fail instead of passing through to zlib; there is no Zopfli support and no host capability supplies `QPDF_ZOPFLI`, which stays treated as unset. The differential lane round-trips both directions against oracle zlib-flate, checks corrupt-input exit parity, and asserts level ordering. The companions stay outside the main flag inventory.

## Linearization checking (work item 38)

The linearization check detects the parameter dictionary in the first bytes, validates its keys (/H bounds, integer /O /E /N /T with /N matching the traversed pages, optional integer /P), resolves hint streams by cross-reference offset, decodes raw or Flate-encoded hint data, and parses the page-offset, shared-object, and outline tables with a governed bit reader under file-bounded counts. A file whose /L disagrees with its size reads as not linearized, matching upstream. Intentional differences: defects fail explicitly (exit 2) where upstream warns (exit 3); managed linearized outputs validate through this check on both sides (work item 39). The differential lane checks oracle-linearized files plus page-count and hint-length mutations on both sides.

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

