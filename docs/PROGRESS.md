# Progress

## Current checkpoint

Work item 06a: single-page selection through the shared engine. The memoized
`GraphCopier` (shared-subgraph-once, `/Parent` never followed, tree-node
guards), the deterministic classic-xref `PdfSerializer` (counting pass plus
emitting pass with replay-length check, recomputed stream lengths, no `/ID`),
and the typed `Qpdf.SelectPageAsync` (inherited-attribute materialization,
staged publication, shared per-job governor) are implemented. Narrow argv/job
adapters (06b) and the package consumer (07) are next.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 122 tests (114
  prior plus 8 selection cases: preserved opaque streams, second-page
  indexing, inherited-attribute materialization, byte-identical repeated
  runs, invalid/conflicting/budget/cancelled paths with untouched targets
  and closed leases).
- Outputs reopen in our strict reader and in PdfPig with matching page
  counts; new synthetic fixture `inherited-attrs.pdf` (page-local bare,
  attributes on the Pages node).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency.
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary. The typed selection covers the initial single-page profile only; the
full section 9 contract (ranges, duplicates, forms, pruning) remains M3 work.

## Next work

1. Work item 06b: narrow argv and job JSON adapters converging on the same
   selection plan, with explicit rejection of everything outside the subset.
2. Work item 07: clean package-consumer exercise of the path and the
   documented supported profile.
3. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.
