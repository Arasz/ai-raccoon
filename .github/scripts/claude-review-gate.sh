#!/usr/bin/env bash
# Decides whether a push gets a Claude review. Reads `gh pr view --json additions,deletions,reviews`
# on stdin and prints `review=true|false` plus a `reason=` line (both GITHUB_OUTPUT-ready).
# Order: an approved PR is never re-reviewed; an open change request always is; otherwise a diff of
# SMALL_DIFF_LINES changed lines or fewer is skipped. Only claude's APPROVED/CHANGES_REQUESTED
# reviews are verdicts; comments and dismissed reviews are not.
set -euo pipefail

SMALL_DIFF_LINES="${SMALL_DIFF_LINES:-50}"

pr=$(cat)
verdict=$(jq -r '
  [.reviews[]
   | select(.author.login == "claude" or .author.login == "claude[bot]")
   | select(.state == "APPROVED" or .state == "CHANGES_REQUESTED")
   | .state] | last // "NONE"' <<<"$pr")
changed=$(jq -r '.additions + .deletions' <<<"$pr")

if [ "$verdict" = "APPROVED" ]; then
  echo "review=false"
  echo "reason=already approved by Claude"
elif [ "$verdict" = "CHANGES_REQUESTED" ]; then
  echo "review=true"
  echo "reason=Claude requested changes earlier"
elif [ "$changed" -le "$SMALL_DIFF_LINES" ]; then
  echo "review=false"
  echo "reason=small diff ($changed changed lines, threshold $SMALL_DIFF_LINES)"
else
  echo "review=true"
  echo "reason=$changed changed lines"
fi
