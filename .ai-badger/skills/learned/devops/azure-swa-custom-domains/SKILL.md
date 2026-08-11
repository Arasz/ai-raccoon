---
name: azure-swa-custom-domains
description: >-
  Use when adding or debugging SWA custom domains.
---

# Azure Static Web Apps custom domains

## When to use

- Adding a domain to an SWA (portal, CLI, or Terraform) — apex or subdomain.
- "The link is not working" with the default `*.azurestaticapps.net` host fine but the custom domain dead.
- A registrar rejects records (CNAME at root, no ALIAS), or someone added SVCB and nothing changed.

## Mental model: TWO record sets

1. **Validation records** — prove ownership: TXT `_dnsauth.<domain>` (or CNAME to the SWA hostname). Binding status must be `Ready`. The TXT can be deleted once Ready.
2. **Routing records** — send traffic: A / ALIAS / CNAME.

The classic miss: binding is Ready but DNS still resolves to the OLD host (the A record was never changed). Always check `dig +short <domain> A` — if it's not an Azure IP, the binding being Ready proves nothing about reachability.

## Apex vs subdomain

- Apex requires TXT (`dns-txt-token`) validation — CNAME delegation is impossible at the root.
- Apex routing options:
    - ALIAS / CNAME-flattening to `<swa>.azurestaticapps.net` (needs registrar support), or
    - plain **A record → the SWA's `stableInboundIP`** (docs-sanctioned).
- A-record caveat (docs apex-domain-external.md §"Set up with an A record"): traffic goes to a single regional host (no global-distribution benefit), and if Azure changes the IP you must re-point; CNAME/ALIAS self-heal.
- Get the IP via ARM API (NOT exposed by `az staticwebapp show`):
  `az rest --method get --url ".../staticSites/<name>?api-version=2023-12-01"` →
  `properties.stableInboundIP`.
- **Root CNAME is off-limits when the apex has MX records** (RFC 1034: no other records may coexist at a CNAME name) — it breaks email. Check `dig +short <domain> MX` before ever suggesting a root CNAME.

## Records that do NOT route traffic

- **SVCB/HTTPS records at the apex do nothing for web traffic.** Browsers still need A/AAAA; SWA neither validates nor routes via SVCB. An SVCB at the root is a no-op fix.
- Docs fallback when the registrar lacks ALIAS: add `www` as an SWA custom domain (CNAME validation) + registrar-level forward apex → www (docs: apex-domain-external.md
  "Forward to www subdomain").

## Terraform (azurerm)

- `azurerm_static_web_app_custom_domain` — apex needs `validation_type = "dns-txt-token"`.
- **Binding created in the portal/CLI then imported into TF**: `terraform import` MUST run before any apply, else CI apply 409s on create. Import works with dummy `-var` values (import only reads Azure + writes state; vars never persist)
  and with az CLI auth alone — no secret TF_VARs needed.
- **Provider read gap**: the Azure API does not return the validation method on read, so an imported binding has `validation_type = null` in state and the config value plans a forced replacement on EVERY apply. Fix:
  `lifecycle { ignore_changes = [validation_type] }` — the binding is already validated; never let it churn. (The plan test should still pin the config's `validation_type`.)
- Mocked-plan tests (`terraform test`): a resource that parses another resource's `id` (custom domain ← SWA id) breaks with "the number of segments didn't match" on the mock's fake short id — `override_resource` the source resource's `id`
  with a well-formed ARM id.

## Registrar pitfalls (OVH observed 2026-08; generalize)

- No ALIAS record type in some panels/zones.
- CNAME creation fails where an A record exists at the same name → delete the A first.
- Relative targets without a trailing dot get the zone origin appended (`target.example.com` → `target.example.com.example.com.`). FQDN targets need the trailing dot; verify the saved record after adding.
- DNS zone edits need an explicit "apply configuration" step at many registrars.

## The third classic miss: the SWA placeholder is a deployment problem, not a DNS one

If the custom domain (and the default host!) serve the SWA v4 placeholder ("Congratulations on your new site!"), DNS/binding are fine and the SWA has simply **never received a deployment**. Check before touching DNS:

- `az rest --method get --url ".../staticSites/<name>/builds?api-version=2023-12-01"` → the
  `default` build shows `status: WaitingForDeployment` when no artifact was ever uploaded (createdTimeUtc = SWA creation time).
- SWA creation time: activity log REST
  `.../providers/Microsoft.Insights/eventtypes/management/values?api-version=2015-04-01&$filter=eventTimestamp ge '<date>' and resourceProvider eq 'Microsoft.Web'`
  → first `Microsoft.Web/staticSites/write`.
- Deploy-action logs: `gh run view <id> --log` and grep the deploy step —
  `deployment_token was not provided` / `Build will not be uploaded as deployment_token was not
  found` with `skip_deploy_on_missing_secrets is enabled` means the token secret was absent and the step **exited 0 anyway**. `SKIP_DEPLOY_ON_MISSING_SECRETS: true` converts "not wired" into a green run — a missing token is only ever
  visible in the step log, never in the run status.
- A workflow `deploy` job gated on `github.event_name == 'push'` cannot be fired by
  `workflow_dispatch` — the fix for a skipped-forever deploy is a real push touching the app path (or the workflow file).

Measured on jsaa.pl 2026-08-05: binding Ready, A record → stableInboundIP, TLS 200 — all good, yet placeholder on both hosts because the SWA was created hours earlier and every deploy step had skipped on the missing environment-scoped token
(recorded in the project's
`docs/work/reviews/2026-08-05-jsaa-default-site-diagnosis.md`).

## `staticwebapp.config.json` validation pitfalls

See `references/swa-config-validation.md` for the full list. Key gotchas:

**Brace expansion is not supported in exclude paths.** `*.{html,css,js}` fails with
"Found an exclude path with multiple wildcard characters '*'" — the error is misleading (there's only one `*`; the brace expansion is the real problem). Expand to individual entries: `*.html`, `*.css`, `*.js`, etc.

**Prefixed globs break exact-match test assertions.** The config may use `/*.html`
(root-level) + `**/*.html` (recursive) instead of bare `*.html`. Tests using
`.toContain("*.html")` will fail — use `.some(p => p.includes(".html"))` instead.

## Verification

- `curl -sI https://<domain>` → HTTP 200; `dig +short <domain> A` → Azure IPs.
- **macOS system dig (9.10.6) does not know SVCB/HTTPS types and silently answers with the A record** — never trust it for SVCB/HTTPS verification. Use DoH JSON:
  `curl -s "https://dns.google/resolve?name=<domain>&type=SVCB"` (check `.Answer[]`).
- Binding status: `az staticwebapp hostname list --name <swa> -g <rg>` → Ready/Validating.

## User preferences (job-search-ai-assistant owner)

- Give DNS instructions in **BIND zone-file format** (`name. TTL IN TYPE target.` with trailing dots) — they paste into a zone editor, not panel field descriptions.
- **Real non-secret values in runbook commands** — placeholders like `<subscription-id>` get copy-pasted literally and fail (observed 400 InvalidSubscriptionId). Secrets keep placeholders with an explicit "not substituted" warning.
- When asked to "fix the record", print the correct record (s) directly rather than asking for account access first.

## References

- `references/jsaa-pl-case.md` — the jsaa.pl case: resource ids, IPs, failed attempts, docs citations, session end-state.
- `references/swa-config-validation.md` — `staticwebapp.config.json` validation pitfalls (brace expansion, misleading error messages, test maintenance).
