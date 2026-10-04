# Progress

## Current checkpoint

JPEG uses a per-job ImageSharp allocator, governed output, token-bearing calls and aggregate frame admission. Four authored progressive grayscale/RGB frames now have exact pinned-oracle samples, public rewrite and tight coefficient-budget disposal evidence. Flate admits state/scratch/capacities/copies and aggregate samples, charging direct/chain/companion/hint decoding once; PNG selectors are working bytes rather than samples. Shared predictors admit output/work, check within rows and release Flate intermediates; packed TIFF padding agrees with the oracle.

Root Release checkpoint: 2932/2932 tests green, pinned reference (including full differential coverage) green, package/symbol boundary verified. Differential binary delimiter searches are ordinal, avoiding culture-sensitive stalls on large raw samples. The explicit parser lane covers 582 exact object comparisons and 64 rewrite trios, including numeric/lexical damage, literal bytes and recovered dictionary policy under valid/rebuilt xrefs. Fresh-artifact consumer verification copies the reviewed lock/project, binds only the supplied local artifact hash, uses a new feed/cache and checks the consumed DLL. All 27 smoke scenarios pass, including long real rounding/rewrite and NUL-name/literal-newline repair; wrong versions and external hash mismatches reject before execution, leaving the tracked lock unchanged.

## Remaining work

- JPEG/Flate state-allowance and retention measurements, textured/progressive fidelity beyond the authored flat frames, other filters' ownership/admission, and measured cancellation during codec/CPU work remain open; passing a token does not establish cancellation latency.
- Final package checks must use verify-package-consumer.ps1 and its negative integration cases; direct restores against a globally cached alpha do not establish freshness. Keep approved external dependency hashes locked.
- Per-object repair covers valid/rebuilt-xref loads and compressed members; strict paths remain strict. Full-range integers, conversion-to-null and invalid references have public/oracle evidence. Long real tokens now preserve text, admit text/state and copy/scan with work/cancellation checks; bounded conversion preserves exact binary64 rounding, proven by independent midpoint rationals. Eight local real-scan cancellations after observed work took 0.084–1.8541 ms with working admission released; 1,000 warmed bounded conversions allocated zero heap bytes. This is component evidence, not closure of A03/A06. Braces now split adjacent tokens; NUL names repair to null; literal CR/CRLF normalize to LF while escaped CR remains. Rebuilt xrefs discard orphan dictionary values, and valid xrefs retain fake keys; inspection hex spelling agrees with qpdf. Unescaping has interval work/cancellation checks and scalar work-failure admission tests. Parser stays partial for remaining lexical/container/orphan-error interactions, full nested loads and temporary-value ownership, including discarded/recovery values.
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
