# Progress

## Current checkpoint

M11 completion audit through item 169; suite 1906/1906 green. Inventory 157 entries (140 flags): 153 complete, 4 partial, 49 excluded-crypto. The 4 page-assembly defaultReview rows (--pages block, --collate[=n[,m,...]], --empty, --split-pages[=n]) are now reviewed against pinned upstream cli.rst: block grammar with -- terminator, collate/split optional group defaults, empty zero-page base, camelCase job keys with argv/job/typed parity and budget pins, oracle select/merge/collate/split lanes green. defaultReview stands at 87 reviewed and 70 pending (49 crypto plus 21 other); flip only in verified batches, never bulk. Next is item 170 in the remaining backlog.

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
