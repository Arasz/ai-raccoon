import sys

import numpy as np
import onnxruntime as ort

a_path, b_path = sys.argv[1], sys.argv[2]
rng = np.random.default_rng(20260823)


def sess(p):
    so = ort.SessionOptions()
    so.intra_op_num_threads = 5
    return ort.InferenceSession(p, so, providers=["CPUExecutionProvider"])


sa, sb = sess(a_path), sess(b_path)

for seq in (64, 128, 256, 510):
    cos = []
    for _ in range(24):
        ids = rng.integers(4, 22739, size=(1, seq)).astype(np.int64)
        ids[0, 0], ids[0, -1] = 2, 3
        mask = np.ones((1, seq), dtype=np.int64)
        feed = {"input_ids": ids, "attention_mask": mask}
        va = sa.run(None, feed)[0].ravel()
        vb = sb.run(None, feed)[0].ravel()
        cos.append(float(va @ vb / (np.linalg.norm(va) * np.linalg.norm(vb))))
    c = np.array(cos)
    print(f"seq={seq:4d} n={len(c)} cosine min={c.min():.6f} mean={c.mean():.6f} max={c.max():.6f}")
