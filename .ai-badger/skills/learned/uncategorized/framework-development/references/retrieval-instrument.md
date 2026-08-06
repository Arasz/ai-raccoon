# The retrieval instrument vs catalog wording (verified 0.79.1)

## What the instrument pins

- Four eval fixture files under `features/common/retrieval/eval/`: `mcp_queries.jsonl` (58),
  `mcp_queries_hard.jsonl` (70, classes incl. paraphrase/adjacent-negative/inert-negative),
  `mcp_queries_observed.jsonl` (11, drawn from real traffic), `mcp_queries_long.jsonl` (48).
- Runner: `tooling/retrieval_eval.py` (CLI `--fixtures/--index/--json-out`, or the
  `evaluate(fixtures, index)` API). Metrics: recall@1/@3, false_fire_rate, coverage_margin, plus per-class and per-length-bucket breakdowns.
- Two tests pin matcher behavior to the CURRENT index wording:
    - `tests/test_mcp_retrieval_hard_fixtures.py::test_paraphrase_fixtures_have_zero_lexical_overlap_with_their_target`
      — query tokens ∩ doc tokens must be ∅ (doc = tool name + tags + intent, tokenized like the matcher; see `retrieval_eval._doc_tokens`).
    - `tests/test_mcp_retrieval_observed_fixtures.py::test_the_matcher_still_answers_exactly_two_conversational_turns`
      — exactly the `KNOWN_FALSE_POSITIVES` fire; the pin fails in both directions. The docstring sanctions updating the pin + `docs/retrieval.md` §5 in one commit, but that RECORDS a regression — prefer removing the cause (below).

## The failure mode

- The matcher ranks by coverage (matched query terms / total terms) gated by
  `DEFAULT_COVERAGE_THRESHOLD` + `TOP_N`, and the tokenizer STEMMS (`everything`→`everyth`,
  `editing`→`edit`, `radius`→`radiu`). One incidental content word in an intent can cross the threshold for a conversational or negative turn. Corpus-wide scoring means any doc change can also shift OTHER docs' ranks.
- `mcp-index update` re-describes catalog-covered tools into `.ai-badger/mcp-tools.json`. Until the first propagation, catalog wording is NEVER measured — the fixtures pin the index wording at authoring time. The first propagation can fail
  tests that were green for months (verified 0.79.1: catalog entries from #181 broke hard+observed tests on first propagation; triggers were `everything` in `get_impact_radius_tool` and `tests` in `query_graph_tool`).

## The workflow when propagation breaks tests

1. **Isolate change-caused failures**: `git stash` the index change, run the failing files, diff the FAILED/ERROR lists (`grep -E '^(FAILED|ERROR)' | sed 's/ - .*//' | sort`, then
   `comm`). Only the comm diff is yours. (This machine's baseline: 40 env failures + 99 errors because tests shell out to `/Library/Developer/CommandLineTools/usr/bin/python3`, which lacks `jsonschema` from engine/requirements.txt;
   `/usr/bin/python3` has it.)
2. **Identify the trigger token**: run the pinned query through
   `mcp_matcher.find_relevant_tools` and the doc through `retrieval_eval._doc_tokens`; compare stemmed tokens.
3. **Fix the CATALOG** (`features/common/mcp/<server>/tools.json`) — the root cause — not the fixtures and not the pin. Keep the curation concept (e.g. symbol-level blast radius, coverage tracing), drop the trigger words.
4. **Re-propagate**: `mcp_index.py update --target <root>` (entries with `origin: catalog`
   re-describe).
5. **Re-measure ALL four fixture sets**, OLD index (`git show HEAD:.ai-badger/mcp-tools.json`)
   vs NEW. Bar: recall not below OLD, false_fire not above OLD.

Verified 0.79.1 result: hard recall@1 0.442→0.462, recall@3 0.481→0.500, false_fire 0.333→0.333; observed false_fire 0.222→0.222; queries/long unchanged.

## Index-sync operational notes (verified 0.79.1)

- `hermes mcp list --json` is broken (issue #188): the update chain falls back to
  `claude mcp list` (health-checks every server, ~14s, NO tool detail) and stops at the first answering source. Run the chain twice — once default, once `--host hermes` — so hermes-side servers (dotnet-sdk, glider, glider-trace, ai-raccoon)
  get recorded with statuses too.
- claude's status is claude's view: `ai-raccoon` read `unreachable` under claude's health check while hermes had it connected (stdio address-in-use when hermes already runs it). For hermes-run projects hermes's view is authoritative; the
  `--host hermes` run corrects it.
- A zero-tool-detail listing never marks sources removed — absence is "not asked", not "gone".
- `.ai-badger/mcp-tools.json` is NOT shipped surface (`release_guard` SHIPPED_PATHS = skills, features, engine, tooling, schemas, index.json). `features/common/mcp/*.json` IS — a catalog edit trips `gates/release_guard.py`, which diffs the
  working tree against the LAST RELEASE TAG (when VERSION == last tag, any shipped-surface change needs a bump). Release chore for a wording fix: VERSION bump (patch) + `docs/changelog/{v}-{slug}.md` +
  `tooling/version_sync.py` (writes plugin.json, marketplace.json, index.json) +
  `tooling/changelog_index.py` (writes the README table; `--check` validates), then re-verify all gates.

## Commands

- OLD-vs-NEW eval comparison: `python3 -c` with sys.path inserts for `tooling/`,
  `features/common/retrieval/`, `engine/`; loop `retrieval_eval.load_fixtures` +
  `retrieval_eval.evaluate` over each fixture file and print recall@1/@3/false_fire/margin.
- mcp-index tool: `/Users/arasz/.hermes/skills/AiRaccoon/mcp-index/scripts/mcp_index.py`
  (external-dir skill — framework-side updates to it go through feed-badger).
