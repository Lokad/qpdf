# Progress

## Current checkpoint

M11 completion audit through item 182; suite 1906/1906 green. Inventory 157 entries (140 flags): 153 complete, 4 partial, 49 excluded-crypto. The 16 main-context crypto rows are now reviewed: exclusion scope correct, all upstream forms match, job keys rejected without output, CryptoOptions set still pinned. defaultReview stands at 156 reviewed and 1 pending: partial --optimize-images, deliberately held open with the permanent CMYK gap. Next is item 183: section 13 release-gate audit.

## Remaining work

- Held-open partials: containment-only limits, no-Zopfli build profile, CMYK four-component images; plus the tested direct-node promotion difference.
- M11 exit needs section 13 gates green and no silent skips.

## Verification

- Offline lane: restore, build, and test the solution in Release with locked mode (1906 green).
- Reference lanes re-ran green at item 177 (log external/diff/w177-lane.log, zero FAIL); they must fail loudly when tools are missing, never skip. Ordinary tests never require external. Lane probes live in ignored external/diff/.
- Editing notes: repo files use LF, so normalize edited files back to LF. Builds often exceed the tool window, so run log-file builds in the background and poll. Before lanes, verify the probe directory Lokad.Qpdf.dll hash matches the fresh library build.

## Reference pin

- qpdf 12.4.2, ImageSharp 3.1.12 as the only runtime package, net10.0, nullable enabled, warnings as errors, no unsafe blocks, interop, subprocess bridges, or vendored code. Crypto operations excluded with negative rejection evidence.
