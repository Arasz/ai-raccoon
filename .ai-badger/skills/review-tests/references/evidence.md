# Evidence

Project-agnostic write-ups of failures a rule above cites as its proof. Each entry describes the
failure class and what it proved, never the originating project, person, PR number, or date — the
rule stays the same regardless of where the instance was first observed. A `cites:` line names an
entry as `` `evidence.md` (slug) ``; the slug is this file's own `###` heading with no `.md`.

### check-the-inputs-not-just-the-rule

A filter, allowlist, or rule can be logically correct and still functionally inert if nothing in
production populates its inputs, or if its seed data never matches real traffic. Several such rules
were found live in one system on a single day, each passing every unit test because the test
constructed the rule directly and handed it inputs by hand — the gap between the rule and its
production caller is exactly where nobody looks. The fix is to ask two questions the logic itself
cannot answer — what actually populates this input in production, and does any real data match it —
and to prove the answer with a test that drives the real input-building path, not a hand-made fixture.

### frontend-happy-dom-blindspot

A frontend unit-test runner backed by a DOM shim with no real layout engine ignores CSS layout,
sizing, and pseudo-element content entirely — nothing has a width, nothing overflows, and generated
text does not exist to it. A suite of hundreds of tests stayed green through three real
visual/accessibility bugs in one session, because the shim cannot observe the property being
violated. Visual, layout, and pseudo-element-dependent work must be verified in a real browser
(bounding boxes, scroll dimensions, the real accessibility tree) — the shimmed suite proves only a
structural/class contract, and its test names should say so rather than implying it proves appearance.

### load-vs-broken-build

A saturated test machine and a genuinely broken build can produce an identical failure signature —
the same "element not visible / not found" errors — because in both cases the page under test never
renders. The cheapest discriminator, in order: what *fraction* of the suite fails (contention
degrades gradually; a broken build fails almost everything), then raising the test timeout (a timing
failure survives a longer timeout, an assertion mismatch does not), then reading the build/server
output rather than the test output. A stale build artifact from an earlier good state can also give a
false all-clear.

### never-run-full-suite-in-lanes

An automated worker running the full, unfiltered test suite on a shared or already-loaded machine
starves every other concurrent process, even when a written policy already says a scoped run is
expected and a separate pipeline stage runs the rest. Stating the constraint only in a project-wide
policy document is not enough — a worker must be told the scoped-filter rule directly in its own
task instructions, because it does not reliably re-derive "run only what you touched" from a general
policy under pressure to be thorough.

### no-wall-clock-test-assertions

A test that asserts on wall-clock time — a millisecond budget, a percentile latency, a ratio of one
duration to another — passes or fails depending on the load of the machine it runs on, not on the
correctness of the code under test. Two such gates were green in CI and red on a loaded developer
machine on the same day, including one written specifically to "make the assertion honest" with a
percentile threshold. The fix is always a seam: a fake or injected clock advanced deterministically
per phase, or a count of units of work done — never a real elapsed duration. Timing may be printed
for information but must never be asserted.

### one-apphost-at-a-time

Two concurrent instances of a local multi-service orchestration stack sharing one persistent data
volume for an emulated datastore silently corrupt each other: the second instance starting causes the
first's embedded database process to self-terminate on a lock-file conflict, while a separate,
always-up gateway process keeps answering requests — so every read then fails with a generic
connection error from a container that still looks healthy from the outside. The symptom (a specific,
recently-changed data path erroring) points nowhere near the actual cause, so root-causing it without
first checking for a second running orchestrator instance costs significant time chasing an innocent
recent change.

### azurite-volume-corpse

A local storage emulator given a persistent, per-checkout data volume can carry a wedged in-progress
workflow instance across unrelated runs against the same checkout: if a process is killed while an
orchestration is still pending, the orchestration's record persists in the volume with no host left
to ever dispatch it, and a concurrency gate keyed on "is one already in progress for this resource"
then rejects every subsequent attempt indefinitely — reproducing even when the run is otherwise alone
against fresh infrastructure, because the infrastructure is not actually fresh. The fix is to identify
and delete the specific poisoned volume for that checkout (matched by creation time against the
run that wedged it), never a shared or unrelated data volume; the underlying product gap is that the
concurrency gate has no staleness escape for a permanently wedged pending instance.

### parked-is-not-in-flight

A long-running workflow instance legitimately blocked waiting on an external human action can still
report itself as "in progress" via both its own status field and its underlying orchestration
engine's runtime status, because neither signal distinguishes "actively executing a step" from
"durably parked awaiting an external event." Any guard built to skip re-processing while a workflow
is "in flight," reading either signal, permanently excludes every instance that ever parked — exactly
the set most in need of the recovery action the guard was meant to protect. The fix is to gate on
step-level or custom-status truth that explicitly encodes the awaiting-user state, never on the
coarse status/runtime-status field alone, and to terminate a parked instance before starting a fresh
run over the same entity so the two do not race.

