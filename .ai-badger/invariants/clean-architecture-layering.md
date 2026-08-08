# Clean layering

Keep the domain/pure-logic layer free of framework, persistence, HTTP, and
third-party-SDK dependencies. Find that layer by shape, not by name: it's the
assembly other layers reference but that itself references none of them, with
no `PackageReference` on a web/data/cloud SDK — usually named `*.Domain` or
`*.Core`. If no project matches that shape, treat this rule as not yet
applicable rather than guessing which one is "the domain."

"Framework" means anything an ArchUnitNET-style `ForbiddenPattern` would
catch: ASP.NET Core, EF Core (`Microsoft.EntityFrameworkCore`),
Azure/`Microsoft.Azure` SDKs, `System.Net.Http`, and other
serialization/HTTP-transport namespaces. Extend that list when a new SDK
crosses the boundary; don't extend the boundary to fit the SDK.

A new dependency on the domain layer is an architecture-level decision.
Record it wherever this project already records decisions (ADR, design doc,
changelog entry); if it keeps none of those, say so explicitly in the PR
description instead of adding the dependency silently.

This stays advisory until it's a failing build. Two ways to enforce it, and
the cheaper one is usually the better one.

**A reference allowlist.** Assert that the domain assembly's
`GetReferencedAssemblies()` is a subset of an approved set. It runs in
milliseconds, needs no extra test-project dependencies, and rejects the next
infrastructure package nobody thought to deny — a denylist only catches what
someone remembered to name.

**An ArchUnitNET rule**, if you want type-level granularity. Two failure modes
to know about first:

- **A rule over types that were never loaded matches nothing, and a rule over
  an empty set passes.** `Types().That().ResideInNamespaceMatching(...)`
  filters against the architecture you built, so if the loader was given only
  the domain assembly, the forbidden types are absent and every input passes.
  There is no error and no zero-match diagnostic. Load the assemblies holding
  the forbidden types, and expect that to cost both suite time and a test-project
  reference to the very dependency closure the rule exists to exclude.
- **A namespace is not an assembly.** `IHttpClientFactory` lives in namespace
  `System.Net.Http` but ships in `Microsoft.Extensions.Http`, so a namespace
  rule and an assembly rule disagree about it. Whichever you pick, know which
  question you are asking.

Either way, [prove the check fails](../../common/invariants/prove-the-check-fails.md)
before trusting it: add a type that violates the rule, watch it go red, remove
it. A gate that has only ever passed is indistinguishable from one that cannot
fail, and this one has a documented history of being the latter.
