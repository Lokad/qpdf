# Security test ownership

Each layer checks its own guarantee. Handler vectors and malformed dictionaries
remain exhaustive; public parser syntax is sampled separately from writer layout.

| Guarantee | Primary owners |
| --- | --- |
| Known answers, malformed handlers, key derivation and payload primitives | `LegacyEncryptionTests`, `Revision4EncryptionTests`, `Revision4Rc4Tests`, `Revision5EncryptionTests`, `Revision6EncryptionTests`, `PdfRc4CipherTests`, `PdfAesCipherTests`, `PdfStringCryptTests`, `DocumentSecurityTests` |
| Password encoding, recovery and credential acquisition | `PasswordModeTests`, `PasswordEncodingRecoveryTests`, `PasswordReverseRecoveryTests`, `PasswordRecoveryOptionTests`, `PasswordFileReaderTests`, `PasswordFileTests`, `PasswordDescriptorTests` |
| Permission words, setter order and output policy | `Commands/EncryptionPermissionTests`, `OutputEncryptionCommandTests`, `OutputEncryptionPolicyTests`, `EncryptionReportTests` |
| Physical writers, count/emission agreement, compression, numbering and ciphertext ownership | `EncryptedSerializerTests` partial files; `XrefStreamRoleTests`; `LegacyDocumentTests`, `Revision4Rc4Tests`, `Revision5EncryptionTests` retain distinct handler integration |
| Source keys, graph roles and explicit JSON policy | `PasswordSelectionTests`, `PasswordMergeTests`, `OverlayPasswordTests`, attachment/extraction password tests and complete JSON roundtrips. Typed JSON creation and update exhaust protection policies. Command samples verify key and metadata settings, template credentials and patched bytes through each adapter. Empty-primary split controls cover independent fresh keys and template reuse. |
| Publication, cancellation and caller ownership | Typed creation, update and selection admission tests. Pending-write success, fault and cancellation belong to physical writer tests. Foundation host-boundary tests cover late failure and post-commit results. Collectability tests observe expired owners. |
| Parser wiring and diagnostics | Password, template, output-encryption and JSON command tests; command configuration tests |
| Independent interoperability and package boundary | Explicit pinned-reference lane, recorded native fixtures and fresh package consumer checks |
