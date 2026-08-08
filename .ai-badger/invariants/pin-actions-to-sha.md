# Pin actions to a commit SHA; declare least-privilege permissions

Every third-party GitHub Action referenced in a workflow is pinned to a full
commit SHA, never a tag or branch — a mutable tag is remote code you re-fetch
on every run, not a fixed dependency. Every workflow (or job, where jobs need
different scopes) declares an explicit `permissions:` block set to the least
privilege that job needs; never rely on the repository's default token scope.
