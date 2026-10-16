# Cooperative memory admission

`IMemoryBudget`, `IMemoryScope` and `IMemoryReservation` let a host expose its
shared memory allowance without adding a runtime package dependency. A library
adapter forwards to the host's allowance; scopes introduce owners, not separate
limits. Admission is immediate and thread-safe. Refusal leaves accounting
unchanged and fails the operation with `QpdfMemoryAdmissionException`, distinct
from cancellation, a local `QpdfLimitException` and an actual allocation failure.

The budget-aware `QpdfInputReader.ReadExactlyAsync` overload admits fresh input
storage before reading. A complete owned array is reused; short or sliced reads
admit the additional assembly capacity before copying. Failed or canceled reads
refund their admission after outstanding input work finishes. The input lease
remains caller-owned.

The caller supplies a scope that outlives the returned bytes, consumes those
bytes and drops all aliases before disposing that scope. Disposal refunds the
charge immediately without waiting for GC; it does not dispose actual storage
or cancel work. Retaining the bytes requires retaining their caller scope.
Do not dispose a scope while its reads, writes or descendants are active.

Hosts separately account for private storage and returned backing capacity
beyond the requested read length. Borrowed inputs retain their existing charge;
independent copies need additional admission. The allowance is an estimate of
owned capacity, not a guarantee about CLR heap size or process RSS.

At this stage shared admission is exposed by the standalone budget-aware read
helper. PDF operations retain their existing local `QpdfLimits` accounting;
the shared execution and host-storage integration is not yet available.
