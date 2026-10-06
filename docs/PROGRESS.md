# Progress

## Current checkpoint

Root Release verification: **5901/5901 tests pass**, with no failures or skips.
Fresh package/symbol verification passes **38 consumer smokes and two specific
rejection cases**. Fourteen focused native job-check cases agree on exits and
reporting. The full pinned reference lane passes for these frozen sources,
including **1846 exact parser comparisons and 432 rewrite validations**;
its terminal exit is zero and library/test identities remain unchanged.
This is local Windows evidence; final audited-commit release metadata and
Windows/Linux reference CI remain open.

The job `check` key now uses the shared checker and requires an empty-string
value. Argv/job checking composes with page counting and linearization inspection;
failed checking stops following inspection. Checking traverses pages quietly when
declared counts disagree, and page-count inspection reports the declaration.
Ordinary typed document reading keeps its count warning. Checker/page-count
outcomes snapshot work/peaks/handles after cleanup; broader counters remain open.
The new regressions cover checker failures, warning modifiers, public filter/
memory limits, recovery, crypto rejection, prior outputs and observed host-read
cancellation. An existing stream-cancellation observation race now retries at most
eight attempts and still requires actual cancellation after observed admission.
The 56 new job cases and one revised argv case pass. With the identical test
assembly, the preceding DLL fails 43 and passes 14 of those 57 cases; the latter
are preservation controls. All five dependency locks remain unchanged.

The preceding reader correction admits cache/loading/decoding collection wrappers,
actual capacities and growth overlap through shared parser helpers. Removed
guard keys retain admitted backing storage. Cache entries carry subtree receipts:
unread duplicate replacements and disposal release their own admission; borrowed
values and nested loads retain theirs. Document disposal drops reader metadata,
closes the input lease and preserves returned immutable bytes. **17 added cases
pass; the preceding DLL fails all 17.** Coverage includes independent backing
allocation, finite typed/argv/job traversal, generous controls, prior targets,
retries, raw/Flate member replacement, unread/borrowed members and observed-work
cancellation. The recovery-tail test retries completed-before-cancel attempts
and still requires actual observed-work cancellation and zero residual admission.

For the 45,211-byte/2,000-null-object probe, warmed cache loading now admits
**102,520 additional bytes**, with **102,520 forced-GC retained bytes** and
1,311,832 cumulative allocation. Cache capacity is 2,333; independently allocated
backing storage is 102,784 bytes. Metadata weak references die after disposal,
and admission returns to the still-rooted xref table's 103,472 bytes. A separately
funded xref/component run rejects at a 100,000-byte cache ceiling after 631
accepted objects; disposal releases all metadata admission. Eight cancellations
after observed cache work take **0.0813–3.9998 ms** locally; accepted cache storage
remains admitted until owner disposal. Source/host allocation is excluded; these
results establish no whole-job/RSS or universal latency bound. Explicit capacity
growth lowers retained storage while increasing cumulative allocation.

Earlier verified corrections cover parser attempt/subtree/reader acceptance,
successful recovery and linearization trials, selected trailer aliases, xref/
revision/trailer capacities, canonical display and object-inspection counters.
The authored numeric/lexical/container/dictionary matrices, null-reference and
generation behavior, per-object repair, encrypted-trailer precedence and host
limits remain covered. JPEG has a per-job allocator and four exact flat
progressive grayscale/RGB fixtures; Flate/predictors admit state/output/scratch,
charge row work and decoded samples once per stage. Those scopes remain partial.

## Remaining work

- **Next A07 compatibility correction: nested job fields and checker contexts.**
  `jobJsonFile` is the missing root handler among the target schema's 98 keys;
  characterize native loading/precedence and implement bounded host-mediated
  nesting. Native also accepts checking with page/object/xref/attachment
  inspection and selection/transformation keys that the managed routes reject.
  These are required gaps, recorded in the partial `--check` entry. Root case
  names do not prove nested/default/interaction coverage.
- **Next A04/A06 unit: input/table/returned-node lifetime ownership.** A strict
  2,097,614-byte padded input leaves 2,101,210 live admitted bytes after public
  document disposal. Forced GC confirms its runtime input copy and xref map
  remain rooted by the disposed document; metadata is gone, leases/handle
  counters are zero and returned bytes remain valid. Drop implementation
  references and release only their actual owners, preserving escaped aliases,
  nested loads and success/failure/cancellation cleanup. Diagnostics and broader
  public repair/recovery nesting remain open. This probe is outside test/reference
  counts.
- **A06 counters:** checker, page-count and object execution snapshots now keep
  observed work/peaks and closed handles. Other failed/canceled routes and
  invocation/presentation counters still need reconciliation, including
  substantial xref work previously hidden by zero counters. Diagnostic entry/
  list/message owners and independent measurements remain required.
- **A01/A02 codecs:** measure JPEG/Flate allowances, BCL/codec/pool retention,
  repeated/concurrent/failed admission, other filters' intermediate owners,
  and textured/progressive samples beyond the four authored flat frames.
- **A03 cancellation:** measure termination during codec/CPU/host work after
  observable progress, with bounded noninterruptible units, delayed hosts,
  cleanup and preserved targets. Passing tokens is insufficient latency evidence.
- **A05 independent validation:** automate pinned oracle/probe freshness and
  isolated runs, add explicit reference CI and development-only visual evidence.
  Missing tools must fail; ordinary tests work without external resources.
- **A07 docs/package:** reconcile supported-profile contexts/defaults and public
  ownership/threading/warning/cancellation/publication contracts, then freshly
  pack/verify/consume final audited sources. Benchmark reports are historical;
  governor counters alone cannot establish admission/retention completeness.
- **M11 and both completion verdicts remain open.** Inventory: 140 flags,
  157 contexts, 153 complete and four truthful partials: required checker
  interactions, parser loading/ownership, accepted CMYK opacity and accepted
  no-Zopfli profile. Pushing,
  tagging and publication are separate actions.

## Verification and resume

- Read AGENTS.md, COMPATIBILITY.md and ARCHITECTURE.md; confirm HEAD and pending
  changes. Preserve unrelated drman.log. Keep local PLAN/evidence/build outputs
  ignored and unshipped; do not force-add them.
- Offline: locked restore, Release build/test. Package: pack, verify-package.ps1,
  verify-package-consumer.ps1 and test-package-consumer.ps1. Use the supplied fresh
  artifact and reviewed external hashes; a cached direct consumer restore adds
  no freshness evidence. Intentional dependency changes require reviewed locks.
- Reference: pinned qpdf 12.4.2 with run-reference-checks.ps1, which includes the
  differential and parser lanes. Check oracle/probe/library identities, isolate
  source/output/TEMP, and retain terminal exits. Poll the same live process;
  observation timeouts do not justify restarting it.
- Record sources/runtime/platform/commands/terminal exits and separate governor
  counters from allocation, retained storage and local timing. Repo files use LF.
  Target net10.0, nullable/warnings-as-errors/no unsafe; ImageSharp 3.1.12 is the
  only runtime package. No native engine, interop, subprocess bridge or vendoring;
  crypto is excluded with negative evidence.
