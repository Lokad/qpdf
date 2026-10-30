# Changelog

## Unreleased

- Keep shared memory owners usable after a refused codec probe. Affordable
  fallback reservations can proceed without changing the failed request's
  accounting or automatically retrying that request.

- Serialize JPEG worker admission with encoded-output buffer accounting so
  concurrent codec work cannot lose charges or strand shared reservations.

- Version 0.1.0-alpha.2 deliberately breaks the alpha host input contract:
  `IQpdfInput.ReadAsync` now accepts `Memory<byte>` and returns a byte count.
  Replace allocating `ReadExactlyAsync` calls with explicitly governed
  `ReadExactlyToArrayAsync`, or fill an already admitted buffer with the
  nonallocating `ReadExactlyAsync`. Short reads need no assembly allocation.
- Replace `QpdfJpegCodecContext.ReserveMemory`/`ResizeMemory` with borrowed
  `MemoryBudget` and standard scopes/reservations in the same alpha.2 refresh.
  Codec failures after admission refusal retain their budget category. Remove
  the development Image adapter's owner registry and blanket workspace estimate.

- Preserve source object generations in overlay/underlay annotation and widget
  imports. Stale references stay null regardless of traversal order, and valid
  nonzero-generation fields retain their AcroForm registration.
- Replace handwritten reference-lane stream scans with pinned native structural
  inspection. Exact content comparisons now retain logical page order and check
  independent page text, closing changed-payload and page-swap false passes.
- Reject malformed, mistyped and unsupported catalog `/Version` values before
  JSON creation/update opens output, intentionally stricter than native qpdf.
  Publish direct JSON trailer `/Root` dictionaries as the first output object.
  Bound malformed version-name diagnostics in the shared reader/import guard.
- Cache failure diagnostic counts and prefix overlap, removing repeated quadratic
  searches and per-record enumeration allocations while preserving failure history.
- Resolve indirect page rotation in ordinary rotation and both flattening paths,
  including inherited values; correct skipped flattening and NoRotate placement.
- Refresh job JSON help for check/stream inspections and parser error/default-limit
  controls already supported by the shared pipeline.
- Avoid input-sized argv option-name copies and bound echoed command error text
  before formatting, retaining useful terminal failures under tight allowances.
- Prevent input/output host lease leaks when allocation fails during acquisition,
  including the first completed call; retain actual OOM propagation and cleanup.
- Reject undefined typed inspection decode levels before host access rather than
  treating corrupt filtered streams as outside the requested decode level.
- Reuse admitted diagnostics for ordinary command results, removing the legacy
  presentation fallback and its extra allocation after publication. Preserve
  warning policies and terminal-failure accounting; clarify result documentation.
- Accept indirect attachment name-tree arrays and string keys across listing,
  extraction, JSON inspection, attachment copying and whole-file rewriting.

- Replace the development JPEG adapter and fixture dependency with Lokad.Image
  0.2.0; remove ImageSharp and its temporary audit exclusions. The core remains
  dependency-free. JPEG output and lossy samples can change between providers.
- Add resizable per-call codec memory reservations, retaining atomic refusal,
  cooperative cancellation and cleanup. Correct PDF JSON Unicode error handling
  and bounded round trips through the supported PDF nesting ceiling.

## 0.1.0-alpha.1 - qualified non-cryptographic supported profile

- Make JPEG processing pluggable through the host; remove ImageSharp from the core
  package's runtime dependencies. Keep the pinned adapter in development tools.
  Codec-free hosts preserve opaque images and report unavailable decoding or
  eligible optimization explicitly.

- Implement qpdf 12.4.2 structural operations through shared typed, argv and job-JSON routes: reading/recovery, page selection/merge/split, inspection, attachments, writer controls, content transforms, overlays, appearances, grayscale/RGB JPEG processing, QDF and linearization.
- Preserve the accepted limitations: four-component CMYK/YCCK images remain opaque with warnings when decoding is requested; Zopfli is unavailable. Crypto operations reject explicitly. Other native differences remain documented.
- Resolve compression/decode setters in configuration order, apply native argv/job defaults and preserve typed opaque copying. Compose repeated/nested job files through the shared loader.
- Repair bounded stream lengths consistently through parsing and copying. Preserve AcroForm relationships, forward page links and overlay inspection; conservatively retain unresolved resources.
- Fund attachment extraction and image candidates through consumption, avoid
  redundant payload copies, and scan default appearances without token lists.
  Resolve escaped appearance font names by decoded bytes and bound generated
  appearance streams by `MaxDecodedBytes`.
- Preserve untouched and escaped font names during overlay conflict rewriting.
  Borrow appearance payloads for header-only changes, admit inspection/value
  conversion buffers before allocation, and omit unused appearance placeholders.
- Validate linearization hints against physical output boundaries and graph membership. Preserve valid layout differences and regressions for checker false passes. Repeated hint encoding completes within the existing 4 KiB control.
- Enforce practical allocation, decompression and work limits with approximate memory estimates; observe cancellation during numeric and JPEG work. Clean up leases and temporary storage while preserving committed outputs and surviving immutable aliases. Runtime code never forces GC.
- Qualify Windows/Ubuntu ordinary tests, pinned-native reference comparisons, seven finite visual cases and fresh locked package consumers, including rejection controls. Historical audit counts and exact CLR accounting are not release requirements.

See [supported behavior](docs/SUPPORTED-PROFILE.md) and
[native differences and option evidence](docs/COMPATIBILITY.md).
