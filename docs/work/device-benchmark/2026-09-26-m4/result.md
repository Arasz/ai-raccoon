# Device benchmark: Apple M4 (Mac16,12)

AiRaccoon 1.53.0+43138aa99e40be6989a3814692cc7dbf7ab0f9d3 · corpus 43138aa99e40be6989a3814692cc7dbf7ab0f9d3 (225 files) · 4914 chunks · SoC: powermetrics cpu/gpu/ane (sudo) · system: AppleSmartBattery SystemLoad accumulator

| device | status | median wall s | system net J | SoC net J | system mJ/chunk | SoC mJ/chunk | server CPU-s | peak phys MiB | peak neural MiB |
|---|---|---|---|---|---|---|---|---|---|
| auto | ok×3 | 70.4 | 1388.7 | 935.8 | 282.6 | 190.4 | 42.0 | 2020 | 0 |
| coreml | ok×3 | 33.1 | 379.3 | 214.0 | 77.2 | 43.5 | 26.2 | 2173 | 371 |
| cpu | ok×3 | 169.6 | 4076.1 | 2493.4 | 829.5 | 507.4 | 863.3 | 2075 | 0 |
| mlx | ok×3 | 48.8 | 1145.7 | 758.7 | 233.1 | 154.4 | 37.3 | 2422 | 0 |

Cold coreml compile: 37.3 s, 948.2 J system, 30.4 compiler CPU-s; warm load 1.0 s.

coreml starts: slot 0 cold 37.3 s, 30.4 compiler CPU-s; slot 4 warm (cache hit) 1.0 s, 0.0 compiler CPU-s; slot 7 warm (cache hit) 1.0 s, 0.0 compiler CPU-s; slot 10 warm (cache hit) 1.0 s, 0.0 compiler CPU-s

- coreml vs auto: net system energy separated? yes (coreml lower); net SoC energy separated? yes (coreml lower); wall time separated? yes (coreml lower)
- cpu vs auto: net system energy separated? yes (cpu higher); net SoC energy separated? yes (cpu higher); wall time separated? yes (cpu higher)
- mlx vs auto: net system energy separated? yes (mlx lower); net SoC energy separated? yes (mlx lower); wall time separated? yes (mlx lower)
- note: calibration: coreml drained tier 0 (docs/adr) in 12.5 s; benchmark corpus is tier 1 (docs/adr, docs/plans, docs/reviews, docs/reference, docs/research)
- note: the cold coreml run drained the tier 0 corpus; only its compile numbers compare
