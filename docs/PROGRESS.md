# Progress

The agreed [supported profile](SUPPORTED-PROFILE.md) is qualified on committed
implementation `34c8097` (2026-10-11). No implementation or verification tasks
remain recorded for that profile. Hosted CI is being completed separately;
tagging and package publication remain separate actions.

Four-component CMYK/YCCK JPEGs remain opaque with warnings when decoding is
requested; Zopfli is explicitly unavailable. Both limitations are tested.
Other unmet original requirements remain documented in the
[compatibility inventory](COMPATIBILITY.md); qualification does not waive another
required compatibility mismatch or create a second blocking verdict.

## Qualification

Both platforms used .NET SDK 10.0.401/runtime 10.0.12. Locked restores and Release
builds passed with zero warnings/errors. Ordinary suites passed with no failures
or skips. Packages passed inspection, fresh locked-consumer execution and the
wrong-package-version/corrupt-external-hash rejection controls on both platforms.

| Check | Platform | Wall time | Result |
| --- | --- | ---: | --- |
| Ordinary suite | Windows | 7m39s | Passed |
| Ordinary suite | Ubuntu 24.04 WSL2 | 7m11s | Passed |
| Complete pinned qpdf 12.4.2 reference | Windows | 6m58s | Passed; 1858 object comparisons, 442 rewrite trios |
| Seven-case visual comparison | Windows | 24s | Passed, including both deliberate output-defect controls |

The reference run retains the original inputs, parser comparisons, limits and
negative controls, with bounded recovery additions. Its original 43m41s wall
time is now 6m58s: parser approximately 33m49s → 5m25s, differential 9m42s →
1m26s. Windows ordinary tests improved from the recorded 33.8-minute baseline
to 7m39s. No parallel verification workers or production forced GC were added.

Practical limits, allocation/decompression amplification protection, cancellation,
exceptional cleanup and surviving output aliases pass their existing controls.
Five representative normal/adversarial memory workload families pass, including
the original configuration fan-out failures and repeated hint encoding under
4 KiB. `MaxMemoryBytes` bounds a charged working-set estimate; unreleased charges
do not establish retained memory. Exact CLR sizes, small-object/metadata-alias
receipts, fixed GC retirement and precise bookkeeping accounting are withdrawn
release obligations. The [governance contract](SUPPORTED-PROFILE.md#governance)
and [development guide](DEVELOPMENT.md#focused-verification-and-memory-estimates)
describe the retained safeguards and representative checks.

Detailed local source/artifact identities, commands and phase results are in
`external/reviews/2026-10-11-delivery/c84059d7915a41fc91d488d5a9d66dc2/result.json`
(ignored run evidence). The Windows and Ubuntu packages were qualified from the
same implementation source. Later documentation-only edits preserve that source.

## Continuing work

Use the [supported profile](SUPPORTED-PROFILE.md) for shipped behavior,
[architecture](../ARCHITECTURE.md) for implementation boundaries and the
[development guide](DEVELOPMENT.md) for checks and the five intentionally
suppressed ImageSharp 3.1.12 advisories. Choose new work by a concrete public
defect or an explicitly agreed feature. Run affected checks while correcting it,
then comprehensive qualification at integration/release checkpoints. Historical
test counts, checkpoint narratives and superseded audit ladders add no tasks;
Git retains that history.
