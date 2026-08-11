# jsaa.pl case (2026-08-05) — SWA custom domain

Session transcript detail for the job-search-ai-assistant project. **RESOLVED same evening:** routing is live and correct (A record → stableInboundIP, binding Ready, TLS 200) — the remaining placeholder was a never-deployed SWA, see
"Resolution (2026-08-05 evening)" at the end. Full evidence record:
`docs/work/reviews/2026-08-05-jsaa-default-site-diagnosis.md` in the repo.

## Identifiers

- Subscription: 577bf13c-8121-4c71-a260-62ccd3fe31b5 ("Base Azure subscription")
- SWA: swa-jobsearch-prod / rg-jobsearch-prod
- Default host: icy-ocean-0877bf803.7.azurestaticapps.net (resolves via trafficmanager → ASE edge, e.g. 132.220.38.112)
- Custom domain binding jsaa.pl: status Ready, managed cert expiresOn 2027-02-05
- stableInboundIP: 9.163.40.246 (ARM API api-version=2023-12-01)
- DNS: zone on OVH (ns200.anycast.me); apex A was 213.186.33.5 (OVH shared hosting — the old site); www A also 213.186.33.5

## Constraints discovered

- Apex MX records: mx1/mx2/mx3.mail.ovh.net — email lives at OVH, so a root CNAME would break mail (RFC 1034). Root A / ALIAS coexist with MX fine.
- OVH panel: no ALIAS record type offered; root CNAME silently not saved; CNAME fails where an A exists at the same name (www) — delete the A first.
- OVH appends the zone origin to relative record targets: an SVCB target entered as
  `icy-ocean-0877bf803.7.azurestaticapps.net` was stored as
  `...azurestaticapps.net.jsaa.pl.` — trailing dot required for FQDN targets.
- SVCB @ does not route traffic (no-op for SWA).
- Docs citations: apex-domain-external.md — "Set up with an A record" (stableInboundIP),
  "Forward to www subdomain" (registrar without ALIAS); custom-domain.md — CNAME/A/ALIAS migration note, IP-change caveat for A records.

## Recommended routes (docs-sanctioned, user picked A)

- Route A (chosen): apex A @ → 9.163.40.246 (replace 213.186.33.5). One edit; MX unaffected; single-region caveat acceptable for a personal app.
- Route B: www.jsaa.pl as SWA custom domain (CNAME validation) + OVH forwarding jsaa.pl → https://www.jsaa.pl; requires deleting the www A record first.
- `az staticwebapp hostname set --name swa-jobsearch-prod -g rg-jobsearch-prod
  --hostname www.jsaa.pl` is the CLI path for adding the www binding.

## Terraform state (merged via PR #740)

- infra/static_web_app.tf: azurerm_static_web_app_custom_domain.jsaa_pl with
  `validation_type = "dns-txt-token"` + `lifecycle { ignore_changes = [validation_type] }`
  (provider read gap — API doesn't return the validation method; without it every plan forces replacement of the portal-created binding).
- infra/tests/plan.tftest.hcl: override_resource for the SWA `id` (mock's fake short id fails StaticSite ID parsing) + run `custom_domain_jsaa_pl_is_wired`.
- infra/functions.tf: CORS + Easy Auth allowed_external_redirect_urls include
  https://jsaa.pl; MonitorChannels__Gmail__OAuth__RedirectUri = var.google_oauth_redirect_uri (default https://jsaa.pl/settings/gmail/callback — GCP OAuth client must register that exact URI byte-for-byte).
- TF_AZURE_ENABLED=false → CI apply skips; production changes apply locally via
  `bun scripts/tf-local.ts <bws-project-id> apply`.

## Resolution (2026-08-05 evening)

The domain worked; the app was missing. Evidence chain that settled it:

- `curl -sI https://jsaa.pl` AND the default host `icy-ocean-0877bf803.7.azurestaticapps.net`
  BOTH returned the SWA v4 placeholder → not a DNS/binding issue (placeholder on the custom domain alone would still be suspicious, on BOTH hosts it is conclusive).
- `az rest .../staticSites/swa-jobsearch-prod/builds?api-version=2023-12-01` → default build `status: WaitingForDeployment`, `createdTimeUtc` == SWA creation time (10:31Z).
- Activity log: first-ever `Microsoft.Web/staticSites/write` at 10:31Z — the SWA had been created that morning by the local tf apply and NEVER had content.
- Deploy action logs (`gh run view <id> --log`): every run printed
  `deployment_token was not provided` / `Build will not be uploaded` and exited 0 —
  `SKIP_DEPLOY_ON_MISSING_SECRETS: true` (frontend.yml:165) makes a missing token a green no-op. The env-scoped token (F26, PR #733) only landed at 13:55Z (`staticSites/resetapikey` in the activity log) — after the last deploy attempt.

Trigger semantics that matter for the fix (repo-specific):

- The deploy job is `push`-only (`github.event_name == 'push' && refs/heads/main`) AND path-filtered to `src/frontend/**` + the workflow file — `workflow_dispatch` does NOT deploy; docs/infra-only merges do NOT deploy.
- The first real deployment shipped via PR #744 (any small frontend change works).
- Verify afterwards: builds API leaves `WaitingForDeployment`; `curl -sI https://jsaa.pl`
  serves the app, not the placeholder.
