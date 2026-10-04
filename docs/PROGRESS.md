# Progress

## Current checkpoint

M11 completion audit through item 181; suite 1906/1906 green. Inventory 157 entries (140 flags): 153 complete, 4 partial, 49 excluded-crypto. The 11 256-bit-encryption crypto rows are now reviewed: exclusion scope correct, all upstream forms match, encrypt-nested job paths parent-covered, argv/job rejection evidence with no output. defaultReview stands at 140 reviewed and 17 pending (16 main-crypto plus partial --optimize-images); flip only in verified batches, never bulk. Next is item 182: main-context crypto rows.

## Remaining work

- Held-open partials: containment-only limits, no-Zopfli build profile, CMYK four-component images; plus the tested direct-node promotion difference.
- defaultReview backlog (16 main-crypto plus partial --optimize-images) verified per entry around capability fixes.
- M11 exit needs section 13 gates green and no silent skips.

## Verification

- Offline lane: restore, build, and test the solution in Release with locked mode.
- Reference lanes need QPDF_EXE pointing at the pinned qpdf 12.4.2 under external/oracle and run the reference and differential scripts; they must fail loudly when tools are missing, never skip. Ordinary tests never require external. Lane probes live in ignored external/diff/.
- Editing notes: repo files use LF, so normalize edited files back to LF. Builds often exceed the tool window, so run log-file builds in the background and poll. Before lanes, verify the probe directory Lokad.Qpdf.dll hash matches the fresh library build.

## Reference pin

- qpdf 12.4.2, ImageSharp 3.1.12 as the only runtime package, net10.0, nullable enabled, warnings as errors, no unsafe blocks, interop, subprocess bridges, or vendored code. Crypto operations excluded with negative rejection evidence.
