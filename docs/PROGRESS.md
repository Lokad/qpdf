# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The target remains open. The latest
qualified primary panel (`c29c5a3`) records five cells at or below 1.0×, with
ratios 0.58–3.55×. [Benchmarks](BENCHMARKS.md) gives current comparative figures
and reproducible commands. Keep changes that reduce time or allocation; no
minimum percentage applies.

Pushed `3e30201` passed [hosted CI run 38559796408](https://github.com/Lokad/qpdf/actions/runs/38559796408):
Windows 6m48s, Ubuntu 4m46s, pinned reference 6m01s. Each ordinary job passes
17,898 checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 exact object
comparisons and 442 rewrite trios. Dependency pins and version are unchanged.

## Current correction

Dictionary parsing inserts ordinary keys with one hash/probe through the existing
name table. Duplicate insertion returns the prior value without mutation, keeping
warning/repair checks before replacement, first-key identity, insertion order
and discarded-value cleanup. No new table, storage contract or ownership scheme.

The original X100 paired probe records 2.352→2.216 ms/job, with overlapping IQRs
(2.212–2.631 / 2.184–2.537); no confirmed speedup is claimed. Allocation remains
about 0.670 MB/job. The same fresh public job uses 270,249→264,269 charged work
units at unchanged charged peak memory, with identical independently validated
output bytes. The redundant lookup is removed structurally.

All 4,184 focused syntax, name-table, reader and selection checks pass (20s).
The existing collision regression now checks prior-value return and nonmutation
on duplicate insertion, alongside first-key identity/order. Recovery, warning,
work/cancellation and discarded-value controls remain. All 26 fresh managed/native
outcomes and three negative controls pass with unchanged inputs. Locked Release
build/explicit pack/inspection/fresh consumer/rejections pass (25s), preserving
the 2 MiB multisource consumer ceiling. Clean committed-source qualification
retains all 26,993 primary and 6,468 additional file-host observations, fresh
independent semantics and unchanged inputs. Five primary cells meet parity; the
original sparse full-input cell remains 3.55×. Additional file-host selection is
0.416×. Separate panels do not isolate timing effects of the parser change.

Private strict file-host selection retains bounded input prefetch and one
complete-reader retry on the same lease before publication. Public documents,
memory-host jobs and other operations retain their storage contracts. Logical
input limits precede reads; significant buffers and short-read overlap are
admitted. Its additional benchmark stays separate from the original thirteen
memory-host cells, including the unresolved full-input sparse boundary cost.

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
