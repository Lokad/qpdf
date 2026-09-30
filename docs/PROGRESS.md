# Progress

## Current checkpoint

Work item 06b: narrow argv and job JSON adapters over the shared selection
plan. `ArgvParser` accepts the upstream spelling subset
(`input --pages [--file=]file [--range=]n -- output`, positional forms
included); `JobJsonParser` accepts `{inputFile, outputFile,
pages:[{file, range}]}` with upstream field names. Both converge on
`Qpdf.SelectPageAsync` through one `SelectionPlan`; crypto options reject
with the stable `UnsupportedCryptoOperation` diagnostic and exit 2 without
echoing values. Everything outside the subset fails explicitly.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 164 tests (122
  prior plus 42 adapter cases: happy argv/job paths and their byte-identical
  parity, structural errors, range/file/terminator rules, response files,
  `--empty`, stdout, unknown options, multi-source, malformed/oversized job
  JSON, encrypted inputs, warning-exit mapping, cancellation, null guards).
- Crypto evidence: `--password=s3cret-value`, `--decrypt`, `--encrypt`,
  `--remove-restrictions`, job `encrypt`/`password` keys all yield
  `UnsupportedCryptoOperation` with exit 2 and no secret echo.
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency.
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary. No compatibility-inventory entry changes: the narrow subset proves
routes, not full option contracts, which remain later work.

## Next work

1. Work item 07: clean package-consumer exercise of the path and the
   documented supported profile.
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.
