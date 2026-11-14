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
