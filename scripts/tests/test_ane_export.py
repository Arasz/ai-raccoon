"""export_ane_encoder.py: exported granite model dirs vs the product ONNX graphs, on the CPU EP.

fp32 exports are compared with the bundled fp32 model.onnx, fp16 exports with the product's
model_fp16.onnx. Needs torch/transformers/onnx/onnxruntime plus the cached HF weights and both
reference graphs, so it skips (never fails) when any is missing, and is a local gate only (not in
build.yml's stdlib-only scripts-harness list).
"""

from __future__ import annotations

import hashlib
import importlib.util
import json
import subprocess
from pathlib import Path

import pytest

np = pytest.importorskip("numpy")
onnx = pytest.importorskip("onnx")
ort = pytest.importorskip("onnxruntime")
pytest.importorskip("torch")
pytest.importorskip("transformers")

from retrieval_tuning import ane_encoder  # noqa: E402

MODEL_NAME = "model_fp16.onnx"  # the name the harness Engine opens, whatever the dtype
CLI_PATH = Path(__file__).resolve().parent.parent / "retrieval_tuning" / "export_ane_encoder.py"
MAX_LEN = 1024
EXPORTS = [("plain", "fp32"), ("ane", "fp32"), ("plain", "fp16"), ("ane", "fp16")]
MIN_COSINE = {"fp32": 0.9999, "fp16": 0.999}
PRODUCT_MODEL_DIR = Path("src/AiRaccoon/Models/granite-embedding-small-english-r2")


def _product_fp16_model() -> Path:
    """The product model_fp16.onnx whose weights file is present: this checkout's, else the main one's."""
    repo = Path(__file__).resolve().parents[2]
    common = subprocess.run(["git", "-C", str(repo), "rev-parse", "--path-format=absolute", "--git-common-dir"],
                            capture_output=True, text=True, check=False).stdout.strip()
    for root in (repo, Path(common).parent if common else repo):
        model = root / PRODUCT_MODEL_DIR / "model_fp16.onnx"
        if model.with_name("model_fp16.onnx_data").exists():
            return model
    return repo / PRODUCT_MODEL_DIR / "model_fp16.onnx"


PRODUCT_FP16 = _product_fp16_model()

if not ane_encoder.BUNDLED_DIR.joinpath("model.onnx").exists():
    pytest.skip(f"bundled model missing at {ane_encoder.BUNDLED_DIR}", allow_module_level=True)
if not PRODUCT_FP16.with_name("model_fp16.onnx_data").exists():
    pytest.skip(f"product fp16 weights missing next to {PRODUCT_FP16}", allow_module_level=True)
if not ane_encoder.hf_weights_cached():
    pytest.skip("granite HF weights not cached", allow_module_level=True)


def _load_cli():
    spec = importlib.util.spec_from_file_location("export_ane_encoder", CLI_PATH)
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


def _session(model_path: Path) -> "ort.InferenceSession":
    return ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])


def _run(session: "ort.InferenceSession", ids: "np.ndarray", mask: "np.ndarray") -> tuple["np.ndarray", "np.ndarray"]:
    last, cls = session.run(["last_hidden_state", "sentence_embedding"], {"input_ids": ids, "attention_mask": mask})
    return last, cls


@pytest.fixture(scope="module")
def references() -> dict[str, "ort.InferenceSession"]:
    return {"fp32": _session(ane_encoder.BUNDLED_DIR / "model.onnx"), "fp16": _session(PRODUCT_FP16)}


@pytest.fixture(scope="module")
def batches() -> dict[str, tuple["np.ndarray", "np.ndarray"]]:
    return ane_encoder.sample_batches(ane_encoder.BUNDLED_DIR / "tokenizer.json")


@pytest.fixture(scope="module", params=EXPORTS, ids=["-".join(e) for e in EXPORTS])
def exported(request, tmp_path_factory) -> tuple[str, str, Path]:
    layout, dtype = request.param
    out = tmp_path_factory.mktemp(f"export-{layout}-{dtype}")
    _load_cli().export_model_dir(layout, out, MAX_LEN, dtype)
    return layout, dtype, out


# ---------------------------------------------------------------------------------------------
# Parity with the bundled graph on the CPU EP


