# Progress

## Current checkpoint

Work item 07: clean package consumer and the M1 close. The out-of-solution
`tests/Lokad.Qpdf.PackageConsumer` restores the packed NuGet from the local
feed and exercises typed, argv, and job selection plus parity and crypto
rejection through the public API only. `docs/SUPPORTED-PROFILE.md` records
the exact M1 behavior. M1 is code-complete: the remaining exit-gate leg is
upstream acceptance of our output, which needs the still-unbootstrapped
reference executable.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 166 tests (164
  prior plus indirect-length and bogus-filter opaque-stream cases).
- `CONSUMER SMOKE PASSED`: typed page-2 selection, argv page-1 selection,
  job page-2 selection, typed/job byte parity, and crypto rejection without
  secret echo, all against the packed `0.1.0-alpha.1`.
- New fixtures `indirect-length.pdf` (`/Length` by reference) and
  `bogus-filter.pdf` (unknown `/Filter` passes through undecoded).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency.
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

CI runs the consumer smoke on Windows and Linux after pack. Ordinary
`dotnet test` stays offline and never needs `external/`, a qpdf binary, or
the packed feed.

## Next work

1. M2: strict-reader breadth — governed Flate and predictors, xref/object
   streams, incremental/hybrid precedence, remaining filters, and byte/
   version/indirect-length semantics.
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1`, then confirm upstream acceptance of
   M1 outputs and reconcile the result.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.
