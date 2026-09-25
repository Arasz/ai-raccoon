"""Rewrite an exported graph to read its weights from the product's model_fp16.onnx_data.

Every weight initializer of the exported graph is matched, by value, to a transform of some product
initializer: as-is, transposed, or transposed with rotate_half's per-head row permutation and sign
flips applied, with row blocks concatenated (a fused Wqkv or Wi). The rewritten graph drops its own
copies, carries the product's initializer entries unchanged (same external-data offsets), and
rebuilds each weight with constant-only Transpose/Gather/Mul/Concat/Reshape nodes that ORT folds at
its basic optimization level. The graph must sit next to the product's weights file.
"""

from __future__ import annotations

import hashlib
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import onnx
from onnx import helper, numpy_helper
from onnx.external_data_helper import uses_external_data

from retrieval_tuning.ane_encoder import rotate_half_matrix

PERM_PREFIX = "shared_weights/rotate_half_perm_"
SIGN_PREFIX = "shared_weights/rotate_half_sign_"


@dataclass(frozen=True)
class Part:
    """One row block of an exported weight: a product initializer, optionally transposed and rotated."""

    source: str
    transpose: bool = False
    rotate: bool = False


def _digest(array: np.ndarray) -> str:
    canonical = np.ascontiguousarray(array) + array.dtype.type(0)  # -0 and +0 hash alike
    return hashlib.sha256(canonical.tobytes()).hexdigest()


