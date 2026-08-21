"""Model bundle contract: filenames, URLs, and SHA-256 pins for AiRaccoon's local embedding models."""

MODEL_NAME = "model_qint8_arm64.onnx"
MODEL_URL = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model_qint8_arm64.onnx"
MODEL_SHA256 = "4278337fd0ff3c68bfb6291042cad8ab363e1d9fbc43dcb499fe91c871902474"

VOCAB_NAME = "vocab.txt"
VOCAB_URL = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt"
VOCAB_SHA256 = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"

GGUF_NAME = "all-MiniLM-L6-v2.Q5_K_M.gguf"
GGUF_URL = "https://huggingface.co/leliuga/all-MiniLM-L6-v2-GGUF/resolve/main/all-MiniLM-L6-v2.Q5_K_M.gguf"
GGUF_SHA256 = "908c82ac3849f9ca23158117cec614bd8ec404040d8794c35b4c81242bf315e3"

# code-daemon-embed-v1's bundled counting tokenizer (WP2/WP3-remainder, OQ3 approved): the
# unconfigured-code-engine default (docs/work/2026-08-21-code-search-implementation-plan.md §3.3).
CODE_TOKENIZER_NAME = "code-sentencepiece.bpe.model"
CODE_TOKENIZER_URL = "https://huggingface.co/faxenoff/code-daemon-embed-v1/resolve/main/sentencepiece.bpe.model"
CODE_TOKENIZER_SHA256 = "3236c10b708765fdfd0720ea6ea932e1472450cd927b331107f05bfacdba7549"
