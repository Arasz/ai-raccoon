# .sh → .py port verification checklist (review gate)

Concrete semantics to verify when a shell script is ported to python or a python script is split into src/ modules.
"Byte-identical port" means the diff below is empty modulo comment dividers and documented parameterization (root=, out=
args).

## Extraction-diff technique (evidence, not eyeballs)

```bash
git show main:scripts/<file> > /tmp/old.py          # original from history
grep -n "^def \|^class \|^[A-Z_]* = " /tmp/old.py   # map ranges
diff <(sed -n 'A,Bp' /tmp/old.py) <(sed -n 'C,Dp' src/new_module.py)
```

Per-function-range diffs prove a verbatim move. Do this for EVERY module; then a single smoke (e.g. hash-map
regeneration vs committed JSON) closes the loop on the real contract.

## Semantics checklist

- **Pins byte-identical**: hashes, URLs, filenames copied from the old .sh — verify each constant against the original,
  don't trust the new file.
- **Exit codes**: usage/unknown-arg path (e.g. bogus model → exit 2), mismatch → exit 1, dotnet failure passthrough. Run
  the usage path live.
- **Skip-if-verified**: `[ -f target ] && sha matches` → "already downloaded and verified" (no re-download).
- **.part flow**: download to `<name>.part`, rename over target; mismatch → delete TARGET (not just .part) and exit
  non-zero; failure → .part cleanup (python port may ADD this cleanup the .sh lacked — improvement, not drift).
- **RID fallback chain**: `dotnet --info` parse → uname mapping table → error + exit 1. Both stages ported 1:1.
- **Entry checks**: exact stored-name match (unzip -Z1 semantics = zipfile namelist), not substring.
- **CLI flags / default modes**: every flag kept; default-mode fallthrough kept (e.g. no flags → ingest-only; --verify
  implies ingest).
- **Message parity**: user-facing OK/FAIL/verified lines identical — tests and humans grep them.
- **Env var defaults**: e.g. AIRACCOON_DATA_ROOT or ~/.ai-raccoon/models; AI_RACCOON_VERSION/SOURCE in gate scripts.
- **Path rebasing**: `Path(__file__).resolve().parent` in a src/ module is one level deeper — re-base (`.parent.parent`)
  or the file resolves wrong.

## Test-honesty triage (reviewer)

- Constant-vs-literal test where the literal came from the ORIGINAL file = **contract pin** (honest; encodes an external
  fact). Constant-vs-itself = tautology.
- Reference-implementation test (independent re-derivation of an algorithm)
  = strongest form; golden-value pin = good.
- Hermeticity: file:// URLs (no network), monkeypatched subprocess (no dotnet), tmp_path fixtures only.
- Tests that pin a current QUIRK with a "current behavior:" comment are honest evidence of a mechanical move, not
  sloppiness.

## Plan-premise traps

- A plan's "gitignored / untracked" claim about an output file may be wrong — verify with `git ls-files` (ai-raccoon
  2026-08: baseline-results.json was tracked and died with its producer).
- "No new CLI flags" ACs can be silently violated by additive --help handlers — flag as a behavior delta, even if
  harmless.
