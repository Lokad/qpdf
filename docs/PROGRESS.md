# Progress

The integrated performance implementation is qualified on `219eca3` (2026-10-12).
All nine representative warmed structural-job medians meet the 2× native qpdf 12.4.2 target on this machine (0.66–1.77×). [Benchmark results](BENCHMARK-RESULTS.md) report variability, allocations, process memory, output sizes and reusable raw observations. The agreed [supported profile](SUPPORTED-PROFILE.md) and completed features remain closed.

CMYK/YCCK JPEGs remain opaque with warnings when decoding is requested; Zopfli remains unavailable. Both accepted limitations are tested. Crypto remains excluded, including empty-password inputs. Other requirements and documented differences remain in [compatibility](COMPATIBILITY.md).

Performance work now targets ≤1.0× native for every agreed structural workload,
using single-CPU library execution and sequential measurements. Benchmark
acceptance now rejects a no-op checker and marker-only object-stream claim.
All 18 original fresh managed/native outcomes and three negative controls pass;
production is unchanged. Four orthogonal cells will add content-derived IDs,
object-heavy rewriting, sparse selection and single-page splitting, with codecs
excluded. Performance commits require reduced time or allocation, without a
minimum percentage gain.

## Integration qualification

Locked restores and Release builds passed on Windows and Ubuntu WSL2 with .NET SDK/runtime 10.0.401/10.0.12 and zero warnings/errors. Both complete ordinary suites passed all 17,846 checks with no failures/skips. These counts record the run; individual historical test identities are not acceptance obligations.

| Check | Platform | Wall time | Result |
| --- | --- | ---: | --- |
| Ordinary suite | Windows | 6m11s | Passed |
| Ordinary suite | Ubuntu 24.04 WSL2 | 6m11s | Passed |
| Complete pinned reference | Windows | 5m57s | Passed; 1,858 object comparisons and 442 rewrite trios |
| Seven visual cases and two defect controls | Windows | 20s | Passed |
| Release pack, package inspection, fresh consumers and rejection controls | Windows / Ubuntu | 19s / 26s | Passed |

Fresh consumers restore the reviewed dependency graph in locked mode from an isolated package/feed/cache. Wrong package-version and corrupt external-hash controls reject. Runtime dependency pins and the five reviewed temporary ImageSharp advisory exceptions remain unchanged. Source archive, logs/TRX, phase timings and package hashes are retained in the ignored `external/reviews/2026-10-11-performance-integration/final-219eca3/` checkpoint; Linux uses the same source. Repeat qualification for changes, failures or unresolved concerns.

## Integrated changes and safeguards

- Names, dictionary enumeration, known arrays and parser scratch avoid redundant copies; bounded raw-name hash/comparison work avoids per-byte governor calls.
- Private copier/checker graphs use scoped ownership. Unchanged immutable stream payloads and object-stream scratch are reused. Public defensive copies and surviving aliases remain covered.
- Exclusive single-source memory-host jobs borrow immutable stream slices and fund the whole pinned input. Public readers, custom retaining hosts and multisource copying keep independent payloads. The unchanged 2 MiB three-source consumer and typed/argv/job regression preserve decoded content and cleanup without retaining mostly unused source inputs.
- Shared fixed writer tokens and pure counting avoid formatting buffers. Default IDs hash incrementally while classic/QDF/linearized golden hashes remain unchanged.
- Counted ordinary, linearized and split output admits staging before publication. Fully overwritten storage avoids redundant clearing; only written bytes publish, underestimated hints grow safely, and settled storage drops promptly. Cancellation, limits, caller mutation and prior-output semantics remain tested.

Performance corrections are retained when time or allocation falls with correctness and practical resource checks, without a minimum percentage gain. Ownership-sensitive changes retain complete-job lifetime and performance evidence. No production forced GC, parsed-document cache, native runtime bridge or new accounting/benchmark framework was added.

The approximate memory contract preserves significant-buffer/growth admission, allocation/decompression amplification protection, work limits, cancellation, host boundaries, cleanup and valid surviving outputs. Representative normal/adversarial workloads suffice. Unreleased charges do not prove retained memory. Exact CLR sizes, individual small-object or metadata-alias receipts, fixed GC-round retirement and precise bookkeeping charges remain withdrawn; see [governance](SUPPORTED-PROFILE.md#governance).

Hosted CI passed the pushed performance tree `d35ade4`: [run 38211652340](https://github.com/Lokad/qpdf/actions/runs/38211652340), Windows 5m06s, Ubuntu 6m04s, pinned reference 4m46s. One push-triggered run was needed, with no reruns. Further parity work proceeds in coherent local batches; avoid a hosted run per probe. Tagging, publication and release-version changes remain separate actions.
