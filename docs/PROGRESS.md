# Progress

## Current checkpoint

Work item 31c: appearance generation for form fields. Widget appearance regeneration (text/list synthesis with WinAnsi/MacRoman support, checkbox/radio sync) when the catalog needs them, ordered generate-then-flatten across argv and job paths.

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

- Locked restore, Release build with zero warnings, and all 887 tests (844
  prior plus 24 annotation cases (stamping, flag filtering, widgets, merge coverage, job round trips) plus 19 appearance cases (text/list synthesis, checkbox/radio sync, encodings, whole-file/merge/generate-then-flatten, job round trips) plus 22 rotation cases plus 32 overlay cases plus 26 inline-image cases: thresholds, abbreviations, color lookup, naming, forms, damaged content, job round trips, plus 25 content transform cases: coalescing, normalization,
  resource pruning with shared heuristics and form recursion, damaged
  content warnings, merge/split coverage, and job round trips).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 32: JPEG/DCT image work (M8).
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.
