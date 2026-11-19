# Focused test support

`PdfTestDocument` owns ordinary classic-PDF fixture framing, including exact
payloads, declared lengths, generations and header/trailer choices. Feature tests
own their object bodies and expected semantics. Physical-layout, malformed-input,
security and native vectors retain independent construction.

The [fixture manifest](../Fixtures/manifest.json) records PDF checksums and page counts.
`independentPageCount` selects the entries also checked by PdfPig.
Probe tests run the executable copied by the matching project build. Source
audits use the checkout path recorded when building the test assembly.

`TestDocumentEngine` supplies an empty, admitted engine for object-graph tests.
`MemoryBudgetHost` borrows storage and a caller budget.

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

Weak-reference tests establish collectability separately from admission refunds.
Reflect only on fields owned by the project. The security ownership map assigns
primitive, writer, source, wrapper and publication guarantees to their primary
tests. [Security test ownership](Security/README.md)

Assign exhaustive semantics to their shared owners, then sample argv/job/mixed
wiring. Keep every distinct native outcome and frozen expected field. The main
coverage boundaries are:

| Guarantee | Ownership and observations |
| --- | --- |
| Syntax, repair and security | JSON refusal policy exhausts settings at one planner route and samples operation/adapter wiring. Primitive tables own grammar and known answers; workflows retain limits, source roles and reader/writer integration. |
| Labels and splitting | The planner owns bare, empty and explicit single-page split sizes. Typed assembly and job wiring assert ordered page text and target names. One typed/argv comparison checks equivalent decoded graphs. The reference lane checks label shapes and samples reordered job selection. |
| Compression setters | Exhaust native encodings and effective setter order; sample argv/job/mixed/nested configuration. |
| Rotation setters | Argv covers the exact-range ordering matrix. Job and response cases sample wiring. Mixed cases check that included and later setters compose and replace matching keys. Content composition follows the edited stream and resource bindings. |
| Content and resources | Feature owners cover alias, content and layout semantics; integration retains selection, merging, splitting and conservative failure behavior. |
| Annotation arrays | Argv owns sharing, private aliases and placement in the page graph. Job cases sample direct and shared arrays. Combined generation and flattening follow the painted forms and their effective font bindings. |
| Placement | Argv covers content array shapes and skipped source pages, with reachable decode-limit and malformed-content controls. Job object and array samples follow the destination and stamp Forms in paint order. |
| Unreferenced graph preservation | The native lane checks tagged edges and shared stream bytes. It preserves separate whole-file, selection, split and contained-copy warning outcomes, with focused job samples. |
| Geometry | Exhaust subtype, operation, indirection, border, coefficient and thin-page semantics through argv; selected job cases retain adapter wiring. NoRotate, private originals, escaped values and canonical/reversed comparisons stay independent. |
| Inspection | Source assembly tests resolve aliases and destinations in one prepared graph. The selector parser owns integer prefixes. Adapter samples verify disabled selectors and exact generations in reports. A malformed selected page tests checker wiring and suppression of later reports. Typed JSON covers names, references and raw descriptors in both versions. Image tests keep sorting, name collisions and text dimension resolution distinct. Transport tests cover named and returned reports, sidecar names and payload roundtrips. |
| JSON imports | Typed cases own page-tree binding, catalog versions and payloads. Command samples check both adapters, including exact parse and depth diagnostics before staging. Source bindings, header setters, packing and job-file loading have separate controls. |
| Physical layout | `WriterIdentifierTests` owns identifier bounds. `ObjectStreamDescriptorTests` owns descriptor syntax and malformed values at the reader and samples publication adapters with actual prior targets. Require `Valid` linearization and actual xref membership. Preserve outline promotion, hint ownership and frozen invalid fixtures independently of the writer. |
| Reuse indexes | Separate repeated work from resource bindings and equal-byte identities; retain warning eligibility, per-page failures, admission and refund controls. Work notifications are not CPU fuel. |
| Literal and partition syntax | Primitive owners exhaust escaping, shape, encoding and container representation; parser/writer/normalizer samples retain framing, quotas, cancellation and physical line markers. |
| Presentation | Output shapes own all-summary count and byte boundaries. Flag-selection tests cover individual summaries, combinations and adapter samples. The byte suite also checks non-invariant cultures. |
| Argument previews | `ArgumentErrorMemoryTests` covers bounded terminal errors and range previews. Adapter samples cover configuration admission and prior targets. |

Reference inspection compares ordered native listings and exact payloads.
Native target checks preseed the path passed to qpdf. Recording-host tests
observe managed transaction calls and prior-target contents.

`Foundation/PublicationAdmissionTests` covers host-fault phases, counters and
sequential commits. Feature suites supply their own fixtures and public calls to
`Foundation/PreparationAdmissionChecks` for admission refusals and refunds.
`Writing/InspectionFailureTests` covers JSON exception translation, inspection
scratch and rewrite-source ownership through commit.

CI keeps the TRX files with the routine verification logs. Available VSTest
traces and dumps from failed or canceled jobs go in `verification-diagnostics-*`.

## Reference and visual checks

Run from the repository root after the locked Release build. On Windows, the
installer verifies the archive and executable digests. For a native source
build, use the pinned commit and record the clean checkout and executable hash.
[Build](../../README.md#build-from-source) · [Reference pin](../../tools/reference-pin.json)

```powershell
$run = Join-Path artifacts ('reference-' + [guid]::NewGuid().ToString('N'))
$oracle = ./tools/install-reference-qpdf.ps1 -Destination (Join-Path $run 'oracle')
$probe = 'tools/QpdfProbe/bin/Release/net11.0/QpdfProbe.dll'
./tools/run-reference-checks.ps1 -QpdfExe $oracle -ProbeDll $probe -WorkRoot (Join-Path $run 'reference')

$renderer = (Get-Command pdftoppm).Source
pwsh -NoProfile -File tools/run-visual-checks.ps1 -QpdfExe $oracle -RendererExe $renderer `
  -ProbeDll $probe -FixtureAssembly tests/Lokad.Qpdf.Tests/bin/Release/net10.0/Lokad.Qpdf.Tests.dll `
  -WorkRoot (Join-Path $run 'visual')
```

On other platforms, point `$oracle` at the pinned native build. Omitting
`-ProbeDll` in the reference runner builds the probe with a locked restore.
Each lane requires its tools and a fresh workspace. Reference results include
stage logs and distinguish the supplied native executable hash from the target
pin. The visual lane needs PowerShell on .NET 10 or later and a test assembly
with its copied fixtures and dependencies. Its receipt hashes the inputs and
binaries. Negative controls must reach pixel comparison and exceed tolerance.
[JPEG fixtures](../Fixtures/JPEG.md)
