# WebGPU footprint at the 1022 chunk budget — M4, 2026-09-30

Task: `webgpu-footprint-1022` of `air-254-vs-1022-retrieval-measurement-809` (issue #809 item 2, F12 falsification).

**Verdict: SURVIVES** — at the 1022 chunk budget, WebGPU's whole-process peak footprint stays bounded
near its 254-era level and far below both pre-registered falsification thresholds. "Footprint is fine"
is not falsified by this run.

## Verdict arithmetic (PR-2.R8, AC4)

Anchor: 254-era `auto` median `2,068,770 KiB` (`docs/work/device-benchmark/2026-09-26-m4/result.json`).

| quantity | value |
|---|---|
| threshold 1: 2 × anchor | `4,137,540 KiB` |
| threshold 2: 2 × same-run cpu median | `2 × 2,357,555 = 4,715,110 KiB` |
| auto (WebGPU) median | `2,662,883 KiB` (2,600.5 MiB / 2.54 GiB) |
| auto max (worst run) | `2,675,475 KiB` |
| cpu control median | `2,357,555 KiB` (2,302.3 MiB / 2.25 GiB) |
| threshold 1 exceeded? | no — 2,662,883 ≤ 4,137,540 (64% of threshold) |
| threshold 2 exceeded? | no — 2,662,883 ≤ 4,715,110 (56% of threshold) |

Both checks also hold on the worst-case auto run: `2,675,475 ≤ 4,137,540` and `2,675,475 ≤ 2 ×
2,552,899 = 5,105,798`. The strongest adversarial variant (auto max vs 2 × cpu min) still survives:
`2,675,475 ≤ 2 × 2,220,051 = 4,440,102`.

Median delta vs the 254-era anchor: **+28.7%** (`(2662883 − 2068770) / 2068770`). Bounded growth — not
the MLX per-shape curve (0.7 → 2.0 → 10.6–11.8 → 17.9 GB across 128/254/510/1022) and nowhere near the
2× thresholds.

**Note on the pre-registered AC4 jq check.** The check as written compares summary *objects* to numbers
(`.summary.devices.auto.phys_footprint_peak_kib as $a | $a > 4137540`), and jq's type ordering makes any
object compare greater than any number. It returns `true` even for a hypothetical auto median of
`99,999,999 KiB` — it is non-discriminating. The verdict above therefore uses the corrected numeric
form (`.median` fields); both the broken and corrected checks are reproduced under AC4 below.

## Protocol and deviations (AC1)

- **Binary**: the Release build of this worktree, `src/AiRaccoon/bin/Release/net10.0/AiRaccoon`
  (`1.55.0+ba2e3982f9304949bea0a625d9185e343f36641f`, HEAD `ba2e3982`, apphost sha256
  `86d3fee11755c3ae6b89b1fd97bcdcf7a4cd6dfa6f5ca9993a1283a368673ed3`). Built first with
  `dotnet build -c Release src/AiRaccoon/AiRaccoon.csproj` (Build succeeded, 0 warnings, 0 errors).
  Deviation from the step's suggested discovery: `command -v ai-raccoon` resolves to the globally
  installed **1.54.0+2795e982** (pre-config-D, deployed live build), so the built apphost was passed
  explicitly via `--binary`.
- **Command** (explicit `--repeats 3 --drain-timeout 1800`; scratch under `/tmp`, never
  `~/.ai-raccoon`; `--no-power` added because sudo is password-locked here and the verdict needs no
  power rails):

```
python3 -u scripts/device-benchmark.py \
  --devices auto,cpu \
  --binary src/AiRaccoon/bin/Release/net10.0/AiRaccoon \
  --repeats 3 --drain-timeout 1800 --no-power \
  --out /tmp/aira-254-vs-1022-webgpu/20260930-164226
```

- **Corpus**: pinned sha `43138aa99e40be6989a3814692cc7dbf7ab0f9d3`, tier 1
  (`docs/adr, docs/plans, docs/reviews, docs/reference, docs/research`), 225 files / 3,660,253 bytes,
  corpus manifest sha `e984abf1d25009de9f250fa88bce1a5220dd7a2cab4d87ec2c6961a0c6966d7b` — the same
  corpus the 254-era anchor used. The script's own calibration drained tier 0 (`docs/adr`) in 22.3 s
  (< 30 s `MIN_WINDOW_S`) and escalated to tier 1 by design.
- **Fresh roots**: data roots are per-run scratch dirs under `/tmp/aira-254-vs-1022-webgpu/<ts>/runs/<slot>/data-root`
  (the script asserts a safe root and wipes/recreates each run dir); the live bank, port 7721, and
  `~/.ai-raccoon` were never written.
- **Manifest**: `chunkTokens: 1022` in `src/AiRaccoon/Models/granite-embedding-small-english-r2/ai-raccoon.manifest.json`.
- **Runtime (was UNVERIFIED)**: the pre-run bound from the 2026-09-26-m4 artifacts (254-era: auto ≈70 s,
  cpu ≈170 s per run) suggested a ~20–40 min session. Actual: **≈51 min** (launch 16:42:26 → finish
  ≈17:33:34 CEST), because at 1022 the same ≈1.25 M-token corpus took auto 62–100 s and cpu 324–486 s
  per run. All runs finished inside the explicit `--drain-timeout 1800 s`; no timeouts.

## Per-run results and medians (AC3)

| slot | device | repeat | provider_actual | chunks | wall s | phys_footprint_peak_kib | neural_footprint_peak_kib |
|---|---|---|---|---|---|---|---|
| 0 | auto | 0 | WebGPU | 1180 | 100.5 | 2,675,475 | 0 |
| 1 | cpu | 0 | CPU | 1180 | 485.7 | 2,357,555 | 0 |
| 2 | cpu | 1 | CPU | 1180 | 455.1 | 2,552,899 | 0 |
| 3 | auto | 1 | WebGPU | 1180 | 78.4 | 2,662,883 | 0 |
| 4 | auto | 2 | WebGPU | 1180 | 62.0 | 2,350,867 | 0 |
| 5 | cpu | 2 | CPU | 1180 | 323.7 | 2,220,051 | 0 |

Per-device medians (`summary.devices`): auto phys `{median: 2662883, min: 2350867, max: 2675475, n: 3}`,
auto neural `{median: 0, ...}`, cpu phys `{median: 2357555, min: 2220051, max: 2552899, n: 3}`,
cpu neural `{median: 0, ...}`. All 6 runs `status: ok`.

HYPOTHESIS (not measured): the monotone auto wall-time decline across repeats (100.5 → 78.4 → 62.0 s)
is a WebGPU shader/kernel cache warming effect; the benchmark's per-run server is fresh, so it is not
in-process warm-up. Not needed for the footprint verdict.

## Host overlap (AC5)

The host was **not quiet**; ambient compute overlapped the whole session and is recorded here.

- At launch (16:42:26 CEST): loadavg `4.26 5.45 5.52`; WindowServer 29.6%, Task Manager 23.1%,
  iTerm 20.9%, OneDrive 18.0%, multiple `pi` sessions (9.4%, 5.5%, 5.9%), JetBrains Toolbox 4.2%; the
  live `ai-raccoon` server PID 73309 (port 7721, live bank) 4.8% CPU — below the script's 20%
  busy-refusal threshold and never touched. AC power attached.
- Mid-run (14:59:09Z): the benchmark apphost at 350.2% CPU alongside Task Manager 206.5%, Finder 88.1%,
  the ai-badger venv python 79.0%, iTerm 30.9%, WindowServer 28.3%, OneDrive 15.8%; loadavg
  `12.06 9.40 7.07`.
- Ambient live-server restart during the window: PID 73309 (live server observed at launch/mid-run) was
  gone at final check; a new `ai-raccoon serve --restart` PID 14180 had been running ≈23 min, i.e. it
  started ≈17:15 CEST — overlapping benchmark runs 3–6 (17:15–17:33). The live `~/.ai-raccoon/memory.db`
  mtime moved to 17:32:27 and its WAL to 17:35:28. HYPOTHESIS: this is the expected R3 follow-up restart
  (attempt 3 / migration-stamp write) of the live bank. It is not this benchmark: every benchmark data
  root is under `/tmp` (verified below), the script refuses unsafe roots, and no benchmark process ever
  opened `~/.ai-raccoon`.
- No competing process was stopped. `phys_footprint_peak_kib` is per-process, so this overlap does not
  inflate the peaks directly; HYPOTHESIS: the elevated load widens wall-time/energy noise but not the
  footprint verdict (all six peaks cluster in 2.12–2.55 GiB across both arms).
- Raw snapshots: `ambient-start.txt`, `ambient-mid.txt` in this directory. Copied serve logs are under
  `runs/*-serve.log`.

## Residual, follow-up, and scope limits

- **Residual (named)**: this is a whole-process peak at the 1022 chunk budget on the tier-1 docs corpus.
  It does **not** attribute footprint to WebGPU per-shape (distinct input length) buffer retention, and
  it does not exercise a 254-era bank upgraded under `auto` or a cold-start variant.
- **Follow-up**: `webgpu-per-shape-attribution` — add a WebGPU execution provider to the Python
  single-row harness (`scripts/src/retrieval_tuning/chunk_window.py` currently rejects devices other
  than cpu/mlx/coreml) and sweep increasing novel lengths in one process, MLX F2's method. **Not built
  here** — this task measured whole-process peaks only.
- Not verdict-relevant: `--no-power` leaves SoC energy unavailable in `result.json`; system-energy
  fields came from AppleSmartBattery telemetry.

## AC evidence commands and outputs

### AC1 — pinned corpus on fresh scratch roots, explicit repeats/drain-timeout, built Release binary

```
$ dotnet build -c Release src/AiRaccoon/AiRaccoon.csproj        # tail
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:02.59
```

```
$ jq -c '{version:.product.version, binary_sha256:.product.binary_sha256}' result.json
{"version":"1.55.0+ba2e3982f9304949bea0a625d9185e343f36641f","binary_sha256":"86d3fee11755c3ae6b89b1fd97bcdcf7a4cd6dfa6f5ca9993a1283a368673ed3"}

$ jq -c '{git_sha:.corpus.git_sha, files:.corpus.files, bytes:.corpus.bytes, manifest_sha256:.corpus.sha256, calibration:.corpus.calibration}' result.json
{"git_sha":"43138aa99e40be6989a3814692cc7dbf7ab0f9d3","files":225,"bytes":3660253,"manifest_sha256":"e984abf1d25009de9f250fa88bce1a5220dd7a2cab4d87ec2c6961a0c6966d7b","calibration":{"device":"auto","tier0_wall_s":22.336724996566772,"tier":1,"min_window_s":30.0}}

$ jq -r '.runs[] | "slot \(.slot) \(.device) rep \(.repeat): data_root=\(.data_root)"' result.json
slot 0 auto rep 0: data_root=/tmp/aira-254-vs-1022-webgpu/20260930-164226/runs/00-auto/data-root
slot 1 cpu rep 0: data_root=/tmp/aira-254-vs-1022-webgpu/20260930-164226/runs/01-cpu/data-root
slot 2 cpu rep 1: data_root=/tmp/aira-254-vs-1022-webgpu/20260930-164226/runs/02-cpu/data-root
slot 3 auto rep 1: data_root=/tmp/aira-254-vs-1022-webgpu/20260930-164226/runs/03-auto/data-root
slot 4 auto rep 2: data_root=/tmp/aira-254-vs-1022-webgpu/20260930-164226/runs/04-auto/data-root
slot 5 cpu rep 2: data_root=/tmp/aira-254-vs-1022-webgpu/20260930-164226/runs/05-cpu/data-root

$ jq -c '.power_sources' result.json
{"soc":"off (--no-power)","system":"AppleSmartBattery SystemLoad accumulator"}
```

### AC2 — provider_actual WebGPU / CPU, parsed from the serve logs

```
$ jq -e '.summary.devices | has("auto") and has("cpu")' result.json
true

$ jq -r '.runs[] | "slot \(.slot) \(.device) rep \(.repeat): provider_actual=\(.provider_actual)"' result.json
slot 0 auto rep 0: provider_actual=WebGPU
slot 1 cpu rep 0: provider_actual=CPU
slot 2 cpu rep 1: provider_actual=CPU
slot 3 auto rep 1: provider_actual=WebGPU
slot 4 auto rep 2: provider_actual=WebGPU
slot 5 cpu rep 2: provider_actual=CPU

# "execution provider <X>" line in each copied serve log:
00-auto-serve.log: WebGPU
01-cpu-serve.log: CPU
02-cpu-serve.log: CPU
03-auto-serve.log: WebGPU
04-auto-serve.log: WebGPU
05-cpu-serve.log: CPU
```

### AC3 — per-run phys/neural peaks plus per-device medians

```
$ jq -e '.summary.devices.auto.phys_footprint_peak_kib and .summary.devices.cpu.phys_footprint_peak_kib' result.json
true

$ jq -r '.runs[] | "slot \(.slot) \(.device) rep \(.repeat): phys=\(.phys_footprint_peak_kib) KiB neural=\(.neural_footprint_peak_kib) KiB"' result.json
slot 0 auto rep 0: phys=2675475 KiB neural=0 KiB
slot 1 cpu rep 0: phys=2357555 KiB neural=0 KiB
slot 2 cpu rep 1: phys=2552899 KiB neural=0 KiB
slot 3 auto rep 1: phys=2662883 KiB neural=0 KiB
slot 4 auto rep 2: phys=2350867 KiB neural=0 KiB
slot 5 cpu rep 2: phys=2220051 KiB neural=0 KiB

$ jq -c '{auto_phys:.summary.devices.auto.phys_footprint_peak_kib, auto_neural:.summary.devices.auto.neural_footprint_peak_kib, cpu_phys:.summary.devices.cpu.phys_footprint_peak_kib, cpu_neural:.summary.devices.cpu.neural_footprint_peak_kib}' result.json
{"auto_phys":{"median":2662883,"min":2350867,"max":2675475,"n":3},"auto_neural":{"median":0,"min":0,"max":0,"n":3},"cpu_phys":{"median":2357555,"min":2220051,"max":2552899,"n":3},"cpu_neural":{"median":0,"min":0,"max":0,"n":3}}
```

### AC4 — pre-registered thresholds

```
# Pre-registered check AS WRITTEN (object-vs-number comparison — non-discriminating, always true):
$ jq -e '(.summary.devices.auto.phys_footprint_peak_kib) as $a | (.summary.devices.cpu.phys_footprint_peak_kib) as $c | (($a > 4137540) or ($a > 2*$c)) | IN(true,false)' result.json
true
$ echo '{"summary":{"devices":{"auto":{"phys_footprint_peak_kib":{"median":99999999}},"cpu":{"phys_footprint_peak_kib":{"median":1}}}}}' | jq -e '(.summary.devices.auto.phys_footprint_peak_kib) as $a | (.summary.devices.cpu.phys_footprint_peak_kib) as $c | (($a > 4137540) or ($a > 2*$c)) | IN(true,false)'
true     # returns true for a 99,999,999 KiB auto peak — proof the check cannot fail

# Corrected numeric check on medians:
$ jq -e '(.summary.devices.auto.phys_footprint_peak_kib.median) as $a | (.summary.devices.cpu.phys_footprint_peak_kib.median) as $c | {auto_med:$a, anchor_2x:4137540, cpu_med_2x:(2*$c), falsified_by_anchor:($a > 4137540), falsified_by_cpu:($a > 2*$c), verdict:(if (($a > 4137540) or ($a > 2*$c)) then "FALSIFIED" else "SURVIVES" end)}' result.json
{
  "auto_med": 2662883,
  "anchor_2x": 4137540,
  "cpu_med_2x": 4715110,
  "falsified_by_anchor": false,
  "falsified_by_cpu": false,
  "verdict": "SURVIVES"
}

# Corrected numeric check on max (worst auto run):
{"auto_max":2675475, "anchor_2x":4137540, "cpu_max_2x":5105798, "falsified_by_anchor":false, "falsified_by_cpu":false, "verdict":"SURVIVES"}
```

### AC5 — residual, follow-up slug, host overlap

- Residual named above; follow-up slug `webgpu-per-shape-attribution` named as **not built here**.
- Host overlap recorded in its own section with timestamps, load averages, and per-process CPU.
- This note itself is the AC5 artifact (`findings.md`).