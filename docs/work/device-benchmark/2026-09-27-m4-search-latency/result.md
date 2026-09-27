# Device benchmark: Apple M4 (Mac16,12)

AiRaccoon 1.53.1+fdab4b12483ef5b7008f519bdf6fc0dbc502b591 · corpus 43138aa99e40be6989a3814692cc7dbf7ab0f9d3 (225 files) · 4914 chunks · SoC: off (--no-power) · system: AppleSmartBattery SystemLoad accumulator

| device | status | median wall s | system net J | SoC net J | system mJ/chunk | SoC mJ/chunk | server CPU-s | median p95 search ms | peak phys MiB | peak neural MiB |
|---|---|---|---|---|---|---|---|---|---|---|
| auto | ok×3 | 71.8 | 1694.7 | – | 344.9 | – | 45.8 | 51.3 | 2065 | 0 |
| coreml | ok×3 | 34.7 | 339.4 | – | 69.1 | – | 29.1 | 37.2 | 2123 | 371 |

Cold coreml compile: 36.4 s, 352.0 J system, 29.3 compiler CPU-s; warm load 1.0 s.

coreml starts: slot 0 cold 36.4 s, 29.3 compiler CPU-s; slot 2 warm (cache hit) 1.0 s, 0.0 compiler CPU-s; slot 3 warm (cache hit) 1.0 s, 0.0 compiler CPU-s; slot 6 warm (cache hit) 1.0 s, 0.0 compiler CPU-s

- coreml vs auto: net system energy separated? yes (coreml lower); net SoC energy separated? no data; wall time separated? yes (coreml lower); p95 search latency: lose? no
- note: calibration: coreml drained tier 0 (docs/adr) in 12.7 s; benchmark corpus is tier 1 (docs/adr, docs/plans, docs/reviews, docs/reference, docs/research)
- note: the cold coreml run drained the tier 0 corpus; only its compile numbers compare
- note: summary re-derived from the stored runs: the run itself loaded a stale mutated .pyc that inverted the p95 lose verdict
