# Progress

## Current checkpoint
Work item 135 done (M11): release-gate audit and cancellation wiring. Audited every PLAN section 13 gate against evidence: code hygiene (no unsafe/interop/subprocess/ambient IO, codec confined), pins (ImageSharp [3.1.12] central/lock/package, verified by script), package and consumer gates (pack, allowlist verify, consumer smoke all green), crypto exclusion (49 entries), API/host-boundary docs, and companion contracts. Found and fixed one latent gap: every public entry built its governor with CancellationToken.None, starving interval and codec cancellation checks; all 15 constructions now carry the caller token (null behavior change for live tokens, proven by the green suite). Recorded the optimize-images partial as permanent with a forced-recompression skip test over genuine CMYK input. 153 complete with 4 partial.

## Remaining work
- Direct-node promotion and malformed parent links: embedded direct kids stay embedded where the reference promotes them to indirect (rename outcomes agree); a /Kids entry without a matching /Parent back-link stays preserved silently where the reference corrects and warns (exit 3, tested difference).
- Containment-only limits (--no-default-limits, --parser-max-errors) and the no-Zopfli build profile: intentional and never completable.
- Radio synchronization already matches the reference (never synchronized); no work remains.

## Verification
- Offline lane: dotnet restore Lokad.Qpdf.slnx --locked-mode, dotnet build, dotnet test (currently 1879 green).
- Reference lanes need QPDF_EXE pointing at external/oracle/msvc64/qpdf-12.4.2-msvc64/bin/qpdf.exe (pinned qpdf 12.4.2): tools/run-reference-checks.ps1 and tools/run-differential-checks.ps1 (currently green, including the dr-encoding, dr-merge, parent-held, direct-rename, overlay-widgets, overlay-file, generate-unreachable, cmyk, show-object generation, and incr cases).
- Ordinary tests must never require external/; reference lanes must fail loudly when tools are missing, never skip.
- Editing notes: repo files use LF, while PowerShell writers emit CRLF, so normalize edited files back to LF. Builds often exceed the 10 s tool window, so run log-file builds in the background and poll. Before lanes, verify the probe directory Lokad.Qpdf.dll hash matches the fresh library build.

## Reference pin
- qpdf 12.4.2, ImageSharp 3.1.12 as the only runtime package, net10.0, nullable enabled, warnings as errors, no unsafe blocks, interop, subprocess bridges, or vendored code. 153 complete with 4 partial behind the gaps above; crypto operations excluded with negative rejection evidence.
