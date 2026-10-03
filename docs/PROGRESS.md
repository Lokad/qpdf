# Progress

## Current checkpoint
Work item 120 done (M11): direct-dictionary collision renaming. RenameTree now returns the effective node so colliding direct kids rename with upward Kids-array rebuilds (probed: the oracle promotes the colliding direct kid and renames it D+1). Evidence: dc-a/b-1page.pdf fixtures, a direct-rename differential case (0/3 exit parity, page counts, renamed direct kid on both sides), and two unit cases (rename plus keep path); 1813 tests green and the full differential lane green.

Work item 121 in progress (M11): overlay/underlay widget import end-to-end. Oracle probes (ephemeral, under the ignored external/ directory) establish the spec: simple merged-widget import (field plus value imported, empty DR created), name collision (existing O plus imported O becomes O+1), DR conflict (destination /F1 kept, source font added as /F1_1, imported /DA rewritten, NeedAppearances propagated), parent-linked top-field import, and AcroForm-default /DA stamped explicitly onto copied fields. Next step: read the GraphCopier copy and memo semantics, then implement the widget branch in CopyOverlayPageAnnots reusing field-subtree copy, DA/Q override, DR union plus DA rewrite, collision renaming, and per-placement appearance copies.

## Remaining work
- Finish item 121 (engine plus unit tests plus fixtures plus differential case plus record updates).
- CMYK/YCCK raw samples: the approved ImageSharp public API yields only converted RGB, so raw four-component samples cannot be preserved through it. Needs a decision: an alternate extraction path or a declared, tested difference with narrow tolerances.
- Codec allocation/cancellation accounting: ImageSharp-internal buffers are opaque to the governor. Needs a decision: a conservative budgeting model or a declared, tested difference.
- Quadding: blocked on font metrics; no metric source exists in the approved dependency set.
- Nonzero-generation inspection: show-object with nonzero generation fails explicitly; no versioned object store exists.
- Page-unreachable form-tree pruning and direct-node promotion: the reference drops, ignores, or promotes where managed code preserves verbatim (declared differences).
- Containment-only limits (--no-default-limits, --parser-max-errors) and the no-Zopfli build profile: intentional and never completable.
- Radio synchronization already matches the reference (never synchronized); no work remains.

## Verification
- Offline lane: dotnet restore Lokad.Qpdf.slnx --locked-mode, dotnet build, dotnet test (currently 1813 green).
- Reference lanes need QPDF_EXE pointing at external/oracle/msvc64/qpdf-12.4.2-msvc64/bin/qpdf.exe (pinned qpdf 12.4.2): tools/run-reference-checks.ps1 and tools/run-differential-checks.ps1 (currently green, including the dr-encoding, dr-merge, parent-held, and direct-rename cases).
- Ordinary tests must never require external/; reference lanes must fail loudly when tools are missing, never skip.
- Editing notes: repo files use LF, while PowerShell writers emit CRLF, so normalize edited files back to LF. Builds often exceed the 10 s tool window, so run log-file builds in the background and poll. Before lanes, verify the probe directory Lokad.Qpdf.dll hash matches the fresh library build.

## Reference pin
- qpdf 12.4.2, ImageSharp 3.1.12 as the only runtime package, net10.0, nullable enabled, warnings as errors, no unsafe blocks, interop, subprocess bridges, or vendored code. 145 inventory entries complete with 12 partial behind the gaps above; crypto operations excluded with negative rejection evidence.
