# Security checklist (3.3)

- [ ] **No hardcoded secrets, credentials, or tokens** in any tracked file
- [ ] **Managed credentials preferred over shared keys** — use identity-based auth over connection strings, account keys, or shared access signatures wherever the platform supports it.
- [ ] **Token/credential storage is encrypted** — secrets at rest use the platform's encryption mechanism, not plaintext.
- [ ] **OAuth/SSO flows handle edge cases** — popup-blocked fallback documented, token refresh implemented, CSRF tokens have TTL/cleanup.
- [ ] **Nothing secret ships in a client artifact** — a build-time environment variable inlined into a bundle, a mobile binary, or anything else the user downloads is public. An API key that reaches the client is a leak even though it
  appears in no tracked file.
- [ ] **Authorization is enforced server-side** — a client-side check is UX, not a control. Every state-changing request re-authorizes at the API, and hiding a button is never the authorization.
- [ ] **Untrusted input never reaches a markup or code-execution sink** — markup templating, DOM injection, dynamic evaluation, shell or query construction. The value goes through the framework's escaping/parameterised path, or through an
  audited sanitizer at the point of insertion. Nothing is trusted because of where it was stored.
- [ ] **Redirect and navigation targets are allowlisted** — a target taken from a query parameter, a stored value, or a header is an open redirect until proven otherwise; rejecting `javascript:` and `data:` schemes is the floor, not the
  control.

> Browser-runtime security items (DOM XSS sinks, CSP, Web Storage, `postMessage`,
> SRI, source maps) live in the `ts` extension, which activates for `ts` and
> `react` projects. Both sets are distilled from the OpenAI `security-best-practices`
> skill's frontend references ([openai/skills](https://github.com/openai/skills),
> Apache-2.0).
