# Progress

## Current checkpoint

M11 completion audit through item 177; suite 1906/1906 green. Inventory 157 entries (140 flags): 153 complete, 4 partial, 49 excluded-crypto. Full reference+differential lanes re-ran green at the audit milestone (log external/diff/w177-lane.log): reference oracle/reader agreement plus the whole differential case list with zero FAIL, including forms-check 3/3, parent-held 3/3, global/warnings/identity trios and linearize families. All complete non-crypto rows reviewed (107 reviewed); 50 pending are 49 crypto plus partial --optimize-images. Next is item 178: crypto-grammar review batches around the exclusion evidence.

## Remaining work

- Held-open partials: containment-only limits, no-Zopfli build profile, CMYK four-component images; plus the tested direct-node promotion difference.
- defaultReview backlog (49 crypto plus partial --optimize-images) verified per entry around capability fixes.
- M11 exit needs section 13 gates green and no silent skips.

## Verification

- Offline lane: restore, build, and test the solution in Release with locked mode.
- Reference lanes need QPDF_EXE pointing at the pinned qpdf 12.4.2 under external/oracle and run the reference and differential scripts; they must fail loudly when tools are missing, never skip. Ordinary tests never require external. Lane probes live in ignored external/diff/.
- Editing notes: repo files use LF, so normalize edited files back to LF. Builds often exceed the tool window, so run log-file builds in the background and poll. Before lanes, verify the probe directory Lokad.Qpdf.dll hash matches the fresh library build.

## Reference pin

- qpdf 12.4.2, ImageSharp 3.1.12 as the only runtime package, net10.0, nullable enabled, warnings as errors, no unsafe blocks, interop, subprocess bridges, or vendored code. Crypto operations excluded with negative rejection evidence.
