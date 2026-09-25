"""granite-embedding-small-english-r2 re-expressed for ONNX export to the CoreML EP.

Two layouts over the same HF ModernBERT weights, both taking (input_ids, attention_mask) and
returning (last_hidden_state, sentence_embedding = CLS of last_hidden_state):

- PlainEncoder: HF's own eager modules; only the masks and RoPE tables become precomputed
  constants sliced to the sequence length, so the export needs no com.microsoft ops.
- AneEncoder: the Apple ml-ane-transformers layout, (B, C, 1, S) between the embedding norm and
  the final norm, 1x1 convs for every Linear, channel-dim LayerNorm, per-head attention, and
  rotate_half folded into extra conv output channels.

Needs torch; transformers and tokenizers are imported only where used.
"""

from __future__ import annotations

import os
from pathlib import Path

import numpy as np
import torch

HF_REPO = "ibm-granite/granite-embedding-small-english-r2"
BUNDLED_DIR = Path.home() / ".ai-raccoon" / "models" / "onnx-community__granite-embedding-small-english-r2-ONNX"
HF_CACHE_DIR = Path.home() / ".cache" / "huggingface" / "hub" / "models--ibm-granite--granite-embedding-small-english-r2"

MASKED = -1e4  # fp16-representable, unlike finfo.min; exp(-1e4) is exactly 0 in fp16 and fp32
PAD_ID = 50283
SAMPLE_NAMES = ("short", "padded_pair", "long_300")


def hf_weights_cached() -> bool:
    return any(HF_CACHE_DIR.glob("snapshots/*/model.safetensors"))


def load_hf_model():
    """The HF ModernBertModel, eager attention, fp32, eval mode, from the local cache only."""
    os.environ["HF_HUB_OFFLINE"] = "1"
    from transformers import AutoModel

    model = AutoModel.from_pretrained(HF_REPO, attn_implementation="eager", dtype=torch.float32)
    return model.eval()


# ---------------------------------------------------------------------------------------------
# Constant tables: sliced to [:S] at runtime, never computed from S


def rope_tables(theta: float, max_len: int, head_dim: int) -> tuple[np.ndarray, np.ndarray]:
    """cos, sin [max_len, head_dim] as HF computes them (fp32, cat(freqs, freqs))."""
    inv_freq = 1.0 / (theta ** (np.arange(0, head_dim, 2, dtype=np.float32) / head_dim))
    freqs = np.outer(np.arange(max_len, dtype=np.float32), inv_freq.astype(np.float32))
    emb = np.concatenate([freqs, freqs], axis=-1).astype(np.float32)
    return np.cos(emb), np.sin(emb)


def band_mask(max_len: int, half_window: int) -> np.ndarray:
    """[max_len, max_len] additive mask: 0 where |i - j| <= half_window, MASKED elsewhere."""
    idx = np.arange(max_len)
    inside = np.abs(idx[:, None] - idx[None, :]) <= half_window
    return np.where(inside, 0.0, MASKED).astype(np.float32)


# ---------------------------------------------------------------------------------------------
# Plain layout


class PlainEncoder(torch.nn.Module):
    """HF eager ModernBertModel with mask/RoPE construction replaced by sliced constants."""

    def __init__(self, hf_model, max_len: int) -> None:
        super().__init__()
        config = hf_model.config
        self.embeddings = hf_model.embeddings
        self.layers = hf_model.layers
        self.final_norm = hf_model.final_norm
        self.layer_types = list(config.layer_types)
        head_dim = config.hidden_size // config.num_attention_heads
        for layer_type in sorted(set(self.layer_types)):
            cos, sin = rope_tables(config.rope_parameters[layer_type]["rope_theta"], max_len, head_dim)
            self.register_buffer(f"{layer_type}_cos", torch.from_numpy(cos), persistent=False)
            self.register_buffer(f"{layer_type}_sin", torch.from_numpy(sin), persistent=False)
        self.register_buffer("band", torch.from_numpy(band_mask(max_len, config.sliding_window)), persistent=False)

    def forward(self, input_ids: torch.Tensor, attention_mask: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        seq = input_ids.shape[1]
        hidden = self.embeddings(input_ids=input_ids)
        padding = (1.0 - attention_mask.to(hidden.dtype))[:, None, None, :] * MASKED
        masks = {"full_attention": padding, "sliding_attention": padding + self.band[:seq, :seq][None, None]}
        rope = {t: (getattr(self, f"{t}_cos")[:seq][None], getattr(self, f"{t}_sin")[:seq][None])
                for t in set(self.layer_types)}
        for layer, layer_type in zip(self.layers, self.layer_types):
            hidden = layer(hidden, attention_mask=masks[layer_type], position_embeddings=rope[layer_type])
        last = self.final_norm(hidden)
        return last, last[:, 0]


# ---------------------------------------------------------------------------------------------
# Parity helpers shared by the tests and the export CLI's self-check


def sample_batches(tokenizer_json: Path) -> dict[str, tuple[np.ndarray, np.ndarray]]:
    """The three parity inputs: a short row, a padded batch of two, a ~300-token row."""
    from tokenizers import Tokenizer

    tokenizer = Tokenizer.from_file(str(tokenizer_json))

    def ids(text: str, limit: int | None = None) -> list[int]:
        row = tokenizer.encode(text, add_special_tokens=True).ids
        return row if limit is None or len(row) <= limit else row[: limit - 1] + [row[-1]]

    short = ids("How does the memory bank consolidate a workspace?")
    other = ids("Hybrid search fuses keyword and vector rankings with reciprocal rank fusion, "
                "then demotes rows whose embeddings are still pending after a reingest.")
    paragraph = ("The server keeps project-scoped memory in SQLite. Each row carries its source path, "
                 "a timestamp, and an embedding computed by the bundled encoder. ")
    long = ids(paragraph * 40, limit=300)

    def batch(rows: list[list[int]]) -> tuple[np.ndarray, np.ndarray]:
        width = max(len(r) for r in rows)
        input_ids = np.full((len(rows), width), PAD_ID, dtype=np.int64)
        mask = np.zeros((len(rows), width), dtype=np.int64)
        for i, r in enumerate(rows):
            input_ids[i, : len(r)] = r
            mask[i, : len(r)] = 1
        return input_ids, mask

    return {"short": batch([short]), "padded_pair": batch([short, other]), "long_300": batch([long])}


def _cosine(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    a = a.astype(np.float64)
    b = b.astype(np.float64)
    return (a * b).sum(-1) / (np.linalg.norm(a, axis=-1) * np.linalg.norm(b, axis=-1))


def min_row_cosine(a: np.ndarray, b: np.ndarray) -> float:
    """Smallest per-row cosine of two (B, D) arrays."""
    return float(_cosine(a, b).min())


def min_token_cosine(a: np.ndarray, b: np.ndarray, mask: np.ndarray) -> float:
    """Smallest per-token cosine of two (B, S, D) arrays over the unmasked tokens."""
    return float(_cosine(a, b)[mask.astype(bool)].min())
