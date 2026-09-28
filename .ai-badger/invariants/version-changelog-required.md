# Always bump VERSION and add changelog entry

Every release — no matter how small — must:
1. Bump `VERSION` (semver patch for fixes, minor for features, major for breaking changes)
2. Add a `docs/changelog/{version}-{slug}.md` entry describing what changed
3. Update `docs/changelog/README.md` if adding a new changelog format convention

This ensures every change is traceable and users can see what changed between versions.
