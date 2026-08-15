# The lane dispatch brief

A lane is an independent expert given one lens over the whole codebase, read-only, in its own
worktree. Its brief is the entire contract — it knows nothing about the orchestrating session.

## Choosing the roster

Derive it. Read the project's configured stacks, its persona routing, and its top-level source
directories, and open one lane per expertise the code actually demands. Then ask what a fixed
roster would have missed: the operations lane on a project with Terraform, the accessibility lane
on one with a UI, the data-modelling lane on one with a managed store.

Recurring lenses, as a starting point and not a list to copy:

| Lens | Asks |
|---|---|
| Architecture | layering, boundaries, god objects, folders that name domain concepts |
| Domain algorithm | the hard part of the product — retrieval, scoring, pricing, scheduling |
| Primary-language quality | idiom, allocation, async correctness, error handling |
| Data access | transactions, indexes, uniqueness, concurrency, migration ladders |
| Test-suite QA | assertion honesty, fake fidelity, skip honesty, coverage of destructive paths |
| Consumer surface | CLI/API/UI contracts, error text, exit codes, documentation drift |
| Product design | flows, information architecture, interaction states, microcopy, adherence to the project's own design language |
| Operations | deployment, secrets, observability, infrastructure definitions |

**Consumer surface and product design are two lanes, not one.** The first asks whether the
contract is correct — the flag parses, the error text matches the exit code, the response shape is
what the documentation promises. The second asks whether the thing is good to use — how many steps
the task costs, what the surface says when there is nothing to show, whether it follows the design
language the project already wrote down. Merged, they collapse into the first, because correctness
is the easier question and it arrives with evidence attached. Run as separate lanes over the same
files on one campaign they overlapped almost nowhere: the design lane produced several times the
findings of the code lane reviewing the same surface, and barely one was the same finding. The
design lane is not a frontend lane either — the flow, the states and the wording are the lens, and
they exist on a command line and in a terminal UI as much as in a rendered view.

Give the expensive reasoning lanes to a high-reasoning model and the survey lanes to a mid-tier
one; the agent-specific extension binds those roles to concrete models.

## Brief template

> **Scope.** Read-only review of `<repo>` at base commit `<sha>`, in the worktree `<path>`. Do not
> modify any file. Your lens is **<lens>**: <two sentences on what to look for and what to ignore>.
>
> **VERIFIED GROUND TRUTH — trust these over anything else in this brief.**
> - Build: `<command>` → `<exact result>`
> - Tests: `<command>` → `<exact counts and duration>`
> - Size: `<production lines by layer>`, `<test lines>`, ratio `<n>`
> - `<each pre-verified claim, with path:line>`
>
> **Leads to check, not facts.** `<anything from a prior review, plan or issue that has not been
> re-verified>`. Treat each as a hypothesis. **If a lead is wrong, proving it wrong is a
> first-class result and is worth more to me than confirming it.** Say so plainly and show the
> evidence.
>
> **Output contract.** One block per finding:
>
> ```
> ### F<n> — <claim in the present tense> [MEASURED|READ|INFERRED|UNVERIFIED]
> **Severity:** BLOCKER | HIGH | MEDIUM | LOW | NIT
> **Evidence:** <path:line, or the command you ran and its output>
> <two to six lines: what is wrong, what it costs, and the smallest fix>
> ```
>
> - The grade goes at the **end of the claim line**, from that closed set, and describes how *you*
>   know it — a true fact you did not check is `UNVERIFIED`, a number someone else measured is
>   `READ`.
> - `MEASURED` and `READ` require an `**Evidence:**` line. `INFERRED` states what it reasoned from.
> - End with `## Still open` (what you did not resolve and why) and a grade-mix line
>   (`N findings: x MEASURED, y READ, …`).
> - Add `## Owner questions` — one line each, phrased as a decision someone can rule on.
>
> **Do not** propose a plan, edit files, or estimate a schedule. Findings and evidence only.

## What separates a good lane from a plausible one

- **It ran something.** A lane whose findings are all `READ` reviewed the code's description of
  itself. Push for at least a few `MEASURED` findings per lane — a query against the real store, a
  benchmark, a probe of the real library's behaviour.
- **It says what it did not check.** An empty `## Still open` means the lane stopped noticing, not
  that it finished.
- **It reports a false positive it withdrew.** A lane that flagged something, tested it, and
  dropped it is calibrating. One that only ever confirms is not.
- **It names missing tests specifically.** "Consider adding tests" is not a finding; "no test
  covers the escape function that stands between a path containing `_` and a cascade delete" is.
- **A design lane started the product.** Its best findings come from using the surface — a
  screenshot of the real view, a session in the real terminal — because a flow that is too long
  and a state with nothing to say are invisible in the source that produces them. Make the lane
  state, per finding, whether it saw the running product or read the code. The same instrument
  cuts the other way: a finding produced against the lane's own mock, fixture or sample data is a
  finding about the mock, and on the source campaign the design lane filed two of those and
  withdrew both itself once it checked.

## Reading the results

- **Convergence is evidence; count is not a vote.** Independent lanes reaching the same finding
  raises confidence. Two lanes disagreeing means go read the code — the minority lane is right
  often enough that counting is not a method.
- **Grades are the triage key.** Re-verify at `path:line` every finding that will drive expensive
  work, whatever its grade. Cheap findings ride on their lane's grade.
- **A withdrawn supporting number does not withdraw the conclusion.** Remove the number, state
  which leg of the argument went with it, keep the claim if its other legs hold.
- **Look for the ratified decision before accepting taste as a defect.** A design lane needs this
  check more than a code lane does: design choices are deliberate far more often than code
  defects are, and the ruling that made one deliberate usually lives in a plan or design document
  rather than in the code the lane read. On the source campaign the design lane's single
  highest-severity finding died this way — it flagged a product-wide default as a defect, and the
  default turned out to be an owner-approved decision recorded in a document the lane never
  opened. Require every design finding to name where it checked for a prior ruling; "nowhere" is
  an answer, and it downgrades the finding to a question for the owner.
- **A cosmetic finding can be sitting on a correctness bug.** "This status is styled
  inconsistently" was, underneath, a status that never rendered at all, because the producer and
  the consumer parsed it differently. Design review lands on exactly the surfaces where a silent
  defect shows up as something that merely looks off, so trace what feeds a cosmetic finding
  before filing it as a nit.
