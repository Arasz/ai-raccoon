# Archify packet provenance

This directory vendors the upstream [Archify](https://github.com/tt-a1i/archify) skill
packet so scaffolded projects receive working diagram renderers without a network fetch.

## Origin pin

- Upstream repo: `https://github.com/tt-a1i/archify`
- Tag: `v2.16.0`, commit `c826e6c3a7abad19c0f3cd1ca57207d54b1ad8de`
- Release asset: `archify.zip` (1,318,273 B)
- Asset sha256: `4c59fa6557a2385beaaef8c7219cc414573acc9f0c30a932d5053b0b20689a46`
- Commit id: asserted from the release page and recorded in `vendor.json`; the **verified**
  anchor is the asset sha256 above, not the commit (a `--revendor` run does not re-resolve it).
- Staging: the asset was verified against that sha, then compared byte-for-byte (`diff -r`) with the output of upstream's own `scripts/stage-clean-skill.mjs` run from
  tag `v2.16.0` — identical. The 76 files here are that staged tree, except `SKILL.md`
  (adapted, see below).

The asset sha above is the origin anchor. It also lives in `vendor.json`, but only as
information: no code path verifies the tree against it. `--check` is offline by design and
proves internal consistency only; `--revendor` takes `--expect-sha256` from the command
line and never reads the expected value from the file it is about to replace, so a forged
zip plus a forged manifest that agree with each other are still refused.

## Re-vendor (the only origin check)

```bash
gh release download v2.16.0 --repo tt-a1i/archify -p archify.zip
python3 tooling/vendor_archify.py --revendor archify.zip \
  --expect-sha256 4c59fa6557a2385beaaef8c7219cc414573acc9f0c30a932d5053b0b20689a46 \
  --tag v2.16.0 --commit c826e6c3a7abad19c0f3cd1ca57207d54b1ad8de
```

`--revendor` verifies the zip against the command-line literal first, stages to a temp
dir (upstream exclusion rules, symlink refusal, `package.json` cleaning, deterministic
`SKILL.md` adaptation), replaces this tree, and regenerates `vendor.json`. It refuses a
dirty target and exits 2 with the tree untouched on any failure. The day-to-day gate is
offline instead: `python3 tooling/vendor_archify.py --check`.

## ai-badger additions (extras, never upstream bytes)

`vendor.json` lists three extra files whose presence `--check` requires but whose content
it never hashes against upstream:

- `VENDOR.md` (this file) — provenance prose, ai-badger authored.
- `vendor.json` — the integrity manifest: per-file sha256 for the 75 byte-identical
  upstream files, plus the adapted-`SKILL.md` record (`upstream_body_sha256` of the
  pristine upstream body, `frontmatter_sha256` of the adapted frontmatter).
- `THIRD_PARTY_NOTICES.md` — backfilled from upstream `main` (the `v2.16.0` packet ships
  none). It applies exactly: `brand-marks/catalog.json` is byte-identical between
  `v2.16.0` and `main` (sha256 `e1a6c555cbf8ea36fb8100a4a25824e7ab87bebe9871e450ba05b6dea3b036d6`
  on both sides). A future re-vendor whose tag carries the notice stages it as upstream
  content instead; until then the backfill is preserved across `--revendor` runs.

## SKILL.md adaptation (rev 1)

Upstream frontmatter replaced wholesale with the ai-badger keys (`name`, `description`
naming the Mermaid fallback, `version` tracking upstream, `author: tt-a1i`, `license`,
`platforms`, `scope: default`, `metadata.hermes` only). Dropped upstream keys are recorded
here, not silently lost: upstream `description` (replaced), `metadata.version`
(`"2.16"` — superseded by top-level `version: 2.16.0`), `metadata.author` (kept as
top-level `author`), `metadata.based_on` (`Cocoon-AI/architecture-diagram-generator`,
MIT, v1.0 — upstream lineage, preserved in this sentence).

Upstream body kept byte-identical except: one explicit condition pinned on the
adjacency-dependent `references/` mention (line 82 upstream), plus `## When NOT to Use`,
`## Gotchas`, and the Mermaid-fallback statement appended. Split rule for independent
verification: `head, frontmatter, body = text.split("---", 2)`; `frontmatter_sha256`
covers parts[1] verbatim, `upstream_body_sha256` covers the pristine upstream parts[2]
(recoverable from the adapted body by stripping the appended sections and reversing the
one-line fix, which is what `--check` does).

## Stager delta (what was NOT copied from upstream)

Upstream's stager is hardened for publishing to strangers (snapshot fds, TOCTOU checks,
mode enforcement). This is a maintainer-run vendor op, so `--revendor` re-implements only
the exclusion semantics, symlink refusal, and `package.json` cleaning in Python (stdlib-only: `--check` must run where Node may be absent). One generalisation: the two
named `scripts/generate-*.mjs` exclusions are matched as a glob, so a future generator is
excluded rather than shipped by accident.

## Notice fidelity

`THIRD_PARTY_NOTICES.md` is upstream `main`'s current notice, kept verbatim. Its
brand-mark section applies to this packet exactly (`brand-marks/catalog.json` is
byte-identical between `v2.16.0` and `main`). Its JetBrains Mono section describes
`main`'s packaging: the `v2.16.0` template references JetBrains Mono through the Google
Fonts CSS (`JetBrains+Mono:wght@400;500;600;700&display=swap`) rather than embedding it,
and this packet ships no `assets/JetBrainsMono-OFL.txt`. The text is not edited; the
divergence is recorded here, and a future re-vendor to a tag that carries the file and the
embedding closes it.
