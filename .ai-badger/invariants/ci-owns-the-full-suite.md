# CI owns the full suite

Run the build and the tests you added or changed; let CI run everything else. A local full-suite run
reproduces work the pipeline already does on every push, and on a machine shared by several agents it
costs wall clock twice — once locally, once in the job it duplicates. Coverage is never the reason to
run the whole suite locally, because CI already has it.

What this asks of you instead: know which jobs cover what, and make sure your new tests land inside
them. A test that carries no category or speed marker is invisible to a filtered pipeline and slips
through a green run — so verify the marker, using the project's own constants rather than grepping
for a literal string that the codebase never writes. Say the same thing to any agent you dispatch;
a delegated brief will otherwise ask for a full run out of habit.

The tradeoff is real and worth stating: a failure now surfaces after a push rather than before it.
That is the intended trade, not an oversight. Push early, keep the pipeline honest, and read what it
tells you.
