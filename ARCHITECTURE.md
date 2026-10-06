# Architecture

This describes the implemented architecture. The library ships host contracts, an in-memory host, a per-job governor, diagnostics, and staged output alongside the strict reader, object graph, filter pipeline with the governed JPEG adapter, shared copy engine, content pipeline, deterministic writer, and the typed, argv, and job-JSON entry points.

Three entry points converge on one operation plan: familiar qpdf argv,
qpdf job JSON, and typed operations including bounded page selection. The
operation plan uses one binary reader, object graph, filter pipeline and
writer. PDF object JSON and job JSON are different formats.

Argv and job checker plans use the typed checker core, with optional page-count
and linearization inspection. Page traversal and declared-count reporting are
distinct; failed checking stops subsequent inspection. Checker/page-count results
snapshot the governor after input disposal. Broader invocation/presentation
counters and diagnostic ownership remain under audit.

The host owns stable input identities and ranged reads, output staging and
publication, scratch storage, descriptors and any supplied environment values.
The library owns PDF semantics, explicit results and per-job memory/work limits.
No runtime path gains filesystem, process or network authority implicitly.

The engine was built bottom-up from strict reading, opaque stream preservation, and a small complete
read/select/write path. The same engine grew into the full page-selection
contract before recovery and advanced transformations arrived. Forms and graph
identity came before general cross-document imports, content analysis before
resource pruning, and the stable writer and replay model before QDF and linearization.

Memory safety includes both safe managed operations and resource governance.
Validate lengths/offsets and checked arithmetic before allocating; bound work,
decompression, graphs and cancellation intervals. ImageSharp and BCL codecs
need explicit allocation/input/output policies; managed code alone is not an
exhaustion limit.

Reader attempts and parsed subtrees carry independent reservation receipts;
discarding an outer attempt preserves previously accepted nested loads. Xref
and cache/guard collections admit their wrappers, capacities and growth overlap.
Cache entries distinguish unread owned subtrees from borrowed values, so
replacement and disposal release only the unread admission. Document disposal
closes its input lease and drops cache metadata while returned immutable bytes
remain usable. Input/table/returned-node and diagnostic lifetimes remain under
audit; [progress](docs/PROGRESS.md) records the measured gaps.

The solution contains the packable library, tests, public API consumer,
development probe and synthetic benchmark harness. Benchmarks are development
tools; resource claims require independent allocation/retention measurements
bound to the tested source tree. Do not create duplicate engines or empty
project layers solely to match the eventual architecture.
