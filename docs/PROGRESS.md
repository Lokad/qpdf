# Progress

## Current checkpoint

JPEG uses a per-job managed ImageSharp allocator, governed output, token-bearing calls and aggregate frame admission. Flate admits state/scratch/capacities/copies and checks aggregate decoded samples before output growth; the shared decoder charges direct, chained, companion and hint calls once. PNG selectors do not consume the sample budget. Shared predictors admit output/work, reuse prior output samples and check cancellation within rows; Flate releases its inflated intermediate. Packed TIFF row padding agrees with the pinned oracle. Isolated Release snapshot: 2010/2010 tests green, pinned reference (including the full differential lane) green, package/symbol boundary verified. Fresh-package consumer passed after reevaluating only the rebuilt local package hash in that snapshot; the checked-in consumer hash still needs a reliable fresh-artifact policy.

## Remaining work

- JPEG/Flate state-allowance and retention measurements, progressive JPEG evidence, other filters' ownership/admission, and measured cancellation during codec/CPU work remain open; passing a token does not establish cancellation latency.
- Consumer verification must use the freshly packed artifact: the current checked-in local-package hash rejects changed package bytes in a fresh cache, while a global cached alpha can hide that mismatch. Keep approved external dependency hashes locked.
- Parser semantics remainder: per-object error budgets with tolerant trial parsing (interim still counts failed repair candidates; tight-budget legs stay managed-only with oracle succeeding per object); valid-xref repairable-syntax inputs participate only as a documented strict delta.
- Pending tolerant-parser edits currently fail to build at a missing BeginTolerantObject call; isolate unrelated corrections until those edits are completed and verified.
- Benchmarks: fix counters/memory ownership before treating peaks as evidence; independent allocation/retention plus mid-operation cancellation; record reproducible results and fix demonstrated problems.
- M11 open with three truthful partials (no-Zopfli profile, CMYK images, parser-max-errors per-object budgets); push/tag/publication are separate actions, not implementation blockers.
- Stale drman.log (151 bytes) left alone; never ships.

## Verification

- Offline lane: restore, build, and test the solution in Release with locked mode.
- Reference lanes need QPDF_EXE pointing at the pinned qpdf 12.4.2 under external/oracle and run the reference and differential scripts; they must fail loudly when tools are missing, never skip. Ordinary tests never require external. Lane probes live in ignored external/diff/.
- Package lane: pack, verify-package.ps1, consumer restore plus run.
- Editing notes: repo files use LF, so normalize edited files back to LF (the worktree mixes CRLF checkouts on this machine; verify with byte counts, never assume). PowerShell `+=` flattens byte arrays: accumulate bytes in List[byte[]]. Builds often exceed the tool window, so run log-file builds in the background and poll without overlapping test sessions. Before lanes, verify the probe directory Lokad.Qpdf.dll hash matches the fresh library build.

## Reference pin

- qpdf 12.4.2, ImageSharp 3.1.12 as the only runtime package, net10.0, nullable enabled, warnings as errors, no unsafe blocks, interop, subprocess bridges, or vendored code. Crypto operations excluded with negative rejection evidence.
