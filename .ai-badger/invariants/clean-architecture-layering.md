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

This stays advisory until it's a failing build. Prefer a **reference
allowlist**: assert the domain assembly's `GetReferencedAssemblies()` is a
subset of an approved set. It runs in milliseconds, needs no extra test-project
dependencies, and rejects the next infrastructure package nobody thought to
deny — a denylist only catches what someone remembered to name.

Reach for an **ArchUnitNET rule** only when you need type-level granularity,
and know two things first. A rule over types that were never loaded matches
nothing, and a rule over an empty set passes — silently, with no zero-match
diagnostic — so `Types().That().ResideInNamespaceMatching(...)` passes on every
input if the loader was given only the domain assembly. Loading the forbidden
types' assemblies fixes that, at the cost of the domain *test* project
referencing the very closure the rule excludes. And a namespace is not an
assembly: `IHttpClientFactory` is in `System.Net.Http` but ships in
`Microsoft.Extensions.Http`, so the two rule shapes disagree about it.

Either way, put a violating type in front of the check and watch it go red
before trusting it — see the `prove-the-check-fails` invariant.
