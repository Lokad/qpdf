# Supported profile (M4)

This describes exactly what the library does today. Everything else in
`docs/COMPATIBILITY.md` is a roadmap, not a claim.

## Entry paths

Three routes converge on one selection plan executed by the shared engine:

- Typed: `Qpdf.SelectPageAsync` with explicit, `FirstAndLast`, or `Normalized` page sets;
  `Qpdf.CheckAsync` with `QpdfCheckOptions` (decode level) for partial inspection.
- Argv: `Qpdf.ExecuteAsync` accepts `input --pages [--file=]file [--range=]n
  -- output` (single positive-integer range), `input --check`, `--job-json-file
  file`, `@file` response files (one argument per line), `--help`/`--version`,
  and the `--suppress-recovery`/`--warning-exit-0`/`--no-warn` modifiers.
- Job JSON: `Qpdf.ExecuteJobAsync` accepts `{inputFile, outputFile,
  pages:[{file, range}]}` with exactly one entry; inspection has no job key yet.

All routes return explicit outcomes (`PdfPageSelectionResult` or
`QpdfExecutionResult` with exit codes 0/2/3). Anything outside the subset —
ranges, collation, multiple sources, `--empty`, `--replace-input`, stdout,
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
- One input file per job through host-mediated ranged reads.

## Selection semantics

- Explicit ordered pages by 1-based index (order and duplicates preserved),
  `FirstAndLast(N)` windows, or sorted/deduplicated `Normalized` sets; out-of-range
  indexes fail explicitly and empty explicit selections are rejected.
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
  trailer with `/Size`, `/Root`, `startxref`, and `%%EOF`.
- Version label echoes the effective version (header with catalog `/Version` override). Numbers keep their original token
  text; names re-emit raw bytes with non-regular bytes `#`-escaped; literal
  and hexadecimal string kinds are preserved with value-faithful escapes.
- Stream `/Length` is always recomputed from the emitted bytes and written
  directly, which can orphan an indirect length object as an unused entry.
- No `/ID` is emitted; identifier policy arrives with writer controls (M6).
- Selection output always normalizes to classic xref, even from
  stream-based inputs; object-stream preservation arrives with writer
  controls (M6).

## Governance

Every path runs under one per-job governor: validated `QpdfLimits` budgets
for inputs, outputs, memory, work, decoded bytes, host operations, handles,
diagnostics, and nesting depth, plus cooperative cancellation. Diagnostics
carry stable codes and never embed input content, paths beyond logical
names, host exception text, or stack traces.
