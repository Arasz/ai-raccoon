import sys
import time

from onnxruntime.quantization import QuantType, quantize_dynamic

src, dst, qt = sys.argv[1], sys.argv[2], sys.argv[3]
weight_type = QuantType.QInt8 if qt == "int8" else QuantType.QUInt8
t0 = time.time()
quantize_dynamic(src, dst, weight_type=weight_type)
print(f"quantize_dynamic weight_type={qt} took {time.time() - t0:.1f}s")
