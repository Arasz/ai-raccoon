import collections
import sys

import onnx

m = onnx.load(sys.argv[1], load_external_data=False)
g = m.graph
inits = {t.name for t in g.initializer}
producer = {}
for n in g.node:
    for o in n.output:
        producer[o] = n.op_type

kinds = collections.Counter()
for n in g.node:
    if n.op_type != "MatMul":
        continue
    a, b = n.input[0], n.input[1]

    def k(x):
        if x in inits:
            return "initializer"
        return producer.get(x, "graph-input")

    kinds[(k(a), k(b))] += 1

for key, v in sorted(kinds.items(), key=lambda kv: -kv[1]):
    print(f"MatMul A={key[0]:<16} B={key[1]:<16} count={v}")
