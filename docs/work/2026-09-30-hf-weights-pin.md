# HF weights pin: pinned snapshot materialized and verified

Date: 2026-09-30. Task: `air-254-vs-1022-retrieval-measurement-809` (issue #809), step `hf-weights`. Environment tooling only: no product code and no harness code changed.

Model: `ibm-granite/granite-embedding-small-english-r2`

revision: 2ab6fa8ea2d674564defd37171ae19079b864b33

bytes: 194286691

The bytes figure comes from `ingest.model_weights_info()` against the standard hub cache (see Verification). The revision comes from [`data/knobs.json`](../../data/knobs.json) `PINNED_MODEL_REVISION`, read by `scripts/src/retrieval_tuning/repo_data.py`, not from the cache's `refs/main`.

## Artifact

- Model repo: `ibm-granite/granite-embedding-small-english-r2`
- Pin source: `data/knobs.json` -> `PINNED_MODEL_REVISION` = `2ab6fa8ea2d674564defd37171ae19079b864b33`
- Cache root: `/Users/arasz/.cache/huggingface/hub` (the CLI's `HF_HUB_CACHE`; no `HF_HOME` was set in the environment)
- Snapshot: `/Users/arasz/.cache/huggingface/hub/models--ibm-granite--granite-embedding-small-english-r2/snapshots/2ab6fa8ea2d674564defd37171ae19079b864b33`
- Tool: `hf` CLI 1.24.0 (`huggingface_hub` 1.24.0), `/opt/homebrew/bin/hf`

The harness resolves the pin from the worktree. `scripts/retrieval_tuning/llamaindex_harness/__init__.py` puts `scripts/src` on `sys.path`, `repo_data.py` reads `REPO_ROOT/data/knobs.json`, and both `repo_data.KNOBS` and `ingest.PINNED_MODEL_REVISION` returned `2ab6fa8ea2d674564defd37171ae19079b864b33` in the worktree.

## Fetch

The cache did not exist before this step. `hf cache list` failed with `Cache directory not found`, and `model_weights_info()` failed loud on the missing `refs/main`:

```text
$ hf cache list --format json
Error: Cache directory not found: /Users/arasz/.cache/huggingface/hub
$ python3 ... "from llamaindex_harness import ingest; print(ingest.model_weights_info())"
ValueError: model weights for 'ibm-granite/granite-embedding-small-english-r2' unresolvable under
/Users/arasz/.cache/huggingface/hub ... No such file or directory: '.../refs/main'
```

The fetch named the commit, never the branch:

```text
$ hf download ibm-granite/granite-embedding-small-english-r2 --revision 2ab6fa8ea2d674564defd37171ae19079b864b33
path=/Users/arasz/.cache/huggingface/hub/models--ibm-granite--granite-embedding-small-english-r2/snapshots/2ab6fa8ea2d674564defd37171ae19079b864b33
```

That single call wrote the snapshot and no `refs/main`. The mechanism is readable in the installed library: `_cache_commit_hash_for_specific_revision` returns early when the requested revision already is the commit hash (`huggingface_hub/file_download.py:703`, called at :1195), so a sha-pinned fetch writes no ref file. `refs/` did not exist afterwards. The harness resolves through `refs/main`, so I then ran a branch fetch, but only after checking the Hub's current main sha equals the pin. It landed on the same sha-named snapshot:

```text
$ hf models info ibm-granite/granite-embedding-small-english-r2 --format json | jq -r .sha
2ab6fa8ea2d674564defd37171ae19079b864b33
$ hf download ibm-granite/granite-embedding-small-english-r2
{"path": ".../models--ibm-granite--granite-embedding-small-english-r2/snapshots/2ab6fa8ea2d674564defd37171ae19079b864b33"}
$ cat .../models--ibm-granite--granite-embedding-small-english-r2/refs/main
2ab6fa8ea2d674564defd37171ae19079b864b33
```

`refs/main` is a cache convenience here, not the source of the pin. The sha was chosen before the ref existed, and the fetched path is named by the sha.

## Verification

`hf cache verify` checked every file of the pinned revision against the Hub's own digests:

```text
$ hf cache verify ibm-granite/granite-embedding-small-english-r2 --revision 2ab6fa8ea2d674564defd37171ae19079b864b33
repo_id=ibm-granite/granite-embedding-small-english-r2 repo_type=model checked=11 path=.../snapshots/2ab6fa8ea2d674564defd37171ae19079b864b33
rc=0
```

The cache holds one repo and one snapshot:

```text
$ hf cache list --revisions --format json
[{"id": "model/ibm-granite/granite-embedding-small-english-r2",
  "repo_id": "ibm-granite/granite-embedding-small-english-r2", "repo_type": "model",
  "revision": "2ab6fa8ea2d674564defd37171ae19079b864b33",
  "snapshot_path": ".../snapshots/2ab6fa8ea2d674564defd37171ae19079b864b33",
  "size": "194.3M", "last_modified": "51 seconds ago", "refs": ["main"]}]
```

The harness reader agrees:

```text
$ python3 ... "from llamaindex_harness import ingest; rev, nbytes = ingest.model_weights_info();
  print('pin:', rev); print('bytes:', nbytes); ingest.check_pinned_revision(rev); print('pin gate: pass')"
pin: 2ab6fa8ea2d674564defd37171ae19079b864b33
bytes: 194286691
pin gate: pass
```

The file bytes match the Hub's declared LFS digests exactly:

```text
Hub metadata at the pinned revision (?blobs=true):
  dcbfaf2ee50d358763cbc0c08e5b045ecbf6aeb02dc0e86ffb910029bf9ebb5f  95332048  model.safetensors
  189cd50b38b2b3ee6c8d03b76a7dfd4574f27d023294c75c105a725b8ad484b6  95334202  pytorch_model.bin
Local shasum -a 256 (snapshot symlinks followed):
  dcbfaf2ee50d358763cbc0c08e5b045ecbf6aeb02dc0e86ffb910029bf9ebb5f  model.safetensors
  189cd50b38b2b3ee6c8d03b76a7dfd4574f27d023294c75c105a725b8ad484b6  pytorch_model.bin
```

An independent sum over the 11 files in the snapshot (symlinks followed) returns `194286691`, the same figure `model_weights_info` produced.

The ingest suite is green with the cache in place:

```text
$ python3 -m pytest scripts/tests/test_llamaindex_harness_ingest.py -q
32 passed, 1 warning in 6.76s
```

## The knobs path and the literal AC1

AC1 names `scripts/data/knobs.json`. No such path exists at this revision, and none ever did. `git log --all -- scripts/data/knobs.json` is empty and `git ls-tree -r origin/main` lists `data/knobs.json` only. The step forbids touching `scripts/data/**`, so I did not create it. The pin's only home is `data/knobs.json`.

The literal command is worse than a wrong path here. This machine's `grep` is BSD grep 2.6.0-FreeBSD (`/usr/bin/grep`), which rejects `-P`. With the plan's pattern elided below (so this note carries exactly one matching line), the literal check behaves like this:

```text
$ diff <(jq -r .PINNED_MODEL_REVISION scripts/data/knobs.json) \
       <(grep -oP '<plan AC1 pattern>' docs/work/2026-09-30-hf-weights-pin.md)
jq: error: Could not open file scripts/data/knobs.json: No such file or directory
grep: invalid option -- P
$ echo rc=$?
rc=0
```

Both process substitutions fail, both emit empty stdout, and `diff` calls two empty files equal. The literal AC1 returns 0 and prints no diff, so it is vacuously green on this machine: as written, it cannot fail. That is the one check in this step I would not trust without the fixed form.

The fixed comparison reads the same two independent sources, using this tree's real knobs path and a BSD-compatible, line-anchored extraction:

```text
$ diff <(jq -r .PINNED_MODEL_REVISION data/knobs.json) \
       <(awk '/^revision:/{print $2}' docs/work/2026-09-30-hf-weights-pin.md)
(no output)
$ echo rc=$?
rc=0
```

It can go red. Self-test against a deliberately wrong value:

```text
$ diff <(jq -r .PINNED_MODEL_REVISION data/knobs.json) <(printf '0000000000000000000000000000000000000000\n')
1c1
< 2ab6fa8ea2d674564defd37171ae19079b864b33
---
> 0000000000000000000000000000000000000000
$ echo rc=$?
rc=1
```

AC3's pattern (a colon, then spaces, then a non-space run) works on this BSD grep; I checked a positive match and a spaces-only negative before relying on it.

## Repo and cache state

- Only file written by this step: this note.
- The HF cache gained exactly one snapshot, named by the pin. The only other cache writes were the `refs/main` file and the revision tree metadata that the `hf` commands manage themselves. No other snapshot or repo exists under the cache root.
- No memory-bank entry writes. One `memory_search` ran because the repo's memory-first hook demands one before repo text searches; I called no memory write tool. The server records the search's own telemetry row by design.
- No touch of `scripts/retrieval_tuning/llamaindex_harness/**`, `data/**`, or `src/**`.

## Decisions and rejected alternatives

- Rejected fetching the branch as the materialization step. That is "whatever main points at" (the exact thing the step warns about). It equals the pin today, and that equality was recorded, but the proof does not rest on it.
- Rejected hand-writing `refs/main` or assembling the snapshot by hand. The `hf` CLI wrote both.
- Rejected a `--local-dir` download. The harness reads the standard hub cache, so a local-dir copy would be invisible to `model_weights_info`.
- Rejected trimming files with `--include`. The published snapshot has 11 files (configs, tokenizer, pooling config, both weight formats); a subset would be a different artifact.
- Did not touch the pin file or the harness. Both are explicitly out of scope for this step.

## Open items

- HYPOTHESIS: the downstream `ab-run-report` offline ingest will resolve these weights. Grounds: `model_weights_info` returns the pin, `refs/main` matches, and the ingest suite passes. No embedder was loaded and no offline ingest ran in this step.
- HYPOTHESIS: the Hub's `used_storage` (285,998,266) exceeding the local snapshot (194,286,691) is upstream bookkeeping beyond one revision's sanitized files. No local evidence was gathered for that split; the local tree is complete per `hf cache verify` (11/11).
- HYPOTHESIS: `pytorch_model.bin` is redundant for the loader given `model.safetensors`. Both are present and verified; which file the loader prefers was not exercised.

## Acceptance status

| AC | Check | Result |
|----|-------|--------|
| 1 | independent comparison of the knobs pin and this note's recorded revision | PASS in fixed form (empty diff, rc=0; self-test red at rc=1). The literal command is vacuously green here: `scripts/data/knobs.json` is absent and BSD grep rejects `-P`, so both sides are empty. Both facts are recorded above. |
| 2 | fetch targeted the pin explicitly; bytes > 0 from `model_weights_info`; ingest suite green | PASS (`--revision 2ab6fa8ea2d674564defd37171ae19079b864b33`; `bytes: 194286691`; 32 passed) |
| 3 | this note carries a revision line and the bytes figure | PASS |