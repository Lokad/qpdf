# Progress

## Current checkpoint
Work item 56: stream-filter budget (M11). `--max-stream-filters=n` (plus job key `maxStreamFilters`, default 25) caps decoded filter chains on every decode path through one governed choke point: over-limit streams stay opaque under a warning (exit 3) instead of failing, with `>` boundary parity, argv/job parity, JSON-import and inspection-combination guards, host-budget and validation evidence; oracle parity stays an explicit gap.

Prior gate

Work item 55: shell completion identity (M11). `--completion-bash` and `--completion-zsh` are identity requests emitting static subset completion scripts (eval-able `complete -F` / `compdef` over the narrow-profile flags, with no argv[0] or environment lookup per containment), with bare/with-input/combination, valued-form, and budget evidence; oracle script parity stays an explicit gap.

Prior gate

Work item 54: JSON help identity (M11). `--json-help[=1|2|latest]` (default 2) is an identity request emitting same-keys/descriptive-values JSON for the matching inspection version (v1 object tables versus v2 qpdf key), with version-selection, unknown-version, wins-over---json, and exhausted-budget evidence; oracle output parity stays an explicit gap.

Prior gate

Work item 53: job-JSON help identity (M11). `--job-json-help` is an identity request printing the supported job-key subset (exit 0 with no output file, valued forms fail, exhausted output budgets ignored), with bare/with-input/combination, valued-form, and budget evidence; upstream prints the full schema so oracle output parity stays an explicit gap.

Work item 52: release-gate honesty audit (M11). Verified the section 13 structural gates
with evidence (no unsafe/interop/process/network code in src, ImageSharp pinned centrally
and in locks and the package group, deterministic locked builds), refreshed stale
public metadata (package description/release notes, README status) and class docs (document
leases, job-parser scope, per-call governor with threading/cancellation/commit contract).

Prior gate

Work item 51: structure-tree removal (M11). `--remove-structure` (plus job key
`removeStructure`) is accepted as a no-op backed by the fresh-catalog invariant, with a
checked-in structure fixture and a dedicated differential case.

Prior gate

Work item 50: shared budget enforcement (M11). One theory proves a dozen public entry
paths fail explicitly with `BudgetExceeded` and publish nothing under exhausted budgets,
filling resource evidence across content, inspection, merge, and writer entries (4 flips).

Prior gate

Work item 49: form-dictionary removal (M11). `--remove-acroform` (plus job key
`removeAcroform`) is accepted as a no-op backed by the fresh-catalog invariant (outputs never
carry `/AcroForm`); a dedicated differential case checks both sides.

Prior gate

Work item 48: selection evidence (M11). Top-level `--pages` is complete with ordered,
range-failure, budget, and differential evidence; `--preserve-unreferenced-resources` moves
to partial with a budget case, oracle preservation parity still open.

Prior gate

Work item 47: cross-reference inspection (M11). `--show-xref` (plus job key `showXref`)
lists live entries with oracle-identical lines (compressed entries included, free entries
skipped), backed by a line-for-line differential case on classic and streamed files.

Prior gate

Work item 46: object inspection (M11). `--show-object` (plus job key `showObject`) renders
canonical single-line object definitions with line-for-line oracle parity (including streams,
trailer, missing-as-null, and compressed members); non-zero generations and binary stream-data
modes stay explicitly out of scope.

Prior gate

Work item 45: page-dictionary inspection (M11). `--show-pages` (plus job key `showPages`)
lists page dictionaries with indirect content references in oracle-identical line layout,
backed by a line-for-line differential case.

Prior gate

Work item 44: page-count inspection (M11). `--show-npages` (plus job key `showNpages`)
reports traversed page counts across argv and job paths with explicit combination failures,
encrypted/budget failures without output, and a managed-versus-oracle count-agreement lane case.

Prior gate

Work item 43: identity outputs and the no-external audit (M11). `--copyright` returns honest
implementation text; valued identity forms fail explicitly; identity requests bypass exhausted
output budgets; the ordinary suite passes 1150/1150 with `external/` absent while the reference
lane fails explicitly without `QPDF_EXE`. Help/version/copyright/json-help move to partial with
evidence.

Prior gate

Work item 42: warning/exit-code contract with job parity (M11). `warningExit0`, `noWarn`,
and `suppressRecovery` job keys ride the shared plan; `--no-warn` keeps exit 3 while dropping
warning messages (matching the reference manual); the differential lane checks warning exit
parity (3/3, 0/0, 3/3); invocation, check, and page-selection entries gain complete
evidence (5 flips) with suppress-recovery and job-json-file moving to partial.

