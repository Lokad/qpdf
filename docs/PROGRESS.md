# Progress

## Final counter correction in progress

DIAG-02C addresses observed counters lost on direct argv/response failures and
cancellation, and typed/companion snapshots captured before cleanup. Public
boundaries now refresh the shared governor snapshot after their core and lease
scopes finish. Loaded jobs retain their existing post-configuration snapshot;
status, diagnostics, payloads and publication order are preserved. Private typed
result copies retain existing warning/mapping/text references and readonly public
properties; no public signatures are added.

All **413 complete public controls** pass: 376 raw/Flate read/write jobs,
21 response/nested/mixed/multiple-file configuration jobs and 16 actual short-read
host-budget failures after the first completed byte. Success, read/write/publish/
close faults and after-start cancellation preserve immutable counters, fileless
payloads, accepted output aliases and existing publication order; zero leases and
handles remain. If disposal fails after commit, the published output stays
published, preserving the existing host contract rather than implying rollback.
All eight fields match the actual final governor in an exclusive 16 MiB no-GC
observation window for the 376 read/write jobs; later weak retirement is not a
snapshot mutation. Existing ownership GC controls remain unchanged.

Identical final tests on preceding `858BEE55`: **149 fail / 264 preserve / 413
eligible**, all failures are counter assertions, no loaders/skips/exclusions.
The additional **331 affected existing checks** pass. Earlier draft build errors,
ten early CPU-work assumptions and a later-GC observation remain separate.
Original fixtures, budgets, deadlines and cancellation/GC controls are preserved.
Final source/reference/package evidence is still pending; DIAG-02C and broader
DIAG-01/02 remain open.

## Hint ownership correction

HINT-03E/W/R/V and HINT-03 are retired by the combined correction at `4c60a78`
and `b7e3b27`, runtime `858BEE55` / tests `E9BE4F1A`.
Both writers scope construction collections, bit buffers, raw tables, compression
and final objects through replay/publication. The shared reader borrows immutable
raw streams and scopes decoding, overflow joins, cursors and temporary arrays.
Check/show own parsed models through reporting; independent table/entry/array/
outline aliases retain separate receipts without keeping discarded siblings or
decoded scratch alive.

The unchanged **4 KiB / 64 calls / four GC rounds** probe completes all 64 calls,
zero surviving bodies, peak **1503 bytes**. Its final **1224 bytes** remain owned
weak-ledger metadata pending job-thread maintenance; the extra maintenance control
retires it to zero. This qualification and the original 28-call/4047-byte failure
remain explicit. Original fixtures, budgets, deadlines and GC controls are preserved.

All **11008** Release cases pass: every **10833** preceding occurrence plus
**175** new controls, including **139 complete public jobs** and 36 component
checks. The 64 writer jobs cover raw/decoded inputs, classic/streamed and raw/
compressed hints, success/write fault/publication fault/cancellation; successful
outputs pass typed check and argv show. The 75 read jobs cover typed/argv/job
check/show on classic/streamed/primary/overflow/Flate inputs, success/input-close
fault/cancellation after observed work. Late admission/work failures and both
tokens retire only the attempt; source/prior output aliases survive and leases
close. Original 32 KiB/32-cycle controls and 128 KiB inflate allowance remain.
Identical writer tests on `9C5B832E`: **67 fail / 2 preserve / 69 eligible**;
reader tests on committed writer `CA21671B`: **30 fail / 76 preserve / 106 eligible**.
These are distinct baselines; no loader failures or skips. Draft build failures
and the corrected reflection-observer cycle/aborted run remain separate.

All **537** tracked raw paths / **514** implementation/test/tool/benchmark paths
match the verified committed freeze; **524** unrelated raw paths, all **247**
preceding test sources and five locks are preserved. Git identities match the
existing LF policy and are recorded separately from raw identities. Strict locked
restore/build have zero warnings and no overrides. The full pinned reference
wrapper completes both differential and parser stages: **1846** exact comparisons
and **432** rewrite trios. Fresh package/symbol inspection, **50** isolated consumer
smokes and both specific pre-smoke negatives pass; DLL/changelog/PDB match exactly.
All **908** public signatures and seven original carrier samples are unchanged.

