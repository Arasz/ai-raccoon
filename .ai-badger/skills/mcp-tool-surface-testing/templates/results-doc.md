# <Server> MCP tools — full test results

Date: <YYYY-MM-DD> · Tested by: <agent/session context>
Target: live <server> MCP server via <host bridge> (data root / env note)
Test project: `<dedicated-test-project>` (dedicated; fully cleaned up after — 0 residue verified)
Scope: all N exported tools (<n> <type> tools + <m> prompts)

## Method

1. Expectations formulated **before** any call, from three sources: the docs contract (tool table), the live tool schemas, and the mcp-index intents (section 1).
2. Each tool called with a test payload; response compared against the expectation.
3. Verdicts: **PASS** = matches expectation · **PARTIAL** = works but differs from the documented/ideal shape · **FAIL** = broken.
4. Test payloads used real end-to-end flows (write → search → promote → search tier → lifecycle → verify), positive + negative controls for destructive tools.

## 1. Expectations (formulated before any call)

| # | Tool     | Expected behaviour | Expected response shape |
|---|----------|--------------------|-------------------------|
| 1 | `tool_a` | ...                | ...                     |
| 2 | `tool_b` | ...                | ...                     |

Sources: <docs table ref>, live schemas, mcp-index intents.

## 2. Actual results (<n> calls)

| # | Tool · payload      | Actual response (abridged) | Verdict                         |
|---|---------------------|----------------------------|---------------------------------|
| 1 | `tool_a(<payload>)` | ...                        | **PASS**                        |
| 2 | `tool_b(<payload>)` | ...                        | **PARTIAL** — works, but <diff> |

**Summary: X/Y calls PASS, Z PARTIAL, W FAIL.** <one-line takeaway>

## 3. Findings

1. Docs drift vs live contract: <param missing from docs table / response key renamed / extra fields>.
2. Index-intent overstatement: <e.g. "denied in rw" but deletes succeeded>.
3. Derived identity: <e.g. promotion re-hashes the row — shared hash ≠ source hash>.
4. Error UX: <typed errors that name/omit the remedy>.
5. Envelope: <bare JSON vs {"result": "<string>"} differences>.

## 4. How the perfect response should look

| Tool     | Perfect response |
|----------|------------------|
| `tool_a` | <ideal shape>    |
| `tool_b` | <ideal shape>    |

## 5. Environment notes

- Test data fully removed after the run: <rows deleted, registrations removed, config restored, temp dirs deleted>; <final verification call> proved 0 residue.
- Real projects/config untouched throughout.
- Expectations corrected during the run: <list> — the corrections came from the live contract, which outranks prior assumptions.
