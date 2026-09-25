"""AneEncoder (ml-ane-transformers layout) vs HF ModernBertModel eager, both fp32 torch.

Needs torch/transformers plus the cached HF weights and the bundled tokenizer, so it skips (never
fails) when any is missing, and is a local gate only (not in build.yml's scripts-harness list).
"""

from __future__ import annotations

import pytest

np = pytest.importorskip("numpy")
torch = pytest.importorskip("torch")
pytest.importorskip("transformers")
pytest.importorskip("tokenizers")

from retrieval_tuning import ane_encoder  # noqa: E402

if not ane_encoder.BUNDLED_DIR.joinpath("tokenizer.json").exists():
    pytest.skip(f"bundled tokenizer missing at {ane_encoder.BUNDLED_DIR}", allow_module_level=True)
if not ane_encoder.hf_weights_cached():
    pytest.skip("granite HF weights not cached", allow_module_level=True)

MAX_LEN = 1024


@pytest.fixture(scope="module")
def hf_model():
    return ane_encoder.load_hf_model()


@pytest.fixture(scope="module")
def ane(hf_model):
    return ane_encoder.AneEncoder(hf_model, MAX_LEN).eval()


@pytest.fixture(scope="module")
def batches():
    return ane_encoder.sample_batches(ane_encoder.BUNDLED_DIR / "tokenizer.json")


@pytest.mark.parametrize("batch", ane_encoder.SAMPLE_NAMES)
def test_ane_encoder_matches_hf_eager(hf_model, ane, batches, batch: str) -> None:
    ids, mask = (torch.from_numpy(a) for a in batches[batch])

    with torch.no_grad():
        want = hf_model(input_ids=ids, attention_mask=mask).last_hidden_state.numpy()
        got_last, got_cls = (t.numpy() for t in ane(ids, mask))

    valid = mask.numpy().astype(bool)
    assert ane_encoder.min_token_cosine(got_last, want, mask.numpy()) >= 0.9999
    assert np.abs(got_last - want)[valid].max() <= 1e-3
    assert ane_encoder.min_row_cosine(got_cls, want[:, 0]) >= 0.9999
    assert np.abs(got_cls - want[:, 0]).max() <= 1e-3


def test_ane_encoder_keeps_the_channel_first_layout_inside_the_layers(ane, batches) -> None:
    ids, mask = (torch.from_numpy(a) for a in batches["short"])
    shapes = []
    hook = ane.layers[1].register_forward_hook(lambda _m, _i, out: shapes.append(tuple(out.shape)))

    with torch.no_grad():
        ane(ids, mask)
    hook.remove()

    assert shapes == [(1, 384, 1, ids.shape[1])]


def test_half_precision_ane_encoder_stays_finite_and_close_on_a_1000_token_row(hf_model, batches) -> None:
    # The ONNX CPU EP runs fp16 graphs through inserted fp32 casts, so an fp16 overflow only shows
    # in real half arithmetic: torch's, or CoreML's on the Neural Engine.
    ids, mask = (torch.from_numpy(a) for a in batches["long_1000"])
    half = ane_encoder.Fp32Outputs(ane_encoder.AneEncoder(hf_model, MAX_LEN)).eval()

    with torch.no_grad():
        want = hf_model(input_ids=ids, attention_mask=mask).last_hidden_state.numpy()
        got_last, got_cls = (t.numpy() for t in half(ids, mask))

    assert np.isfinite(got_last).all()
    assert ane_encoder.min_token_cosine(got_last, want, mask.numpy()) >= 0.999
    assert ane_encoder.min_row_cosine(got_cls, want[:, 0]) >= 0.999