Prior gate

Work item 41: crypto-exclusion evidence (M11). All 49 password/permission/inspection/restriction
entries now carry negative evidence: every argv flag, top-level and nested job JSON crypto key,
and encrypted file input fails explicitly with `UnsupportedCryptoOperation` (exit 2) publishing
no output, backed by 58 exclusion cases (top-level job crypto keys newly rejected to match the
argv surface).

Prior gate

Work item 40: linearized object-stream modes (M10). `--linearize` with `--object-streams=generate` (or `preserve` over compressed inputs) publishes streamed linearized output through the shared selection engine: members resolve to their container during part classification with page-shared containers winning placement, uncompressed units sort low per half with container members numbered after their half, and raw cross-reference streams replace both classic tables under the same two-pass replay check. Outline hint tables are omitted whenever outline objects compress (as observed upstream); xref streams stay unfiltered and member numbering follows container encounter order as documented intentional differences. `preserve`/`disable` over classic inputs still publish the classic layout; QDF stays silently disabled and `--json`, JSON import, attachment inspection and `--check` combinations still fail explicitly. Eleven new classification/emission/mode cases plus streamed oracle fixtures (check/show) plus the `linearize-streams` differential case (managed versus oracle writers on one-, two-page, and outlined selections, both outputs validating under both checkers) pass with no engine discrepancies.

Prior gate

Work item 38: linearization checking (M10). The check detects the parameter dictionary, validates its keys against traversed pages, resolves hint streams by cross-reference offset, and parses page-offset, shared-object, and outline tables with a governed bit reader. Committed oracle-linearized one- and two-page fixtures back unit and differential coverage, including malformed dictionary, offset, encoding, and bit-level cases.

Prior gate

Work item 37: managed zlib-flate facade (M9). The typed RunZlibFlateAsync companion compresses (levels 1..9) or uncompresses zlib streams between explicit host paths through the governed Flate pipeline with staged publication and explicit failure on bad modes, levels, corrupt input, and budgets. The differential lane gains a zlibflate case round-tripping both directions against oracle zlib-flate with corrupt-input parity and level ordering.

Prior gate

Work item 36: managed fix-qdf repair (M9). The typed FixQdfAsync companion recomputes object-stream tables, stream lengths, cross-references, and startxref offsets over edited QDF with sequential numbering enforcement, bounded governor budgets, staged host publication, and explicit failure on carriage returns, truncation, and empty input. Classic QDF stream lengths now pair through renumbering (multi-stream fix with a two-page regression). The differential lane gains a fixqdf case comparing managed repair against oracle fix-qdf byte for byte on edited classic and streamed files.

Prior gate

Work item 35: interoperable QDF editable view (M9). Classic layout (binary marker, %QDF-1.0, original-ID/page comments, indented values, indirect lengths, ignore_newline tolerance) plus object-stream layout (unfiltered containers with member comments, upstream-style /First and value offsets, unfiltered xref stream with /W [ 1 f1 f2 ]) across argv and job paths with QDF-implied decode/normalize defaults. Differential lane gains a two-page QDF object-stream case (oracle trio, marker agreement, upstream fix-qdf round trip green).

Prior gate

Work item 34 extended: external validation lane. Bootstrapped the official qpdf 12.4.2 portable build (checksums recorded in docs/COMPATIBILITY.md), fixed the reference script exit-code capture, and added tools/run-differential-checks.ps1 comparing managed vs oracle outputs (select, merge, uncompress/normalize payload identity, JSON pages, forms appearances, overlay) with oracle trios green and no engine discrepancies.
Work item 34: inline optimization and codec hardening. Externalized inline images join shrink-only DCT optimization, plus adversarial JPEG cases (truncation, components, chains, budgets), DCT attachment extraction, and valid-JPEG check coverage.

Prior gate

Work item 31a: rotation flattening and page-content checks. Counter-transform baking with box and annotation geometry across argv and job paths, plus tokenizer-backed page-content inspection.

Prior gate

Work item 30b: overlay and underlay transforms. Repeatable stamping blocks with to/from/repeat mapping, form XObject placement with centered shrink-to-fit matrices, and stacking across argv and job paths.

Prior gate

Work item 30a: inline-image externalization. Threshold-based lifting to image XObjects with abbreviation expansion, color lookup, unique naming, and form recursion across argv and job paths.

