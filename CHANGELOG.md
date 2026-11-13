# Changelog

## Unreleased — 0.2.0-dev.3

- Correct package description and release notes to describe the implemented
  password-security, signed-page and host-codec contracts. Package checks reject
  the obsolete blanket security exclusion in the built manifest.
- Remove `QpdfLimits.MaxHostOperations` and publication call prediction. Host-call
  counters remain observations; short reads no longer exhaust a cumulative quota.
  Memory, handle, structural and decoded-output bounds and cancellation remain.
- Use one semantic planner for argv and job JSON. Adapt argv directly, removing
  per-setting serialization, synthetic JSON documents and the alternate planner.
  Preserve ordered files, credentials, permissions and exclusive declarations;
  accept repeated scalar setters consistently across argv routes. Test syntax
  repetition at the planner and retain representative public/native workflows.
- Share output model finalization, catalog/version policy and original-ID
  preparation across selection, merging and splitting. Preserve distinct source
  roles and independent split encryption while removing parallel writer setup.
- Separate operation execution, source assembly and publication from public API
  adaptation. Translate command exceptions at the shared presentation boundary
  and call focused internal test owners directly instead of reflecting on facade
  helpers.
- Share rectangle transforms, matrix composition and finite numeric-array readers
  across annotation flattening, rotation and placement. Keep feature-specific
  clipping/fallback rules and use spans for temporary bounds.
- Assign exhaustive parser/security/layout tables and shared publication faults
  primary test owners. Use explicit fixtures and recording hosts/budgets for API
  lifetimes; retain isolated core collectability controls without compiler-frame
  or private BCL collection probing.


- Support Standard password security R2–R6 through the shared typed, argv and job
  pipelines, including legitimate empty passwords and non-secret encryption facts.
  Passwords, keys and protected payloads obey the existing ownership, cancellation
  and publication contracts; the core retains no runtime package dependencies.
- Preserve or remove existing protection, create fresh AES-256 R6 with permissions
  and metadata policy, or copy an independently authenticated template. R2/R3 retain
  RC4, R4 output normalizes to AES-128, and R5/R6 retain their revision. Classic,
  object-stream, QDF and linearized writers share these policies; newly protected
  split outputs use independent keys.
- Authenticate primary, page, attachment, placement and template sources with
  separate credentials. Support typed text/raw passwords and command text, hex,
  host password files and host-mapped descriptor input. The primary source governs
  preservation; empty-primary merges remain plaintext unless explicitly protected.
- Add bounded recovery among UTF-8, PDFDoc, WinAnsi and MacRoman, plus well-formed
  BOM-marked UTF-16 input. Keep typed raw bytes exact, support suppression and retain
  enough host-file prefix for modern 127-byte credentials. Document strict malformed
  UTF-16 handling and the standard WinAnsi tilde mapping as native differences.
- Export authenticated plaintext JSON with null password/key fields. Apply JSON
  updates and creation through explicit preserve/remove/new/template policies;
  an encryption declaration alone cannot authenticate a key.
- Implement encryption predicates, non-secret reports, `--show-crypto` and
  `--remove-restrictions`. Named/positional encryption credentials, password modes,
  permission setter order and independent templates have native-reference coverage.
  New weak encryption, raw-key interfaces and static-IV overrides remain excluded
  or deferred as listed in the compatibility inventory.
- Preserve stored appearances when extracting or rewriting signed pages while
  unregistering signature fields and certification restrictions. Derived PDFs do
  not retain signature validity. Read-only inspection preserves source values;
  signing, certificate encryption and signature validation remain excluded.
- Allow codecs to opt into CMYK/YCCK decoding through `SupportsCmykDecoding`,
  preserving raw PDF samples, color spaces, Decode arrays and admitted ownership.
  Existing codecs remain compatible; four-component recompression stays unavailable.
- Qualify rotation destination projection and retain explicit refusals for
  unrepresentable viewer-state combinations. Visual reports identify the development
  RGB encoder's quality difference, retain original-image bounds and check negative
  controls instead of claiming cross-encoder equivalence.
- Extend offline fixtures, pinned native comparisons and package consumers across
  security handlers, encrypted operation combinations, resource refusal and cleanup.
  Add prepared plaintext/R4/R6 authentication, opening, rewrite and selection
  workloads to the existing memory and BenchmarkDotNet runners.

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
