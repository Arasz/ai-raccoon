# One PR per task

Every unit of work ends in a pull request; never push directly to the main/trunk branch. One task maps to one PR — don't bundle unrelated work into the same change so review and rollback stay scoped.

**The one exception is an explicit instruction from the person you are working with.** When they ask you to merge locally, push straight to main, or skip the PR for a particular change, that is theirs to decide. An agent never grants itself this exception — not to save a step, not because the change looks trivial, and not because a rebase turned awkward. Absent that instruction, the rule above is absolute.

The exception lifts the PR requirement and nothing else. Every gate still runs before the push: the PR was the record, not the safety net.
