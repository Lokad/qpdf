# Progress

## Current checkpoint
Work item 133 done (M11): codec decision. Four-component opacity is final, not held open: the pinned 3.1.12 assembly offers no CMYK pixel type and no raw-component decoder surface (proven by reflection and failed compile), so DCTDecode of CMYK/YCCK stays opaque with an explicit warning on every path. A hand-built baseline 4-component JPEG with known DC-only samples pins the boundary from the oracle side (plain decodes to exactly 128 + DC, Adobe YCCK converts, both exit 0; managed exits 3). Codec allocation/cancellation is proven bounded: cancellation checked at codec entry and per decoded row, 64 KiB working set verified at 1 MP. New cmyk differential (recorded oracle samples, 0/3 exits) plus unit tests over genuine four-component inputs. 153 complete with 4 partial.

## Remaining work
- Nonzero-generation inspection: show-object with nonzero generation fails explicitly; no versioned object store exists.
- Direct-node promotion and malformed parent links: embedded direct kids stay embedded where the reference promotes them to indirect (rename outcomes agree); a /Kids entry without a matching /Parent back-link stays preserved silently where the reference corrects and warns (exit 3, tested difference).
- Containment-only limits (--no-default-limits, --parser-max-errors) and the no-Zopfli build profile: intentional and never completable.
- Radio synchronization already matches the reference (never synchronized); no work remains.

## Verification
- Offline lane: dotnet restore Lokad.Qpdf.slnx --locked-mode, dotnet build, dotnet test (currently 1873 green).
- Reference lanes need QPDF_EXE pointing at external/oracle/msvc64/qpdf-12.4.2-msvc64/bin/qpdf.exe (pinned qpdf 12.4.2): tools/run-reference-checks.ps1 and tools/run-differential-checks.ps1 (currently green, including the dr-encoding, dr-merge, parent-held, direct-rename, overlay-widgets, overlay-file, generate-unreachable, and cmyk cases).
- Ordinary tests must never require external/; reference lanes must fail loudly when tools are missing, never skip.
- Editing notes: repo files use LF, while PowerShell writers emit CRLF, so normalize edited files back to LF. Builds often exceed the 10 s tool window, so run log-file builds in the background and poll. Before lanes, verify the probe directory Lokad.Qpdf.dll hash matches the fresh library build.

## Reference pin
- qpdf 12.4.2, ImageSharp 3.1.12 as the only runtime package, net10.0, nullable enabled, warnings as errors, no unsafe blocks, interop, subprocess bridges, or vendored code. 153 inventory entries complete with 4 partial behind the gaps above; crypto operations excluded with negative rejection evidence.
