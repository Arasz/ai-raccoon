"""Fusion retrieval (P2): FTS leg + dual-vector leg -> RRF -> affinity -> floor -> Take.

Pipeline order mirrors ExecuteSearchPipeline/SearchResultFusion/SearchResultMerge:
per-bucket legs (FTS with the M5 fallback trigger; content+structure cosine fused
at structureAlpha) -> ModalityCandidates content-dedupe -> FuseWithEvidence
(floor 0, no limit) -> SearchResultMerger (single-list re-fuse, affinity with
lambda 0 for path queries, relative floor, Take(limit)).

FusionRetriever subclasses llama_index BaseRetriever and serves NodeWithScore
over the ingested Documents. The embedding engine is a seam: production passes
model.get_query_embedding; tests pass a deterministic toy.
"""

from __future__ import annotations

import hashlib
import math
from typing import Any, Optional

from llama_index.core.base.base_retriever import BaseRetriever
from llama_index.core.schema import NodeWithScore, QueryBundle, TextNode

from . import fts as fts_plan
from . import fusion
from . import scopes
from retrieval_tuning import repo_data

_FROZEN = repo_data.KNOBS["PARAMS"]
EVAL_LIMIT = repo_data.KNOBS["EVAL_LIMIT"]
from .fusion import DocScoreFormula, RankedHit
from .ingest import StoreHandle, query_fts


def candidate_window(limit: int, mode: str = _FROZEN["candidateWindow"]) -> int:
    """Port of SqliteMemoryStore.CandidateWindowFor (default Max3X100)."""
    if mode == "max5x50":
        return max(limit * 5, 50)
    return max(limit * 3, 100)


def dedupe_by_content(
    items: list[tuple[str, float, str, str]], ascending: bool
) -> list[tuple[str, float]]:
    """Port of ModalityCandidates.Deduplicated: group by sha256(value); the
    project copy wins over a shared/ copy; best absolute score wins the group."""
    groups: dict[str, list[tuple[str, float, str]]] = {}
    for h, score, value, path in items:
        groups.setdefault(hashlib.sha256((value or "").encode()).hexdigest(), []).append(
            (h, score, path)
        )

    def pick(group: list[tuple[str, float, str]]) -> tuple[str, float]:
        ordered = sorted(group, key=lambda t: (t[2].startswith("shared/"),
                                               t[1] if ascending else -t[1]))
        return ordered[0][0], ordered[0][1]

    picked = [pick(g) for g in groups.values()]
    picked.sort(key=lambda item: (item[1] if ascending else -item[1]))
    return picked


def _finite_hits(ids: list[str], distances: list[float]) -> dict[str, float]:
    """Chroma distances -> cosine similarities; non-finite entries are dropped.

    A NaN/inf similarity would poison every downstream RRF sum and floor
    comparison, so the leg filters them instead of serving them."""
    out = {}
    for h, d in zip(ids, distances):
        if isinstance(d, (int, float)) and not isinstance(d, bool) and math.isfinite(d):
            out[h] = 1.0 - d
    return out


def _query_tie_complete(collection, qvec, window: int, where: dict) -> dict:
    """Chroma ANN query with the window cut expanded past exact-distance ties.

    Chroma truncates an exact-distance tie group at the requested k and the
    surviving subset is process-dependent (measured: a 17-item tie group at
    d=2^-24 spanning the k=100 cut). Grow the request while the (window+1)-th
    distance equals the window-th one, bounded by the collection count, so the
    caller can sort by (distance, hash) and cut deterministically."""
    count = collection.count()
    if count == 0:
        return {"ids": [[]], "distances": [[]]}
    k = min(count, window + 1)
    while True:
        hits = collection.query(query_embeddings=[qvec], n_results=k,
                                where=where, include=["distances"])
        dists = hits["distances"][0]
        if (len(dists) < k  # every matching row is in hand
                or len(dists) <= window  # the cut is the collection's end
                or k >= count  # no rows left to grow into
                or dists[window] != dists[window - 1]):  # no tie at the cut
            return hits
        k = min(count, max(k * 2, window + 1))


def _top_window_similarity(collection, qvec, window: int, where: dict) -> dict[str, float]:
    """Tie-complete top-window keyed by hash, sorted (distance, hash) before the cut."""
    hits = _query_tie_complete(collection, qvec, window, where)
    rows = [(h, d) for h, d in zip(hits["ids"][0], hits["distances"][0])
            if isinstance(d, (int, float)) and not isinstance(d, bool) and math.isfinite(d)]
    rows.sort(key=lambda item: (item[1], item[0]))
    return _finite_hits([h for h, _ in rows[:window]], [d for _, d in rows[:window]])


