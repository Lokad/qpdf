# Security test ownership

Each layer checks its own guarantee. Handler vectors and malformed dictionaries
remain exhaustive; public parser syntax is sampled separately from writer layout.

| Guarantee | Primary owners |
| --- | --- |
| Known answers, malformed handlers, key derivation and payload primitives | `LegacyEncryptionTests`, `Revision4EncryptionTests`, `Revision4Rc4Tests`, `Revision5EncryptionTests`, `Revision6EncryptionTests`, `PdfRc4CipherTests`, `PdfAesCipherTests`, `PdfStringCryptTests`, `DocumentSecurityTests` |
| Password encoding, recovery and credential acquisition | `PasswordModeTests`, `PasswordEncodingRecoveryTests`, `PasswordReverseRecoveryTests`, `PasswordRecoveryOptionTests`, `PasswordFileReaderTests`, `PasswordFileTests`, `PasswordDescriptorTests` |
| Permission words, setter order and output policy | `Commands/EncryptionPermissionTests`, `OutputEncryptionCommandTests`, `OutputEncryptionPolicyTests`, `EncryptionReportTests` |
| Physical writers, count/emission agreement, compression, numbering and ciphertext ownership | `EncryptedSerializerTests` partial files; `LegacyDocumentTests`, `Revision4Rc4Tests`, `Revision5EncryptionTests` retain distinct handler integration |
| Source keys, graph roles and explicit JSON policy | `PasswordSelectionTests`, `PasswordMergeTests`, `OverlayPasswordTests`, attachment/extraction password tests, typed JSON creation/update and complete roundtrip tests |
| Publication, cancellation and caller ownership | Typed creator/updater/selection sweeps, physical writer pending-write tests, Foundation host-boundary tests and completed-job collectability controls |
| Parser wiring and diagnostics | Password, template, output-encryption and JSON command tests; command configuration tests |
| Independent interoperability and package boundary | Explicit pinned-reference lane, recorded native fixtures and fresh package consumer checks |

The post-dev.4 reduction maps the former combinations as follows:

| Former coverage | Retained proof |
| --- | --- |
| 40 primary-preservation revision × parser × transformation cases | 11 targeted public cases retain every transformation and both parsers; serializer owners retain the handler × physical-writer evidence. |
| 28 new-encryption parser × mode cases | 10 public cases retain both parsers and copy, selection, merge, split, attachment, re-encryption and empty-primary roles. Serializer owners prove each physical writer. |
| 42 complete-JSON route × revision × policy cases | 14 typed cases retain both revisions, creation/update and every valid policy; four targeted complete-export command cases supplement the command creation/update policy tables. |
| Six implicit-protection rejection cases | Both typed revisions plus argv/R4 and job/R6 rejection controls. |
| 20 full R6 create/authenticate permission-setter cases | All 20 recorded expected words in the permission primitive table; four representative authenticated integrations plus existing argv-order and JSON lexical-order controls. |
| Full command-creation admission sweep | Typed creation sweeps the common creator/key/publication owners; command creation checks early and late refusal translation and closure. |
| Four full R4 writer sweeps | Classic retains every legacy-key refusal; deflate, linear-stream and QDF-stream retain early/late boundaries, with complete shared-writer sweeps in their R6 owners. |

Other sweeps remain where their ownership differs: primitive key/scratch buffers,
reader versus writer contexts, password-file/descriptor awaits and recovery
candidates, complete versus partial JSON replacement, authenticated template
inputs, split output contexts, per-source keys, and annotation/attachment graphs.
The empty-primary template/split sweep is retained because it joins distinct
source and output-key owners. Full-update command coverage additionally owns the
template-authentication branch. A shared helper or common writer alone does not
prove those lifetimes interchangeable.

Pending-write success, fault and cancellation, late publication failures,
post-commit behavior, independent split protection and real collectability
controls remain in their existing owners. Counts report discovered/executed
cases; admission loops and concurrent test-duration sums are different measures.

On 2026-11-13, net11 discovery listed 1,345 security cases before and 1,256
after. Both target frameworks executed the same counts successfully; the
20 relocated permission cases bring the affected run to 1,276 cases per
target, with no failures or skips. Primitive/native fixtures and pending
ownership controls were retained. Full release gates run at final delivery.
