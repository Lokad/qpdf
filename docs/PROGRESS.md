# Progress

## Current checkpoint

Work item 12: byte, version, and indirect-length semantics. `QpdfDocument.Version` is
the effective version (header overridden by a valid catalog `/Version` name, direct or
indirect); malformed, mistyped, unresolvable, or unsupported catalog versions fail.
Object loads carry a reentrancy guard so self-referential indirect `/Length` fails as
a parse error; over-declared lengths fail before allocating and stream bytes slice
strictly by `/Length`, preserving delimiter-like bytes. Selection output echoes the
effective version in its header.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 277 tests (261
  prior plus 16 version/length cases: header preservation, catalog override
  (direct, indirect, null-keeps-header), effective-version output echo,
  malformed/mistyped/unresolvable/unsupported version rejection, indirect
  length resolution, self-referential cycle guard, missing/negative/
  unresolvable/huge length rejection, and delimiter-like stream bytes).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 13: ordered ranges, duplicates, first/last selection, source mapping.
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.
