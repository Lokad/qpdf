# Focused test support

`PdfTestDocument` owns ordinary classic-PDF fixture framing, including exact
payloads, declared lengths, generations and header/trailer choices. Feature tests
own their object bodies and expected semantics. Physical-layout, malformed-input,
security and native vectors retain independent construction.

`TestDocumentEngine` centralizes the identical empty-input inspection engine
setup used by isolated object-graph tests. It preserves table/value/engine
admission and disposal. Callers still construct their own meaningful objects and
expected results. `MemoryBudgetHost` borrows storage and a caller budget without
closing either.

Keep narrow observation seams where an ordinary final result cannot prove the
ownership or cancellation boundary:

| Seam | Guarantee and primary tests |
| --- | --- |
| `FilterChain.ResolveObserved` | Retires the observer and completed governor while escaped filter names remain funded; `FilterPlanTests`, `OwnedFilterPlanTests`. |
| `LinearizationCheck.TryReadOwnedDataObserved` | Retires decoded/combined temporary backing while parsed owners and source survive, and cancels after decode; `LinearizationDataOwnershipTests`. |
| `PdfParser.CopyNumericTextObserved` | Stops the actual string-copy loop after deterministic work for either token; `NumericCopyCancellationTests`. Pre-cancelled copies use the same observer. |
| `JsonFormatValidator.CheckObserved` | Cancels actual format matching and unconstrained PDF-value skipping after syntax validation; `JsonSchemaMemoryTests`. |
| Owned output-key reflection in `EncryptedSerializerTests` | Withholds the engine's own key to prove size-only passes make no key-dependent cipher calls, with failing emission and restored-key controls. |
| Governor work-field reflection | `SubtreeReuseTests`, `PageInheritanceTests` and `JsonPageContainerPreparationTests` measure finite traversal work and guard identities with resets disabled. Counts are not a public CPU allowance. |

Own-type backing-field reflection and isolated weak-reference controls remain
where they prove real collectability independently of admission refunds. No
compiler-generated frame or private BCL collection layout is used for those
proofs. `PageInheritance.InheritedValues._first` is required InlineArray backing.
The security [ownership map](Security/README.md) assigns primitive, writer,
source, wrapper and publication guarantees to their primary tests.

Assign exhaustive semantics to their shared owners, then sample argv/job/mixed
wiring. Keep every distinct native outcome and frozen expected field. The main
coverage boundaries are:

| Guarantee | Ownership and observations |
| --- | --- |
| Syntax, repair and security | JSON refusal policy exhausts settings at one planner route and samples operation/adapter wiring. Primitive tables own grammar and known answers; workflows retain limits, source roles and reader/writer integration. |
| Labels and splitting | Typed selection owns label/range/split-size combinations; argv/job cases cover reordering, duplicates, boundaries, prefixes and unlabelled output. |
| Compression setters | Exhaust native encodings and effective setter order; sample argv/job/mixed/nested configuration. |
| Content and resources | Feature owners cover alias, content and layout semantics; integration retains selection, merging, splitting and conservative failure behavior. |
| Geometry | Exhaust subtype, operation, indirection, border, coefficient and thin-page semantics through argv; selected job cases retain adapter wiring. NoRotate, private originals, escaped values and canonical/reversed comparisons stay independent. |
| Inspection | Transformed reports cover page identity and order. A malformed selected page tests checker wiring and suppression of later reports. Typed JSON covers names, references and raw descriptors in both versions. Image tests keep sorting, name collisions and text dimension resolution distinct. Transport tests cover named and returned reports, sidecar names and payload roundtrips. |
| JSON imports | Typed cases own graph binding, catalog versions and payloads; argv owns header setters. Create/update, source bindings, packing and job/file-job wiring retain separate controls. |
| Physical layout | `WriterIdentifierTests` owns identifier bounds. `ObjectStreamDescriptorTests` owns descriptor syntax and malformed values at the reader and samples publication adapters with actual prior targets. Require `Valid` linearization and actual xref membership. Preserve outline promotion, hint ownership and frozen invalid fixtures independently of the writer. |
| Reuse indexes | Separate repeated work from resource bindings and equal-byte identities; retain warning eligibility, per-page failures, admission and refund controls. Work notifications are not CPU fuel. |
| Literal and partition syntax | Primitive owners exhaust escaping, shape, encoding and container representation; parser/writer/normalizer samples retain framing, quotas, cancellation and physical line markers. |
| Presentation | Own count/byte boundaries for each output shape; sample flag combinations, decimal/culture behavior and route adaptation separately. |

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

`ArgumentErrorMemoryTests` owns terminal argument-preview bounds, including known
range values; adapter samples retain configuration admission and target preservation.
