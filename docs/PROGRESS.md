# Progress

## Current checkpoint

M11 completion audit through item 162; suite 1906/1906 green. Inventory 157 entries (140 flags): 153 complete, 4 partial, 49 excluded-crypto. The 2 pages defaultReview rows (--file=file, --range=numeric-range) are now reviewed against pinned upstream cli.rst: = and space named forms plus positional file/range with argv/job parity, omitted range selects all pages, []-element job schemas covered by parent entries, differential merge case green. defaultReview stands at 64 reviewed and 93 pending (49 crypto plus 44 other); flip only in verified batches, never bulk. Next is item 163 in the remaining backlog.

## Remaining work

- Held-open partials: containment-only limits, no-Zopfli build profile, CMYK four-component images; plus the tested direct-node promotion difference.
- defaultReview backlog verified per entry around capability fixes.
- M11 exit needs full lanes per behavior change, section 13 gates green, and no silent skips.

## Verification

- Offline lane: restore, build, and test the solution in Release with locked mode.
- Reference lanes need QPDF_EXE pointing at the pinned qpdf 12.4.2 under external/oracle and run the reference and differential scripts; they must fail loudly when tools are missing, never skip. Ordinary tests never require external.
- Editing notes: repo files use LF, so normalize edited files back to LF. Builds often exceed the tool window, so run log-file builds in the background and poll. Before lanes, verify the probe directory Lokad.Qpdf.dll hash matches the fresh library build.

## Reference pin

- qpdf 12.4.2, ImageSharp 3.1.12 as the only runtime package, net10.0, nullable enabled, warnings as errors, no unsafe blocks, interop, subprocess bridges, or vendored code. Crypto operations excluded with negative rejection evidence.
