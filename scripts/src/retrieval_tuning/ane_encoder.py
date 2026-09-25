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

import copy
import os
from pathlib import Path

import numpy as np
import torch

HF_REPO = "ibm-granite/granite-embedding-small-english-r2"
BUNDLED_DIR = Path.home() / ".ai-raccoon" / "models" / "onnx-community__granite-embedding-small-english-r2-ONNX"
HF_CACHE_DIR = Path.home() / ".cache" / "huggingface" / "hub" / "models--ibm-granite--granite-embedding-small-english-r2"

MASKED = -1e4  # fp16-representable, unlike finfo.min; exp(-1e4) is exactly 0 in fp16 and fp32
PAD_ID = 50283
SAMPLE_NAMES = ("short", "padded_pair", "long_300", "long_1000")
NORM_SCALE = 64.0  # ChannelNorm squares x/64, not x: residual channels reach ~1800, so x*x overflows fp16


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
# ANE layout: (B, C, 1, S) tensors, 1x1 convs, channel-dim norms, per-head attention


def _conv(weight: torch.Tensor) -> torch.nn.Conv2d:
    """A bias-free 1x1 Conv2d carrying a Linear's [out, in] weight."""
    conv = torch.nn.Conv2d(weight.shape[1], weight.shape[0], kernel_size=1, bias=False)
    conv.weight = torch.nn.Parameter(weight.detach().clone()[:, :, None, None], requires_grad=False)
    return conv


def rotate_half_matrix(heads: int, head_dim: int) -> torch.Tensor:
    """R with R @ x == HF rotate_half(x) applied per head over a heads*head_dim channel vector."""
    half = head_dim // 2
    block = torch.zeros(head_dim, head_dim)
    block[torch.arange(half), torch.arange(half) + half] = -1.0
    block[torch.arange(half) + half, torch.arange(half)] = 1.0
    return torch.block_diag(*[block] * heads)


class ChannelNorm(torch.nn.Module):
    """Bias-free LayerNorm over dim 1 of a (B, C, 1, S) tensor, as explicit mean/variance ops."""

    def __init__(self, weight: torch.Tensor, eps: float) -> None:
        super().__init__()
        self.weight = torch.nn.Parameter(weight.detach().clone().view(1, -1, 1, 1), requires_grad=False)
        self.eps = eps

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        # x/sqrt(var+eps) == (x/s)/sqrt(var/s^2 + eps/s^2): same result, squares stay inside fp16 range.
        scaled = (x - x.mean(dim=1, keepdim=True)) * (1.0 / NORM_SCALE)
        var = (scaled * scaled).mean(dim=1, keepdim=True)
        return scaled * torch.rsqrt(var + self.eps / NORM_SCALE**2) * self.weight


class AneLayer(torch.nn.Module):
    """One ModernBERT encoder layer on (B, C, 1, S); rotate_half lives in extra Wqkv output channels."""

    def __init__(self, hf_layer, heads: int, eps: float) -> None:
        super().__init__()
        hidden = hf_layer.attn.Wo.weight.shape[0]
        self.heads = heads
        self.head_dim = hidden // heads
        self.scale = self.head_dim ** -0.5
        self.hidden = hidden
        self.attn_norm = (ChannelNorm(hf_layer.attn_norm.weight, eps)
                          if isinstance(hf_layer.attn_norm, torch.nn.LayerNorm) else torch.nn.Identity())
        wq, wk, wv = hf_layer.attn.Wqkv.weight.split(hidden, dim=0)
        rotate = rotate_half_matrix(heads, self.head_dim)
        self.Wqkv = _conv(torch.cat([wq, wk, wv, rotate @ wq, rotate @ wk], dim=0))
        self.Wo = _conv(hf_layer.attn.Wo.weight)
        self.mlp_norm = ChannelNorm(hf_layer.mlp_norm.weight, eps)
        self.Wi = _conv(hf_layer.mlp.Wi.weight)
        self.mlp_Wo = _conv(hf_layer.mlp.Wo.weight)

    def forward(self, x: torch.Tensor, cos: torch.Tensor, sin: torch.Tensor, mask: torch.Tensor) -> torch.Tensor:
        q, k, v, q_rot, k_rot = self.Wqkv(self.attn_norm(x)).split(self.hidden, dim=1)
        q = q * cos + q_rot * sin
        k = k * cos + k_rot * sin
        # ml-ane-transformers' per-head einsums as MatMul: the CoreML EP does not claim Einsum.
        # (B, C, 1, S) -> (B, 1, C, S) is a free reshape; only K needs a real transpose.
        batch, seq = x.shape[0], x.shape[3]
        heads_q = q.reshape(batch, 1, self.hidden, seq).split(self.head_dim, dim=2)  # (B, 1, d, Sq)
        heads_k = k.transpose(1, 3).split(self.head_dim, dim=3)                     # (B, Sk, 1, d)
        heads_v = v.reshape(batch, 1, self.hidden, seq).split(self.head_dim, dim=2)  # (B, 1, d, Sk)
        outputs = []
        for qh, kh, vh in zip(heads_q, heads_k, heads_v):
            weights = torch.matmul(kh.transpose(1, 2), qh) * self.scale + mask     # (B, 1, Sk, Sq)
            outputs.append(torch.matmul(vh, weights.softmax(dim=2)))                # (B, 1, d, Sq)
        attn = torch.cat(outputs, dim=2).reshape(batch, self.hidden, 1, seq)
        x = x + self.Wo(attn)
        gate_in, gate = self.Wi(self.mlp_norm(x)).chunk(2, dim=1)
        return x + self.mlp_Wo(torch.nn.functional.gelu(gate_in) * gate)


