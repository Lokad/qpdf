# Progress

## Current checkpoint

Limits work: host ceilings enforced, --no-default-limits accepted, bounded --parser-max-errors added; suite 1931/1931 green. Inventory 157 entries (140 flags): 153 complete, 4 partial, 49 excluded-crypto; defaultReview 157/157 reviewed. Verdict: every mechanically verifiable gate is green (tests, lanes zero FAIL, package, consumer, docs pins, review sweep), but full M11 completion cannot be claimed: 3 required-scope partials are permanent by settled scope (containment limits, CMYK codec), measured benchmark work is future work (no benchmarks/ project), and push/tag/publication are separate unauthorized actions. These need user scope decisions, not more code. Goal stays active pending those decisions.

## Remaining work

- Held-open partials: containment-only limits, no-Zopfli build profile, CMYK four-component images; plus the tested direct-node promotion difference.
- Scope decisions for the user: accept the 4 partials as permanent declared differences with subset labeling (CHANGELOG already labels 153/157), defer benchmark work explicitly, and authorize any release action separately.
- Publication is a separate explicit action; no push, tag, or release from implementation work.

## Verification

- Offline lane: restore, build, and test the solution in Release with locked mode (1906 green).
- Reference lanes re-ran green at item 177 (log external/diff/w177-lane.log, zero FAIL); they must fail loudly when tools are missing, never skip. Ordinary tests never require external. Lane probes live in ignored external/diff/.
- Package lane: pack, verify-package.ps1, consumer restore plus run (green at items 183-184).
- Editing notes: repo files use LF, so normalize edited files back to LF. Builds often exceed the tool window, so run log-file builds in the background and poll. Before lanes, verify the probe directory Lokad.Qpdf.dll hash matches the fresh library build.

## Reference pin

- qpdf 12.4.2, ImageSharp 3.1.12 as the only runtime package, net10.0, nullable enabled, warnings as errors, no unsafe blocks, interop, subprocess bridges, or vendored code. Crypto operations excluded with negative rejection evidence.
