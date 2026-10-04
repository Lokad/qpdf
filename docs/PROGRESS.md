# Progress

## Current checkpoint

Containment, names, recovery, reader and lease governance done: argv/job governors now use the caller cancellation token; Flate inflate charges work/decoded bytes with per-chunk cancellation; empty Flate emits a valid 8-byte zlib stream; FilterChain charges DCT once; page-range expansion admits entry budgets before allocating (null/empty plus span checks); names unescape exactly once for display/JSON/filter dispatch. Suite 1951/1951 green (19 new: canceled Flate, empty round-trip, DCT single-charge, null-range budget, tight zlib work budget, plus single-unescape name/JSON and escaped-filter checks, plus damaged-encrypted rejection, plus objstm allocation guard, plus load-depth bound, plus JPEG retained-memory ownership, plus governed leases (per-job handles on every input/output lease, per-call short-read charging, disposal-safe admission ordering, response memory admission, duplicate-lease refcounts), plus input-preparation admission (governed job-JSON parsing with model release, base64 pre-reserve), plus /Extends reads with an oracle differential leg). Inventory stays 157 entries (140 flags): 155 complete, 2 partial (no-Zopfli profile, CMYK images, accepted as tested compatibility exceptions); crypto rejection stays separate from implemented behavior.

## Remaining work

- Review findings: all R01-R16 probes pass (17/17; R14 closed this batch).
- Parser semantics: per-object error counting, 0 means unlimited, --no-default-limits lifts errors/damaged/filters only (never nesting), explicit host-set tracking instead of value equality, oracle evidence for finite/zero/default.
- Benchmarks: fix counters/memory ownership before treating peaks as evidence; independent allocation/retention plus mid-operation cancellation; record reproducible results and fix demonstrated problems.
- M11 narrowed to the two accepted partials with truthful labeling; push/tag/publication are separate actions, not implementation blockers.
- Stale drman.log (151 bytes) left alone; never ships.

## Verification

- Offline lane: restore, build, and test the solution in Release with locked mode.
- Reference lanes need QPDF_EXE pointing at the pinned qpdf 12.4.2 under external/oracle and run the reference and differential scripts; they must fail loudly when tools are missing, never skip. Ordinary tests never require external. Lane probes live in ignored external/diff/.
- Package lane: pack, verify-package.ps1, consumer restore plus run.
- Editing notes: repo files use LF, so normalize edited files back to LF (the worktree mixes CRLF checkouts on this machine; verify with byte counts, never assume). PowerShell `+=` flattens byte arrays: accumulate bytes in List[byte[]]. Builds often exceed the tool window, so run log-file builds in the background and poll without overlapping test sessions. Before lanes, verify the probe directory Lokad.Qpdf.dll hash matches the fresh library build.

## Reference pin

- qpdf 12.4.2, ImageSharp 3.1.12 as the only runtime package, net10.0, nullable enabled, warnings as errors, no unsafe blocks, interop, subprocess bridges, or vendored code. Crypto operations excluded with negative rejection evidence.
