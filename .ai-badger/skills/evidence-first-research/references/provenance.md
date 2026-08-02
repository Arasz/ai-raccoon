# Provenance — what each grade means, and what takes it away

The grade records **your relationship to the claim**, not the claim's truth. A true fact you did
not check is `UNVERIFIED`. That is not a criticism of the fact; it is information about the
report.

## MEASURED

You ran something and read the result.

**Requires:** the command or procedure, the machine or environment, and the conditions. A number
without conditions is not reproducible and will be quoted as though it were universal.

> **F1 — The whole gate takes about 75 seconds [MEASURED]**
> **Evidence:** `.lefthook/pre-push/verify.sh` run directly on this machine, three consecutive
> runs: twelve non-pytest lanes 14s, pylint 12s, pytest 49s. Warm caches, no CI wait.

**Disqualified when:**

- You ran it once and are reporting it as the value. Run it three times or say you ran it once.
- The number came from a tool's summary line you did not look behind. A CI job's total includes
  queue time; a benchmark harness's mean hides its spread.
- You ran a *different* thing and are reasoning across the gap ("the test suite takes 50s, so the
  gate must be about a minute"). That is `INFERRED`.
- Someone else ran it. That is `READ`.

## READ

You read it in a source that is authoritative **for this specific claim**.

**Requires:** `path:line`, a spec section, a documented URL, or a named person's statement.

> **F2 — The gate was already selective [READ]**
> **Evidence:** `.lefthook/pre-push/verify.sh`, the `_lanes_for` function — it has routed by
> changed path since it was written.

**Disqualified when:**

- The source is not authoritative for this claim. A design document says what was *intended*; the
  code says what *is*. A changelog entry describes a release, not current behaviour.
- You read a summary of the source rather than the source. Another agent's report, a wiki page
  paraphrasing a spec, your own earlier note — those make it `INFERRED` at best.
- You cannot produce the citation now. If finding it again would take five minutes, it is not
  a citation, it is a memory.

## INFERRED

You reasoned to it from things you measured or read.

**Requires:** saying what it reasons *from*. "Reasoning" is not a source; the inputs are.

> **F4 — The wrong figure came from pushes that also waited on CI [INFERRED]**
> Reasoning about where the bad number originated, not a reconstruction of the original timings.

Inference is legitimate and often the most valuable part of a report. What is not legitimate is
inference wearing a measurement's clothes. If the sentence contains a number you did not observe,
either mark the number's provenance separately or downgrade the whole finding.

## UNVERIFIED

You did not check.

**Requires nothing.** This is deliberate. Every requirement attached here makes silence cheaper
than admission, and silence is the failure mode this whole format exists to prevent — an
unverified claim that simply does not appear reads, to the next reader, as a claim nobody had a
concern about.

> **F5 — Windows lanes behave the same [UNVERIFIED]**
> No Windows machine was available.

Say what would settle it if you know. Do not let that become a requirement.

## Downgrade, never delete

When a grade is challenged and does not hold, **downgrade it and keep the finding**. A deleted
finding takes its history with it, and the next person investigates the same thing.

The 75-second measurement in this skill's own motivating case began life as "3-5 minutes per
push" — ungraded, in a changelog. Had it been graded at all, the challenge would have been
mechanical: *which is it, measured or estimated?* Instead it took a hand re-derivation to catch,
after it had shipped in two places.

## The mix is the headline

A report of ten findings where one is `MEASURED` and nine are `INFERRED` is a **hypothesis**, and
should be reported as one. The same ten with nine `MEASURED` is a finding. Same conclusion, same
word count, entirely different thing to act on — which is why the provenance chart is drawn
first, before any content, and drawn even when a grade has zero findings.
