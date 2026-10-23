"""coreml_cache_key: the shipped ANE graph names its own CoreML compiled-model folder.

Without COREML_CACHE_KEY, ONNX Runtime's CoreML provider keys the compiled model by a hash of the
graph's install path, so every tool update compiles a new ~200 MB copy per bucket. Needs onnx; skips
when it is missing.
"""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

onnx = pytest.importorskip("onnx")
from onnx import TensorProto, helper  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from retrieval_tuning import coreml_cache_key  # noqa: E402


def _graph_with_external_weight(path: Path) -> None:
    weight = TensorProto(name="w", data_type=TensorProto.FLOAT, dims=[2])
    weight.data_location = TensorProto.EXTERNAL
    for key, value in (("location", "model_fp16.onnx_data"), ("offset", "64"), ("length", "8")):
        entry = weight.external_data.add()
        entry.key, entry.value = key, value
    x = helper.make_tensor_value_info("x", TensorProto.FLOAT, [2])
    y = helper.make_tensor_value_info("y", TensorProto.FLOAT, [2])
    graph = helper.make_graph([helper.make_node("Add", ["x", "w"], ["y"])], "g", [x], [y], [weight])
    model = helper.make_model(graph)
    helper.set_model_props(model, {"COREML_CACHE_KEY": "stale", "producer": "export"})
    onnx.save_model(model, str(path))


def _metadata(path: Path) -> dict[str, list[str]]:
    props: dict[str, list[str]] = {}
    for prop in onnx.load(str(path), load_external_data=False).metadata_props:
        props.setdefault(prop.key, []).append(prop.value)
    return props


def test_stamp_sets_exactly_one_cache_key_and_keeps_other_metadata(tmp_path: Path) -> None:
    graph = tmp_path / "model_fp16_ane.onnx"
    _graph_with_external_weight(graph)

    coreml_cache_key.stamp(graph)

    assert _metadata(graph) == {"COREML_CACHE_KEY": [coreml_cache_key.CACHE_KEY], "producer": ["export"]}


def test_stamp_leaves_the_external_weight_references_untouched(tmp_path: Path) -> None:
    graph = tmp_path / "model_fp16_ane.onnx"
    _graph_with_external_weight(graph)

    coreml_cache_key.stamp(graph)

    weight = onnx.load(str(graph), load_external_data=False).graph.initializer[0]
    assert {e.key: e.value for e in weight.external_data} == {
        "location": "model_fp16.onnx_data", "offset": "64", "length": "8"}
    assert not (tmp_path / "model_fp16.onnx_data").exists()


def test_the_key_is_one_ort_accepts() -> None:
    assert coreml_cache_key.CACHE_KEY.isalnum() and len(coreml_cache_key.CACHE_KEY) <= 64
