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
| `PdfParser.CopyNumericTextObserved` | Stops the actual string-copy loop after deterministic work for either token; `NumericCopyCancellationTests`. Its neutral observer also replaces private-method reflection for pre-cancelled copy checks. |
| Owned output-key reflection in `EncryptedSerializerTests` | Withholds the engine's own key to prove size-only passes make no key-dependent cipher calls, with failing emission and restored-key controls. |

Own-type backing-field reflection and isolated weak-reference controls remain
where they prove real collectability independently of admission refunds. No
compiler-generated frame or private BCL collection layout is used for those
proofs. `PageInheritance.InheritedValues._first` is required InlineArray backing.
The security [ownership map](Security/README.md) assigns primitive, writer,
source, wrapper and publication guarantees to their primary tests.
