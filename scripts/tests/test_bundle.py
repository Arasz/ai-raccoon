"""Bundle contract: the pinned granite export (ADR-0108) and the legacy MiniLM vocab/GGUF pins."""

import bundle


def _all_pins():
    return [sha for _name, _url, sha in bundle.BUNDLED_FILES] + [
        bundle.BUNDLED_MANIFEST[1],
        bundle.VOCAB_SHA256,
        bundle.GGUF_SHA256,
    ]


def test_bundled_files_are_the_granite_fp16_export():
    assert bundle.BUNDLED_DIR == "granite-embedding-small-english-r2"
    assert [name for name, _url, _sha in bundle.BUNDLED_FILES] == [
        "model_fp16.onnx",
        "model_fp16.onnx_data",
        "tokenizer.json",
        "config.json",
        "tokenizer_config.json",
    ]
    assert bundle.BUNDLED_MANIFEST[0] == "ai-raccoon.manifest.json"


def test_bundled_model_pins():
    pins = {name: sha for name, _url, sha in bundle.BUNDLED_FILES}
    assert pins["model_fp16.onnx"] == "ee200de55cb2f94e858aabca54be7697a9c0805a14c858ee26ad0922b05f57d7"
    assert pins["model_fp16.onnx_data"] == "28d16e29cd623f25cc6fa0968700c5bc31036466091a5fa06d1353c1777f050e"


def test_vocab_pins():
    assert bundle.VOCAB_NAME == "vocab.txt"
    assert bundle.VOCAB_SHA256 == "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"


def test_gguf_pins():
    assert bundle.GGUF_NAME == "all-MiniLM-L6-v2.Q5_K_M.gguf"
    assert bundle.GGUF_SHA256 == "908c82ac3849f9ca23158117cec614bd8ec404040d8794c35b4c81242bf315e3"


def test_sha256_pins_are_hex():
    for sha in _all_pins():
        assert len(sha) == 64
        int(sha, 16)


def test_urls_point_at_pinned_filenames():
    for name, url, _sha in bundle.BUNDLED_FILES:
        assert url.endswith("/" + name)
    assert bundle.VOCAB_URL.endswith("/" + bundle.VOCAB_NAME)
    assert bundle.GGUF_URL.endswith("/" + bundle.GGUF_NAME)


def test_bundled_urls_are_pinned_to_a_commit_not_a_branch():
    # A re-upload upstream must not change what a build packs.
    for _name, url, _sha in bundle.BUNDLED_FILES:
        assert "/resolve/main/" not in url
        revision = url.split("/resolve/", 1)[1].split("/", 1)[0]
        assert len(revision) == 40
        int(revision, 16)
