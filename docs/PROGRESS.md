# Progress

## Current checkpoint
Work item 129 done (M11): codec working-set budgeting. Allocation probes across 64 to 1024 pixel frames show ImageSharp fixed overhead peaking near 26 KiB above frame bytes, so decode and encode now reserve frame bytes plus a documented 64 KiB working set (released on return) instead of the frame alone; mid-call cancellation stays a declared boundary since the synchronous codec honors cancellation between calls. Evidence: decode/encode reservation unit cases with tight and roomy budgets; 1858 tests green and the full differential lane green.

## Remaining work
- CMYK/YCCK raw samples: the approved ImageSharp public API yields only converted RGB, so raw four-component samples cannot be preserved through it. Needs a decision: an alternate extraction path or a declared, tested difference with narrow tolerances.
- Nonzero-generation inspection: show-object with nonzero generation fails explicitly; no versioned object store exists.
- Page-unreachable form-tree pruning and direct-node promotion: the reference drops, ignores, or promotes where managed code preserves verbatim (declared differences).
- Containment-only limits (--no-default-limits, --parser-max-errors) and the no-Zopfli build profile: intentional and never completable.
- Radio synchronization already matches the reference (never synchronized); no work remains.

## Verification
- Offline lane: dotnet restore Lokad.Qpdf.slnx --locked-mode, dotnet build, dotnet test (currently 1846 green).
- Reference lanes need QPDF_EXE pointing at external/oracle/msvc64/qpdf-12.4.2-msvc64/bin/qpdf.exe (pinned qpdf 12.4.2): tools/run-reference-checks.ps1 and tools/run-differential-checks.ps1 (currently green, including the dr-encoding, dr-merge, parent-held, direct-rename, overlay-widgets, and overlay-file cases).
- Ordinary tests must never require external/; reference lanes must fail loudly when tools are missing, never skip.
- Editing notes: repo files use LF, while PowerShell writers emit CRLF, so normalize edited files back to LF. Builds often exceed the 10 s tool window, so run log-file builds in the background and poll. Before lanes, verify the probe directory Lokad.Qpdf.dll hash matches the fresh library build.

## Reference pin
- qpdf 12.4.2, ImageSharp 3.1.12 as the only runtime package, net10.0, nullable enabled, warnings as errors, no unsafe blocks, interop, subprocess bridges, or vendored code. 152 inventory entries complete with 5 partial behind the gaps above; crypto operations excluded with negative rejection evidence.
