# Linux benchmark comparison — 2026-10-11

Native qpdf was faster and used less peak process memory across this small structural workload set. Managed whole-process medians were 233–577 ms; native medians were 3.5–29 ms. These are complete public jobs with startup and file transport. The separate warmed managed results below are not native engine comparisons.

[Reproduction and protocol](BENCHMARKS.md#linux-process-comparison) · [raw process samples, metadata and commands](../benchmarks/results/2026-10-11-linux.json) · [full warmed measurements](../benchmarks/results/2026-10-11-warmed.json)

## Whole-process results

Times are median milliseconds, with interquartile **width** in parentheses. Peak RSS is the median target-process peak in MiB; output is total KiB, including every split file. All seven samples, min/max and memory variability remain in the raw JSON. M/N means managed/native.

| Workload | Managed ms (IQR) | Native ms (IQR) | Peak RSS MiB M/N | Output KiB M/N |
| --- | ---: | ---: | ---: | ---: |
| select-S10 | 233.23 (19.61) | 3.49 (0.17) | 44.0/6.4 | 3.9/4.1 |
| select-M500 | 391.77 (6.25) | 23.51 (0.46) | 99.6/7.7 | 5096.3/5105.1 |
| merge-250x2 | 403.36 (24.22) | 24.75 (3.18) | 93.4/8.3 | 5096.7/5105.5 |
| split-M500 | 475.78 (27.07) | 29.06 (2.23) | 111.2/8.0 | 5101.1/5108.7 |
| rewrite-M500 | 415.86 (29.01) | 18.46 (0.58) | 108.9/7.6 | 5096.3/5095.9 |
| rewrite-X100 | 263.44 (11.11) | 4.84 (0.60) | 48.1/6.6 | 69.5/69.5 |
| object-streams-X100 | 275.30 (11.09) | 5.35 (0.34) | 50.0/6.6 | 65.8/65.8 |
| linearize-M500 | 577.32 (2.03) | 24.58 (0.58) | 115.4/8.2 | 5099.2/5109.0 |
| check-X100 | 264.08 (32.55) | 5.32 (1.11) | 48.2/6.8 | 0.0/0.0 |

The 10-page managed job spends most of its elapsed time outside the warmed public call: startup, JIT and probe/file transport are included here. Larger jobs add graph traversal, serialization and staging. Peak RSS includes the CLR, the managed in-memory host and multiple input/output copies. This run does not attribute the difference precisely to individual engine components or establish retained-memory leaks. Larger output sizes differ by less than 0.3%; the 10-page output differs by about 4.8% (199 bytes). Numbering, object packing and linearization hints describe different valid physical layouts.

## Warmed managed public jobs

Each result uses all seven measured BenchmarkDotNet iterations with outlier removal disabled. Times below use raw workload time per operation; no native process ratio is inferred. Allocations are cumulative managed bytes per operation, separate from cross-implementation peak RSS. GC counts remain in the full report.

| Workload | Median ms (IQR width) | Managed allocation KiB/op |
| --- | ---: | ---: |
| select-S10 | 1.82 (2.56) | 341.1 |
| select-M500 | 101.68 (10.99) | 68483.1 |
| merge-250x2 | 92.80 (6.08) | 68315.2 |
| split-M500 | 89.57 (10.77) | 71477.9 |
| rewrite-M500 | 121.88 (12.34) | 80536.3 |
| rewrite-X100 | 12.25 (0.88) | 3713.5 |
| object-streams-X100 | 14.36 (2.32) | 5277.1 |
| linearize-M500 | 168.12 (26.63) | 79318.4 |
| check-X100 | 7.25 (0.43) | 1578.2 |

Warmed jobs construct a fresh in-memory host, copy inputs and execute the public argv operation. Disk transport is excluded; setup is outside measurement. Allocation/GC variation and the single WSL machine limit generalization. A preliminary 250 ms run remains in ignored local evidence; the final lane uses 1000 ms targets and retains every sample. The whole-process pairs were not rerun.

## Inputs, validation and environment

- Generated S10: 10 pages, 3,892 bytes. M500: 500 pages, 5,217,570 bytes; merge sources A250/B250 are 2,609,054 bytes each. X100: classic 70,952 bytes; streamed 67,406 bytes; streamed plus an incremental metadata update 67,615 bytes. Shared Helvetica fonts/forms and unique visible page markers are preserved; comment padding provides the larger pass-through byte workload. This is synthetic structural evidence, not a representative rendering or image workload.
- All nine managed/native cells passed native structural checks, original-content/resource fingerprints and independent PdfPig page-text/order checks before timing. Rewrite catalog/incremental metadata, ten 50-page split groups, object-stream presence and both linearization checkers passed. A wrong-order expectation rejected. All 126 recorded process outputs matched their validated counterparts; 36 conditioning outputs are also retained. Original input hashes remained unchanged. Checking includes structural, stream and page-content checks on these filter-free content streams; no image/rendering stage exists in these inputs.
- Intel Xeon W-2223 at 3.60 GHz, four physical/eight logical CPUs; host RAM 31.7 GiB, WSL-visible RAM 15.5 GiB. Ubuntu 24.04.3 WSL2, kernel 6.18.40.1-microsoft-standard-WSL2, ext4. Warm filesystem cache, sequential alternating pairs and fresh directories; buffered file closure, no fsync. Host/VM scheduling may affect variability.
- Managed Release artifacts: source `35eb3fb`, .NET SDK 10.0.401/runtime 10.0.12, BenchmarkDotNet 0.15.8. The source tree was clean. Subsequent runner-only corrections bind native provenance, recognize compressed JSON exports and use the final unfiltered warmed protocol; measured C# and production code are unchanged.
- Native qpdf 12.4.2: clean pinned commit `4eba95899886e851cc41d76886483b347612f2a8`, GCC 13.3.0, CMake Release (`-O3 -DNDEBUG`), static libqpdf with system zlib/libjpeg, native crypto provider, optional Zopfli disabled. Binary/library hashes, compiler/cache details, exact commands and input hashes are in the raw result.

No production tuning, accounting changes, dependency/version changes, image work or CI timing gates were introduced. Generated inputs use repository code and its license; commands regenerate every input, including the native streamed form and explicit incremental update. Large inputs/outputs and complete run logs remain under ignored local evidence.
