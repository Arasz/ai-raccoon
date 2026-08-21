"""evaluate(server, settings_dict, corpus) -> Metrics — the shared objective (plan §8).

Used by BOTH the matrix sweeps and the Optuna objective: apply the settings to
the scratch bank, run every query through the server (limit 5), score with the
pure functions, and summarize into means + per-query + per-category records.
"""

from __future__ import annotations

from typing import Optional

from . import scoring, settings as settings_mod


def evaluate(
    server,
    settings_dict: dict,
    corpus_entries: list[dict],
    apply_settings: bool = True,
    binary: Optional[str] = None,
) -> scoring.Metrics:
    """Score one configuration over one corpus against a (scratch) server.

    `server` needs `.search(entry) -> list[dict]` and, when settings are applied,
    `.data_root` (+ `.port`, `.binary`) — ScratchServer provides all of them.
    """
    if apply_settings and settings_dict:
        data_root = getattr(server, "data_root", None)
        if data_root is None:
            raise ValueError("evaluate: apply_settings=True requires a server with a data_root")
        binary = str(binary or getattr(server, "binary", None) or "ai-raccoon")
        settings_mod.apply_settings(
            data_root,
            settings_dict,
            port=getattr(server, "port", None),
            binary=binary,
        )

    query_scores = [scoring.score_query(server.search(entry), entry) for entry in corpus_entries]
    return scoring.summarize(query_scores, config=dict(settings_dict))
