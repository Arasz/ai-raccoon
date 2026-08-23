"""Write ORT's own optimized copy of a model — step 1 of the §4.2 int8 recipe.

    python ort_optimize.py <in.onnx> <out.onnx> [basic|all]

`basic` folds Transpose(weight) into an initializer, which is what makes
quantize_dynamic see the weight MatMuls at all (§4.1/§4.2). `all` also folds
but introduces com.microsoft-domain fusions, after which quantize_dynamic
fails outright — kept here so that rejection is reproducible.
"""

import sys

import onnxruntime as ort

src, dst = sys.argv[1], sys.argv[2]
level = sys.argv[3] if len(sys.argv) > 3 else "basic"

so = ort.SessionOptions()
so.graph_optimization_level = {
    "basic": ort.GraphOptimizationLevel.ORT_ENABLE_BASIC,
    "all": ort.GraphOptimizationLevel.ORT_ENABLE_ALL,
}[level]
so.optimized_model_filepath = dst
ort.InferenceSession(src, so, providers=["CPUExecutionProvider"])
print(f"wrote {dst} (level={level})")