Prior gate

Work item 29: page content operations. Coalescing, newline normalization, and unreferenced-resource pruning with shared-resource heuristics across argv and job paths.

Prior gate

Work item 28: content tokenizer and inline-image boundary tests. Strict operand tokenizing with governed inline-image scanning backs upcoming content transforms.

Prior gate

Work item 27b: PDF update from JSON. Merged object identity with kept stream bytes, optional trailer replacement, and pdfversion override across argv, job, and typed paths.

Prior gate

Work item 27a: JSON import from qpdf JSON. Strict structural validation with canonical value decoding, inline and sidecar payloads, and renumbered classic publication across argv, job, and typed paths.

Prior gate

Work item 26b: JSON stream modes, object filtering, acroform details, and minimal serialization output. Inline/file payloads with decode-or-raw fallback, canonical object selectors, inherited form-field reporting, and json-output defaults across argv, job, and typed paths.

Prior gate

Work item 26a: PDF JSON inspection. Canonical v1/v2 object encoding with omitted stream data backs selected top-level keys (pages, labels, outlines, attachments, encrypt, objects) across argv, job, and typed paths.

Prior gate

Work item 25b: attachment copy plus the typed editing surface. Repeatable copy blocks rechains per-source filespecs after removals and additions with grouped collision errors, and the typed add/remove/copy surface shares the engine while reporting final attachment counts.

Prior gate

Work item 25a: attachment add and remove. Repeatable add blocks plus repeatable removals build on the EmbeddedFiles reader with staged host file reads, sorted rebuilt trees, and per-group split coverage.

Prior gate

Work item 24b: attachment list and extract. EmbeddedFiles name-tree traversal with text-key decoding backs shared typed/argv/job paths: listing diagnostics plus staged byte extraction with decode-or-raw fallback warnings.

Prior gate

Work item 24a: document info and metadata. Trailer `/Info` and catalog `/Metadata` are preserved by default with `--remove-info` (ModDate-only info, also drops metadata) and `--remove-metadata` semantics matching upstream, across typed/argv/job paths with replay-checked serialization.

Prior gate

Work item 23b: container Flate packing. Generated object/xref streams Flate-encode (plain `/FlateDecode`, no predictor) when compression is active, honoring `--compression-level` through the governed pipeline with replay-checked serialization; otherwise they stay raw.

Prior gate

Work item 23a: stream compression controls. `--stream-data`, `--compress-streams`, `--decode-level`, `--recompress-flate`, and `--compression-level` decode (generalized/specialized sets) and Flate-encode regular streams through one governed pipeline with opaque fallback warnings; `--zopfli` reports the no-Zopfli build profile. Defaults stay opaque-preserving (intentional difference from upstream compress defaults). One implementation across typed/argv/job paths with replay-checked serialization.

Prior gate

Work item 22c: preserve-unreferenced objects. `--preserve-unreferenced` keeps unreached input objects (ascending original order per source) through the shared copier with omitted-page pruning intact, joins object-stream packing and split-group closure, and skips unloadable objects under one warning. One implementation across typed/argv/job paths with replay-checked serialization.

Prior gate

Work item 22b: writer object-stream modes. `--object-streams` (`disable`/`preserve` default/`generate`) packs eligible non-stream objects into uncompressed object streams with a cross-reference stream, raising the header to at least 1.5 unless a forced version below 1.5 disables them; `preserve` keeps input compressed-ness without exact grouping. One shared implementation across typed/argv/job paths with replay-checked serialization.

Prior gate

Work item 22a: writer versions, IDs and stream endings. `--min-version`/`--force-version` negotiate the header (`major.minor[.extension]`, 1.0-1.7 and 2.0) with catalog `/Extensions` for positive levels; trailer `/ID` always emits (original first word preserved, default second word is a deterministic content hash, `--static-id` selects the upstream test constant, `--deterministic-id` matches the default); stream `endstream` defaults to no extra newline with `--newline-before-endstream` forcing `\\n`. All entry paths share one implementation with replay-checked serialization.

Prior gate