Original native results stay **868 headers / 804 complete decoded / 868 exits**,
with every fixture/setter/route and all 64 differing pairs preserved.
All 868 managed headers and complete raw/decoded streams are identical to their
preceding observations; this ownership correction changes no compared payload.
Independent physical checks validate all **128** layouts and **512** oracle checks; these valid
layout differences do not redefine complete-byte equality.

Phase timings: full test console **6m13s** (process 6m28s);
native matrix 7m28s; full reference wrapper
41m41s, including approximately
33m13s parser artifact interval and
8m28s before that stage.
Strict restore/build 1.9s/1.5s;
pack/inspection 2.0s/3.3s;
fresh consumer/negatives 24.9s/11.2s.
Full tests/native/package ran independently alongside reference; parser timing uses
unmodified script artifact timestamps, while total wrapper timing is exact.
No expensive lane was repeated during this correction's final freeze. These are
local Windows results; explicit cross-platform/reference CI remains REF-02.

The next public failure is DIAG-02: direct argv failures/cancellation discard
observed counters, while loaded jobs report the final governor snapshot. Broader
ownership/presentation/cold/BCL/codec/RSS/CPU/host audit entries, eight AcroForm
schema gaps, M11/A01–A07 and both completion verdicts remain open.

## Physical linearization checker

HINT-02 is retired at `8326940` / `ea6445f`. The shared reader parses physical
object ends; the shared classifier resolves compressed members to actual containers.
Validation checks /O,/E,/T, primary/overflow hint spans, page runs/counts/lengths,
complete shared groups/members/identifiers and outline identity/count/offset/length.
Integer /P and deprecated content fields preserve the pinned checker's ignored
semantics. Show-linearization retains parsed raw fields on physical failures;
the established outline-offset diagnostic is preserved.

Runtime `9C5B832E` / tests `F7244CFD` passes **10833**
Release cases: all **9767** preceding occurrences and **1066** new controls.
The **1058 complete public jobs** cover 24 preserved form observations and 54
native-classified mutations/valid controls over seven routes, plus all original
64 pairs/128 layouts over argv/job check/show. All eight old form false passes
reject; valid multi-object groups and alternate /E,/T/primary/overflow boundaries
pass. Eight scratch controls keep the 32 KiB/32-cycle limits and exercise failed
admission, work exhaustion and both cancellation tokens. Identical final tests
on preceding `4972E00D`: **312 fail / 10521 preserve / 10833 eligible**, zero
loader failures, exclusions or skipped cases.

All **508** frozen implementation/test/tool/benchmark paths match the verified
tree; **516** unrelated raw files, all **246** preceding C# test sources and
five locks are preserved. Committed contents match the existing LF policy;
raw working-tree and Git identities are recorded separately. Strict restore/build
have zero warnings; the final pinned broad reference/differential stage passes.
The resumed unmodified parser lane passes **1846** comparisons and **432** rewrite
trios on the same freeze. Package/symbol checks, 50 fresh consumer
smokes and both specific negatives pass. Packed DLL/changelog/PDB exactly match
the freeze. The **908** public signatures and sampled carrier sizes are unchanged.

Phase timings: full execution **6m19s** (process 6m37s),
resumed parser **25m05s**, corrected build 44s,
focused execution 3s; pack 3.6s, inspection 1.3s,
fresh consumer 22.5s and negatives 7.2s.
The first full run's one outline-message regression, interrupted reference
(168s) and qualified package remain historical evidence. The final broad lane
finished before its session disappeared during parser checking after an environment
change; the interrupted log is retained, and only the unchanged parser lane was
repeated. No original fixture, budget, deadline, cancellation or GC
control changed. The original 4 KiB/28-call/4047-byte finding is preserved by the subsequent
HINT-03 correction above. M11/A01–A07 and both verdicts stay open.

