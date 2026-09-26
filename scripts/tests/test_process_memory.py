"""Process memory probe of the chunk-size eval: RSS and footprint on Linux and macOS."""

from __future__ import annotations

import ctypes
import sys

import pytest

from retrieval_tuning.process_memory import _RusageInfoV4, _RusageInfoV6, _sample_from_v6, memory_kib


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
    assert ctypes.sizeof(_RusageInfoV4) == 296


def test_v6_rusage_struct_matches_the_sdk_size() -> None:
    # sizeof(struct rusage_info_v6): v4's 296 + 15 new fields + 6 reserved, all uint64.
    assert ctypes.sizeof(_RusageInfoV6) == 464


def test_v6_field_offsets_match_the_macos_sdk() -> None:
    # <sys/resource.h> struct rusage_info_v6, verified field-for-field against the SDK header:
    # ri_uuid[16] then 35 v4 uint64 fields (offset 16..296), then the v6 extras in declaration order.
    assert _RusageInfoV6.energy_nj.offset == 336
    assert _RusageInfoV6.neural_footprint.offset == 368
    assert _RusageInfoV6.lifetime_max_neural_footprint.offset == 376


# ---------------------------------------------------------------------------------------------
# _sample_from_v6: pure field extraction out of a filled RUSAGE_INFO_V6 struct


def test_sample_from_v6_reads_every_field_from_its_own_offset() -> None:
    info = _RusageInfoV6()
    info.resident_size = 111 * 1024
    info.phys_footprint = 222 * 1024
    info.lifetime_max_phys_footprint = 333 * 1024
    info.neural_footprint = 444 * 1024
    info.lifetime_max_neural_footprint = 555 * 1024
    info.energy_nj = 666

    sample = _sample_from_v6(info, peak_rss_kib=50)

    assert sample.rss == 111
    assert sample.rss_peak == 111
    assert sample.footprint == 222
    assert sample.footprint_peak == 333
    assert sample.neural_footprint == 444
    assert sample.neural_footprint_peak == 555
    assert sample.energy_nj == 666


def test_sample_from_v6_rss_peak_is_the_larger_of_maxrss_and_resident() -> None:
    info = _RusageInfoV6()
    info.resident_size = 100 * 1024

    sample = _sample_from_v6(info, peak_rss_kib=999)

    assert sample.rss_peak == 999


# ---------------------------------------------------------------------------------------------
# darwin-only: real RUSAGE_INFO_V6 reads, not just the struct layout


@pytest.mark.skipif(sys.platform != "darwin", reason="RUSAGE_INFO_V6 is darwin-only")
def test_energy_nj_is_positive_after_real_cpu_work() -> None:
    total = 0
    for i in range(50_000_000):
        total += i

    assert memory_kib().energy_nj > 0
    assert total > 0  # keep the loop from being optimized away by a future refactor


@pytest.mark.skipif(sys.platform != "darwin", reason="RUSAGE_INFO_V6 neural_footprint is darwin-only")
def test_neural_footprint_peak_is_at_least_current() -> None:
    sample = memory_kib()

    assert sample.neural_footprint_peak >= sample.neural_footprint >= 0


@pytest.mark.skipif(sys.platform != "darwin", reason="proc_pid_rusage of another pid is darwin-only")
def test_memory_of_another_pid_reads_that_process_not_this_one() -> None:
    import subprocess

    child = subprocess.Popen(
        [sys.executable, "-c",
         "import sys; b = bytearray(200 * 1024 * 1024); b[::4096] = b'x' * len(b[::4096]); "
         "print('ready', flush=True); sys.stdin.read()"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
    try:
        assert child.stdout.readline().strip() == "ready"

        theirs = memory_kib(child.pid)
        ours = memory_kib()

        assert theirs.footprint_peak >= 190 * 1024
        assert ours.footprint < 190 * 1024
    finally:
        child.stdin.close()
        child.wait(timeout=10)
