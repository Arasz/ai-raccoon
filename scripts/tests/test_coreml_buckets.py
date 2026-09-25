"""CoreML EP pure helpers: length buckets, free-dim overrides, cache dirs, log parsing, sessions."""

from __future__ import annotations

from pathlib import Path

import pytest

from retrieval_tuning.coreml import (
    BucketSessions,
    bucket_lengths,
    cache_dir_for,
    compiler_cpu_delta,
    free_dim_overrides,
    parse_compute_plan,
    parse_partition_log,
    parse_ps_pids,
    parse_ps_time,
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


@pytest.mark.parametrize("step,top", [(0, 100), (-1, 100), (100, 0), (100, -1)])
def test_non_positive_step_or_top_raises(step: int, top: int) -> None:
    with pytest.raises(ValueError):
        bucket_lengths(step, top)


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


def test_cache_dir_leaf_names_the_bucket_and_the_default_specialization() -> None:
    path = cache_dir_for(Path("/tmp/coreml-cache"), "sha-a", "1.30.0", "CPUAndNeuralEngine", 64)

    assert path.parent.name == "bucket-64"
    assert path.name == "default"


def test_cache_dir_differs_per_specialization() -> None:
    root = Path("/tmp/coreml-cache")
    default = cache_dir_for(root, "sha-a", "1.30.0", "CPUAndNeuralEngine", 64)
    fast = cache_dir_for(root, "sha-a", "1.30.0", "CPUAndNeuralEngine", 64, specialization="FastPrediction")

    assert fast != default, "two specialization strategies sharing a cache dir serve the wrong compiled model"
    assert fast.name == "FastPrediction"


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
    built: list[int] = []
    sessions = BucketSessions(factory=lambda bucket: built.append(bucket) or bucket, max_live=2)
    sessions.get(64)
    sessions.get(128)
    sessions.get(64)  # touch 64 so 128 is the least-recently-used
    sessions.get(192)  # evicts 128, not 64

    assert sessions.live_count == 2
    assert sessions.evictions == 1
    assert built == [64, 128, 192]

    sessions.get(128)  # 128 was evicted: fetching it again rebuilds
    assert built == [64, 128, 192, 128]


def test_bucket_sessions_tracks_the_live_peak_across_evictions() -> None:
    sessions = BucketSessions(factory=lambda bucket: object(), max_live=2)

    for bucket in (64, 128, 192):
        sessions.get(bucket)

    assert sessions.peak_live == 2
    assert sessions.live_count == 2


def test_bucket_sessions_eviction_drops_the_reference_without_calling_close() -> None:
    # onnxruntime.InferenceSession has no close() method; BucketSessions must not assume one.
    class NoClose:
        def __init__(self, bucket: int) -> None:
            self.bucket = bucket

    sessions = BucketSessions(factory=NoClose, max_live=1)
    sessions.get(64)
    sessions.get(128)  # evicts 64; must not raise looking for a close() that doesn't exist

    assert sessions.evictions == 1
    assert sessions.live_count == 1
    assert not hasattr(sessions, "disposed")
    assert not hasattr(sessions, "dispose_all")  # unused elsewhere in the harness; dropped, not tested-around


# ---------------------------------------------------------------------------------------------
# ANE compiler CPU: ps parsing and per-pid delta


def test_parse_ps_time_reads_colon_separated_fields_base_60() -> None:
    assert parse_ps_time("1:23.45") == 83.45
    assert parse_ps_time("1:02:03") == 3723.0


def test_parse_ps_pids_keeps_only_the_named_processes() -> None:
    text = "  501   0:12.50 aned\n  502   1:00.00 python3\n  503   0:05.00 ANECompilerService\n"

    pids = parse_ps_pids(text, ("aned", "ANECompilerService"))

    assert pids == {501: 12.5, 503: 5.0}


def test_parse_ps_pids_resolves_comm_by_basename() -> None:
    text = "  501   0:01.00 /usr/libexec/aned\n"

    assert parse_ps_pids(text, ("aned",)) == {501: 1.0}


def test_parse_ps_pids_skips_unparsable_lines() -> None:
    assert parse_ps_pids("garbage\n", ("aned",)) == {}


def test_compiler_cpu_delta_sums_incremental_cpu_over_pids_present_after() -> None:
    before = {501: 10.0, 502: 3.0}
    after = {501: 12.0, 502: 3.0}

    total, vanished = compiler_cpu_delta(before, after)

    assert total == 2.0
    assert vanished == []


def test_compiler_cpu_delta_counts_a_new_pid_in_full() -> None:
    before = {501: 10.0}
    after = {501: 11.0, 777: 4.0}  # 777 started and ran entirely inside the sampled window

    total, vanished = compiler_cpu_delta(before, after)

    assert total == 5.0
    assert vanished == []


def test_compiler_cpu_delta_reports_vanished_pids_instead_of_going_negative() -> None:
    # A pid present before but gone after did CPU work the after-only sum can't see: report it
    # rather than let its now-missing before-value make the total go negative.
    before = {501: 10.0, 999: 50.0}
    after = {501: 11.0}

    total, vanished = compiler_cpu_delta(before, after)

    assert total == 1.0
    assert vanished == [999]
