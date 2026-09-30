# Progress

## Current checkpoint

Work item 04: minimal M1 foundation in the shipped library. `IQpdfHost`,
`IQpdfInput`, staged `IQpdfOutputTransaction`, logical `QpdfPath`, `QpdfLimits`,
structured diagnostics, execution results/counters, and the public
`InMemoryQpdfHost` are implemented with per-job governor budgets, ownership
rules, and failure semantics. No PDF parsing or operation exists yet.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 79 tests (39
  prior plus 40 foundation cases: governor budgets/cancellation, diagnostic
  mapping and message hygiene, in-memory host ownership/reads/leases,
  staged commit/discard/conflict/budget/cancellation paths).
- Failure evidence: over-budget reservations fail before allocating;
  non-progressing reads and alias conflicts fail explicitly; failed commits
  leave existing targets untouched; uncommitted staging never publishes and
  every lease closes on success, failure, and cancellation.
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency.
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary; internals are white-box tested via `InternalsVisibleTo`.

## Next work

1. Work item 05: parse binary objects/classic xrefs and expose bounded
   page-count/object inspection over this foundation for the initial profile.
2. Work item 06: copy and serialize a selected page, add narrow argv/job
   adapters, verify independent output plus replay counts.
3. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.
