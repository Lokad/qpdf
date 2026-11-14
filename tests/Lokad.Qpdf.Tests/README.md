# Focused test support

`TestDocumentEngine` centralizes the identical empty-input inspection engine
setup used by isolated object-graph tests. It preserves table/value/engine
admission and disposal. Callers still construct their own meaningful objects and
expected results. `MemoryBudgetHost` borrows storage and a caller budget without
closing either; it replaces identical pass-through wrappers.

Keep narrow observation seams where an ordinary final result cannot prove the
ownership or cancellation boundary:

| Seam | Guarantee and primary tests |
| --- | --- |
| `FilterChain.ResolveObserved` | Retires the observer and completed governor while escaped filter names remain funded; `FilterPlanTests`, `OwnedFilterPlanTests`. |
| `LinearizationCheck.TryReadOwnedDataObserved` | Retires decoded/combined temporary backing while parsed owners and source survive, and cancels after decode; `LinearizationDataOwnershipTests`. |
| `PdfParser.CopyNumericTextObserved` | Stops the actual string-copy loop after deterministic work for either token; `NumericCopyCancellationTests`. Pre-cancelled copies use the same observer. |
| Owned output-key reflection in `EncryptedSerializerTests` | Withholds the engine's own key to prove size-only passes make no key-dependent cipher calls, with failing emission and restored-key controls. |

Own-type backing-field reflection and isolated weak-reference controls remain
where they prove real collectability independently of admission refunds. No
compiler-generated frame or private BCL collection layout is used for those
proofs. `PageInheritance.InheritedValues._first` is required InlineArray backing.
The security [ownership map](Security/README.md) assigns primitive, writer,
source, wrapper and publication guarantees to their primary tests.

Native inline-image and document-open tables run every distinct semantic/native
observation at a primary route. They retain differing route observations and
targeted job/mixed/nested wiring controls. Split-label tables exhaust label,
selection and split-size semantics through typed selection, with explicit argv/job
reordering, duplicate, boundary, prefix and unlabelled cases. Frozen native records
remain complete; route sampling does not replace independent expected fields.

`Foundation/PublicationAdmissionTests` owns shared host-fault phases, executed
counters and sequential commits. Prepared graph tests retain their own admission
refusal sweeps and write-failure refunds; form generation and placement retain
complete boundary controls. `Writing/InspectionFailureTests` owns JSON exception
translation, distinct inspection scratch and the rewrite source that survives
through commit. Feature tables reuse these shared phase guarantees.

Preparation admission cases live with forms, placements, content, page assembly,
object streams and attachments. Each owner supplies its fixture and actual public
invocation to `Foundation/PreparationAdmissionChecks`, which shares only the
admission/fault assertions and host wrapper. Case names select within one owner.
