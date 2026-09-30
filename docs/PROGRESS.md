# Progress

## Current checkpoint

Work item 01: solution scaffolding. The .NET 10 library/test solution, central
dependencies, Release packaging, initial CI, compatibility inventory and public
project instructions are in place. No PDF operation or public execution API
exists. M0 is still incomplete: reference validation and dependency probes are
subsequent work items.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 13 scaffold tests.
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency.
- Rejection of unsafe compilation, Debug packaging and an unapproved dependency.
- Deterministic inventory regeneration against the pinned public reference.
- A fresh export of staged files, without the local handoff or `external/`,
  passed restore/build/tests/pack/inspection using the populated package cache
  and an empty local feed. Network advisory lookup was disabled only for this
  offline restore; the ordinary restore passed with auditing enabled.
- Public files and package/symbol contents passed local private-path checks.

The Windows/Linux CI workflow is defined but has not run remotely; no remote
is configured yet. These tests validate scaffold contracts, not PDF behavior.

## Next work

1. Work item 02: add pinned qpdf and independent validation runners with tiny
   public/synthetic fixtures, checksums, and positive/negative runner tests.
2. Work item 03: probe ImageSharp 3.1.12's PDF-relevant JPEG semantics and
   allocation/cancellation controls, plus the .NET Flate APIs. Record gaps;
   do not change the approved dependency to hide them.
3. M1: implement minimal owned input/output, governor and diagnostics, then a
   strict classic-xref reader and one complete read/select/write operation.
   Exercise it through narrow argv/job routes and a real package consumer.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.
