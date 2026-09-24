"""Bucket padding of the chunk-size eval: rows pad to a multiple with a masked-out pad id."""

from __future__ import annotations

from retrieval_tuning.padding import pad_row


def test_row_pads_to_the_next_multiple_with_masked_pad_ids() -> None:
    ids, mask = pad_row([7, 8, 9], 4, 0)

    assert ids == [7, 8, 9, 0]
    assert mask == [1, 1, 1, 0]


def test_exact_multiple_is_left_alone() -> None:
    assert pad_row([1, 2, 3, 4], 4, 0) == ([1, 2, 3, 4], [1, 1, 1, 1])


def test_multiple_zero_means_no_padding() -> None:
    assert pad_row([1, 2, 3], 0, 0) == ([1, 2, 3], [1, 1, 1])


def test_buckets_pad_to_the_smallest_bucket_that_fits() -> None:
    ids, mask = pad_row([1, 2, 3], 0, 0, buckets=(2, 5, 8))

    assert ids == [1, 2, 3, 0, 0]
    assert mask == [1, 1, 1, 0, 0]


def test_row_longer_than_every_bucket_stays_unpadded() -> None:
    assert pad_row([1, 2, 3], 0, 0, buckets=(2,)) == ([1, 2, 3], [1, 1, 1])


def test_bucket_equal_to_row_length_adds_nothing() -> None:
    assert pad_row([1, 2], 0, 0, buckets=(2, 4)) == ([1, 2], [1, 1])
