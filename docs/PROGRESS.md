# Progress

## Current checkpoint

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

- Locked restore, Release build with zero warnings, and all 649 tests (620
  prior plus 36 JSON mode cases covering inline/file payloads with sidecars,
  decode fallback, object selection, minimal output, acroform fields, job
  and typed round trips with malformed payloads, minus 7 retired
  narrow-profile rejections).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 27: JSON import/update, sidecars, round trips and schema validation (M6).
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.
