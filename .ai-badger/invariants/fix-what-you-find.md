# Fix what you find

An observed issue is fixed now — first, before the work that surfaced it continues.
Whether your changes caused it is irrelevant: the cost of a defect is set by when it is
seen, not by who wrote it, and the observer is the one person who provably has the
context loaded.

"Fixed now" means the fix lands in the branch you are standing in, with the same
discipline as any other change (failing witness first, gate re-run). Documenting the
issue, filing it, or labelling it "pre-existing" is not a fix — a report parks the
context you already paid for and hands the defect to someone who must rebuild it.

If a fix is genuinely beyond the current task's reach — a design decision only the
owner can make, a change that would collide with another active lane — that is
surfaced immediately as a blocking item to whoever can act, not recorded in a
final report. The distance between "observed" and "acted on" is measured in minutes,
never in documents.

The corollary for review lanes and subagents: a finding you are not allowed to fix
(read-only mandate, file owned by another lane) is handed to the orchestrator the
moment it is confirmed, and the orchestrator routes it for an immediate fix.
