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

Recorded gap: no real qpdf 12.4.2 executable has been bootstrapped yet, so no
executable checksum, `--version` text or JPEG/Zopfli build features are
recorded. The target baseline remains JPEG support with no optional Zopfli
support.

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
prompts, or value echo. Catalog `/Version` overrides and indirect-length edge
cases stay open for M2 byte/version review.

## Single-page selection (work item 06a)

Typed `Qpdf.SelectPageAsync` copies one page by 1-based index through the
same reader, graph copier, and classic-xref writer. Reachable shared objects
are copied once with destination identities registered before descending;
`/Parent` links are dropped and the four inheritable attributes are
materialized from ancestry (missing `/MediaBox` fails). Streams travel
opaque with recomputed `/Length`; output carries a fresh catalog, no `/ID`,
and generation 0 throughout. Every serialization runs a counting pass and
an emitting pass whose lengths must agree (the M1 replay canary); repeated
runs are byte-identical. References to page-tree nodes or foreign pages fail
explicitly: annotations, forms, and multi-page surgery stay open for M3/M5.

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

## Exclusions and differences

Passwords, encryption/decryption, encryption inspection, cryptographic options,
permission enforcement, signing and signature validation are out of scope.
Reject encrypted input structurally; do not prompt for or read passwords.
`--remove-restrictions` is excluded. Ordinary document IDs, deterministic IDs
and non-secret file checksums remain part of structural processing.

Rendering, OCR, text extraction/layout and general document authoring are not
project goals. Do not claim PDF/A, PDF/X or PDF/UA conformance from a rewrite.

There is no vendored code, qpdf/libqpdf binding or upstream runtime dependency.
The differential runner invokes the pinned qpdf executable as a development
oracle only. It remains separate from production and ordinary tests.

The target reference build has JPEG support and no optional Zopfli support.
`--zopfli` will follow that build profile's explicit unavailable behavior;
ordinary Flate compression remains required. Host limits remain enforceable
even when a caller disables qpdf-compatible default parser limits.

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

