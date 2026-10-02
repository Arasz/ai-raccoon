# Device benchmark: Apple M4 (Mac16,12)

AiRaccoon 1.55.0+ba2e3982f9304949bea0a625d9185e343f36641f · corpus 43138aa99e40be6989a3814692cc7dbf7ab0f9d3 (225 files) · 1180 chunks · SoC: off (--no-power) · system: AppleSmartBattery SystemLoad accumulator

| device | status | median wall s | system net J | SoC net J | system mJ/chunk | SoC mJ/chunk | server CPU-s | median p95 search ms | peak phys MiB | peak neural MiB |
|---|---|---|---|---|---|---|---|---|---|---|
| auto | ok×3 | 78.4 | 927.0 | – | 785.6 | – | 28.4 | 45.5 | 2600 | 0 |
| cpu | ok×3 | 455.1 | 313.8 | – | 265.9 | – | 1829.1 | 36.1 | 2302 | 0 |

Cold coreml compile: not measured.

- cpu vs auto: net system energy separated? no (ranges overlap); net SoC energy separated? no data; wall time separated? yes (cpu higher); p95 search latency: lose? no
- note: calibration: auto drained tier 0 (docs/adr) in 22.3 s; benchmark corpus is tier 1 (docs/adr, docs/plans, docs/reviews, docs/reference, docs/research)
