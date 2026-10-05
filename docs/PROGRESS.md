# Progress

## Current checkpoint

JPEG uses a per-job ImageSharp allocator, governed output, token-bearing calls and aggregate frame admission. Four authored progressive grayscale/RGB frames now have exact pinned-oracle samples, public rewrite and tight coefficient-budget disposal evidence. Flate admits state/scratch/capacities/copies and aggregate samples, charging direct/chain/companion/hint decoding once; PNG selectors are working bytes rather than samples. Shared predictors admit output/work, check within rows and release Flate intermediates; packed TIFF padding agrees with the oracle.

Root Release checkpoint: 5555/5555 tests green, pinned reference (including full differential coverage) green, package/symbol boundary verified. The parser lane covers 1846 exact object comparisons and 432 rewrite validations. Its latest 56 lexical/container bodies cover binary/octal strings, display thresholds, malformed names, hex/EOF, mismatched terminators and root words through valid/rebuilt xrefs at six budgets. All new rewrites compare argv/job bytes and oracle/independent-reader outcomes; 28 malformed-name scenarios explicitly require warning exits on reopening. The prior 37-body dictionary guard matrix remains covered; its page-tree-reference case has inspection/offline copy-rejection evidence and stays excluded from rewrite validations. Fresh-artifact consumer verification passes 30 smokes and both specific rejection cases, with supplied-artifact DLL identity and all five reviewed locks unchanged.

## Remaining work

- JPEG/Flate state-allowance and retention measurements, textured/progressive fidelity beyond the authored flat frames, other filters' ownership/admission, and measured cancellation during codec/CPU work remain open; passing a token does not establish cancellation latency.
- Final package checks must use verify-package-consumer.ps1 and its negative integration cases; direct restores against a globally cached alpha do not establish freshness. Keep approved external dependency hashes locked.
- Next A04/A06 unit: govern display buffers, growth overlap, private dictionary-key copies and final strings/diagnostics before allocation, with explicit ownership/release and diagnostic-size preflight. A 4 MiB binary-value component allocated 33,637,808 bytes cumulatively under a 64 KiB memory ceiling while the governor reported zero peak. Input/value allocation was excluded; this proves missing display admission, not a whole-job peak/RSS/retention measurement. Preserve meaningful failed/canceled counters and independently verify the correction.
- Parser stays partial for broader nested loads (indirect stream lengths, recovery trials and compressed members through public routes) and trial/discarded-value ownership. The authored lexical/container and dictionary guard matrices now agree. Inspection selects string representation from decoded bytes; stray hashes use distinct zero-byte name markers and warn once per token without consuming the error budget. Rewrites preserve malformed names and warn again; strict loading rejects them. Root words survive rebuilt-xref sanity checks. Preserve the prior null-reference/generation fixes, per-object state, encrypted-trailer precedence, host limits and cancellation regressions.
- Numeric component evidence remains local/historical: eight cancellations after observed real-scan work took 0.084–1.8541 ms with working admission released; 1,000 warmed bounded conversions allocated zero heap bytes. Exact-rational midpoint tests prove the authored rounding matrix. These results do not close A03/A06 or establish whole-parser containment.
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
