# Changelog

## Unreleased

- Copy pages from signed, unencrypted PDFs, including incremental updates, shared
  fields and repeated pages. Retain stored signature appearances, unrelated forms
  and private graph roles while disabling active signature semantics and document
  certification restrictions. Apply the same policy to placement and JSON publication;
  derived output does not preserve signature validity.
- Add AES-256 R6 opening through `QpdfDocument`, per-input UTF-8/raw credentials,
  user/owner authentication, empty passwords and non-secret encryption facts.
  Distinguish missing/invalid credentials, unsupported handlers and malformed data.
  Encrypted transformations and argv/job password options remain unsupported.
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
