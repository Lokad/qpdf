# Supported profile (M4)

This describes exactly what the library does today. Everything else in
`docs/COMPATIBILITY.md` is a roadmap, not a claim.

## Entry paths

Three routes converge on one selection plan executed by the shared engine:

- Typed: `Qpdf.SelectPageAsync` with explicit, `FirstAndLast`, or `Normalized` page sets;
  `Qpdf.SelectPagesAsync` merges ordered `PdfPageSource` inputs with per-page source
  mapping; `Qpdf.CheckAsync` with `QpdfCheckOptions` (decode level) for partial inspection.
- Argv: `Qpdf.ExecuteAsync` accepts `input --pages [--file=]file [--range=]n
  -- output` (repeated file/range groups with single-integer ranges), `input --check`, `--job-json-file`
  file`, `@file` response files (one argument per line), `--help`/`--version`,
  `--rotate=[+|-]angle[:range]`, `--split-pages[=n]`, `--replace-input`,
  and the `--suppress-recovery`/`--warning-exit-0`/`--no-warn` modifiers.
- Job JSON: `Qpdf.ExecuteJobAsync` accepts `{inputFile, outputFile,
  pages:[{file, range}, ...]}` concatenating entries in order, plus optional
  `rotate` and `setPageLabels` arrays; inspection and `collate` have no job
  key yet.

All routes return explicit outcomes (`PdfPageSelectionResult` or
`QpdfExecutionResult` with exit codes 0/2/3). Anything outside the subset —
ranges, `--empty`, `--replace-input`, stdout,
response files, unknown options — fails explicitly. Password and encryption
flags fail with `UnsupportedCryptoOperation` and exit 2 without retaining or
echoing values.

## Input profile

- Chained cross references: classic tables and Flate- or LZW-compressed
  xref streams with exact entry sizing and coverage; `/Prev` chains and hybrid
  `/XRefStm` links merge latest-wins with cycle and revision budgets. Object streams
  decode once with validated headers and extents. No `/Extends` links, recovery,
  or object-stream preservation in output.
- `%PDF-` header at offset 0; versions 1.0-1.7 and 2.0 with catalog `/Version` override.
- Unencrypted inputs; any effective `/Encrypt` declaration rejects
  structurally, including empty-password forms.
- Strict parsing: damaged inputs fail as parse errors. The CLI repairs boundedly by
  default (warning, exit 3); `--suppress-recovery` keeps strict failure. Typed
  selection and inspection stay strict.
  Malformed, mistyped, unresolvable, or unsupported catalog `/Version` fails;
  null or absent `/Version` keeps the header.
- Page streams travel opaquely and are never decoded: direct and indirect
  `/Length` resolve with a load-cycle guard, unknown filter encodings pass through untouched, and
  copying a page needs no image support. Structural streams decode through Flate,
  LZW, ASCIIHex, ASCII85, and RunLength (direct or indirect filter specs), with
  shared predictors; anything else fails explicitly as unsupported.
- One input file per selection job, several per merge, through host-mediated ranged reads.

## Selection semantics

- Explicit ordered pages by 1-based index (order and duplicates preserved),
  `FirstAndLast(N)` windows, or sorted/deduplicated `Normalized` sets; out-of-range
  indexes fail explicitly and empty explicit selections are rejected.
- Ordinary rotation (`Set`/`Add`/`Subtract` multiples of 90 over output positions)
  folds over the inherited base; untouched pages keep their bytes. Flattening
  stays unsupported.
- Merge options interleave per-source groups (collation), rotate, and emit
  explicit page labels; source label trees are dropped and never merged.
- Output carries a fresh catalog with the copied pages only; each occurrence is an
  independent page object sharing immutable resources. Shared non-page objects
  are copied once; `/Parent` links are dropped. Results carry the source page
  count and the exact per-output-page source mapping.
- The four inheritable attributes (`Resources`, `MediaBox`, `CropBox`,
  `/Rotate`) materialize from ancestry into each copied page; explicit null counts
  as absent. A missing `/MediaBox` after ancestry fails; missing `Resources` stays absent.
- Page-tree nodes require `/Type /Page` or `/Pages`. Traversal wins over
  `/Count`, which only warns. Page-tree (`/Pages`) and catalog references fail
  explicitly; references to already-copied objects remap while references to any
  other page prune to null, so omitted pages never resurrect. Widget annotations
  travel with appearances; `/AcroForm`, outlines, names, and metadata are dropped.
- Repeated runs over the same input and options are byte-identical.
- Publication is staged: validation, budgets, and serialization complete
  before commit, so failures leave existing targets untouched. Every lease
  closes on success, failure, and cancellation.
- `--split-pages` publishes one file per page group with pruned objects and
  sliced labels; `--replace-input` overwrites the single input with no backup.
  Partial split outputs can remain if a later group fails.

## Inspection profile

- `Qpdf.CheckAsync` reports eight declared checks: header, cross-reference,
  trailer, page tree, object-graph traversal, decode-level-gated stream
  decoding, page content, and linearization.
- Skipped stages stay visible with reasons and never pass silently; failures
  carry capped per-object detail. Content (M7) always skips; linearization
  passes when absent and skips with a warning when present; unavailable
  filters skip streams with a warning at level `all`.
- `input --check` publishes no files and maps diagnostics to exit 0/2/3.
  Inspection never claims conformance, safe content, or rendering correctness.

## Writer profile

- Classic xref tables with 20-byte entries, generation 0 throughout, and a
  trailer with `/Size`, `/Root`, `/ID`, `startxref`, and `%%EOF`.
- Version label negotiates the effective version: the greatest input version
  (header with catalog `/Version` override), raised by `--min-version` and
  overridden by `--force-version` (`major.minor[.extension]`; supported
  headers are 1.0-1.7 and 2.0). A positive extension level adds a direct
  catalog `/Extensions << /ADBE << /BaseVersion /M.m /ExtensionLevel N >> >>`.
- Identifiers always emit `/ID [<id1><id2>]`: the first word preserves the
  source trailer identifier when present, otherwise it repeats the second.
  The default second word is a deterministic content hash (stable across runs
  within the contained profile, unlike upstream time-based random IDs);
  `--static-id` selects the upstream test constant
  `<31415926535897932384626433832795>`. `--deterministic-id` is accepted
  and matches the default deterministic behavior.
- Stream `/Length` is always recomputed from the emitted bytes and written
  directly, which can orphan an indirect length object as an unused entry.
  By default streams close as `data` + `endstream` with no extra newline;
  `--newline-before-endstream` always inserts `\n` before `endstream`
  (doubling a trailing newline when present), preserving PDF/A compliance.
- Object streams follow `--object-streams` (`disable`, `preserve` (the
  default), `generate`): `disable` writes only classic xref tables;
  `preserve` keeps compressed-ness from the input (no streams in, no streams
  out) without reproducing exact original groupings; `generate` packs every
  eligible non-stream object into new object streams. Any object streams imply
  a cross-reference stream instead of a classic table and raise the header to
  at least 1.5; a forced version below 1.5 disables object streams.
  Payloads and xref entries are emitted uncompressed (no `/Filter`);
  Flate packing arrives with the compression controls. Unreferenced-object
  preservation arrives next in M6.

## Governance

Every path runs under one per-job governor: validated `QpdfLimits` budgets
for inputs, outputs, memory, work, decoded bytes, host operations, handles,
diagnostics, and nesting depth, plus cooperative cancellation. Diagnostics
carry stable codes and never embed input content, paths beyond logical
names, host exception text, or stack traces.
