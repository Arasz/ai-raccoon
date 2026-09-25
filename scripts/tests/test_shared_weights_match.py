"""shared_weights.match_weights on synthetic tensors: no HF weights or product data file needed.

The product graph stores q/k/v as separate [in, out] MatMul weights; the ANE Wqkv conv stacks
their transposes plus rotate_half of q and k. Each block must match a whole product tensor.
"""

from __future__ import annotations

import pytest

np = pytest.importorskip("numpy")
onnx = pytest.importorskip("onnx")
pytest.importorskip("torch")

from onnx import helper, numpy_helper  # noqa: E402

from retrieval_tuning import ane_encoder, shared_weights  # noqa: E402
from retrieval_tuning.shared_weights import Part  # noqa: E402

HEADS, HEAD_DIM = 2, 4
HIDDEN = HEADS * HEAD_DIM


def _model(tensors: dict[str, "np.ndarray"]) -> "onnx.ModelProto":
    inits = [numpy_helper.from_array(a, n) for n, a in tensors.items()]
    return helper.make_model(helper.make_graph([], "g", [], [], initializer=inits))


def test_fused_wqkv_blocks_match_the_products_separate_projections() -> None:
    rng = np.random.default_rng(7)
    q, k, v = (rng.standard_normal((HIDDEN, HIDDEN)).astype(np.float16) for _ in range(3))  # [in, out]
    rotate = ane_encoder.rotate_half_matrix(HEADS, HEAD_DIM).numpy()
    rotated = [(rotate @ w.T.astype(np.float32)).astype(np.float16) for w in (q, k)]
    wqkv = np.concatenate([q.T, k.T, v.T, *rotated])[:, :, None, None]
    product = _model({"q_proj": q, "k_proj": k, "v_proj": v})
    ane = _model({"Wqkv": wqkv})

    plan = shared_weights.match_weights(ane, product, HEADS)

    assert plan["Wqkv"] == [Part("q_proj", True), Part("k_proj", True), Part("v_proj", True),
                            Part("q_proj", True, True), Part("k_proj", True, True)]


def test_a_block_no_product_tensor_explains_is_reported() -> None:
    rng = np.random.default_rng(8)
    q = rng.standard_normal((HIDDEN, HIDDEN)).astype(np.float16)
    stray = rng.standard_normal((HIDDEN, HIDDEN)).astype(np.float16)
    ane = _model({"Wqkv": np.concatenate([q.T, stray])[:, :, None, None]})

    with pytest.raises(ValueError, match="Wqkv"):
        shared_weights.match_weights(ane, _model({"q_proj": q}), HEADS)
