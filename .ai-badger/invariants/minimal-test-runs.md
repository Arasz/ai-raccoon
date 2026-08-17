# Spend the minimum test time that still proves quality

A work package should consume the least processing time on test runs that still establishes the
change is good. Test execution is not free background noise — an agent re-runs a suite several times
per task, so a long lane is a tax on every unit of work in the repo, and most of a session's
wall-clock silently becomes test execution instead of progress.

**The ladder. Climb it once, in order.**

- **Unit / fast tests over the affected scope — unlimited.** These are the working loop. Run them as
  often as you like; they cost seconds and they are how you stay honest while editing.
- **Full-scope fast tests — a checkpoint, not a habit.** Run them once per *packet* of changes, to
  catch what the scoped run could not see. Not after every edit.
- **Slow tests — once.** Start with the scoped part, and only widen if the scope cannot answer the
  question. One run, at the end, when there is something worth confirming.

**A failure resets the count.** The ladder buys its economy from the assumption that each rung
passed. When a rung goes red, the runs after your fix are new evidence, not repeats — climb again
from the scoped part. Economy never means shipping on a stale green.

**If CI can run it, let CI run it — do not duplicate the work.** A local sweep that the pipeline is
about to perform buys no coverage and spends the same minutes twice. Push and let the lane report.
A **draft PR is the delegation mechanism**: open one early and let the pipeline grind through the
expensive lanes while you work on something else. The result you wait for is CI's, not a local
re-run of it.

**Nightly-scoped tests run when you touched what they guard.** They are out of the push gate because
they are expensive, not because they are optional. Reach for one deliberately when the change lands
inside the area it protects; do not sweep the nightly set for reassurance, and do not assume a change
outside its scope needs it.

Two failure modes this exists to prevent, both of which have happened here: burning most of a task's
wall-clock on repeated full-suite runs that told you nothing new after the first; and treating a
green run from before the last edit as though it still applied.

Related: [[pipeline-runs-the-rest]] states the same split between local and pipeline; this invariant
is the spending rule underneath it. [[measure-when-it-pays]] is the same instinct applied to
benchmarks rather than gates.
