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

This stays advisory until it's a failing build: wire the ArchUnitNET check
from `dotnet-domain-modeling`'s "Domain Purity Enforcement" section
(`Types().That().ResideInAssembly(...).Should().NotDependOnAny(...)`) into
CI. Without that test, nothing actually stops the dependency from landing.
