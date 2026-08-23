import collections
import sys

import onnx
from onnx import TensorProto

p = sys.argv[1]
m = onnx.load(p, load_external_data=False)
print("ir_version:", m.ir_version)
print("producer:", repr(m.producer_name), repr(m.producer_version))
for o in m.opset_import:
    print("opset:", repr(o.domain), o.version)

g = m.graph
print("inputs:", [(i.name, [d.dim_param or d.dim_value for d in i.type.tensor_type.shape.dim]) for i in g.input])
print("outputs:", [(o.name, [d.dim_param or d.dim_value for d in o.type.tensor_type.shape.dim]) for o in g.output])

ops = collections.Counter(n.op_type for n in g.node)
print("node count:", len(g.node))
for k, v in sorted(ops.items(), key=lambda kv: (-kv[1], kv[0])):
    print(f"  {k}: {v}")

dt = collections.Counter()
byt = collections.Counter()
elems = collections.Counter()
for t in g.initializer:
    name = TensorProto.DataType.Name(t.data_type)
    dt[name] += 1
    n = 1
    for d in t.dims:
        n *= d
    elems[name] += n
    byt[name] += len(t.raw_data)
print("initializers:", len(g.initializer))
for k in sorted(dt):
    print(f"  {k}: tensors={dt[k]} elements={elems[k]} raw_bytes={byt[k]}")
