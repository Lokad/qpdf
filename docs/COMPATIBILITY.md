# Compatibility target and scope

Reference: qpdf **12.4.2**, commit
`4eba95899886e851cc41d76886483b347612f2a8`, from
https://github.com/qpdf/qpdf. This target must not move silently.

**Implemented PDF operations: none.** Every inventory entry is initially
`not-started`, including excluded operations whose future runtime rejection
still needs tests. Passing the scaffold and runner tests establishes inventory
consistency, fixture/independent-reader contracts and package boundaries, not
PDF compatibility.

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
established against the approved codec; no JPEG capability has been proved yet.

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
support. Bounded ImageSharp JPEG and BCL Flate probes (work item 03) are also
pending; no JPEG capability has been proved yet.

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
| M0 | Build/package baseline, inventory, reference runner and dependency probes. Scaffold plus reference/independent runners and fixtures are present; JPEG/Flate probes and a real-executable smoke remain. |
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