### vacuous-test-trap

A newly written test or automated audit passing proves nothing on its own — it must be watched
failing at least once, either by running it before the fix existed or by mutating the exact
production line it claims to cover. Several independently-discovered failure shapes produced
confident, meaningless green results in one work stream: an assertion that was already true before
the action under test ran; a pattern-matching audit whose pattern never appears in the code path it
claims to audit; a verification sweep run in the one state where the failure condition is
structurally impossible; and a measurement captured before, not after, the action that would have
changed it. A mutation that survives can also mean the wrong layer was mutated — trace which layer an
assertion actually reaches before concluding it is worthless.

### verify-behavior-not-changelog

Whether a bug is fixed or a capability exists should be settled by running the behavior — never by
reading a commit title, changelog entry, PR description, or a prior design-record's claim. In one
session this failed three separate ways: a fix's own title overstated its actual scope and a real gap
survived it; that premature belief was then written into a new design record, which a later reviewer
correctly treated as settled authority even though its premise was false; and official third-party
documentation claimed a feature worked when direct testing showed it was silently ignored. The remedy
is to run it, grep for the symptom, or construct the failing case and watch it pass — and to say
plainly when something could not be verified rather than inheriting someone else's unverified
assertion.

### archunitnet-rules-pass-vacuously

An architecture-testing-library rule that names a type from an assembly the test run never loaded
filters its check to an empty set — and a rule evaluated over an empty set passes, silently, with no
error or zero-match warning. Such a rule shipped live in one codebase for months, and an equivalent
bug was nearly introduced into two newly written rules at the same time; both were rescued only
because a companion reflection-based test happened to be airtight for the same invariant. The only
way to know an architecture gate can actually fail is to plant a real violation and watch it go red
before trusting it — a gate that has only ever passed is worse than an ordinary vacuous unit test,
because instead of missing one regression it can silently license an entire forbidden-dependency
class.

### functions-binding-expression-config

In a serverless functions runtime, a templated binding expression naming a configuration key is
resolved by the platform host process itself, not by the application/worker process — so it reads
only the host's own configuration sources and never the worker's own application-configuration file,
even when that file is wired into the worker's configuration builder. The failure is silent: the host
logs an indexing failure for that one function and disables it, while every other function keeps
working and the deployment reports success, so only the specific trigger with the unresolved key goes
dead. Diagnosing this on a machine whose local secrets happen to already contain the missing key will
mislead the investigation, since the asymmetry between two identically-declared bindings — one
resolves, one doesn't — is the actual tell.

### incomplete-state-transitions

A state machine can execute a transition that is entirely legal, whose write succeeds, and whose
whole test suite is green, while still omitting a companion side effect that the new state's own
meaning requires — leaving the system asserting something for which no supporting record exists.
Several independent instances of this shape were found in one review sweep, and in every case the
existing tests were not merely absent but actively misleading — one test's name asserted the exact
opposite of what the code did, because it checked a flag beside the failure rather than the state of
the thing that actually failed. The fix shape that makes the defect structurally unrepresentable is
to make the companion effect a required argument on the transition itself, rather than adding a
save-time invariant or a switch inside the state machine; a general static/architecture rule was
considered and rejected, because this is a data-flow property inside a method body that such a
rule's vocabulary cannot express.

### cv-truthfulness-gate-design

Given a generation system whose invariant permits paraphrasing source content but forbids inventing
or inflating facts — numbers, in particular — a naive full-vocabulary containment check rejects
legitimate paraphrase, and a tolerance/budget on how much new vocabulary is allowed is a trap: the
budget bounds the wrong quantity, treating a synonym and a fabrication as the same kind of "residue."
Proven on a real system: a schema/field check, a required-but-unvalidated justification field, and a
check that an instruction string is merely present in a prompt all independently passed content
containing a fabricated, inflated number. The shape that works is to partition the check by token
class rather than by budget — gate only digit-bearing tokens, at zero tolerance, since paraphrase
introduces new words but not new numbers — which also removes the need to tune any threshold at all.
Explicitly scoped: such a gate catches quantitative inflation only, not a fabricated qualitative claim
carrying no digit.

### stryker-blind-spots

A mutation-testing tool can report a mutant as "survived" without ever having genuinely exercised it,
in at least three distinct ways: a mutation inside a static/class-level field initializer that runs
once at type load, before the tool's per-mutant instrumentation activates; a mutation the tool's own
safe-mode silently discards because it would otherwise produce an unassigned-variable compile error,
counted as untested without ever running; and a meaningful fraction of mutants excluded from the
reported denominator entirely because their variant failed to compile, rather than being reported as
"unknown." A survivor should always be verified by hand — apply the mutation, run the targeted test,
confirm it reddens, revert, confirm it greens again — before trusting either an individual survivor or
an aggregate score. Separately, a mutation run scoped by a namespace/directory filter can silently
test zero mutants if that filter's expansion pattern does not match any source files at the depth
intended, and still exit as if it succeeded — a scoped run that tested nothing must be made to fail
loudly, not read as a clean pass.

