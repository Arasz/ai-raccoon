## Trigger choice: push to trunk, not pull_request (branch-policy trap)

A `pull_request`-triggered run executes from the PR **merge ref**
(`refs/pull/<n>/merge`). If the target environment has a branch policy, the run is rejected:

```
Branch "refs/pull/15/merge" is not allowed to deploy to production due to
environment protection rules.
```

Fix: trigger on the trunk push — a merge to master IS a push, and the run's ref is `refs/heads/master`, which branch policies allow:

```yaml
on:
  push:
    branches: [master]
  workflow_dispatch:
```

Consequence: every push to trunk creates a pending run; the approval gate is the release control (approve, or ignore/cancel). Manual `workflow_dispatch` runs from the default branch ref and also passes branch policies.
