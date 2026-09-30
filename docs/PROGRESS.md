# Progress

## Current checkpoint

Work item 05b: strict classic-xref document inspection in the shipped library.
`QpdfDocument` opens single-section classic-xref PDFs from any host with a
header/version gate, tail `startxref`/`%%EOF` validation, fixed-width xref
parsing with `/Size` coverage, structural `/Encrypt` rejection, and a bounded
page-tree walk (cycle set, depth budget, traversal-wins `/Count` warnings).
`GetObject` resolves with header/xref consistency checks; free entries read
as null. No writer, operation, or command adapter exists yet.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 114 tests (99
  prior plus 15 document cases: 1/2-page counts and version, catalog/page
  spot checks, free-as-null, unknown/negative objects, truncation, corrupt
  `startxref`, missing `%%EOF`, encrypted-declaration rejection without
  secrets, page-tree cycles, `/Count` mismatch warnings, counters, lease
  closure on every failure path, cancellation, and input/work budgets).
- New synthetic fixtures `encrypted-decl.pdf` (trailer `/Encrypt`) and
  `cyclic-pages.pdf` (2-object page cycle); the compatibility manifest is
  unchanged because neither is an oracle-runner fixture.
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency.
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary; XRef streams, hybrid/incremental structures, and recovery remain
explicit M2/M4 work and fail as strict parse errors today.

## Next work

1. Work item 06: copy and serialize a selected page, add narrow argv/job
   adapters, verify independent output plus replay counts.
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.
