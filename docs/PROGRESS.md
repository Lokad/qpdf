# Progress

## Current checkpoint

Work item 02: reference and independent validation runners. `tools/QpdfProbe`
invokes the pinned qpdf 12.4.2 oracle (`--version`, `--show-npages`, `--check`)
and the pinned independent reader (PdfPig 0.1.16), with a tiny synthetic
fixture manifest (`tests/Fixtures/manifest.json`). No PDF operation or public
execution API exists. M0 is still incomplete: bounded JPEG/Flate probes (work
item 03) are next.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 25 tests (13
  scaffold plus 12 fixture/probe cases).
- `QpdfProbe --help`, `validate` on `minimal-1page.pdf` (npages=1) and
  explicit failure on `truncated-1page.pdf`; `probe-oracle` fails explicitly
  for a missing executable; `compare` passes with a matching fake oracle and
  exits 2 on a deliberate page-count mismatch and on a wrong oracle version.
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency. PdfPig is
  development validation only and is not packaged.
- Real qpdf 12.4.2 executable smoke has not run yet; no exe checksum or
  JPEG/Zopfli build features are recorded. `tools/bootstrap-qpdf.ps1` and
  `tools/run-reference-checks.ps1` fail explicitly when `QPDF_EXE` is missing.
  See `docs/COMPATIBILITY.md` for the recorded gap.

The Windows/Linux CI workflow builds the probe via the solution but the
reference lane remains an explicit local job requiring `QPDF_EXE`; ordinary
`dotnet test` stays offline and never needs `external/` or a qpdf binary.
These tests validate runner contracts and fixtures, not PDF behavior.

## Next work

1. Work item 03: probe ImageSharp 3.1.12's PDF-relevant JPEG semantics and
   allocation/cancellation controls, plus the .NET Flate APIs. Record gaps;
   do not change the approved dependency to hide them.
2. M1: implement minimal owned input/output, governor and diagnostics, then a
   strict classic-xref reader and one complete read/select/write operation.
   Exercise it through narrow argv/job routes and a real package consumer.
3. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.
