# Progress

## Current checkpoint

M11 completion audit through item 185; suite 1906/1906 green. Inventory 157 entries (140 flags): 153 complete, 4 partial, 49 excluded-crypto. defaultReview is now 157/157 reviewed: the last row (partial --optimize-images) was reviewed for forms/keys/evidence while its status stays partial behind the permanent CMYK gap, like the other 3 partials (rejection-only containment limits, no-Zopfli profile). All 4 partials keep non-empty remainingGaps, pinned by test. Next is item 186: release-action follow-ups, which stay separate explicit actions.

## Remaining work

- Held-open partials: containment-only limits, no-Zopfli build profile, CMYK four-component images; plus the tested direct-node promotion difference.
- Publication is a separate explicit action; no push, tag, or release from implementation work.

## Verification

- Offline lane: restore, build, and test the solution in Release with locked mode (1906 green).
- Reference lanes re-ran green at item 177 (log external/diff/w177-lane.log, zero FAIL); they must fail loudly when tools are missing, never skip. Ordinary tests never require external. Lane probes live in ignored external/diff/.
- Package lane: pack, verify-package.ps1, consumer restore plus run (green at items 183-184).
- Editing notes: repo files use LF, so normalize edited files back to LF. Builds often exceed the tool window, so run log-file builds in the background and poll. Before lanes, verify the probe directory Lokad.Qpdf.dll hash matches the fresh library build.

## Reference pin

- qpdf 12.4.2, ImageSharp 3.1.12 as the only runtime package, net10.0, nullable enabled, warnings as errors, no unsafe blocks, interop, subprocess bridges, or vendored code. Crypto operations excluded with negative rejection evidence.
