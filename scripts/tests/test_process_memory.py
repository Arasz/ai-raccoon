"""Process memory probe of the chunk-size eval: RSS and footprint on Linux and macOS."""

from __future__ import annotations

import ctypes

from retrieval_tuning.process_memory import memory_kib


def test_peak_is_at_least_current() -> None:
    sample = memory_kib()

    assert sample.rss > 0
    assert sample.rss_peak >= sample.rss
    assert sample.footprint_peak >= sample.footprint > 0


def test_allocation_raises_the_peak() -> None:
    before = memory_kib()
    block = bytearray(64 * 1024 * 1024)
    block[::4096] = b"x" * len(block[::4096])

    after = memory_kib()

    assert after.rss_peak - before.rss >= 60 * 1024
    assert after.footprint_peak - before.footprint >= 60 * 1024


def test_rusage_struct_matches_the_sdk_size() -> None:
    # sizeof(struct rusage_info_v4) in <sys/resource.h>; ri_flags only arrives in v5 (304).
    from retrieval_tuning.process_memory import _RusageInfoV4

    assert ctypes.sizeof(_RusageInfoV4) == 296
