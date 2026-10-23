"""Stamp the ANE graph with the COREML_CACHE_KEY ONNX Runtime's CoreML provider names its compiled model by.

The key must equal CoreMlGraph.CacheKey in AiRaccoon.Infrastructure; AneGraphSharesProductWeightsTests
checks the committed graph. Only the metadata changes: the graph is loaded and saved without its
external weights, so their location, offset and length references stay as they were.
"""

from __future__ import annotations

from pathlib import Path

import onnx

CACHE_KEY = "graniteR2ane"
METADATA_KEY = "COREML_CACHE_KEY"


def stamp(graph: Path, key: str = CACHE_KEY) -> None:
    """Set graph's COREML_CACHE_KEY to key in place, replacing any earlier value."""
    model = onnx.load(str(graph), load_external_data=False)
    kept = [prop for prop in model.metadata_props if prop.key != METADATA_KEY]
    del model.metadata_props[:]
    model.metadata_props.extend(kept)
    entry = model.metadata_props.add()
    entry.key, entry.value = METADATA_KEY, key
    onnx.save_model(model, str(graph))
