# Changelog

## 0.1.0-alpha.1 â€” non-cryptographic subset, M11 audit in progress

- Implements qpdf 12.4.2 non-cryptographic structural operations in managed C#: strict reading with bounded recovery, page selection and merge, writer controls, content transforms, overlays, appearances, JPEG processing, QDF, and linearization, through typed, argv, and job JSON routes.
- 154 of 157 option/context entries complete with oracle-differential evidence; 3 stay partial: the no-Zopfli build profile and four-component CMYK image samples (accepted as tested compatibility exceptions), plus `--parser-max-errors` pending per-object error budgets with tolerant trial parsing. Crypto operations are excluded and fail explicitly.
- See docs/SUPPORTED-PROFILE.md, docs/COMPATIBILITY.md, and docs/PROGRESS.md for the exact profile, per-option evidence, and audit status.
