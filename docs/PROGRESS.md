# Progress

## Current checkpoint
Work item 122 done (M11): overlay/underlay block option forms match the reference. Block options require the `=` form (`--file=file`, `--to=`, `--from=`, `--repeat=`); space-separated values fail on both sides with exit 2. Empty ranges are accepted on both sides and select nothing (previously `--to=`/`--repeat=` were wrongly rejected). Evidence: equals-form rejection theory (8 argv shapes over both blocks), long-form/positional byte identity, empty-`--to` no-stamp case, `--file=` budget case, and a new `overlay-file` differential case (long-form trios plus 2/2 exit parity on all four space forms); 1843 tests green.

## Remaining work
- CMYK/YCCK raw samples: the approved ImageSharp public API yields only converted RGB, so raw four-component samples cannot be preserved through it. Needs a decision: an alternate extraction path or a declared, tested difference with narrow tolerances.
- Codec allocation/cancellation accounting: ImageSharp-internal buffers are opaque to the governor. Needs a decision: a conservative budgeting model or a declared, tested difference.
- Quadding: blocked on font metrics; no metric source exists in the approved dependency set.
- Nonzero-generation inspection: show-object with nonzero generation fails explicitly; no versioned object store exists.
- Page-unreachable form-tree pruning and direct-node promotion: the reference drops, ignores, or promotes where managed code preserves verbatim (declared differences).
- Containment-only limits (--no-default-limits, --parser-max-errors) and the no-Zopfli build profile: intentional and never completable.
- Radio synchronization already matches the reference (never synchronized); no work remains.

## Verification
- Offline lane: dotnet restore Lokad.Qpdf.slnx --locked-mode, dotnet build, dotnet test (currently 1843 green).
- Reference lanes need QPDF_EXE pointing at external/oracle/msvc64/qpdf-12.4.2-msvc64/bin/qpdf.exe (pinned qpdf 12.4.2): tools/run-reference-checks.ps1 and tools/run-differential-checks.ps1 (currently green, including the dr-encoding, dr-merge, parent-held, direct-rename, overlay-widgets, and overlay-file cases).
- Ordinary tests must never require external/; reference lanes must fail loudly when tools are missing, never skip.
- Editing notes: repo files use LF, while PowerShell writers emit CRLF, so normalize edited files back to LF. Builds often exceed the 10 s tool window, so run log-file builds in the background and poll. Before lanes, verify the probe directory Lokad.Qpdf.dll hash matches the fresh library build.

## Reference pin
- qpdf 12.4.2, ImageSharp 3.1.12 as the only runtime package, net10.0, nullable enabled, warnings as errors, no unsafe blocks, interop, subprocess bridges, or vendored code. 151 inventory entries complete with 6 partial behind the gaps above; crypto operations excluded with negative rejection evidence.
