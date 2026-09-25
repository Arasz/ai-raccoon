"""export_ane_encoder.py --share-weights-with: an ANE-layout graph that reads the product's own weights file.

The shared graph must reference model_fp16.onnx_data at the product graph's own offsets, compute
what the standalone fp16 ANE export computes, and leave ORT's basic constant folding nothing but
folded weights. Needs torch/transformers/onnx/onnxruntime, the cached HF weights and the product
model_fp16.onnx_data, so it skips (never fails) when any is missing; a local gate only.
"""

from __future__ import annotations

import hashlib
import importlib.util
import subprocess
from pathlib import Path

import pytest

np = pytest.importorskip("numpy")
onnx = pytest.importorskip("onnx")
ort = pytest.importorskip("onnxruntime")
pytest.importorskip("torch")
pytest.importorskip("transformers")

from onnx import numpy_helper  # noqa: E402
from onnx.external_data_helper import uses_external_data  # noqa: E402

from retrieval_tuning import ane_encoder, shared_weights  # noqa: E402

MODEL_NAME = "model_fp16.onnx"
DATA_NAME = "model_fp16.onnx_data"
CLI_PATH = Path(__file__).resolve().parent.parent / "retrieval_tuning" / "export_ane_encoder.py"
MAX_LEN = 1024
MAX_NEW_BYTES = 15 * 1024 * 1024
FOLDABLE_OPS = {"Transpose", "Gather", "Cast", "Mul", "Concat", "Reshape"}
PRODUCT_MODEL_DIR = Path("src/AiRaccoon/Models/granite-embedding-small-english-r2")


def _product_dir() -> Path:
    """The product model dir whose weights file is present: this checkout's, else the main one's."""
    repo = Path(__file__).resolve().parents[2]
    common = subprocess.run(["git", "-C", str(repo), "rev-parse", "--path-format=absolute", "--git-common-dir"],
                            capture_output=True, text=True, check=False).stdout.strip()
    for root in (repo, Path(common).parent if common else repo):
        if (root / PRODUCT_MODEL_DIR / DATA_NAME).exists():
            return root / PRODUCT_MODEL_DIR
    return repo / PRODUCT_MODEL_DIR


PRODUCT_DIR = _product_dir()

if not ane_encoder.BUNDLED_DIR.joinpath("tokenizer.json").exists():
    pytest.skip(f"bundled model missing at {ane_encoder.BUNDLED_DIR}", allow_module_level=True)
if not (PRODUCT_DIR / DATA_NAME).exists():
    pytest.skip(f"product fp16 weights missing in {PRODUCT_DIR}", allow_module_level=True)
if not ane_encoder.hf_weights_cached():
    pytest.skip("granite HF weights not cached", allow_module_level=True)


def _load_cli():
    spec = importlib.util.spec_from_file_location("export_ane_encoder", CLI_PATH)
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def _session(model_path: Path, **options) -> "ort.InferenceSession":
    so = ort.SessionOptions()
    for name, value in options.items():
        setattr(so, name, value)
    return ort.InferenceSession(str(model_path), so, providers=["CPUExecutionProvider"])


def _run(session, ids, mask):
    return session.run(["last_hidden_state", "sentence_embedding"], {"input_ids": ids, "attention_mask": mask})


@pytest.fixture(scope="module")
def product_data_sha() -> str:
    return _sha256(PRODUCT_DIR / DATA_NAME)


@pytest.fixture(scope="module")
def standalone(tmp_path_factory) -> Path:
    out = tmp_path_factory.mktemp("standalone")
    _load_cli().export_model_dir("ane", out, MAX_LEN, "fp16")
    return out


@pytest.fixture(scope="module")
def shared(tmp_path_factory, product_data_sha) -> Path:
    out = tmp_path_factory.mktemp("shared")
    _load_cli().export_model_dir("ane", out, MAX_LEN, "fp16", share_weights_with=PRODUCT_DIR)
    return out


@pytest.fixture(scope="module")
def batches():
    return ane_encoder.sample_batches(ane_encoder.BUNDLED_DIR / "tokenizer.json")


# ---------------------------------------------------------------------------------------------
# AC1: the shared graph reads the product weights file, at the product's own offsets


def _external_entries(model) -> dict[str, dict[str, str]]:
    return {init.name: {e.key: e.value for e in init.external_data}
            for init in model.graph.initializer if uses_external_data(init)}


def test_every_external_reference_is_the_product_graphs_own_entry(shared) -> None:
    got = _external_entries(onnx.load(str(shared / MODEL_NAME), load_external_data=False))
    want = _external_entries(onnx.load(str(PRODUCT_DIR / MODEL_NAME), load_external_data=False))

    assert got, "the shared graph references no external data"
    for name, entry in got.items():
        assert entry["location"] == DATA_NAME, name
        assert name in want, f"{name} is not a product initializer"
        assert (entry["offset"], entry["length"]) == (want[name]["offset"], want[name]["length"]), name


