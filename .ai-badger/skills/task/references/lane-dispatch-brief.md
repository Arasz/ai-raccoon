# The lane dispatch brief

A template for the prompt you hand a subagent that owns one unit of work end to end — a "lane". Copy the shape, fill the bracketed slots, delete what does not apply. Every heading below is load-bearing: each one exists because a lane that
was not told this got it wrong.

The brief is written so the lane can improve on it. You are dispatching an agent that can read the code, not a script that cannot — say what you believe and how confident you are, and let the lane correct you.

---

## Template

> **Task.** [One sentence: the outcome, not the steps.]
>
> **The finding.** [What you believe is wrong, and where — file paths, symbols, the commit that
> introduced it.] **Verify this before fixing it.** It was found by [a review / a grep / another
> agent], not proven. If the finding is wrong, say so and stop; a fix for a defect that does not
> exist is worse than no change.
>
> **The recommendation.** [What you would do.] **Evaluate this rather than execute it.** If a
> simpler shape serves the same acceptance criteria, take the simpler shape and say in your
> report what you rejected and why. If the recommendation is wrong, ignore it and explain.
>
> **Acceptance criteria.**
> 1. [Criterion, stated so it can be checked by someone who was not here.]
> 2. [...]
>
> Report each criterion with the evidence that satisfies it — the command you ran and its output,
> not a claim that it passes. Anything you believe but did not verify is labelled a hypothesis.
>
> **How to work.**
> - TDD: the failing test comes first, and **paste its RED output** into your report. A test that
>   was never seen red is not a test.
> - Any gate you add or change: break it on purpose, watch it go red, restore it, watch it go
>   green. Paste both.
> - [Any project-specific ritual: release steps, regeneration order, re-scaffold.]
>
> **Version.** [x.y.z] — assigned at dispatch. Do not pick your own. [Concurrent lanes all edit
> the version marker and the changelog index; an unassigned version guarantees a collision.]
>
> **Files you own.** [Exact paths or globs.] **Files you must not touch:** [paths another lane
> owns this wave]. If the work seems to require a file you do not own, stop and report it rather
> than editing it.
>
> **Workspace.** [Absolute path to the worktree/branch, base commit, and how to get an
> interpreter/toolchain.] Use absolute paths; do not assume the working directory persists.
>
> **Sub-agents.** At most [N], at model [tier] or cheaper, no further depth. Every dispatch names
> its model.
>
> **Report.** [Where the result goes — a PR number, a comment, a message back.] Include what you
> changed, what you rejected, and every criterion with its evidence.

---

## Why each part is there

**Verify before fixing.** Findings arrive from reviews and greps that never ran the code. A lane that treats the finding as settled will "fix" a non-defect and add a test that pins the wrong behaviour.

**Evaluate, do not execute.** The dispatcher usually knows less about the file than the lane will in ten minutes. A brief that demands compliance throws that away; most lanes that were allowed to disagree produced something better than the
brief asked for.

**RED output pasted.** "I wrote a failing test first" is unfalsifiable in a report. The pasted failure is the only version of that claim anyone can check.

**Break every gate you add.** A gate that has only ever passed may be incapable of failing. Make it fail before you trust it.

**Version assigned at dispatch.** Lanes running concurrently against one repo collide on the version file and the release notes index. Assigning it centrally makes the collision impossible rather than merely unlikely.

**Ownership stated explicitly.** Two lanes editing one file at once is the most expensive recoverable mistake in a parallel wave. Naming the boundary — including what is *not* owned — costs one line.

**Sub-agents capped, with a model.** Uncapped depth burns budget invisibly and makes the run impossible to attribute. A named model per dispatch is what makes the model mix readable afterwards.

**Evidence per criterion, hypotheses labelled.** A report that says "all criteria met" is not a report. Separating what was proven from what was believed is what makes the next lane's brief accurate.
