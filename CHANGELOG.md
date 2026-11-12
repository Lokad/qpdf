# Changelog

## Unreleased

- Protect typed JSON creation with fresh AES-256 or an authenticated R4/R6 template,
  including metadata policy and the required PDF version. Preserve/Remove keep
  plaintext JSON creation unencrypted; JSON `/Encrypt` trailers still reject.
- Authenticate typed JSON updates with AES R4/R6 credentials and preserve, remove,
  replace or copy output protection. Encrypt patched strings and streams after
  renumbering, honoring the final catalog metadata policy. Replacement JSON
  trailers declaring `/Encrypt` remain unsupported.
  Argv/job updates accept primary text/host-file credentials, password modes,
  recovery settings and the same output policies.
- Copy AES R4/R6 protection from an independent template through typed page
  output policies, retaining both password entries, permissions, metadata policy
  and identifiers after template closure. Plaintext templates remove protection.
  Argv/job `copyEncryption` and independent `encryptionFilePassword` use the same
  policy, including password modes, output-policy ordering and empty-primary splits.
- Accept independent AES R4/R6 credentials for overlay/underlay sources in argv
  and job JSON, including transformed inspections and global password modes.
  Authenticate distinct credentials for repeated files before reusing placement
  graphs; preserve the requested primary/output protection.
- Exclude indirect null values from linearized page and document dependencies,
  preserving correct hint counts while retaining their physical objects.
- Copy pages from signed, unencrypted PDFs, including incremental updates, shared
  fields and repeated pages. Retain stored signature appearances, unrelated forms
  and private graph roles while disabling active signature semantics and document
  certification restrictions. Apply the same policy to placement and JSON publication;
  derived output does not preserve signature validity.
- Add AES-128 R4 and AES-256 R6 opening through `QpdfDocument`, per-input UTF-8/raw credentials,
  user/owner authentication, empty passwords and non-secret encryption facts.
  Distinguish missing/invalid credentials, unsupported handlers and malformed data.
  R4 text passwords can retry PDFDocEncoding; raw passwords remain exact bytes.
  Typed structural checks accept `QpdfCheckOptions.Password` and inspect the decrypted graph.
  Output-free argv/job inspections accept primary text passwords with distinct missing/invalid
  credential diagnostics.
- Preserve AES R4/R6 protection during typed single-source page selection, with
  text/raw user or owner credentials, empty passwords, repeated pages and rotations.
  Derived output retains both passwords, permissions and metadata policy. Explicit
  output policies can remove protection or create fresh AES-256 R6 with chosen
  passwords, permissions and metadata treatment. Typed merging/splitting accepts
  per-source credentials and uses the first source's protection by default;
  repeated paths require consistent credentials, and newly encrypted split outputs
  receive independent keys.
- Accept primary text/host-file credentials for argv/job PDF transformations and
  preserve supported primary protection through selection, merge/split, compression,
  object streams, QDF, linearization and attachment edits. Retain protection when
  the primary contributes no selected pages. Shared typed attachment edits also
  preserve empty-password inputs. Authenticated command JSON updates use the same output policies.
- Add `--decrypt` / `decrypt` for authenticated AES R4/R6 PDF transformations,
  including split and linearized output. Preserve prior output on authentication
  failure or pre-publication cancellation; output-free inspections ignore the policy.
- Add positional/named `--encrypt` and job `encrypt.256bit` creation through the
  shared AES-256 R6 writer, including permissions, metadata policy, password modes,
  empty-primary merges and independent split keys. Keep option-like passwords out
  of option/help dispatch. Legacy creation and insecure-owner overrides remain unsupported.
- Authenticate page sources through argv `--pages ... --password=...` and job
  `pages[].password`, sharing typed merge and inspection readers. Keep the primary's
  output policy, share credentials across repeated secondary paths, reject conflicts, and
  apply global password modes to each source. Empty-primary merges default to plaintext.
- Authenticate attachment-copy sources through typed `QpdfAttachmentCopy` and
  argv/job source credentials, including text/raw/hex input and empty passwords.
  Preserve the destination's output policy and retain copied payloads after the
  source closes. Record independent R4/R6 attachment fixtures for offline tests.
- Add `--is-encrypted` and `--requires-password` (and job JSON equivalents) for
  supported handlers, retaining native query exit codes independently of warning presentation.
- Add `--show-encryption` / `showEncryption` for supported handlers, reporting
  declared permissions, cipher methods and password-match status without secrets.
  Wrong credentials skip combined content inspections while allowing this report.
- Authenticate JSON inspection through typed `QpdfJsonOptions.Password` and primary
  argv/job credentials. Export decrypted graphs and stream data, and report non-secret
  encryption facts in JSON versions 1 and 2; password/key fields remain null.
- Support `--suppress-password-recovery` / `suppressPasswordRecovery` for primary
  inspections and JSON export, disabling alternate password-encoding attempts.
- Support primary input password modes in inspections and JSON export, including
  bounded hex decoding and the supported R4 recovery path for UTF-8 hex passwords.
- Support primary password files for inspections and JSON export through host
  inputs, with bounded cleared storage and native first-line/setter semantics.
  Descriptor input and secondary credentials remain unsupported.
- Start development version 0.2.0-dev.1.
- Separate planned password-security requirements, deferred legacy-output options
  and explicit raw-key/static-IV exclusions in the compatibility inventory.
  Existing rejection coverage remains current behavior, not implementation evidence.

## 0.1.0 (2026-11-05)

First release published on NuGet.

- Target .NET 10 and .NET 11; pin the .NET 11 RC 3 SDK and use .NET 11 for
  the performance baseline. Package and public-consumer checks cover both targets.
- Provide managed qpdf 12.4.2 structural operations through shared typed, argv
  and job-JSON entry points: reading/recovery, inspection, page assembly,
  attachments, content transforms, QDF and two-pass linearization.
- Keep the core free of runtime NuGet dependencies. Hosts supply JPEG codecs,
  storage, descriptors, environment values and transactional publication.
- Add cooperative memory admission with coarse execution/result ownership,
  independent cancellation, structural/decompression limits and bounded diagnostics.
  Input reads fill caller-owned buffers; codecs borrow the active memory budget.
  Cumulative work/fuel APIs and compatibility wrappers have been removed.
- Preserve primary-document metadata and attachments independently of selected
  pages. Keep raw PDF values and private aliases; apply explicit edits afterward.
- Preserve shared graph identities, field registrations, source resources and
  local action/layer meaning through selection and placement. Correct annotation
  geometry, appearance clipping and retained navigation after rotation baking.
- Make PDF JSON v2 byte-reversible; validate imported graphs before publication.
  Report malformed data, unsupported features, resource refusals and host faults
  through their existing distinct failure categories.
- Keep crypto/signatures excluded, CMYK/YCCK decoding opaque and Zopfli unavailable.
  Rotation baking explicitly refuses retained destinations whose prior-viewer or
  quarter-turn axis-fit state cannot be preserved.

See the [supported profile](docs/SUPPORTED-PROFILE.md),
[native differences and API migrations](docs/COMPATIBILITY.md), and
[memory/host contract](docs/MEMORY.md) for the current caller-facing contract.