The preceding HINT-03 baseline includes **64 complete public jobs**, argv/job across classic/
streamed and raw/compressed hints, success/write fault/publication fault/cancellation.
All outcomes close leases and preserve prior targets; 16 successful publications
pass typed checking. The compressed-input half performs 316/456 decoded bytes;
all 32 hint backings collect after four GC rounds, while governed residue remains.
The original unchanged 4 KiB encoder probe reproduces 28 calls/4047 bytes, zero
surviving bodies. Whole-job residue is not attributed solely to hints. Earlier
completion-stack observations and one unresolved cold backing stay qualified;
argv failures/cancellation return zero counters despite observed work (DIAG-02).
No ownership entry is retired by these observations.

## Combined implementation

The owned parser/diagnostic/copier and inspection/writer changes are integrated
at `2c1b2d2` and `6ff5236`. Raw-name tables charge collision work; diagnostic
storage and future export work are admitted before acceptance; JSON stages warnings
through input closure. Escaping value/check/diagnostic aliases remain funded while
completed jobs release ledger metadata. Writer row/index/capture/header/payload,
encoding/order/xref/remap/layout owners detach dead backing at their last consumer.
HINT-03 subsequently corrects hint bodies; broader complete-job ownership remains open.

Document-open traversal now includes direct containers/indirect roots, stops at
neighboring pages before assigning physical owners and orders generated object-stream
containers with catalog objects. Catalog copying preserves non-name PageMode.
The original eight native form warnings disappear. All **152** native observations
remain: **144** outputs pass native linearization and match physical assignments,
zero warnings; **eight** array-AcroForm schema rejections/native successes remain
explicit (HINT-05). HINT-02 corrects the checker's false passes of eight preserved
invalid old form outputs; hint ownership HINT-03 is subsequently retired above.

The preceding combined runtime `4972E00D` / tests `BDA364D9` passed **9767** Release cases,
retaining all **9597** preceding occurrences; 170 new controls. Identical tests
on preceding `8941BF8C` yield **120 fail / 9647 preserve / 9767 eligible**, no
loader failures/exclusions. The intermediate foundation builds with zero warnings
and passes **380** focused checks, including four public JSON repair jobs at their
original 247494/247489-byte ceilings. Draft dependency/build/public failures are
retained; fixtures, budgets, deadlines, cancellation and GC controls are unchanged.

The preceding full reference/differential passed **1846** exact parser comparisons and
**432** rewrite trios on the combined runtime. Original native results remain
**868 headers / 804 decoded / 868 exits**; all 64 hint pairs/128 physical layouts
are independently valid. Raw byte equality remains separate. The 908 public
signatures and independently sampled carrier sizes are unchanged.

The five ImageSharp 3.1.12 advisories have explicit temporary URL-specific
exclusions; audit and other warnings remain enabled, all five locks unchanged.
Strict restore/build and fresh artifact-consumer restore pass without overrides.
The [assessment](DEVELOPMENT.md) records reachability and review triggers.
Policy/checklist commit: `7b0379b`. Final strict package/symbol verification,
50 fresh consumer smokes and both specific pre-smoke negatives pass; packed DLL
and changelog exactly match the final tree.

Measured phases: combined test execution 4m57s (TRX interval 5m05s); full reference/
differential/parser 39m41s; strict build 37s. Intermediate focused execution 12s
(process 17s), build 34s. Final pack 1.5s, package inspection 0.6s, fresh consumer 14.9s and negative
consumer checks 6.1s. Exact source/artifact identities and terminal timings
are retained with verification evidence.

The [finite checklist](AUDIT-CLOSURE.md) names every remaining route/check/pass
condition. Keep the 4 KiB/28-call/4047-byte hint finding, grouped +24 bytes,
1768-byte failed zero-allocation sample and unchanged passing repeats explicit.
Original controls, qualifications and history remain in
[PROGRESS-HISTORY](PROGRESS-HISTORY.md). M11/A01–A07, overlay/form/default/context,
cold/codec/RSS/CPU/host, visual/reference CI and both completion verdicts stay open.