### test-suite-shape

A single aggregate coverage percentage for a large test suite can conceal near-total blindness in a
narrow, high-value slice: in one measured system, headline coverage in the high 70s hid a
data-access layer near 20% and an authentication/authorization middleware at exactly 0% coverage —
the only code resolving an identity header into an authorized user and issuing an access-denied
response, exercised by no test in the default gate, with every other test setting the identity by
hand instead of going through it. The same measurement found that a "generated content must never
invent facts" invariant had no test able to fail it: three separate mechanisms looked like
enforcement and none of them actually verified provenance, so fabricated content generated against a
real template still validated as schema-correct. The general lesson: coverage percentage measures
exposure, not verification, and a single blended number for a whole codebase can average away exactly
the surface most worth protecting.

### loose-assertion-bound-admits-regression

A numeric assertion bound set one or more orders of magnitude looser than the value production
actually enforces admits a real regression while staying green: a confidence threshold asserted at
a round, arbitrary cutoff passed even after the documented invariant tightened well past it, and a
length assertion left several times the actual configured limit, silently permitting a
multiple-times increase in truncated content before the assertion would ever fail. The fix is to
pin the assertion at the production value itself, not one convenient round number away from it, and
to widen it — deliberately, by class — only where legitimate variation demands it.

### broad-catch-swallows-real-bugs

A negative-path test that only asserts an exception's base type, or a production `catch` clause
narrowed no further than a broad built-in exception type, both fail the same way: a later change
can widen what the code silently treats as "expected and ignored" to include a real, unrelated
defect, and the test suite stays green because it never distinguished the narrow case it meant to
test from the general one it now also matches. A connectivity-guard catch clause was found broad
enough to also swallow a genuine, unrelated processing failure with no test able to tell the
difference. Every narrowed catch needs a negative test that throws a different instance of the same
base type through the same path and asserts it still propagates.

### arrange-does-not-reach-branch-under-test

Several independently-found tests each asserted a specific competing branch's precedence,
isolation, or ordering rule while their own arrange never actually reached that branch — a
competitor built from a near-identical but not byte-identical value, a field that happened to
coincide with the value under test so the discriminating case never occurred, one gate's
precondition satisfied before the gate under test could ever run, and an isolation check whose fake
ignored the discriminating parameter beneath it. Every one of these tests passed and would have kept
passing after the production rule was deleted. The only way to confirm an arrange reaches its target
branch is to delete the branch and watch the specific test redden.

### double-ignores-parameter

A test double that silently ignores one of its constructor or method parameters — most often a
tenancy or ownership id — makes every test that varies only that parameter pass for the wrong
reason: the assertion is true regardless of the value, because the double's behaviour never
depended on it in the first place. This was found live in more than one double at once: an
isolation test that queried a *different* record and still passed because the fake never filtered
by the id either, and a hand-set literal id left in production code with nothing able to catch it
because the fake it was tested against didn't honour that field. A double's behaviour is a claim
about production and needs the same parameter-honouring guarantee the real thing gives.

### fake-contradicts-production-backend

An in-memory or hand-written fake standing in for a real backend can implement a filter or an
ordering differently from the production engine — applying a filter the real store silently drops,
or sorting by a different field or direction — and every test written against the fake stays green
while the identical assertion would fail against the real thing. The only way to catch this class of
divergence is a single contract-test suite executed against both the fake and the real backend, so
the divergence itself becomes the failing test rather than something a reviewer has to notice by
inspection.

### degenerate-fixture-hides-untested-path

A test fixture built at a degenerate value for the parameter under test — one item where the code
branches on more-than-one, or a request fully populated where an empty/absent case is what the
logic actually has to handle — makes the test pass without ever exercising the mechanism it claims
to cover. A diff-matching test built from one item per side left duplicate-handling entirely
untested, and a suite whose every fixture was fully populated left the empty-input path with no
coverage at all despite a green suite. The fix is a fixture at the tightest value the parameter
under test can meaningfully take, with at least one case deliberately pushed toward empty or absent.

### schema-accepts-everything-no-rejection-test

A schema or contract-parity test suite built entirely from acceptance cases proves only that valid
input is accepted — it says nothing about whether an invalid shape is rejected, and a schema that
has quietly grown permissive enough to accept everything passes every one of those tests without
exception. A parity suite that pinned one discriminated field's accepted values, but carried no
test asserting a value outside that set was rejected, shipped a selector the backend could not
actually register — the exact drift the missing rejection case would have caught. Every
discriminated union or constrained enum in a contract needs at least one assertion that the wrong
shape is refused, not only that the right one is accepted.