def rotate_rows(rows: int, heads: int) -> tuple[np.ndarray, np.ndarray]:
    """(perm, sign) with rotate_half_matrix(heads, rows // heads) @ W == W[perm] * sign[:, None]."""
    matrix = rotate_half_matrix(heads, rows // heads).numpy()
    perm = np.abs(matrix).argmax(axis=1)
    return perm.astype(np.int64), matrix[np.arange(rows), perm]


def _as_matrix(array: np.ndarray) -> np.ndarray:
    """A conv weight [O, I, 1, 1] or channel vector [1, C, 1, 1] with its unit dims dropped."""
    squeezed = array.reshape([d for d in array.shape if d != 1]) if array.ndim > 2 else array
    return squeezed if squeezed.ndim else array.reshape(-1)


def _candidates(product: onnx.ModelProto, heads: int) -> dict[tuple, list[Part]]:
    index: dict[tuple, list[Part]] = {}

    def add(array: np.ndarray, part: Part) -> None:
        index.setdefault((array.shape, _digest(array)), []).append(part)

    for init in product.graph.initializer:
        array = numpy_helper.to_array(init)
        if array.dtype.kind != "f":
            continue
        add(array, Part(init.name))
        if array.ndim != 2:
            continue
        transposed = array.T
        add(transposed, Part(init.name, transpose=True))
        rows = transposed.shape[0]
        if rows % heads == 0 and (rows // heads) % 2 == 0:
            perm, sign = rotate_rows(rows, heads)
            add(transposed[perm] * sign.astype(array.dtype)[:, None], Part(init.name, transpose=True, rotate=True))
    return index


def match_weights(ane: onnx.ModelProto, product: onnx.ModelProto, heads: int) -> dict[str, list[Part]]:
    """Every float initializer of ane (data loaded) as row blocks of product initializers; ValueError if any has none."""
    index = _candidates(product, heads)
    plan: dict[str, list[Part]] = {}
    failures: list[str] = []
    for init in ane.graph.initializer:
        array = numpy_helper.to_array(init)
        if array.dtype.kind != "f":
            continue
        matrix = _as_matrix(array)
        sizes = sorted({shape[0] for shape, _ in index if shape[1:] == matrix.shape[1:]}, reverse=True)
        parts: list[Part] = []
        row = 0
        while row < matrix.shape[0]:
            found = [(n, index[key]) for n in sizes if row + n <= matrix.shape[0]
                     for key in [((n, *matrix.shape[1:]), _digest(matrix[row:row + n]))] if key in index]
            if not found:
                failures.append(f"{init.name} {list(array.shape)}: rows {row}.. match no product initializer")
                break
            n, matches = found[0]
            if len(matches) > 1:
                failures.append(f"{init.name} {list(array.shape)}: rows {row}.. match {matches}")
                break
            parts.append(matches[0])
            row += n
        else:
            plan[init.name] = parts
    if failures:
        raise ValueError("unmatched exported weights:\n  " + "\n  ".join(failures))
    return plan


def build_shared_graph(ane: onnx.ModelProto, product: onnx.ModelProto, plan: dict[str, list[Part]],
                       heads: int) -> onnx.ModelProto:
    """ane (external data not loaded) with its planned initializers rebuilt from product's entries."""
    product_inits = {init.name: init for init in product.graph.initializer}
    ane_inits = {init.name: init for init in ane.graph.initializer}
    clash = set(product_inits) & set(ane_inits)
    if clash:
        raise ValueError(f"product initializer names clash with the exported graph: {sorted(clash)}")

    model = onnx.ModelProto()
    model.CopyFrom(ane)
    graph = model.graph
    kept = [init for init in graph.initializer if init.name not in plan]
    del graph.initializer[:]
    graph.initializer.extend(kept)

    nodes: list[onnx.NodeProto] = []
    added: set[str] = set()
    renames: dict[str, str] = {}

    def initializer(tensor: onnx.TensorProto) -> str:
        if tensor.name not in added:
            graph.initializer.append(tensor)
            added.add(tensor.name)
        return tensor.name

    def node(op: str, inputs: list[str], output: str, **attrs) -> str:
        if output not in added:
            nodes.append(helper.make_node(op, inputs, [output], name=output, **attrs))
            added.add(output)
        return output

    for name, parts in plan.items():
        target = ane_inits[name]
        blocks = []
        for part in parts:
            source = initializer(product_inits[part.source])
            if part.transpose:
                source = node("Transpose", [source], f"{part.source}/T", perm=[1, 0])
            if part.rotate:
                rows = product_inits[part.source].dims[1]
                perm, sign = rotate_rows(rows, heads)
                perm_name = initializer(numpy_helper.from_array(perm, f"{PERM_PREFIX}{rows}"))
                sign_name = initializer(numpy_helper.from_array(sign.astype(np.float32)[:, None], f"{SIGN_PREFIX}{rows}"))
                # ORT's CPU EP has no fp16 Mul kernel, so constant folding would skip an fp16 Mul.
                gathered = node("Gather", [source, perm_name], f"{part.source}/T/gather", axis=0)
                widened = node("Cast", [gathered], f"{part.source}/T/gather/float", to=onnx.TensorProto.FLOAT)
                signed = node("Mul", [widened, sign_name], f"{part.source}/T/rotated/float")
                source = node("Cast", [signed], f"{part.source}/T/rotated", to=product_inits[part.source].data_type)
            blocks.append(source)
        rebuilt = blocks[0] if len(blocks) == 1 else node("Concat", blocks, f"{name}/concat", axis=0)
        source_dims = [] if len(parts) > 1 or parts[0].transpose else list(product_inits[parts[0].source].dims)
        if source_dims == list(target.dims):
            renames[name] = rebuilt
            continue
        shape = initializer(numpy_helper.from_array(np.asarray(target.dims, dtype=np.int64), f"{name}/shape"))
        node("Reshape", [rebuilt, shape], name)

    for existing in graph.node:
        for i, value in enumerate(existing.input):
            if value in renames:
                existing.input[i] = renames[value]
    existing_nodes = list(graph.node)
    del graph.node[:]
    graph.node.extend(nodes + existing_nodes)
    return model


def share_weights(ane_model: Path, product_model: Path, out_model: Path, heads: int) -> dict[str, list[Part]]:
    """Write out_model: ane_model reading product_model's weights file; returns the match plan."""
    product_data = {e.value for init in onnx.load(str(product_model), load_external_data=False).graph.initializer
                    for e in init.external_data if e.key == "location"}
    if len(product_data) != 1:
        raise ValueError(f"expected one product weights file, found {sorted(product_data)}")
    plan = match_weights(onnx.load(str(ane_model)), onnx.load(str(product_model)), heads)
    model = build_shared_graph(onnx.load(str(ane_model), load_external_data=False),
                               onnx.load(str(product_model), load_external_data=False), plan, heads)
    shared = {part.source for parts in plan.values() for part in parts}
    leftover = [init.name for init in model.graph.initializer if uses_external_data(init) and init.name not in shared]
    if leftover:
        raise ValueError(f"exported external initializers left unshared: {leftover}")
    out_model.write_bytes(model.SerializeToString())  # onnx.save_model could write external data; never here
    return plan


def inline_constants(model_path: Path, min_bytes: int = 4096) -> list[tuple[str, list[int], int]]:
    """(name, shape, bytes) of every inline initializer or Constant node of at least min_bytes, largest first."""
    model = onnx.load(str(model_path), load_external_data=False)
    tensors = [(init.name, init) for init in model.graph.initializer if not uses_external_data(init)]
    tensors += [(node.output[0], attr.t) for node in model.graph.node if node.op_type == "Constant"
                for attr in node.attribute if attr.name == "value"]
    sized = [(name, list(t.dims), t.ByteSize()) for name, t in tensors]
    return sorted((s for s in sized if s[2] >= min_bytes), key=lambda s: -s[2])
