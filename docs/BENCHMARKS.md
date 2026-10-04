# Benchmarks

Developer-only performance evidence for the managed qpdf implementation. The harness lives in `benchmarks/Lokad.Qpdf.Benchmarks`, is built with the solution, is never packed, and uses only synthetic inputs: no qpdf executable or upstream checkout is required.

## Counter report (deterministic)

`--counter-report` runs representative operations and prints governor counters: work units, decoded bytes, peak/live retained memory, host operations, and diagnostics. Counters are deterministic for the pinned inputs; rerun the command to reproduce them. Recorded 2026-10-04 against the current sources:

```
[counter] show-npages-50 status=Completed exit=0 in=11321 out=-1 work=767 decoded=0 peakmem=37057 livemem=37057 hostops=2 diags=1
[counter] select-few-50 status=Completed exit=0 in=11321 out=902 work=1359 decoded=0 peakmem=39519 livemem=39519 hostops=3 diags=0
[counter] merge-shared-8 status=Completed exit=0 in=898 out=1918 work=1278 decoded=0 peakmem=18382 livemem=18382 hostops=17 diags=0
[counter] merge-many-20 status=Completed exit=0 in=8980 out=4355 work=2814 decoded=0 peakmem=45515 livemem=45515 hostops=41 diags=0
[counter] decode-chain-30 status=Completed exit=0 in=1161 out=502 work=452 decoded=4829 peakmem=10175 livemem=10175 hostops=3 diags=0
[counter] json-4 status=Completed exit=0 in=1104 out=2126 work=169 decoded=0 peakmem=4178 livemem=4178 hostops=3 diags=0
[counter] rewrite-compress-4 status=Completed exit=0 in=1104 out=1221 work=817 decoded=0 peakmem=7487 livemem=7487 hostops=3 diags=0
[counter] optimize-photo status=Completed exit=0 in=1798 out=1851 work=544 decoded=0 peakmem=8717 livemem=8717 hostops=3 diags=0
[counter] linearize-10 status=Completed exit=0 in=2433 out=2868 work=2351 decoded=0 peakmem=13791 livemem=13791 hostops=3 diags=0
[counter] cancel-select-50 status=Canceled exit=2 in=11321 work=0 hostops=0
counter-report: 10 scenarios, 0 failures
```

Reading: inspection decodes no payload bytes; work and retained memory scale with page and input counts (merge of 20 one-page inputs costs about twice the work of an 8-page shared merge); the 30-filter chain decodes to under 5 KiB for 452 work units; host operations scale with open inputs; pre-canceled selection reports Canceled before charging work. No anomalies required code changes at this baseline.

## Timed benchmarks (local only)

BenchmarkDotNet benchmarks over the same synthetic inputs (page selection, filter-chain decode, page-count inspection) run locally via `dotnet run -c Release --project benchmarks/Lokad.Qpdf.Benchmarks -- --filter <pattern>`. Timings and allocations are machine-specific: they are measured locally, never asserted, and never recorded here as gates.