def test_the_data_file_is_the_product_file_and_stays_byte_unchanged(shared, standalone, batches,
                                                                    product_data_sha) -> None:
    ids, mask = batches["short"]
    _run(_session(shared / MODEL_NAME), ids, mask)

    assert _sha256(shared / DATA_NAME) == product_data_sha
    assert _sha256(PRODUCT_DIR / DATA_NAME) == product_data_sha


def test_the_shared_graph_adds_at_most_15_mb(shared) -> None:
    new_files = [p.name for p in shared.glob("*.onnx*") if p.name != DATA_NAME]

    assert new_files == [MODEL_NAME]
    assert (shared / MODEL_NAME).stat().st_size <= MAX_NEW_BYTES


def test_an_ane_weight_with_no_product_match_fails_loudly(standalone) -> None:
    ane = onnx.load(str(standalone / MODEL_NAME))
    product = onnx.load(str(PRODUCT_DIR / MODEL_NAME))
    target = next(init for init in ane.graph.initializer if init.name.endswith("layers.3.Wo.weight"))
    array = numpy_helper.to_array(target).copy()
    array.flat[7] += np.float16(0.5)
    target.CopyFrom(numpy_helper.from_array(array, target.name))

    with pytest.raises(ValueError, match=r"layers\.3\.Wo\.weight"):
        shared_weights.match_weights(ane, product, heads=12)


# ---------------------------------------------------------------------------------------------
# AC2: same math as the standalone export, product parity kept


@pytest.mark.parametrize("batch", ane_encoder.SAMPLE_NAMES)
def test_shared_graph_matches_the_standalone_export(shared, standalone, batches, batch: str) -> None:
    ids, mask = batches[batch]

    want_last, want_cls = _run(_session(standalone / MODEL_NAME), ids, mask)
    got_last, got_cls = _run(_session(shared / MODEL_NAME), ids, mask)

    assert ane_encoder.min_row_cosine(got_cls, want_cls) >= 0.99999
    assert ane_encoder.min_token_cosine(got_last, want_last, mask) >= 0.99999


@pytest.mark.parametrize("batch", ane_encoder.SAMPLE_NAMES)
def test_shared_graph_matches_the_product_graph(shared, batches, batch: str) -> None:
    ids, mask = batches[batch]

    want_last, want_cls = _run(_session(PRODUCT_DIR / MODEL_NAME), ids, mask)
    got_last, got_cls = _run(_session(shared / MODEL_NAME), ids, mask)

    assert ane_encoder.min_row_cosine(got_cls, want_cls) >= 0.999
    assert ane_encoder.min_token_cosine(got_last, want_last, mask) >= 0.999


# ---------------------------------------------------------------------------------------------
# AC3: ORT's basic level folds every weight-rebuild subgraph before partitioning


def _constant_only_nodes(model) -> list[str]:
    """Foldable-op nodes computed from initializers and constants alone, through any chain of such nodes."""
    constants = {init.name for init in model.graph.initializer}
    found = []
    for node in model.graph.node:  # saved graphs are topologically sorted
        if node.op_type == "Constant" or (node.input and all(i in constants for i in node.input if i)):
            constants.update(node.output)
            if node.op_type in FOLDABLE_OPS:
                found.append(f"{node.op_type}:{node.name}")
    return found


def test_basic_optimization_folds_every_weight_rebuild_subgraph(shared, tmp_path) -> None:
    # Only nodes of the exported graph count: the CPU EP adds its own fp16->fp32 Casts on weights
    # after partitioning (InsertedPrecisionFreeCast_*), which no other EP sees.
    before = onnx.load(str(shared / MODEL_NAME), load_external_data=False)
    optimized = tmp_path / "optimized.onnx"

    _session(shared / MODEL_NAME, graph_optimization_level=ort.GraphOptimizationLevel.ORT_ENABLE_BASIC,
             optimized_model_filepath=str(optimized))
    after = onnx.load(str(optimized), load_external_data=False)
    exported = {node.name for node in before.graph.node}

    assert _constant_only_nodes(before), "the shared graph has no weight-rebuild nodes to fold"
    assert [n for n in _constant_only_nodes(after) if n.split(":", 1)[1] in exported] == []


# ---------------------------------------------------------------------------------------------
# Harness compatibility


def test_harness_engine_embeds_a_row_from_the_shared_dir(shared) -> None:
    from retrieval_tuning.chunk_window import Engine

    engine = Engine(shared, threads=1, device="cpu")

    vector, tokens, _, _ = engine.embed("hello world")

    assert vector.shape == (384,)
    assert tokens > 2
    assert abs(float(np.linalg.norm(vector)) - 1.0) < 1e-5


def test_sharing_refuses_to_write_into_the_product_dir() -> None:
    with pytest.raises(ValueError):
        _load_cli().export_model_dir("ane", PRODUCT_DIR, MAX_LEN, "fp16", share_weights_with=PRODUCT_DIR)


def test_sharing_refuses_the_plain_layout(tmp_path) -> None:
    with pytest.raises(ValueError, match="ane"):
        _load_cli().export_model_dir("plain", tmp_path, MAX_LEN, "fp16", share_weights_with=PRODUCT_DIR)