class AneEncoder(torch.nn.Module):
    """ModernBERT in the ml-ane-transformers layout, loaded from an HF ModernBertModel."""

    def __init__(self, hf_model, max_len: int) -> None:
        super().__init__()
        config = hf_model.config
        heads = config.num_attention_heads
        head_dim = config.hidden_size // heads
        self.embeddings = copy.deepcopy(hf_model.embeddings)  # a later .half() must not touch hf_model
        self.layers = torch.nn.ModuleList(AneLayer(layer, heads, config.norm_eps) for layer in hf_model.layers)
        self.layer_types = list(config.layer_types)
        self.final_norm = ChannelNorm(hf_model.final_norm.weight, config.norm_eps)
        for layer_type in sorted(set(self.layer_types)):
            cos, sin = rope_tables(config.rope_parameters[layer_type]["rope_theta"], max_len, head_dim)
            for name, table in (("cos", cos), ("sin", sin)):
                channels = np.tile(table, (1, heads)).T[None, :, None, :]  # [1, heads*head_dim, 1, L]
                self.register_buffer(f"{layer_type}_{name}", torch.from_numpy(np.ascontiguousarray(channels)),
                                     persistent=False)
        band = band_mask(max_len, config.sliding_window)
        self.register_buffer("band", torch.from_numpy(band)[None, None], persistent=False)  # [1, 1, Lk, Lq]

    def forward(self, input_ids: torch.Tensor, attention_mask: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        seq = input_ids.shape[1]
        hidden = self.embeddings(input_ids=input_ids)             # (B, S, C)
        x = hidden.transpose(1, 2).unsqueeze(2)                   # (B, C, 1, S)
        padding = ((1.0 - attention_mask.to(x.dtype)) * MASKED)[:, None, :, None]  # (B, 1, Sk, 1)
        masks = {"full_attention": padding, "sliding_attention": padding + self.band[:, :, :seq, :seq]}
        rope = {t: (getattr(self, f"{t}_cos")[..., :seq], getattr(self, f"{t}_sin")[..., :seq])
                for t in set(self.layer_types)}
        for layer, layer_type in zip(self.layers, self.layer_types):
            x = layer(x, *rope[layer_type], masks[layer_type])
        last = self.final_norm(x).squeeze(2).transpose(1, 2)      # (B, S, C)
        return last, last[:, 0]


class Fp32Outputs(torch.nn.Module):
    """Runs an encoder in half precision but returns float32, like the product's model_fp16.onnx."""

    def __init__(self, encoder: torch.nn.Module) -> None:
        super().__init__()
        self.encoder = encoder.half()

    def forward(self, input_ids: torch.Tensor, attention_mask: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        last, cls = self.encoder(input_ids, attention_mask)
        return last.float(), cls.float()


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
    assert len(ids(paragraph * 150)) > 1000

    def batch(rows: list[list[int]]) -> tuple[np.ndarray, np.ndarray]:
        width = max(len(r) for r in rows)
        input_ids = np.full((len(rows), width), PAD_ID, dtype=np.int64)
        mask = np.zeros((len(rows), width), dtype=np.int64)
        for i, r in enumerate(rows):
            input_ids[i, : len(r)] = r
            mask[i, : len(r)] = 1
        return input_ids, mask

    return {"short": batch([short]), "padded_pair": batch([short, other]), "long_300": batch([long]),
            "long_1000": batch([ids(paragraph * 150, limit=1000)])}


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