@pytest.mark.parametrize("batch", ane_encoder.SAMPLE_NAMES)
def test_exported_graph_matches_the_product_graph(exported, references, batches, batch: str) -> None:
    _, dtype, out = exported
    ids, mask = batches[batch]

    want_last, want_cls = _run(references[dtype], ids, mask)
    got_last, got_cls = _run(_session(out / MODEL_NAME), ids, mask)

    assert ane_encoder.min_row_cosine(got_cls, want_cls) >= MIN_COSINE[dtype]
    assert ane_encoder.min_token_cosine(got_last, want_last, mask) >= MIN_COSINE[dtype]


def test_exported_graph_has_no_contrib_ops(exported) -> None:
    *_, out = exported
    model = onnx.load(str(out / MODEL_NAME), load_external_data=False)

    domains = {node.domain for node in model.graph.node}

    assert domains <= {"", "ai.onnx"}, f"non-standard op domains in graph: {domains}"


def test_exported_inputs_and_outputs_match_the_bundled_graph(exported) -> None:
    *_, out = exported

    def io(path: Path) -> tuple[list, list]:
        graph = onnx.load(str(path), load_external_data=False).graph
        sig = lambda v: (v.name, v.type.tensor_type.elem_type,  # noqa: E731
                         [d.dim_param or d.dim_value for d in v.type.tensor_type.shape.dim])
        return [sig(v) for v in graph.input], [sig(v) for v in graph.output]

    assert io(out / MODEL_NAME) == io(ane_encoder.BUNDLED_DIR / "model.onnx")


def test_exported_weights_are_stored_in_the_requested_dtype(exported) -> None:
    *_, dtype, out = exported
    model = onnx.load(str(out / MODEL_NAME), load_external_data=False)
    want = {"fp32": onnx.TensorProto.FLOAT, "fp16": onnx.TensorProto.FLOAT16}[dtype]

    matrices = [init for init in model.graph.initializer if len(init.dims) >= 2]

    assert matrices
    assert {init.data_type for init in matrices} == {want}


# ---------------------------------------------------------------------------------------------
# Model dir: manifest hashes and harness compatibility


def test_manifest_sha256_matches_every_listed_file(exported) -> None:
    *_, out = exported
    manifest = json.loads((out / "ai-raccoon.manifest.json").read_text())

    listed = manifest["onnx"]["files"] + manifest["tokenizer"]["files"] + manifest["provenanceFiles"]

    assert {f["path"] for f in manifest["onnx"]["files"]} == {MODEL_NAME, f"{MODEL_NAME}_data"}
    assert sorted(p.name for p in out.glob("*.onnx*")) == [MODEL_NAME, f"{MODEL_NAME}_data"]
    assert not (out / MODEL_NAME).is_symlink()
    for entry in listed:
        actual = hashlib.sha256((out / entry["path"]).read_bytes()).hexdigest()
        assert entry["sha256"] == actual, entry["path"]


def test_harness_engine_embeds_a_row_from_the_exported_dir(exported) -> None:
    from retrieval_tuning.chunk_window import Engine

    *_, out = exported
    engine = Engine(out, threads=1, device="cpu")

    vector, tokens, _, _ = engine.embed("hello world")

    assert vector.shape == (384,)
    assert tokens > 2
    assert abs(float(np.linalg.norm(vector)) - 1.0) < 1e-5


def test_export_refuses_to_write_into_the_bundled_models_root(tmp_path) -> None:
    with pytest.raises(ValueError):
        _load_cli().export_model_dir("plain", ane_encoder.BUNDLED_DIR, MAX_LEN, "fp16")

def test_ane_graph_has_no_ops_the_coreml_ep_leaves_on_the_cpu(exported) -> None:
    # Measured on ORT 1.30: the CoreML EP places Neg (HF rotate_half) and Einsum on the CPU EP,
    # splitting the graph into one partition per run of claimed nodes.
    layout, _, out = exported
    if layout != "ane":
        pytest.skip("the plain layout keeps HF's rotate_half Neg by design")
    model = onnx.load(str(out / MODEL_NAME), load_external_data=False)

    ops = {node.op_type for node in model.graph.node}

    assert not ops & {"Einsum", "Neg"}


def test_re_export_into_the_same_dir_does_not_grow_the_weights_file(exported) -> None:
    # onnx.save_model appends external data to an existing file: a second export into the same
    # dir left the first run's bytes in front of the second's, doubling the file on disk.
    layout, dtype, out = exported
    data = out / f"{MODEL_NAME}_data"
    size_before = data.stat().st_size

    _load_cli().export_model_dir(layout, out, MAX_LEN, dtype)

    assert data.stat().st_size == size_before
