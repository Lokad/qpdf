# Progress

Performance targets single-CPU parity (<=1.0x) with pinned native qpdf 12.4.2 using
equivalent operations, options and input delivery. The target remains open. The
original thirteen memory-host workloads (`bf03d9a`, ratios 0.61-3.47x) remain
end-to-end evidence, including complete fresh input acquisition. Comparable
file-backed measurements supplement them; they never replace an original result.
[Benchmarks](BENCHMARKS.md) gives current figures and reproducible commands.
Keep reduced time or allocation; no minimum percentage applies.

Pushed `d67be39` passes [hosted CI run 38620390134](https://github.com/Lokad/qpdf/actions/runs/38620390134):
Windows 7m14s, Ubuntu 5m57s, reference 6m12s. Both ordinary platforms pass 17,918
checks without failures/skips, locked Release build/pack, inspection, fresh
consumer and rejection controls. Reference covers 1,858 exact object comparisons
and 442 rewrite trios. Pins, advisory exceptions and version remain unchanged.

## Current correction

Argv/job single-page and one-source range selections now use the existing bounded
classic-xref reader on custom hosts. Catalog copying remains intact. Missing
ranges, diagnostics or uncertain parsing retry once on the same lease with the
requested recovery policy; publication never retries. Other layouts/operations
and public/in-memory storage contracts remain. All 3,535 focused selection,
recovery, catalog, content and publication checks pass (32s), including bounded
reads, short reads, changing future inputs, required catalog payloads, default
repair versus strict rejection, limits, cancellation and failed publication.

Original sparse-input file-backed pairs against saved `79061c7`, with the same
driver/options and independently validated output: argv 5.969 ->0.734 ms versus
native 1.771 ms (0.414x); job JSON 6.522 ->0.862 ms versus native 1.876 ms (0.459x).
Both paired probes finish under one minute. Managed output is byte-identical to
the baseline. The original memory-host sparse measurement remains 3.4732x with
full acquisition explicit. Final integration qualification is pending.

The earlier unused compressed-inventory removal remains integrated; default/
preserve behavior and validation order remain. Its overlapping rewrite timing
does not establish a speedup. The unmeasured staging prototype is set aside.
Earlier completed corrections, rejected trials and all raw evidence remain.

## Preserved scope

The [supported profile](SUPPORTED-PROFILE.md) stays delivered. CMYK/YCCK JPEG
remains opaque with warnings, Zopfli is unavailable, and crypto including
empty-password inputs is excluded. Other [compatibility requirements](COMPATIBILITY.md)
are not waived. Closed features and historical test counts add no obligations.

`MaxMemoryBytes` bounds approximate charged working storage. Admit significant
buffers, growth/copy overlap and decompression/allocation amplification; preserve
work limits, cancellation, host boundaries, cleanup, output integrity, public
immutability and surviving aliases. Keep the unchanged 2 MiB multisource consumer
ceiling. Unreleased charges do not prove retained memory. Exact CLR sizes,
individual small-object/metadata-alias receipts, fixed GC rounds and precise
bookkeeping charges remain withdrawn; see [governance](SUPPORTED-PROFILE.md#governance).

No CPU parallelism, forced production GC, parsed-job cache, runtime native bridge,
new accounting/benchmark framework, dependency/version change, tagging or package
publication. The five reviewed temporary ImageSharp 3.1.12 exceptions remain.
