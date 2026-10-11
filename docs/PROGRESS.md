# Progress

The agreed [supported profile](SUPPORTED-PROFILE.md) is qualified on committed
implementation `34c8097` (2026-10-11). No implementation or verification tasks
remain recorded for that profile. Hosted CI passes on `7bc2fc8`:
[run 38154808432](https://github.com/Lokad/qpdf/actions/runs/38154808432), with
Windows 7m01s, Ubuntu 5m26s and pinned qpdf 12.4.2 reference 3m45s. Both ordinary
jobs passed 17,874 tests with no failures/skips, package inspection, fresh locked
consumers and both rejection controls; logs/TRX/reference reports were uploaded.
Tagging and package publication remain separate actions.

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
same implementation source. Subsequent documentation, assets and initial
benchmark tools preserved it. The performance corrections below require a new
integration checkpoint to qualify their final source/package.

## Continuing work

The initial finite Linux structural benchmark comparison is complete on `35eb3fb`,
using WSL and pinned native qpdf 12.4.2. All nine cells validate independently;
seven process pairs and separate unfiltered warmed measurements are recorded in
[the comparison report](BENCHMARK-RESULTS.md). It adds no production, accounting
or CI timing obligations.

Warmed complete-job optimization is in progress, targeting at most 2× pinned
native on those nine structural workloads. Both persistent workers validate
fresh outputs; process startup and protocol are excluded, necessary file/host
transport included. The first correction removes quadratic thumbnail resource
exclusion during linearization using the existing page-user index. Seventy-five
focused tests pass, including thumbnail placement, streamed classification,
work/memory failures, cancellation and a new work-growth regression. An
interleaved seven-batch WSL confirmation improves the 500-page median from
155.03 ms to 115.67 ms (25.4%); native is 22.76 ms. Outputs validate independently
and their sizes are unchanged. The 2× target is still unmet; comprehensive
qualification follows the completed correction batch.

The second correction transfers the parser's fresh stream buffer directly to
the immutable stream and writes memory-host slices directly into private
staging. Interleaved 500-page rewriting improves 107.80 → 93.56 ms (13.2%);
measured allocation falls 88.61 → 78.19 MiB/job (11.8%). All seven paired batches
improve; native is 16.25 ms. Eight hundred ninety-three focused parser, recovery,
ownership, host and transaction checks pass, including input/write alias
isolation, canceled/rejected writes and active public stream cancellation.
Cancellation observes the remaining material payload and uses the existing
nonparallel controls so other large tests cannot consume its observation window.

Direct allocation reductions now transfer fresh name bytes, construct name
display once, enumerate serializer dictionary entries without repeated lookups,
compare stream keys as raw bytes and allocate copied arrays once at their known
size. Eight hundred fifty-six focused checks pass, including ordering/null
semantics, escapes, source aliases and selection. Fresh Linux rewrite outputs
validate independently and match the previous managed bytes. These structural
reductions have no timing acceptance gate; short measurements show no reliable
speedup. The complete-job target and final integration qualification remain pending.

Private selection/merge/rewrite sources now use the existing exclusive document
owner and transfer borrowed immutable values to the complete job before closing
each source. Effective catalog versions retain their own carrier. All 2,170
focused checks pass, covering versions, practical limits, failure/cancellation,
cleanup and surviving outputs. The unchanged 16 KiB ceiling may now admit small
jobs that previously exhausted receipt bookkeeping; successful publication is
validated, while work and material-memory exhaustion remain covered.
Interleaved warmed X100 rewriting improves 13.54 → 7.60 ms (43.9%), with allocation
3,514,864 → 3,019,200 bytes/job (14.1%) and identical output bytes. Collection
rates are mixed across generations; this does not establish an overall GC gain.
Native is 2.22 ms (3.42× gap); final qualification and the 2× target remain pending.

Parser scratch now counts/validates directly over tokens and allocates decoded
hex, name and literal-string buffers once, removing the digit array and trimming
copies. All 785 focused syntax/recovery/ownership checks pass. Whitespace/escape-
heavy values fit their decoded-storage budgets; material decoded storage still
rejects under the unchanged smaller ceiling. Grammar, repair markers, cancellation
and public byte immutability remain covered.

Use the [supported profile](SUPPORTED-PROFILE.md) for shipped behavior,
[architecture](../ARCHITECTURE.md) for implementation boundaries and the
[development guide](DEVELOPMENT.md) for checks and the five intentionally
suppressed ImageSharp 3.1.12 advisories. Choose new work by a concrete public
defect or an explicitly agreed feature. Run affected checks while correcting it,
then comprehensive qualification at integration/release checkpoints. Historical
test counts, checkpoint narratives and superseded audit ladders add no tasks;
Git retains that history.
