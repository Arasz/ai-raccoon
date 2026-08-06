# Plugin-to-Skills Migration Pattern

When an agent framework has a separate "plugins" concept that overlaps with "skills",
merge them into a unified model. This was the core of ai-badger v0.7.0.

## Before (separate concepts)
```
features/{stack}/plugins/
  marketplaces.json    # where plugins come from
  plugins.json         # what to install
```

## After (unified under skills)
```
features/{stack}/skills-source.json   # where skills come from
features/{stack}/skills.json          # what to install
features/{agent}/plugins-instructions.json  # how each agent installs
```

## Key design choices

1. **skills-source.json** has a `type` field (marketplace, hub, tap, url, well-known)
   and a `support` field ("common" = all agents, or explicit array of agent names).

2. **plugins-instructions.json** maps source types to shell command templates with
   `{source}`, `{name}`, `{scope}` placeholders. Each agent gets its own file.
   Empty `instructions: {}` = nop (for agents with no plugin system).

3. **Extension-only stacks** use `{"skills": []}` — the empty array is the marker.

4. **install_plugins.py** is a library module imported by scaffold.py, not standalone CLI.

## Pitfalls

- Remove old data directories BEFORE running validation against new schemas
- Schema field renames must happen atomically with the code that writes the field
- Cross-stack source references work at runtime but aren't schema-validatable — add
  runtime checks in validate.py

## Breaking version detection

When migrating between major schema versions, add a `BREAKING_VERSIONS` file at the
framework root listing versions that require a full re-scaffold (not incremental refresh).

```
# BREAKING_VERSIONS — one semver per line
0.7.0
```

The refresh tool reads this file and compares `from_version < breaking <= to_version`.
If the transition crosses a boundary, it backs up `.ai-badger/` to `.ai-badger.bckp/`
before re-scaffolding. Projects already at or past the breaking version skip the backup.

Implementation in `badger_lib.py`:
```python
def is_breaking_transition(from_version, to_version, root):
    from_v = tuple(int(p) for p in from_version.split("."))
    to_v = tuple(int(p) for p in to_version.split("."))
    for bv in read_breaking_versions(root):
        bv_v = tuple(int(p) for p in bv.split("."))
        if from_v < bv_v <= to_v:
            return True
    return False
```

## Version + changelog invariant

After any refactor that touches production code, always:
1. Bump VERSION (semver)
2. Add `docs/changelog/{version}-{slug}.md`
3. Commit both with the code changes

Codify this as an invariant file so all agents enforce it.
