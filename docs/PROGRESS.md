# Progress

## Current checkpoint

Work item 13: ordered multi-page selection. `PdfPageSelectionOptions` accepts explicit
ordered pages (duplicates preserved), `FirstAndLast(N)` windows, and
sorted/deduplicated `Normalized` sets; results carry the source page count and the
exact per-output-page source mapping. Each occurrence copies to an independent page
object with shared resources; full selections rewrite rather than echo.
Oversized selections fail on work budgets; out-of-range pages fail explicitly.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 286 tests (277
  prior plus 9 selection cases: ordered/duplicated pages with source mapping,
  distinct duplicate objects, `FirstAndLast` windows/meet/overlap, full-selection
  rewrite, out-of-range failure, fast option validation, and normalization).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 14: catalog/inheritance/form/widget policies, omitted-page references.
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.