class FusionRetriever(BaseRetriever):
    """BaseRetriever over a harness store; retrieve(query, limit=EVAL_LIMIT) serves NodeWithScore."""

    def __init__(self, handle: StoreHandle, query_embed, project_id: str = "ai-raccoon",
                 scope: str = scopes.DEFAULT_QUERY_SCOPE,
                 default_limit: int = EVAL_LIMIT) -> None:
        super().__init__()
        self._handle = handle
        self._query_embed = query_embed
        self._project_id = project_id
        self._scope = scope
        self._default_limit = default_limit
        params = handle.params
        self._rrf_k = int(params.get("rrfK", _FROZEN["rrfK"]))
        self._fts_weight = float(params.get("ftsWeight", _FROZEN["ftsWeight"]))
        self._vector_weight = float(params.get("vectorWeight", _FROZEN["vectorWeight"]))
        self._min_rel = float(params.get("minRelativeScore", _FROZEN["minRelativeScore"]))
        self._lambda = float(params.get("sourceLambda", _FROZEN["sourceLambda"]))
        self._threshold = float(params.get("consolidationThreshold",
                                          _FROZEN["consolidationThreshold"]))
        self._formula = (DocScoreFormula.SUM if params.get("docScoreFormula") == "sum"
                         else DocScoreFormula.MAX)
        self._window_mode = str(params.get("candidateWindow", _FROZEN["candidateWindow"]))
        self._alpha = float(params.get("structureAlpha", _FROZEN["structureAlpha"]))

    # -- legs (public for the skipped-leg wiring tests) --

    def fts_leg(self, query: str, limit: int) -> tuple[list[tuple[str, float]], Any]:
        """FTS leg: primary MATCH, M5 fallback trigger; skipped ([]) on empty plan."""
        plan = fts_plan.build_plan(query)
        window = candidate_window(limit, self._window_mode)
        if not plan.expression or self._fts_weight == 0:
            return [], plan
        primary = query_fts(self._handle, plan.expression, self._project_id, self._scope,
                            window)
        if fts_plan.should_use_fallback(plan, len(primary), limit):
            primary = query_fts(self._handle, plan.fallback, self._project_id,
                                self._scope, window)
        return primary, plan

    def vector_leg(self, query: str, limit: int) -> list[tuple[str, float]]:
        """Dual-vector leg: content + structure cosine fused at structureAlpha (M3)."""
        if self._vector_weight == 0:
            return []
        window = candidate_window(limit, self._window_mode)
        qvec = self._query_embed(query)
        where = scopes.chroma_where(self._project_id, self._scope)
        content = _top_window_similarity(self._handle.content, qvec, window, where)
        struct = _top_window_similarity(self._handle.structure, qvec, window, where)
        return fusion.structure_rank(content, struct, self._alpha, window)

    # -- pipeline --

    def _payloads(self, hashes: list[str]) -> dict[str, tuple[str, dict]]:
        got = self._handle.content.get(ids=list(hashes), include=["documents", "metadatas"])
        return {h: (t or "", m or {}) for h, t, m in
                zip(got["ids"], got["documents"], got["metadatas"])}

    def _retrieve_with_limit(self, query: str, limit: int) -> list[NodeWithScore]:
        fts_rows, plan = self.fts_leg(query, limit)
        vec_rows = self.vector_leg(query, limit)

        payload_ids = {h for h, _ in fts_rows} | {h for h, _ in vec_rows}
        if not payload_ids:
            return []
        payloads = self._payloads(sorted(payload_ids))

        def with_payload(rows: list[tuple[str, float]]) -> list[tuple[str, float, str, str]]:
            return [(h, s, payloads[h][0], payloads[h][1].get("path", "")) for h, s in rows
                    if h in payloads]

        legs: list[tuple[str, float, list[str]]] = []
        carriers: dict[str, tuple[str, dict]] = {}
        if fts_rows and self._fts_weight != 0:
            fts_deduped = dedupe_by_content(with_payload(fts_rows), ascending=True)
            legs.append(("fts", self._fts_weight, [h for h, _ in fts_deduped]))
            for h, _ in fts_deduped:
                carriers.setdefault(h, payloads[h])
        if vec_rows and self._vector_weight != 0:
            vec_deduped = dedupe_by_content(with_payload(vec_rows), ascending=False)
            legs.append(("vector", self._vector_weight, [h for h, _ in vec_deduped]))
            for h, _ in vec_deduped:
                carriers.setdefault(h, payloads[h])
        if not legs:
            return []

        paths = {h: carriers[h][1].get("path", "") for h in carriers}
        fused = fusion.fuse_rrf(legs, paths, self._rrf_k, 0.0, 2**31 - 1)
        cands = [RankedHit(h, r, paths.get(h, ""),
                           carriers[h][1].get("source_file") or None,
                           int(carriers[h][1].get("chunk_index", 0)),
                           int(carriers[h][1].get("total_chunks", 0)))
                 for h, r in fused]
        source_lambda = 0.0 if plan.is_path_query else self._lambda
        merged = fusion.merge_results(cands, limit, self._min_rel, self._rrf_k,
                                      source_lambda, self._threshold, self._formula)
        served = []
        for c in merged:
            text, meta = carriers[c.hash]
            served.append(NodeWithScore(
                node=TextNode(id_=c.hash, text=text, metadata=dict(meta)),
                score=c.ranking))
        return served

    def _retrieve(self, query_bundle: QueryBundle) -> list[NodeWithScore]:
        return self._retrieve_with_limit(query_bundle.query_str, self._default_limit)

    def retrieve(self, query, limit: Optional[int] = None) -> list[NodeWithScore]:
        """BaseRetriever.retrieve plus an optional per-call limit (default 8)."""
        text = query.query_str if isinstance(query, QueryBundle) else str(query)
        return self._retrieve_with_limit(text, self._default_limit if limit is None else limit)
