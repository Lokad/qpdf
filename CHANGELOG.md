# Changelog

## 0.1.0-alpha.1 - qualified non-cryptographic supported profile

- Implement qpdf 12.4.2 structural operations through shared typed, argv and job-JSON routes: reading/recovery, page selection/merge/split, inspection, attachments, writer controls, content transforms, overlays, appearances, grayscale/RGB JPEG processing, QDF and linearization.
- Preserve the accepted limitations: four-component CMYK/YCCK images remain opaque with warnings when decoding is requested; Zopfli is unavailable. Crypto operations reject explicitly. Other native differences remain documented.
- Resolve compression/decode setters in configuration order, apply native argv/job defaults and preserve typed opaque copying. Compose repeated/nested job files through the shared loader.
- Repair bounded stream lengths consistently through parsing and copying. Preserve AcroForm relationships, forward page links and overlay inspection; conservatively retain unresolved resources.
- Validate linearization hints against physical output boundaries and graph membership. Preserve valid layout differences and regressions for checker false passes. Repeated hint encoding completes within the existing 4 KiB control.
- Enforce practical allocation, decompression and work limits with approximate memory estimates; observe cancellation during numeric and JPEG work. Clean up leases and temporary storage while preserving committed outputs and surviving immutable aliases. Runtime code never forces GC.
- Qualify Windows/Ubuntu ordinary tests, pinned-native reference comparisons, seven finite visual cases and fresh locked package consumers, including rejection controls. Historical audit counts and exact CLR accounting are not release requirements.

See [supported behavior](docs/SUPPORTED-PROFILE.md) and
[native differences and option evidence](docs/COMPATIBILITY.md).
