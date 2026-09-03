## Metadata checklist (NuGet package-authoring best practices)

- `PackageLicenseExpression` (SPDX/OSI-approved; match the repo LICENSE — missing license = legal default to exclusive copyright)
- `Authors` = pretty name (NOT the username), `Copyright`
- `PackageProjectUrl`, `RepositoryUrl` + `RepositoryType=git`
- `PackageReadmeFile`, `PackageReleaseNotes` (URL link is acceptable)
- `Description` = what it is; it is the first line of search results
- `PackageTags` = **terms a user would type to find the tool — never internal features** (no observability/sync/encryption/implementation details). Space-delimited, <4000 chars.
- Icon: optional (doc says CONSIDER); skip when no asset exists.
