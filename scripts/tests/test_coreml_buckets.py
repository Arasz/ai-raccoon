"""CoreML EP pure helpers: length buckets, free-dim overrides, cache dirs, log parsing, sessions."""

from __future__ import annotations

from pathlib import Path

import pytest

from retrieval_tuning.coreml import (
    BucketSessions,
    bucket_lengths,
    cache_dir_for,
    free_dim_overrides,
    parse_compute_plan,
    parse_partition_log,
)

FIXTURES = Path(__file__).parent / "fixtures" / "coreml"

GRANITE_INPUT_SHAPES = {
    "input_ids": ["batch_size", "sequence_length"],
    "attention_mask": ["batch_size", "total_sequence_length"],
}


# ---------------------------------------------------------------------------------------------
# bucket_lengths


def test_buckets_are_multiples_of_step_up_to_top() -> None:
    assert bucket_lengths(128, 1024) == [128, 256, 384, 512, 640, 768, 896, 1024]


def test_last_bucket_is_always_the_top() -> None:
    buckets = bucket_lengths(64, 200)

    assert buckets[-1] == 200
    assert buckets == [64, 128, 192, 200]


def test_top_already_a_multiple_is_not_duplicated() -> None:
    buckets = bucket_lengths(64, 256)

    assert buckets == [64, 128, 192, 256]
    assert buckets.count(256) == 1


def test_top_smaller_than_step_yields_a_single_bucket() -> None:
    assert bucket_lengths(128, 64) == [64]


# ---------------------------------------------------------------------------------------------
# free_dim_overrides


def test_batch_is_pinned_to_one() -> None:
    overrides = free_dim_overrides(GRANITE_INPUT_SHAPES, 64)

    assert overrides["batch_size"] == 1


def test_every_symbolic_dim_gets_the_bucket_length_including_the_separately_named_mask_dim() -> None:
    overrides = free_dim_overrides(GRANITE_INPUT_SHAPES, 64)

    assert overrides["sequence_length"] == 64
    assert overrides["total_sequence_length"] == 64


def test_fixed_integer_dims_are_left_out() -> None:
    shapes = {"input_ids": ["batch_size", "sequence_length"], "extra": ["batch_size", 384]}

    overrides = free_dim_overrides(shapes, 64)

    assert 384 not in overrides.values()
    assert set(overrides) == {"batch_size", "sequence_length"}


# ---------------------------------------------------------------------------------------------
# cache_dir_for


def test_cache_dir_is_scoped_to_every_axis_that_changes_the_compiled_artifact() -> None:
    root = Path("/tmp/coreml-cache")

    a = cache_dir_for(root, "sha-a", "1.30.0", "CPUAndNeuralEngine", 64)
    b = cache_dir_for(root, "sha-a", "1.30.0", "CPUAndNeuralEngine", 128)

    assert a != b, "two buckets sharing a cache dir load the wrong compiled shape"


def test_cache_dir_differs_per_model_sha_ort_version_and_compute_units() -> None:
    root = Path("/tmp/coreml-cache")
    base = cache_dir_for(root, "sha-a", "1.30.0", "CPUAndNeuralEngine", 64)

    assert cache_dir_for(root, "sha-b", "1.30.0", "CPUAndNeuralEngine", 64) != base
    assert cache_dir_for(root, "sha-a", "1.31.0", "CPUAndNeuralEngine", 64) != base
    assert cache_dir_for(root, "sha-a", "1.30.0", "CPUOnly", 64) != base


def test_cache_dir_is_under_root() -> None:
    root = Path("/tmp/coreml-cache")

    assert root in cache_dir_for(root, "sha-a", "1.30.0", "CPUOnly", 64).parents


# ---------------------------------------------------------------------------------------------
# parse_partition_log


def test_parse_partition_log_reads_the_ort_getcapability_line() -> None:
    text = (FIXTURES / "partition-log.txt").read_text()

    parsed = parse_partition_log(text)

    assert parsed == {"partitions": 25, "nodes_total": 376, "nodes_supported": 338}


def test_parse_partition_log_raises_when_the_line_is_absent() -> None:
    with pytest.raises(ValueError):
        parse_partition_log("nothing useful here\n")


# ---------------------------------------------------------------------------------------------
# parse_compute_plan


def test_parse_compute_plan_counts_ops_per_device() -> None:
    text = (FIXTURES / "compute-plan.txt").read_text()

    parsed = parse_compute_plan(text)

    assert parsed == {"CPU": 18, "NeuralEngine": 10}


def test_parse_compute_plan_is_empty_for_a_log_with_no_operations() -> None:
    assert parse_compute_plan("no operations logged\n") == {}


# ---------------------------------------------------------------------------------------------
# BucketSessions


def test_bucket_sessions_builds_lazily_and_reuses_the_built_session() -> None:
    built: list[int] = []
    sessions = BucketSessions(factory=lambda bucket: built.append(bucket) or object(), max_live=0)

    first = sessions.get(64)
    second = sessions.get(64)

    assert built == [64]
    assert first is second


def test_bucket_sessions_unbounded_when_max_live_is_zero() -> None:
    sessions = BucketSessions(factory=lambda bucket: object(), max_live=0)

    for bucket in (64, 128, 192, 256):
        sessions.get(bucket)

    assert sessions.live_count == 4
    assert sessions.evictions == 0


def test_bucket_sessions_lru_evicts_the_least_recently_used() -> None:
    class Fake:
        def __init__(self, bucket: int) -> None:
            self.bucket = bucket
            self.closed = False

        def close(self) -> None:
            self.closed = True

    made: dict[int, Fake] = {}

    def factory(bucket: int) -> Fake:
        made[bucket] = Fake(bucket)
        return made[bucket]

    sessions = BucketSessions(factory=factory, max_live=2)
    sessions.get(64)
    sessions.get(128)
    sessions.get(64)  # touch 64 so 128 is the least-recently-used
    sessions.get(192)  # evicts 128, not 64

    assert sessions.live_count == 2
    assert sessions.evictions == 1
    assert sessions.disposed == 1
    assert made[128].closed is True
    assert made[64].closed is False


def test_bucket_sessions_tracks_the_live_peak_across_evictions() -> None:
    sessions = BucketSessions(factory=lambda bucket: object(), max_live=2)

    for bucket in (64, 128, 192):
        sessions.get(bucket)

    assert sessions.peak_live == 2
    assert sessions.live_count == 2


def test_bucket_sessions_eviction_disposes_before_forgetting() -> None:
    disposed_order: list[int] = []

    class Fake:
        def __init__(self, bucket: int) -> None:
            self.bucket = bucket

        def close(self) -> None:
            disposed_order.append(self.bucket)

    sessions = BucketSessions(factory=Fake, max_live=1)
    sessions.get(64)
    sessions.get(128)

    assert disposed_order == [64]
    assert sessions.evictions == 1
    assert sessions.disposed == 1
