# Progress

## Current checkpoint

Work item 16: inspection breadth and partial-check orchestration. `Qpdf.CheckAsync`
reports eight declared checks (header, cross-reference, trailer, page tree,
object graph, decode-level-gated streams, page content, linearization) with
explicit skipped reasons; `input --check` maps the same engine to exit 0/2/3.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 316 tests (307
  prior plus 9 check cases: declared outcomes on a valid document, level-none
  skip, corrupt-decode failure, unavailable-filter skip, linearized skip, and
  argv check success/warning/rejection/truncation paths).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 17: bounded repair with strict/recovery comparisons (M4).
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.
