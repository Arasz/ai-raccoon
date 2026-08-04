"""Hermes session source for the /task tracker (installed by the hermes adjustment).

Everything hermes-specific about task tracking lives here, not in the common task skill
scripts: the session id env var, the state.db location, the SQLite parser that maps Hermes's
store onto the tracker's checkpoint shape, and the delegation token reader.

The hermes adjustment (features/hermes/adjustments/adjust_task.py) copies this module into
the scaffolded `.ai-badger/skills/task/scripts/hermes_session_source.py`, where tracker_lib's
discovery import finds and asks it to `register(lib)`. Absent that copy, the common tracker
never hears about hermes.

Reads are read-only (`sqlite3` `mode=ro`): the tracker never writes, locks, or creates the
Hermes store, and degrades to zeroed checkpoints when Hermes is absent — it never crashes a
session over missing data.
"""
from __future__ import annotations

import json
import os
import sqlite3
from datetime import datetime, timezone
from pathlib import Path

HERMES_SESSION_ENV = "HERMES_SESSION_ID"
HERMES_HOME_ENV = "HERMES_HOME"


def _now_iso() -> str:
    """Same shape as tracker_lib.now_iso; kept local so this module is self-contained."""
    return datetime.now(timezone.utc).isoformat()


def register(tracker_lib) -> None:
    """Register the hermes session source with a tracker_lib module.

    Called by tracker_lib's guarded optional import (and by tests, against whichever
    tracker_lib instance they loaded). Wires the env var, checkpoint maker, resume command
    and delegation reader the common CLI dispatches through.
    """
    tracker_lib.register_session_source(
        "hermes",
        env_var=HERMES_SESSION_ENV,
        resolve=_resolve,
        checkpoint=lambda session: make_hermes_checkpoint(session["sessionId"]),
        resume=lambda session_id: f"hermes --resume {session_id}",
        delegation_usage=lambda delegation_id: hermes_delegation_usage(
            hermes_state_db_path(), delegation_id),
    )


def _resolve() -> dict:
    """Identify the invoking hermes session: HERMES_SESSION_ID, and nothing else.

    Hermes sets the env var on every tool subprocess, so the env id is the identity — no
    current-session.json consult, no PID/cwd fallback.
    """
    sid = os.environ.get(HERMES_SESSION_ENV)
    if sid:
        return {"sessionId": sid, "transcriptPath": None}
    return {}


def hermes_state_db_path(env: dict | None = None) -> Path:
    """Locate Hermes's session store: $HERMES_HOME/state.db, else ~/.hermes/state.db.

    Returns the path even when it does not exist — existence is the parser's problem, and a
    missing store must degrade to zeroed checkpoints, never crash (see parse_hermes_session_usage).
    """
    env = os.environ if env is None else env
    override = env.get(HERMES_HOME_ENV, "").strip()
    base = Path(override).expanduser() if override else Path.home() / ".hermes"
    return base / "state.db"


def _zeroed_usage() -> dict:
    """The shape parse_transcript_usage returns for a missing transcript, all zeros."""
    return {
        "contextTokens": 0, "assistantMessages": 0, "byModel": {},
        "dispatches": {"count": 0, "undeclaredModel": 0, "byAgentType": {}},
        "cumulative": {"inputTokens": 0, "outputTokens": 0, "cacheReadTokens": 0,
                       "cacheCreationTokens": 0},
        "transcriptFound": False,
    }


def _hermes_connect(db_path: Path):
    """Read-only SQLite connection to the Hermes store; None on any failure.

    mode=ro (SQLITE_OPEN_READONLY): the tracker never writes the store, never creates tables,
    and never contends with a live Hermes writer. sqlite3.OperationalError covers a locked or
    busy store; OSError a missing or unreadable file.
    """
    try:
        return sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    except (OSError, sqlite3.Error):
        return None


