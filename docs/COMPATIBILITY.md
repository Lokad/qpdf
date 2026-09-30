# Compatibility target and scope

Reference: qpdf **12.4.2**, commit
`4eba95899886e851cc41d76886483b347612f2a8`, from
https://github.com/qpdf/qpdf. This target must not move silently.

**Implemented PDF operations: none.** Every inventory entry is initially
`not-started`, including excluded operations whose future runtime rejection
still needs tests. Passing the scaffold tests establishes inventory consistency
and package boundaries, not PDF compatibility.

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

## Exclusions and differences

Passwords, encryption/decryption, encryption inspection, cryptographic options,
permission enforcement, signing and signature validation are out of scope.
Reject encrypted input structurally; do not prompt for or read passwords.
`--remove-restrictions` is excluded. Ordinary document IDs, deterministic IDs
and non-secret file checksums remain part of structural processing.

Rendering, OCR, text extraction/layout and general document authoring are not
project goals. Do not claim PDF/A, PDF/X or PDF/UA conformance from a rewrite.

There is no vendored code, qpdf/libqpdf binding or upstream runtime dependency.
The future differential runner may invoke a pinned qpdf executable as a
development oracle. It must remain separate from production and ordinary tests.

The target reference build has JPEG support and no optional Zopfli support.
`--zopfli` will follow that build profile's explicit unavailable behavior;
ordinary Flate compression remains required. Host limits remain enforceable
even when a caller disables qpdf-compatible default parser limits.

## Progression and dependency gates

| Phase | Intended result |
| --- | --- |
| M0 | Build/package baseline, inventory, reference runner and dependency probes. Only the scaffold is present so far. |
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
