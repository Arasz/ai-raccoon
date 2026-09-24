"""Rewrites granite's MultiHeadAttention(attention_bias) into standard ops and folds Range, so the
onnxruntime MLX plugin EP can claim the whole graph as one fused subgraph (ADR-0110).

Every original initializer keeps pointing at the unchanged model_fp16.onnx_data — this only
rewrites node topology, never touches external-data weights. Measured against the plugin: the
rewritten graph claims 547/547 nodes in one fused subgraph, at cosine 0.999999 against the
original CPU output (docs/work/2026-09-24-onnx-runtime-providers-gpu-mlx.md F17/F27).

Usage: python3 scripts/src/make_mlx_graph.py <model_fp16.onnx> <out model_fp16_mlx.onnx>
"""

from __future__ import annotations

import sys

import numpy as np
import onnx
from onnx import helper, numpy_helper

_SPLIT_HEADS_SHAPE = np.array([0, 0, 12, 32], dtype=np.int64)
_MERGE_HEADS_SHAPE = np.array([0, 0, 384], dtype=np.int64)


def _shared_constants() -> list[onnx.TensorProto]:
    return [
        numpy_helper.from_array(_SPLIT_HEADS_SHAPE, "mlx/shape_split"),
        numpy_helper.from_array(_MERGE_HEADS_SHAPE, "mlx/shape_merge"),
        numpy_helper.from_array(np.array([0], dtype=np.int64), "mlx/axes0"),
        numpy_helper.from_array(np.array(0, dtype=np.int64), "mlx/axis0"),
        numpy_helper.from_array(np.array(1, dtype=np.int64), "mlx/one"),
    ]


def _rewrite_attention(node: onnx.NodeProto, consts: list[onnx.TensorProto]) -> list[onnx.NodeProto]:
    """MultiHeadAttention(Q, K, V, attention_bias) -> Reshape/Transpose/MatMul/Mul/Add/Softmax/MatMul,
    the shape the MLX plugin's `Attention`-lowering claims (F15-F17)."""
    q, k, v, bias, y = node.input[0], node.input[1], node.input[2], node.input[5], node.output[0]
    attrs = {a.name: helper.get_attribute_value(a) for a in node.attribute}
    assert attrs["num_heads"] == 12, f"{node.name}: expected 12 heads, got {attrs['num_heads']}"

    def t(suffix: str) -> str:
        return f"{node.name}/mlx/{suffix}"

    consts.append(numpy_helper.from_array(np.array(attrs["scale"], dtype=np.float16), t("scale")))
    return [
        helper.make_node("Reshape", [q, "mlx/shape_split"], [t("q4")]),
        helper.make_node("Transpose", [t("q4")], [t("qh")], perm=[0, 2, 1, 3]),
        helper.make_node("Reshape", [k, "mlx/shape_split"], [t("k4")]),
        helper.make_node("Transpose", [t("k4")], [t("kt")], perm=[0, 2, 3, 1]),
        helper.make_node("Reshape", [v, "mlx/shape_split"], [t("v4")]),
        helper.make_node("Transpose", [t("v4")], [t("vh")], perm=[0, 2, 1, 3]),
        helper.make_node("MatMul", [t("qh"), t("kt")], [t("s")]),
        helper.make_node("Mul", [t("s"), t("scale")], [t("ss")]),
        helper.make_node("Add", [t("ss"), bias], [t("sb")]),
        helper.make_node("Softmax", [t("sb")], [t("pr")], axis=-1),
        helper.make_node("MatMul", [t("pr"), t("vh")], [t("o")]),
        helper.make_node("Transpose", [t("o")], [t("ot")], perm=[0, 2, 1, 3]),
        helper.make_node("Reshape", [t("ot"), "mlx/shape_merge"], [y]),
    ]


def _rewrite_range(node: onnx.NodeProto) -> list[onnx.NodeProto]:
    """Range(0, limit, 1) -> CumSum(ConstantOfShape(Unsqueeze(limit), 1), axis 0) - 1: the plugin
    refuses a runtime-bounds Range, so this is the last node standing between it and one fused
    subgraph (F27)."""
    _, limit, _ = node.input
    prefix = node.name
    return [
        helper.make_node("Unsqueeze", [limit, "mlx/axes0"], [prefix + "/mlx/len1d"]),
        helper.make_node("ConstantOfShape", [prefix + "/mlx/len1d"], [prefix + "/mlx/ones"],
                          value=numpy_helper.from_array(np.array([1], dtype=np.int64))),
        helper.make_node("CumSum", [prefix + "/mlx/ones", "mlx/axis0"], [prefix + "/mlx/cum"]),
        helper.make_node("Sub", [prefix + "/mlx/cum", "mlx/one"], [node.output[0]]),
    ]


def rewrite(src: str, out: str) -> onnx.ModelProto:
    """Loads src, rewrites MultiHeadAttention and Range in place, and saves to out. Never loads or
    converts external data, so the saved model keeps referencing src's own .onnx_data file."""
    model = onnx.load(src, load_external_data=False)
    graph = model.graph
    consts = _shared_constants()

    rewritten: list[onnx.NodeProto] = []
    for node in graph.node:
        if node.op_type == "MultiHeadAttention":
            rewritten.extend(_rewrite_attention(node, consts))
        elif node.op_type == "Range":
            rewritten.extend(_rewrite_range(node))
        else:
            rewritten.append(node)

    del graph.node[:]
    graph.node.extend(rewritten)
    graph.initializer.extend(consts)
    onnx.save(model, out)
    return model


def report(out: str) -> None:
    model = onnx.load(out, load_external_data=False)
    external = {
        entry.value
        for initializer in model.graph.initializer
        for entry in initializer.external_data
        if entry.key == "location"
    }
    mha = sum(node.op_type == "MultiHeadAttention" for node in model.graph.node)
    ranges = sum(node.op_type == "Range" for node in model.graph.node)
    print(f"nodes {len(model.graph.node)} MHA {mha} Range {ranges} external data files {external}")


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: make_mlx_graph.py <model_fp16.onnx> <out model_fp16_mlx.onnx>", file=sys.stderr)
        return 2

    src, out = argv
    rewrite(src, out)
    report(out)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