def parse_hermes_session_usage(session_id: str, db_path: Path | None = None) -> dict:
    """Aggregate token usage for a Hermes session from state.db, in the transcript shape.

    Maps onto the Claude checkpoint shape so compute_usage/status need no adaptation:
    cumulative  — sessions row (input/output/cache_read/cache_write); Hermes's "write" is
                  treated as Claude's "creation" (freshly-written cacheable input). Not
                  identical accounting; no parity claim.
    byModel     — session_model_usage rows; slot assistantMessages carries api_call_count,
                  the only per-model proxy Hermes stores (compute_usage never reads it).
    dispatches  — completed async_delegations with origin_session = this session; Hermes
                  records no agent type, so byAgentType stays {} — an empty dict is the
                  honest "no data", never a guessed partition.
    contextTokens — always 0: Hermes stores no per-message tokens (messages.token_count is
                  NULL on every row) and no context-window snapshot. A fabricated estimate
                  would be worse than an honest zero.
    transcriptFound — True iff the session row exists (i.e. a usage record is present).
    """
    db_path = hermes_state_db_path() if db_path is None else db_path
    con = _hermes_connect(db_path)
    if con is None:
        return _zeroed_usage()
    try:
        row = con.execute(
            "SELECT input_tokens, output_tokens, cache_read_tokens, cache_write_tokens "
            "FROM sessions WHERE id = ?", (session_id,)
        ).fetchone()
        if row is None:
            return _zeroed_usage()
        cumulative = {
            "inputTokens": row[0] or 0,
            "outputTokens": row[1] or 0,
            "cacheReadTokens": row[2] or 0,
            "cacheCreationTokens": row[3] or 0,
        }
        # Child (delegated) sessions carry their own rows; the parent's sessions row does NOT
        # include them (measured: fed404 row 309344 vs its child 59265). Fold one level so
        # cumulative matches the Claude path, which sums subagents/*.jsonl into cumulative.
        child = con.execute(
            "SELECT COALESCE(SUM(input_tokens),0), COALESCE(SUM(output_tokens),0), "
            "COALESCE(SUM(cache_read_tokens),0), COALESCE(SUM(cache_write_tokens),0) "
            "FROM sessions WHERE parent_session_id = ?", (session_id,)
        ).fetchone()
        cumulative["inputTokens"] += child[0] or 0
        cumulative["outputTokens"] += child[1] or 0
        cumulative["cacheReadTokens"] += child[2] or 0
        cumulative["cacheCreationTokens"] += child[3] or 0
        by_model = {}
        for (model, api_calls, inp, out, cr, cw) in con.execute(
            "SELECT model, api_call_count, input_tokens, output_tokens, cache_read_tokens, "
            "cache_write_tokens FROM session_model_usage WHERE session_id = ?",
            (session_id,)
        ):
            # session_model_usage has a composite key (session, model, billing_*, task) — one
            # (session, model) legitimately has several rows split by task. Accumulate rather
            # than assign, or modelMix corrupts for any session with approval/title rows.
            slot = by_model.setdefault(model, {
                "inputTokens": 0, "outputTokens": 0,
                "cacheReadTokens": 0, "cacheCreationTokens": 0, "assistantMessages": 0,
            })
            slot["inputTokens"] += inp or 0
            slot["outputTokens"] += out or 0
            slot["cacheReadTokens"] += cr or 0
            slot["cacheCreationTokens"] += cw or 0
            slot["assistantMessages"] += api_calls or 0
        messages = con.execute(
            "SELECT COUNT(*) FROM messages WHERE session_id = ? AND role = 'assistant'",
            (session_id,)
        ).fetchone()[0] or 0
        delegations = con.execute(
            "SELECT result_json FROM async_delegations "
            "WHERE state = 'completed' AND origin_session = ?", (session_id,)
        ).fetchall()
        count = 0
        undeclared = 0
        for (result_json,) in delegations:
            count += 1
            try:
                parsed = json.loads(result_json) if result_json else None
                results = parsed.get("results") if isinstance(parsed, dict) else None
                if not isinstance(results, list):
                    results = []
                if not any(isinstance(r, dict) and r.get("model") for r in results):
                    undeclared += 1
            except (TypeError, ValueError):
                undeclared += 1  # malformed result_json degrades to "no model declared"
        return {
            "contextTokens": 0,
            "assistantMessages": messages,
            "byModel": by_model,
            "dispatches": {"count": count, "undeclaredModel": undeclared,
                           "byAgentType": {}},
            "cumulative": cumulative,
            "transcriptFound": True,
        }
    except sqlite3.Error:
        return _zeroed_usage()
    finally:
        con.close()


def make_hermes_checkpoint(session_id: str, db_path: Path | None = None) -> dict:
    """Checkpoint shape for a Hermes session, mirroring make_checkpoint's keys."""
    usage = parse_hermes_session_usage(session_id, db_path)
    return {
        "timestamp": _now_iso(),
        "contextTokens": usage["contextTokens"],
        "assistantMessages": usage["assistantMessages"],
        "byModel": usage["byModel"],
        "cumulative": usage["cumulative"],
    }


def hermes_delegation_usage(db_path: Path, delegation_id: str) -> dict | None:
    """Tokens recorded for one completed Hermes delegation, or None.

    result_json embeds results[].tokens {input, output} (plus model and api_calls). The
    total is the sum over results — a delegation's work may span several API calls, and each
    result carries its own tokens. None when the row is missing, not completed, or malformed
    — the caller must refuse rather than record a fabricated number.
    """
    con = _hermes_connect(db_path)
    if con is None:
        return None
    try:
        row = con.execute(
            "SELECT state, completed_at, result_json FROM async_delegations "
            "WHERE delegation_id = ?", (delegation_id,)
        ).fetchone()
        if row is None or row[0] != "completed" or not row[2]:
            return None
        try:
            parsed = json.loads(row[2]) if row[2] else None
            results = parsed.get("results") if isinstance(parsed, dict) else None
            if not isinstance(results, list):
                results = []
        except (TypeError, ValueError):
            return None
        total = 0
        api_calls = 0
        model = None
        for result in results:
            if not isinstance(result, dict):
                continue
            tokens = result.get("tokens")
            if not isinstance(tokens, dict):
                tokens = {}
            total += (tokens.get("input") or 0) + (tokens.get("output") or 0)
            api_calls += result.get("api_calls") or 0
            model = model or result.get("model")
        if not total:
            return None
        return {"totalTokens": total, "model": model, "apiCalls": api_calls,
                "at": row[1]}
    except sqlite3.Error:
        return None
    finally:
        con.close()
