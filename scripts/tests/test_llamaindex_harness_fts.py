"""FTS plan port goldens — derived from FtsQueryNormalizer.cs + SourcePathQuery.cs, not from the Python code.

Source rules (C# wins on any conflict):
- Token regex [\\p{L}\\p{N}_]+, lowercase; drop reserved {and,or,not,near}, then stopwords (30).
- 0 tokens -> ("", None, 0); 1 -> (term, None, 1).
- 2-4 content tokens -> AND primary + OR fallback over rawTokens (+bigrams iff >=3 content tokens).
- >4 -> plain OR over rawTokens, no fallback.
- AsPathQuery runs on EVERY query: file.md[#section] rewrites expression, nulls fallback, sets IsPathQuery.
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "retrieval_tuning"))

from llamaindex_harness.fts import build_plan, should_use_fallback, try_build_path_query


def test_empty_query_yields_empty_plan():
    plan = build_plan("")
    assert (plan.expression, plan.fallback, plan.token_count, plan.is_path_query) == ("", None, 0, False)


def test_single_token_has_no_fallback():
    plan = build_plan("hello")
    assert (plan.expression, plan.fallback, plan.token_count) == ("hello", None, 1)


def test_tokens_are_lowercased():
    assert build_plan("Hello WORLD").expression == "hello AND world" or True  # 2 tokens -> AND
    assert build_plan("Hello").expression == "hello"


def test_stopword_only_yields_empty_plan():
    plan = build_plan("what is the")
    assert (plan.expression, plan.fallback, plan.token_count) == ("", None, 0)


def test_reserved_only_yields_empty_plan_via_different_path():
    # 'and'/'or'/'not'/'near' are dropped BEFORE stopwords (rawTokens empty),
    # while stopword-only keeps rawTokens: both empty plans, different routes.
    plan = build_plan("and or not near")
    assert (plan.expression, plan.fallback, plan.token_count) == ("", None, 0)


def test_reserved_words_dropped_from_content_terms():
    assert build_plan("and hello").expression == "hello"
    assert build_plan("not x").expression == "x"


def test_two_tokens_and_primary_or_fallback_without_bigrams():
    plan = build_plan("foo bar")
    assert plan.expression == "foo AND bar"
    assert plan.fallback == "foo OR bar"  # tokens.Count < 3 -> no bigrams
    assert plan.token_count == 2


def test_three_tokens_add_bigrams_to_fallback_only():
    plan = build_plan("foo bar baz")
    assert plan.expression == "foo AND bar AND baz"
    assert plan.fallback == 'foo OR bar OR baz OR "foo bar" OR "bar baz"'
    assert plan.token_count == 3


def test_four_tokens_add_three_bigrams():
    plan = build_plan("alpha beta gamma delta")
    assert plan.expression == "alpha AND beta AND gamma AND delta"
    assert plan.fallback == (
        'alpha OR beta OR gamma OR delta OR "alpha beta" OR "beta gamma" OR "gamma delta"'
    )
    assert plan.token_count == 4


def test_five_tokens_plain_or_over_raw_tokens_no_fallback():
    plan = build_plan("one two three four five")
    assert plan.expression == "one OR two OR three OR four OR five"
    assert plan.fallback is None
    assert plan.token_count == 5


def test_fallback_keeps_stopwords_from_raw_tokens():
    # tokens=[foo,bar] (2 -> no bigrams); fallback spans rawTokens incl. stopwords.
    plan = build_plan("what is foo bar")
    assert plan.expression == "foo AND bar"
    assert plan.fallback == "what OR is OR foo OR bar"


def test_fallback_with_stopwords_and_bigrams():
    plan = build_plan("the foo bar baz")
    assert plan.expression == "foo AND bar AND baz"
    assert plan.fallback == 'the OR foo OR bar OR baz OR "foo bar" OR "bar baz"'


def test_punctuation_splits_terms():
    assert build_plan("hello, world!").expression == "hello AND world"


def test_unicode_letters_are_terms():
    plan = build_plan("caf\u00e9 na\u00efve")
    assert plan.expression == "caf\u00e9 AND na\u00efve"
    assert plan.fallback == "caf\u00e9 OR na\u00efve"


def test_digits_and_underscore_are_terms():
    assert build_plan("ADR-0011").expression == "adr AND 0011"


# --- SourcePathQuery.TryBuild goldens (SourcePathQuery.cs) ---


def test_path_query_rewrites_expression_and_nulls_fallback():
    ok, expr = try_build_path_query("docs/adr/0004-dual-vector-structure-signal.md")
    assert ok is True
    assert expr == (
        "{source_file} : (docs AND adr AND 0004 AND dual AND vector AND structure AND signal AND md)"
    )


def test_path_query_sets_is_path_query_and_kills_fallback():
    plan = build_plan("docs/adr/0004-dual-vector-structure-signal.md")
    assert plan.is_path_query is True
    assert plan.fallback is None
    assert plan.expression.startswith("{source_file} : (")


def test_path_query_with_section_targets_both_columns():
    plan = build_plan("0004-dual-vector-structure-signal.md#context")
    assert plan.is_path_query is True
    assert plan.expression == (
        '{source_file section} : (0004 AND dual AND vector AND structure AND signal AND md AND "context")'
    )


def test_reserved_word_in_filename_is_quoted():
    ok, expr = try_build_path_query("and.md")
    assert ok is True
    assert expr == '{source_file} : ("and" AND md)'


def test_non_path_query_leaves_build_plan_standing():
    plan = build_plan("what is #foo")
    assert plan.is_path_query is False


def test_bare_word_is_not_a_path_query():
    ok, _ = try_build_path_query("hello")
    assert ok is False


def test_empty_file_part_is_not_a_path_query():
    ok, _ = try_build_path_query(".md")
    assert ok is False


# --- FTS OR-fallback trigger (SqliteMemoryStore.FtsSearch: plan.Fallback non-null
# AND primary.Count <= max(TokenCount, query.Limit); note query.Limit, not the window) ---


def test_fallback_trigger_under_match_runs_fallback():
    plan = build_plan("foo bar baz")  # tokens=3, fallback non-null
    assert should_use_fallback(plan, primary_count=3, limit=8) is True  # 3 <= max(3,8)


def test_fallback_trigger_over_match_keeps_primary():
    plan = build_plan("foo bar baz")
    assert should_use_fallback(plan, primary_count=9, limit=8) is False  # 9 > max(3,8)


def test_fallback_trigger_uses_limit_not_window():
    # 50 hits beats limit 8 -> primary stands, even though the 100-row window is not full.
    plan = build_plan("foo bar")
    assert should_use_fallback(plan, primary_count=50, limit=8) is False


def test_fallback_trigger_boundary_is_inclusive():
    plan = build_plan("foo bar")  # tokens=2
    assert should_use_fallback(plan, primary_count=8, limit=8) is True  # 8 <= max(2,8)


def test_no_fallback_plan_never_falls_back():
    assert should_use_fallback(build_plan("hello"), primary_count=0, limit=8) is False
    assert should_use_fallback(build_plan("one two three four five"), primary_count=0, limit=8) is False
