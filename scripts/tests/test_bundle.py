"""Bundle contract: pins must stay byte-identical to the .sh this package replaces."""

import bundle


def test_onnx_pins():
    assert bundle.MODEL_NAME == "model_qint8_arm64.onnx"
    assert bundle.MODEL_SHA256 == "4278337fd0ff3c68bfb6291042cad8ab363e1d9fbc43dcb499fe91c871902474"


def test_vocab_pins():
    assert bundle.VOCAB_NAME == "vocab.txt"
    assert bundle.VOCAB_SHA256 == "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"


def test_gguf_pins():
    assert bundle.GGUF_NAME == "all-MiniLM-L6-v2.Q5_K_M.gguf"
    assert bundle.GGUF_SHA256 == "908c82ac3849f9ca23158117cec614bd8ec404040d8794c35b4c81242bf315e3"


def test_sha256_pins_are_hex():
    for sha in (bundle.MODEL_SHA256, bundle.VOCAB_SHA256, bundle.GGUF_SHA256):
        assert len(sha) == 64
        int(sha, 16)


def test_urls_point_at_pinned_filenames():
    assert bundle.MODEL_URL.endswith("/" + bundle.MODEL_NAME)
    assert bundle.VOCAB_URL.endswith("/" + bundle.VOCAB_NAME)
    assert bundle.GGUF_URL.endswith("/" + bundle.GGUF_NAME)
