# Progress

## Current checkpoint

Work item 14: catalog, inheritance, and widget policy. Preview output keeps a fresh
catalog (`/Type`+`/Pages` only); page-tree/catalog refs fail, copied refs remap, and
other page refs prune to null so omitted pages never resurrect. Widget annots keep
appearances without field reconstruction. Null attributes count as absent; page-only
boxes never inherit.
Oversized selections fail on work budgets; out-of-range pages fail explicitly.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 294 tests (286
  prior plus 8 policy cases: omitted-dest prune, backward remap, catalog-drop,
  page-only-box guard, null/missing MediaBox handling, and widget appearance).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 15: selector regression families, package/API validation, subset record.
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.