Work item 21: split and replacement publication. `qpdf --split-pages[=n]`
partitions output into upstream-named groups sharing one object pool with
per-group reachability pruning, renumbering, and sliced labels; `--replace-input`
rewrites the single distinct input with no backup. Contradictions, collisions,
and oversized names fail before publication; typed callers split by looping.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 1302 tests (844
  prior plus 24 annotation cases (stamping, flag filtering, widgets, merge coverage, job round trips) plus 19 appearance cases (text/list synthesis, checkbox/radio sync, encodings, whole-file/merge/generate-then-flatten, job round trips) plus 23 rotation cases (checked-in /Rotate fixture flattening) plus 32 overlay cases plus 26 inline-image cases: thresholds, abbreviations, color lookup, naming, forms, damaged content, job round trips, plus 7 JPEG decode cases (1-/3-component decode, 4-component and corrupt opacity, level gating, job parity, header-first budgets) plus 24 image optimization cases (eligibility, thresholds, quality, shrink-only replacement, merge/job coverage) plus 11 hardening cases (inline externalize-and-optimize, keep-inline, truncation, components, chains, budgets, extraction, check) plus 26 content transform cases (checked-in unreferenced-font fixture pruning): coalescing, normalization,
  resource pruning with shared heuristics and form recursion, damaged
  content warnings, merge/split coverage, and job round trips, plus 16 QDF editable-view cases plus 1 stream-data regression plus 4 streamed-QDF cases replacing the interim rejection, plus 13 fix-qdf repair cases plus 1 two-page QDF length regression, plus 19 zlib-flate cases, plus 12 linearization cases plus 2 manifest page-count rows, plus 14 linearized-writing cases, plus 16 linearization-inspection cases, plus 13 linearization-display cases, plus 8 first-pass capture cases, plus 6 outline cases and 2 outline manifest rows, plus 2 thumbnail cases and 1 thumbnail manifest row, retiring the thumbnail rejection case,
  plus 2 streamed-manifest rows (oracle object-stream fixtures), plus 5 streamed-inspection cases
  (check/show over object-stream files), plus 3 streamed-parts cases (container placement and numbering),
  plus 3 streamed-emission cases (managed check, determinism, classic fallback), plus 17 page-count cases (counts, combinations, job parity, budgets), plus 18 page-dictionary cases (exact layouts, combinations, job parity, budgets), plus 27 object cases (canonical definitions, streams, trailer, missing-as-null, compressed members, combinations, job parity, budgets), plus 21 xref cases (classic/compressed entries, combinations, job parity, budgets), plus 10 form-dictionary cases (invariant, merge, check-combination, job parity, budgets), plus 10 structure-tree cases (invariant, merge, check-combination, job parity, budgets) plus 1 structure manifest row, plus 12 budget-enforcement cases (entry-path BudgetExceeded with no output) plus 5 streamed-mode cases
  (generate/preserve/disable, outlines, job parity) plus 3 job-JSON-help identity cases (bare/with-input/combination wins, valued-form failure, exhausted output budget) plus 8 JSON-help identity cases (version selection, unknown versions, wins-over---json, exhausted output budget) plus 5 shell-completion identity cases (bash/zsh scripts, valued-form failure, exhausted output budget) plus 12 stream-filter budget cases (cap/boundary, argv/job parity, combination guards, host budget, validation, malformed values)).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Pinned oracle bootstrapped: official qpdf-12.4.2-msvc64.zip (zip SHA-256 db87077e…, exe SHA-256 1bdaf878…), `qpdf version 12.4.2`, libjpeg linked, Zopfli absent. Reference lane passes; differential lane (select with first-page coverage, merge, uncompress, normalize, coalesce, flatten-rotation,
  prune with an unreferenced-font fixture, structural JSON page descriptors, error exit-class parity, warning exit parity, show-npages page-count agreement, show-pages/show-object/show-xref line-for-line agreement, remove-acroform/remove-structure invariants, forms, overlay, qdf with fix-qdf round trip, fixqdf byte-identity, zlibflate round trips, linearized check parity, linearize writer parity, linearize-streams parity (generated object streams on one-, two-page, and outlined selections), check-linearization inspection parity, show-linearization dump parity, linearize-pass1 shape parity, linearize-outlines parity, linearize-thumbs parity) passes with oracle trios green and no engine discrepancies (see docs/COMPATIBILITY.md).

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. M11 audit (in progress): crypto, warning/exit-code, identity, page-count, page-dictionary, object, xref, selection, form-dictionary, structure-tree, budget, and release-gate entries evidenced
   (work items 41-42, 53-56).
   Next slices: remaining stale required entries (17 not-started: global limits, --empty, stream-data inspection modes,
   progress/keep-open/verbose, with-images, test-json-schema, report-memory-usage), no-external build/test
   audit, package/docs checks.
   Re-run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.
