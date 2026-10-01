# Progress

## Current checkpoint

Work item 22a: writer versions, IDs and stream endings. `--min-version`/`--force-version` negotiate the header (`major.minor[.extension]`, 1.0-1.7 and 2.0) with catalog `/Extensions` for positive levels; trailer `/ID` always emits (original first word preserved, default second word is a deterministic content hash, `--static-id` selects the upstream test constant, `--deterministic-id` matches the default); stream `endstream` defaults to no extra newline with `--newline-before-endstream` forcing `\\n`. All entry paths share one implementation with replay-checked serialization.

Prior gate

Work item 21: split and replacement publication. `qpdf --split-pages[=n]`
partitions output into upstream-named groups sharing one object pool with
per-group reachability pruning, renumbering, and sliced labels; `--replace-input`
rewrites the single distinct input with no backup. Contradictions, collisions,
and oversized names fail before publication; typed callers split by looping.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 414 tests (383
  prior plus 31 writer-control cases: version raising/forcing/extension,
  static/deterministic IDs with preservation and determinism, stream
  newline defaults and flags, argv/job parity, and malformed/duplicate
  rejections).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 22b: writer object/xref-stream modes (`disable`/`preserve`/`generate`) and unreferenced-object controls (M6).
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.
