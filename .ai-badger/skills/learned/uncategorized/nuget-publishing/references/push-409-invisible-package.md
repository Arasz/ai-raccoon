# Publish run green but nothing published: 409 on every push (2026-08-05, ai-raccoon)

Case: `publish.yml` (Trusted Publishing OIDC, `dotnet nuget push ... --skip-duplicate`)
dispatched after merging a version bump. The push job showed 12/12 pushes (6 RID
payloads + 6 shell copies) all `Conflict ... Package '...' already exists at feed
'https://www.nuget.org/api/v2/package'`. The run concluded **SUCCESS** — with
`--skip-duplicate` every 409 is swallowed and the step exits 0. NO package was published.

## The read-API sweep (all invisible)

| Probe | Result |
|---|---|
| `https://api.nuget.org/v3-flatcontainer/ai-raccoon/index.json` | 404 (no versions AT ALL — listed OR unlisted) |
| `https://api.nuget.org/v3/registration5-gz-semver2/ai-raccoon/index.json` | XML `BlobNotFound` error page (has a UTF-8 BOM — parse with utf-8-sig or just `head -c`) |
| `https://azuresearch-usnc.nuget.org/query?q=packageid:ai-raccoon&prerelease=true` | `{"totalHits":0,"data":[]}` |
| `https://www.nuget.org/packages/ai-raccoon` | 404 |
| `https://www.nuget.org/api/v2/FindPackagesById()?id='ai-raccoon'` | `<m:count>0</m:count>` (the `$filter=Id eq 'x'` OData form ERRORS — use FindPackagesById) |
| nupkg HEADs on the flat container (1.0.0, 0.1.0-beta, 1.0.1) | 404 all |

## Control experiment (proves queries AND mechanism)

`dotnet-ignore` — same account (Arasz), same OIDC pattern: flat container lists 11
versions, FindPackagesById count=11, gallery 200. Control visible ⇒ queries sound,
account's OIDC publishing works, problem is specific to the `ai-raccoon*` ids.

## Run-history logic

`gh run list --workflow publish.yml` + `gh run view <n> --log | grep -E "Pushing|PUT http|Conflict|Created"`:
THREE runs (0.1.0-beta, 1.0.0, 1.0.1) all-conflicted from their very first push; the
run before those failed at pack (MSB3030 pre-fix). No workflow run ever produced a
single `201 Created`. `NuGet/login@v1` always printed "Successfully exchanged OIDC
token" — the policy matches, not a policy/config problem.

## The false lead: "fresh version" theory, FALSIFIED

learn.microsoft.com/en-us/nuget/nuget-org/policies/deleting-packages: nuget.org does
NOT support permanent deletion — only unlisting, and unlisted versions REMAIN in the
flat container. That rules out "unlisted/deleted", and led to the WRONG conclusion
that a fresh version would push. MEASURED: bumped to 1.0.1 (TDD version-contract test),
merged, re-dispatched → **12/12 pushes 409'd again** for a version that had never been
pushed anywhere. The bump-as-workaround theory is dead; a persistent 409 across
versions means the block is at the ID level.

## The real cause: RESERVED NAMESPACE (prefix reservation)

Proven from the gallery's own source — NuGetGallery
`src/NuGetGallery/Controllers/ApiController.cs`, push handler's
`GetHttpResultFromFailedApiScopeEvaluationForPush`:

```csharp
if (result.PermissionsCheckResult == PermissionsCheckResult.ReservedNamespaceFailure)
{
    // We return a special error code for reserved namespace failures.
    return new HttpStatusCodeWithBodyResult(HttpStatusCode.Conflict, Strings.UploadPackage_IdNamespaceConflict);
}
else if (result.PermissionsCheckResult == PermissionsCheckResult.OwnerlessReservedNamespaceFailure)
{
    return new HttpStatusCodeWithBodyResult(HttpStatusCode.Conflict, Strings.UploadPackage_OwnerlessIdNamespaceConflict);
}
```

Message texts (Strings.resx):
- `UploadPackage_IdNamespaceConflict`: "This package ID has been reserved. Please
  request access to upload to this reserved namespace from the owner of the reserved
  prefix, or re-upload the package with a different ID."
- `UploadPackage_OwnerlessIdNamespaceConflict`: "The package ID is reserved. You can
  upload your package with a different package ID. Reach out to support@nuget.org..."

The dotnet CLI renders ANY 409 as "Package '...' already exists at feed" and never
shows the body — the "duplicate" message is a lie for namespace conflicts.

Why every symptom fit:
- Per-ID check → every version (0.1.0-beta, 1.0.0, 1.0.1) conflicts.
- Prefix matching → a reservation on `ai-raccoon` also covers `ai-raccoon.win-x64`,
  `ai-raccoon.osx-arm64`, ... (all 6 RID payloads conflict too).
- Reserved-but-never-published ids have NO packages → every read API 404s.
- Auth/scope failures return 401/403 (code comment: "Push returns Unauthorized instead
  of Forbidden for failures not related to reserved namespaces") → a 409 on a valid key
  IS the namespace check.
- The user's account shows nothing (the reservation belongs to someone else; an
  empty reserved id is invisible to the owner's Manage packages too).

## Fix paths

1. Fastest confirmation: nuget.org -> Upload, drag the nupkg in — the reservation
   message shows verbatim in the browser. MEASURED 2026-08-05 (ai-raccoon 1.0.1):
   the upload returned the OWNERLESS variant verbatim — "The package ID is reserved.
   You can upload your package with a different package ID. Reach out to
   support@nuget.org if you have questions." — so this reservation has no owner to
   request access from; support@nuget.org is the only release path (email drafted:
   state the id, the 409-on-every-push pattern, the all-invisible read-API sweep,
   and that nothing is published under the prefix).
2. Ownerless reservation → email support@nuget.org (the message points there).
3. Owned reservation → request access from the owner of the reserved prefix.
4. Rename `PackageId` to an unreserved id if you must publish now — `ToolCommandName`
   is independent, so the installed command name (`ai-raccoon`) does NOT change; only
   the `dotnet tool install -g <id>` name changes. (Docs/user-visible strings change.)
   EXECUTED 2026-08-05: bridge PR renamed PackageId -> `arasz.ai-raccoon` (csproj +
   .mcp/server.json packages[0].identifier + README install lines); the
   version-contract test gained an id fact pinning PackageId == server.json identifier
   == bridge id AND ToolCommandName == `ai-raccoon` (TDD: 1 failing -> 4 passing).
   Reversible: when support releases the reservation, flip PackageId back and deprecate
   `arasz.ai-raccoon` with the alternate-package pointer set to `ai-raccoon`; versions
   are per-id so both ids can carry 1.0.1. Users migrate via uninstall/reinstall —
   `dotnet tool update` does not cross ids.

## Packing context (why the ids look like they do)

.NET 10 platform-specific tool packaging (andrewlock.net post "Packaging
self-contained and native AOT .NET tools for NuGet", 2025-09-09): `PackAsTool` +
`RuntimeIdentifiers` makes `dotnet pack` emit a root/shell package (id = PackageId,
type DotnetTool) plus one payload per RID (id = `<PackageId>.<rid>`, type
DotnetToolRidPackage). This is the DOCUMENTED pattern — the pack is not the problem.
A malformed pack yields 400, never 409. Payload sub-ids are all covered by the parent
prefix reservation.

## Ground-truth rule

A publish run's CONCLUSION is not evidence of publication. The push-step lines are the
truth: `PUT ... 201 Created` = published; `Conflict ... already exists` = skipped.
Grep the log for both before declaring a release live — and never trust "already
exists at feed" as proof of an actual duplicate.
