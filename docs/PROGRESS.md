# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The target remains open. The latest
qualified primary panel (`7a55bff`) records five cells at or below 1.0×, with
ratios 0.61–3.40×. [Benchmarks](BENCHMARKS.md) gives current comparative figures
and reproducible commands. Keep changes that reduce time or allocation; no
minimum percentage applies.

Pushed `e36d3e4` passed [hosted CI run 38552302663](https://github.com/Lokad/qpdf/actions/runs/38552302663):
Windows 6m28s, Ubuntu 5m45s, pinned reference 6m04s. Each ordinary job passes
17,890 checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 exact object
comparisons and 442 rewrite trios. Dependency pins and version are unchanged.

## Current correction

Private strict selections on custom hosts now prefetch bounded ranges from
standalone classic xrefs, skipping large unused extents. The existing parser
and copier consume the ranges. Missing data, diagnostics or parse failures retry
once through the complete reader on the same open lease, before publication.
Public documents, memory-host jobs and other operations retain their existing
contracts. Full logical input limits apply before reads; material buffers and
short-read assembly overlap are admitted. No parallelism or parser rewrite.

An additional typed file-host comparison uses the original sparse fixture:
managed 5.649→0.667 ms/job, versus native 1.628 ms (0.410×); managed allocation
16.933→0.156 MB/job. Both managed revisions use the same file adapter and driver.
Their output bytes match; native/PdfPig semantics and the wrong-content control
pass. This additional job does not replace or close the original memory-host
sparse comparison, whose complete fresh input read remains its largest cost.

Eight public controls cover normal/short reads, retained metadata across omitted
payload, required-payload and warning fallback, physical error offsets, input
limits, cancellation, nonprogressing reads, cleanup and surviving prior output.
All pass (589ms); the wider reader/selection/presentation/cleanup run passes
3,980 checks. Locked Release build/explicit pack/inspection/fresh consumer and
rejections pass (17s), preserving the 2 MiB multisource consumer ceiling.
Clean-source qualification retains all 26,031 primary observations and 6,330
additional file-host observations, fresh independent semantics and original inputs.
The current file-host ratio is 0.430×. Grouped splitting is narrowly above parity
(1.034×); separate panels do not isolate timing effects. One combined hosted push
qualifies the committed implementation and current report.

## Preserved scope

Useful completed allocation, parser, copier and writer improvements remain in
place. Further work must name a concrete defect or finite verification task;
closed supported features and historical test counts add no obligations.
Source/artifact identities and all observations, including rejected trials,
remain in the [benchmark raw archives](BENCHMARKS.md).

The [supported profile](SUPPORTED-PROFILE.md) stays delivered. CMYK/YCCK JPEG
remains opaque with warnings, Zopfli is unavailable, and crypto including
empty-password inputs is excluded. These limitations are explicit and tested;
other [compatibility requirements](COMPATIBILITY.md) are not waived.

`MaxMemoryBytes` bounds approximate charged working storage. Admit significant
buffers, material growth/copy overlap and decompression/allocation amplification;
retain work limits, cancellation, host boundaries, cleanup and output integrity.
Unreleased charges do not prove retained memory. Exact CLR sizes, individual
small-object/metadata-alias receipts, fixed GC rounds and precise bookkeeping
charges remain withdrawn; see [governance](SUPPORTED-PROFILE.md#governance).
No CPU parallelism, forced production GC, parsed-job cache, runtime native bridge
or replacement accounting/benchmark framework was introduced.

Runtime pins and the five reviewed temporary ImageSharp 3.1.12 advisory exceptions
remain unchanged. No release-version change, tagging or package publication.
