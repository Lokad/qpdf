# Progress

## Current checkpoint
Work item 39: linearized writing (M10). `--linearize` (plus job key `linearize`) publishes two-pass linearized output over the shared engine: upstream-style part classification with low second-half renumbering, a counting pass with placeholder dictionaries and zeroed cross-references, generated page-offset/shared-object hint tables (Flate only under active stream compression), padded reservation fields, an emitting pass verified by a replay canary, deterministic content-hash IDs with `--static-id` support, classic-xref output only (object-stream groups fail), silently disabled QDF, and explicit failures for outlines, thumbnails, `--linearize-pass1`, `--json`/import, attachment inspection and `--check` combinations. Fourteen writer cases plus the `linearize` differential case (managed versus oracle writer outputs validating on both sides) pass with no engine discrepancies.

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

- Locked restore, Release build with zero warnings, and all 1071 tests (844
  prior plus 24 annotation cases (stamping, flag filtering, widgets, merge coverage, job round trips) plus 19 appearance cases (text/list synthesis, checkbox/radio sync, encodings, whole-file/merge/generate-then-flatten, job round trips) plus 23 rotation cases (checked-in /Rotate fixture flattening) plus 32 overlay cases plus 26 inline-image cases: thresholds, abbreviations, color lookup, naming, forms, damaged content, job round trips, plus 7 JPEG decode cases (1-/3-component decode, 4-component and corrupt opacity, level gating, job parity, header-first budgets) plus 24 image optimization cases (eligibility, thresholds, quality, shrink-only replacement, merge/job coverage) plus 11 hardening cases (inline externalize-and-optimize, keep-inline, truncation, components, chains, budgets, extraction, check) plus 26 content transform cases (checked-in unreferenced-font fixture pruning): coalescing, normalization,
  resource pruning with shared heuristics and form recursion, damaged
  content warnings, merge/split coverage, and job round trips, plus 16 QDF editable-view cases plus 1 stream-data regression plus 4 streamed-QDF cases replacing the interim rejection, plus 13 fix-qdf repair cases plus 1 two-page QDF length regression, plus 19 zlib-flate cases, plus 12 linearization cases plus 2 manifest page-count rows, plus 14 linearized-writing cases, plus 16 linearization-inspection cases, plus 13 linearization-display cases, plus 8 first-pass capture cases, plus 6 outline cases and 2 outline manifest rows, plus 2 thumbnail cases and 1 thumbnail manifest row, retiring the thumbnail rejection case,
  plus 2 streamed-manifest rows (oracle object-stream fixtures), plus 5 streamed-inspection cases
  (check/show over object-stream files), plus 3 streamed-parts cases (container placement and numbering)).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Pinned oracle bootstrapped: official qpdf-12.4.2-msvc64.zip (zip SHA-256 db87077e…, exe SHA-256 1bdaf878…), `qpdf version 12.4.2`, libjpeg linked, Zopfli absent. Reference lane passes; differential lane (select with first-page coverage, merge, uncompress, normalize, coalesce, flatten-rotation,
  prune with an unreferenced-font fixture, structural JSON page descriptors, error exit-class parity, forms, overlay, qdf with fix-qdf round trip, fixqdf byte-identity, zlibflate round trips, linearized check parity, linearize writer parity, check-linearization inspection parity, show-linearization dump parity, linearize-pass1 shape parity, linearize-outlines parity, linearize-thumbs parity) passes with oracle trios green and no engine discrepancies (see docs/COMPATIBILITY.md).

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 40 (in progress): `--check-linearization`, `--show-linearization`, `--linearize-pass1`, outlines and
   thumbnails committed (argv/job inspection plus full hint-table dump matching the oracle line for line, with
   a shared file-numbering classifier); streamed fixtures plus check/show coverage plus container-aware part
   classification committed; remaining: streamed emission, object-stream mode plumbing, differential evidence (M10). (argv/job inspection plus full hint-table dump matching the oracle line for line, with a shared file-numbering classifier); remaining: object-stream modes (M10).
2. Extend differential coverage (rotation flattening with /Rotate fixtures, error-path parity,
   coalesce/prune content operations, structural JSON, first-page selection) � done and green;
   remaining validation work rides the M11 completion audit.
   Re-run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.
