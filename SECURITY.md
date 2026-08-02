# Security policy

## Reporting a vulnerability

**Do not open a public issue for a security problem.**

Report privately through GitHub's [private vulnerability reporting][pvr] (the
**Security → Report a vulnerability** tab) once this repository is hosted there. If
private reporting is unavailable, email **araszkiewiczrafal@gmail.com** with
`ai-raccon security` in the subject.

### What to include

- What an attacker can do, and what they need in order to do it (a malicious MCP client?
  a crafted tool argument? a hostile package in the local NuGet feed?).
- The affected file and, where possible, a failing test or a reproduction command.
- The version — the `PackageVersion` in `src/AiRaccon/AiRaccon.csproj`, or the commit.

### What to expect

This is a **one-maintainer project**. There is no on-call rotation and no guaranteed
response time. Realistically: acknowledgement within a week, and a fix released as a
normal version bump.

## Supported versions

Only the **latest tagged release** is supported. There are no tagged releases yet — until
the first tag exists, the supported surface is `main` HEAD. Fixes ship forward; there are
no backports to older versions.

## What this project actually is, security-wise

AiRaccon is a **local MCP server process**. There is no hosted service, no account, and no
network surface beyond an optional localhost HTTP endpoint. The honest threat model is:

| Surface | What it does | Who controls the input |
|---|---|---|
| stdio transport (default) | Reads MCP JSON-RPC from the client's stdin, writes protocol messages to stdout, logs to stderr | The MCP client that launched the process |
| HTTP transport (opt-in) | Serves MCP over Streamable HTTP at `/mcp` on `localhost` | Any process that can reach the listening port |
| `get_random_number` tool | Generates a random integer from `Random.Shared`; touches no files, network, or secrets | The calling assistant |
| NuGet package / local feed | Ships the built tool via `dotnet pack` and the local `.nupkg-local/` feed | The pack/push commands and feed contents |

**The dangerous direction is the client that launches the process.** A stdio MCP server
inherits the privileges of whatever starts it and trusts the protocol messages it reads —
a malicious client can invoke tools, and (for any future tool) anything a tool does runs
with the server's privileges. Keep the HTTP endpoint opt-in and loopback-only for the same
reason: an unauthenticated `localhost` listener is reachable by any local process.

## What is deliberately not here yet

State plainly, so nobody assumes coverage that does not exist:

- **No automated secret scanning.** This repository has no CI workflow configured yet
  (no CodeQL, Dependabot, or gitleaks). Secrets are kept out by review and by the
  "no hardcoded secrets" invariant in [`CLAUDE.md`](CLAUDE.md) — verify with a manual
  scan (`grep -riE 'api[_-]?key|secret|password' src tests`) before any push.
- **No release automation.** Versions are set by hand in the csproj; releases are
  traceable per the "releases are traceable" invariant, nothing more.

## Out of scope

- Vulnerabilities in the [ModelContextProtocol C# SDK](https://www.nuget.org/packages/ModelContextProtocol)
  or the .NET runtime — report those to their maintainers.
- Vulnerabilities in the MCP clients (VS Code, Copilot, Claude, …) — report upstream.
- Anything requiring you to already have write access to this repository or to the
  machine it runs on.

[pvr]: https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability
