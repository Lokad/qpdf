# Progress

## Current checkpoint

JPEG uses a per-job ImageSharp allocator, governed output, token-bearing calls and aggregate frame admission. Four authored progressive grayscale/RGB frames now have exact pinned-oracle samples, public rewrite and tight coefficient-budget disposal evidence. Flate admits state/scratch/capacities/copies and aggregate samples, charging direct/chain/companion/hint decoding once; PNG selectors are working bytes rather than samples. Shared predictors admit output/work, check within rows and release Flate intermediates; packed TIFF padding agrees with the oracle.

Root Release checkpoint: 2376/2376 tests green, pinned reference (including full differential coverage) green, package/symbol boundary verified. Differential binary delimiter searches are ordinal, avoiding culture-sensitive stalls on large raw samples. The explicit parser lane now covers 306 exact object comparisons and 18 rewrite trios, including integer/reference damage under valid and rebuilt xrefs. Fresh-artifact consumer verification copies the reviewed lock/project, binds only the supplied local artifact hash, uses a new feed/cache and checks the consumed DLL. All 25 smoke scenarios pass; wrong versions and external hash mismatches reject before execution, leaving the tracked lock unchanged.

## Remaining work

- JPEG/Flate state-allowance and retention measurements, textured/progressive fidelity beyond the authored flat frames, other filters' ownership/admission, and measured cancellation during codec/CPU work remain open; passing a token does not establish cancellation latency.
- Final package checks must use verify-package-consumer.ps1 and its negative integration cases; direct restores against a globally cached alpha do not establish freshness. Keep approved external dependency hashes locked.
- Per-object repair covers valid/rebuilt-xref loads and compressed members; strict paths remain strict. Full-range integers, long leading-zero tokens, conversion-to-null and budgeted invalid-reference outcomes now have public/oracle evidence with integer work/text admission. Unknown words, threshold/null, proximity, duplicate/fake keys and invocation ceilings remain covered. Parser stays partial for long real tokens, lexical damage, container transitions, orphan-key/error interactions, full nested loads and trial-allocation lifetimes. New oracle probes expose long-real rejection, brace token boundaries, NUL names, literal newline normalization and orphan/error interactions; finish these before closing A04.
- Benchmarks: fix counters/memory ownership before treating peaks as evidence; independent allocation/retention plus mid-operation cancellation; record reproducible results and fix demonstrated problems.
- M11 open with three truthful partials (no-Zopfli profile, CMYK images, parser-max-errors per-object budgets); push/tag/publication are separate actions, not implementation blockers.
- Stale drman.log (151 bytes) left alone; never ships.

## Verification

- Offline lane: restore, build, and test the solution in Release with locked mode.
- Reference lane needs QPDF_EXE pointing at pinned qpdf 12.4.2 under external/oracle; run-reference-checks.ps1 includes the full differential script and run-parser-checks.ps1. Missing tools fail loudly, never skip. Ordinary tests never require external. Lane probes live in ignored external/diff/.
- Package lane: pack, verify-package.ps1, verify-package-consumer.ps1 plus test-package-consumer.ps1.
- Editing notes: repo files use LF, so normalize edited files back to LF (the worktree mixes CRLF checkouts on this machine; verify with byte counts, never assume). PowerShell `+=` flattens byte arrays: accumulate bytes in List[byte[]]. Builds often exceed the tool window, so run log-file builds in the background and poll without overlapping test sessions. Before lanes, verify the probe directory Lokad.Qpdf.dll hash matches the fresh library build.

## Reference pin

- qpdf 12.4.2, ImageSharp 3.1.12 as the only runtime package, net10.0, nullable enabled, warnings as errors, no unsafe blocks, interop, subprocess bridges, or vendored code. Crypto operations excluded with negative rejection evidence.
