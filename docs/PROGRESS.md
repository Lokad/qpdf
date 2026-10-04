# Progress

## Current checkpoint

Limits work done: invocation flags cannot raise host ceilings (BudgetExceeded), --no-default-limits lifts compatible parser defaults, bounded --parser-max-errors (default 15) governs repair trial parses. Suite 1931/1931 green. Inventory 157 entries (140 flags): 155 complete, 2 partial (no-Zopfli profile, CMYK images, accepted as tested compatibility exceptions); defaultReview 157/157 reviewed. Reference+differential lanes green with zero FAIL (log external/diff/w188-lane.log), including new over-limit NDL decode parity and budgeted-repair legs. Benchmarks done: 10-scenario counter report green with recorded baseline (docs/BENCHMARKS.md); timed BenchmarkDotNet run green (3/3, sub-millisecond means, proportional allocations, no anomalies to fix). Next: final full-gate verification.

## Remaining work

- Held-open partials: no-Zopfli build profile, CMYK four-component images; plus the tested direct-node promotion difference.
- Benchmarks: representative operations, allocations/retained memory, work counters, cancellation latency; record reproducible results and address findings.
- Publication is a separate explicit action; no push, tag, or release from implementation work.

## Verification

- Offline lane: restore, build, and test the solution in Release with locked mode.
- Reference lanes need QPDF_EXE pointing at the pinned qpdf 12.4.2 under external/oracle and run the reference and differential scripts; they must fail loudly when tools are missing, never skip. Ordinary tests never require external. Lane probes live in ignored external/diff/.
- Package lane: pack, verify-package.ps1, consumer restore plus run.
- Editing notes: repo files use LF, so normalize edited files back to LF (the worktree mixes CRLF checkouts on this machine; verify with byte counts, never assume). PowerShell `+=` flattens byte arrays: accumulate bytes in List[byte[]]. Builds often exceed the tool window, so run log-file builds in the background and poll without overlapping test sessions. Before lanes, verify the probe directory Lokad.Qpdf.dll hash matches the fresh library build.

## Reference pin

- qpdf 12.4.2, ImageSharp 3.1.12 as the only runtime package, net10.0, nullable enabled, warnings as errors, no unsafe blocks, interop, subprocess bridges, or vendored code. Crypto operations excluded with negative rejection evidence.
