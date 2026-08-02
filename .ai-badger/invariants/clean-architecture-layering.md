# Clean layering

Keep the domain/pure-logic layer free of framework, persistence, HTTP, and third-party-SDK dependencies. A new dependency on the domain layer is an architecture-level decision that needs an ADR, not a routine `dotnet add package`.
