# Progress

## Current checkpoint
Work item 131 done (M11): evidence-reference audit. Cross-checked all 725 test names and 89 oracle tags cited in options.json against the suite and the lane: fixed 5 stale budget-test references pointing at a nonexistent test, verified every other reference resolves to a real test or lane assertion. No behavior change; 152 complete with 5 partial.

## Remaining work
- CMYK/YCCK raw samples: true-4-component probes show only --check on valid CMYK diverges (oracle silent exit 0; managed skip+warning); optimize, json-decode, and corrupt-stream checks match. Needs a decision: a CMYK-capable extraction path or a declared, tested difference scoped to valid-stream checks.
- Nonzero-generation inspection: show-object with nonzero generation fails explicitly; no versioned object store exists.
- Page-unreachable form-tree pruning and direct-node promotion: the reference drops, ignores, or promotes where managed code preserves verbatim (declared differences).
- Containment-only limits (--no-default-limits, --parser-max-errors) and the no-Zopfli build profile: intentional and never completable.
- Radio synchronization already matches the reference (never synchronized); no work remains.

## Verification
- Offline lane: dotnet restore Lokad.Qpdf.slnx --locked-mode, dotnet build, dotnet test (currently 1858 green).
- Reference lanes need QPDF_EXE pointing at external/oracle/msvc64/qpdf-12.4.2-msvc64/bin/qpdf.exe (pinned qpdf 12.4.2): tools/run-reference-checks.ps1 and tools/run-differential-checks.ps1 (currently green, including the dr-encoding, dr-merge, parent-held, direct-rename, overlay-widgets, and overlay-file cases).
- Ordinary tests must never require external/; reference lanes must fail loudly when tools are missing, never skip.
- Editing notes: repo files use LF, while PowerShell writers emit CRLF, so normalize edited files back to LF. Builds often exceed the 10 s tool window, so run log-file builds in the background and poll. Before lanes, verify the probe directory Lokad.Qpdf.dll hash matches the fresh library build.

## Reference pin
- qpdf 12.4.2, ImageSharp 3.1.12 as the only runtime package, net10.0, nullable enabled, warnings as errors, no unsafe blocks, interop, subprocess bridges, or vendored code. 152 inventory entries complete with 5 partial behind the gaps above; crypto operations excluded with negative rejection evidence.
