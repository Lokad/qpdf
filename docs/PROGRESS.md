# Progress

## Current checkpoint

JPEG decode/encode uses a per-job managed ImageSharp allocator, governed encoded output and token-bearing codec calls. Flate decode/encode now admits codec state, scratch, output capacities, growth and copies before allocation; input refills charge work even without decoded output. Failed codec/output budgets release working owners and preserve published targets. Isolated Release snapshot: 1985/1985 tests green, pinned reference (including the full differential lane) green, package/symbol boundary verified. Fresh-package consumer passed after reevaluating only the rebuilt local package hash in that snapshot; the checked-in consumer hash still needs a reliable fresh-artifact policy.

## Remaining work

- JPEG/Flate state-allowance and retention measurements, progressive JPEG evidence, shared predictor allocation, aggregate decode admission, and measured cancellation during codec/CPU work remain open; passing a token does not establish cancellation latency.
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
