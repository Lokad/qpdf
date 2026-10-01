# Progress

## Current checkpoint

Work item 21: split and replacement publication. `qpdf --split-pages[=n]`
partitions output into upstream-named groups sharing one object pool with
per-group reachability pruning, renumbering, and sliced labels; `--replace-input`
rewrites the single distinct input with no backup. Contradictions, collisions,
and oversized names fail before publication; typed callers split by looping.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 383 tests (375
  prior plus 8 publication cases: split naming and groups, pruning, aliasing
  conflicts, in-place replacement, contradictory combinations, and
  split/direct byte equality).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 22: writer object/xref-stream modes and version/ID controls (M6).
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.
