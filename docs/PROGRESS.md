# Progress

## Current checkpoint

Work item 18: invocation breadth. Response files expand once through the host
(one per line, budgeted, `@-` explicit failure); `--job-json-file` reuses the
identical job schema; `--warning-exit-0`/`--no-warn` ride the shared plan; and
`--help`/`--version` return identity text with exit 0.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 333 tests (323
  prior plus 10 invocation cases: response expansion, missing/stdin/nested files,
  response budgets, job-file parity and errors, warning-exit-0, no-warn, and
  help/version identity).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 19: cross-document annotation/form import identity (M5).
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.
