"""Tests for prometheus_grade.py grade parsing.

The model (prometheus-7b-v2.0 via LM Studio) writes prose feedback and states the
grade at the END of the response ("...So the overall grade is 5."), not on the
first line. The parser must find the grade anywhere in the response, and return
None when the response was truncated before stating any grade (max_tokens hit).
"""

import importlib.util
from pathlib import Path

import pytest

SCRIPT = Path(__file__).resolve().parent.parent / "prometheus_grade.py"


@pytest.fixture(scope="module")
def prometheus_grade():
    spec = importlib.util.spec_from_file_location("prometheus_grade", SCRIPT)
    assert spec is not None and spec.loader is not None
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# Real response captured 2026-08-12: truncated at max_tokens=200 mid-feedback,
# no grade stated anywhere -> must be None (ungradable).
TRUNCATED_NO_GRADE = (
    "The search results provided by the AI agent are not only highly relevant but also directly "
    "address the query. The agent's memory search resulted in a series of detailed reports, each "
    "corresponding to a specific question asked. Each report is comprehensive and provides clear "
    "answers to the questions posed.\n\nThe first part of the search results addresses the "
    "capability answer for the first question, detailing the token's ability to write comments, "
    "submit reviews, and mark approvals. The second part of the search results provides an "
    "in-depth explanation of `sqlite3_analyzer`, a space-utilization analyzer, which directly "
    "answers the second question.\n\nThe repetition of the same information regarding the "
    "`sqlite3_analyzer` in the third and fourth parts of the search results does not detract from "
    "their usefulness. Instead, it reinforces the relevance of the information provided. The "
    "final part of the search results again addresses the capability answer for the first "
    "question, providing"
)

# Real graded-row shapes (grade stated mid/late response, never on first line).
GRADE_5_AT_END = (
    "The search results provided by the AI agent are not only highly relevant but also "
    "comprehensive, covering a wide range of issues that were identified during the debugging "
    "process. The agent's query was about understanding the reasons behind failing tests and "
    "implementing high quality fixes. The search results address this need by providing detailed "
    "information on the status of various tasks, including PR reviews, research findings, and "
    "integration reviews. Each result is directly related to the query, offering a clear picture "
    "of what has been done and what needs to be done next. This level of detail and relevance "
    "makes the search results extremely useful for the agent's purpose. Therefore, based on the "
    "score rubric, the search results are decisive hits that directly answer the agent's "
    "information need. So the overall grade is 5."
)

GRADE_1_AS = (
    "The search results provided by the AI agent are repetitive and do not offer any new or "
    "useful information. The same data is presented multiple times, which does not contribute to "
    "answering the agent's query about ADR schema versioning user_version migration ladder. This "
    "lack of variety and depth in the search results makes them irrelevant and noisy, failing to "
    "meet the criteria for a higher score on the rubric. Therefore, based on the provided rubric, "
    "the search results are graded as 1."
)

GRADE_1_SCORE_IS = (
    "The search results provided by the AI agent are all identical, repeating the same "
    "information about a space-utilization analyzer. This repetition does not contribute to "
    "answering the agent's query regarding BankMaintenance WAL checkpoint VACUUM ANALYZE timer. "
    "The results do not address any aspect of the query and therefore cannot be considered useful "
    "in this context. They are essentially irrelevant noise, which is why they would receive a "
    "score of 1 according to the rubric. This lack of relevance means that the agent would need "
    "to search again for any meaningful information related to its query. So the overall score "
    "is 1."
)

# Live probe with max_tokens=512: response completes with "score of 5" mid final paragraph.
GRADE_5_SCORE_OF = (
    "The search results provided by the AI agent are not only highly relevant but also directly "
    "address the query. The agent's memory search resulted in a series of detailed reports, each "
    "corresponding to a specific question asked. Each report is comprehensive and provides clear "
    "answers to the questions posed.\n\nThe first part of the search results addresses the "
    "capability answer for the first question, detailing the token's ability to write comments, "
    "submit reviews, and mark approvals. The second part of the search results provides an "
    "in-depth explanation of `sqlite3_analyzer`, a space-utilization analyzer, which directly "
    "answers the second question.\n\nThe repetition of the same information regarding the "
    "`sqlite3_analyzer` in the third and fourth parts of the search results does not detract from "
    "their usefulness. Instead, it reinforces the relevance of the information provided. The "
    "final part of the search results again addresses the capability answer for the first "
    "question, providing a complete wrap-up of the token's abilities.\n\nIn conclusion, the AI "
    "agent's memory search results are not only highly relevant but also directly answer the "
    "query in its entirety. Therefore, based on the score rubric, the search results would "
    "receive a score of 5 as they directly address the query and provide comprehensive answers "
    "to all questions asked."
)

# Older response shape that used to parse: digit first on the first line.
DIGIT_FIRST_LINE = "5 The search results are highly relevant.\nRest of feedback."


@pytest.mark.parametrize(
    ("content", "expected"),
    [
        (TRUNCATED_NO_GRADE, None),
        (GRADE_5_AT_END, 5),
        (GRADE_1_AS, 1),
        (GRADE_1_SCORE_IS, 1),
        (GRADE_5_SCORE_OF, 5),
        (DIGIT_FIRST_LINE, 5),
    ],
)
def test_parse_grade(prometheus_grade, content, expected):
    assert prometheus_grade._parse_grade(content) == expected
