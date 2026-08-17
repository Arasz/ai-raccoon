# docs

Design and architecture documentation for arasz-home-page.

| Doc                                                                | What                                                                                                                                                                       |
|--------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| [design-direction.md](design-direction.md)                         | Accepted minimal-dark black-and-white UI direction: monochrome token set, type/spacing scale, section map, experience-widget specs. Drives the Phase-2 UI issues (#8–#12). |
| [contact-backend-architecture.md](contact-backend-architecture.md) | Accepted Azure event-driven contact/CV-request design (Event Grid fan-out, Durable Functions human-in-the-loop, free tier). Drives infra/backend issues (#14–#18).         |
| [speed-insights-rca.md](speed-insights-rca.md)                     | Root-cause analysis for the Vercel Speed Insights outage (integration dropped in the Angular 22 rewrite); fixed in #3 / PR #20.                                            |
| [ci-cd.md](ci-cd.md)                                               | GitHub Actions workflow list, OIDC/no-stored-secrets approach, and the owner's one-time Azure bootstrap steps for `infrastructure.yml` and `functions-deploy.yml` (#15).   |
| [contact-pipeline.md](contact-pipeline.md)                         | Runtime data flow as built in `backend/` for the contact fan-out and the published event contract.                                                                         |
| [beta-access-pipeline.md](beta-access-pipeline.md)                 | The `[ request access ]` button end to end: its own topic and subscription, the issue-body contract the jsaa-beta-access sync parses, and the operator checklist.          |
| [github-oauth-app-setup.md](github-oauth-app-setup.md)             | Owner runbook for the GitHub OAuth App that verifies a beta requester's login: create it, store the two secrets, apply, verify, rotate (ADR-0017).                         |
| [deployment-guide.md](deployment-guide.md)                         | Owner runbook for deploying the backend + infrastructure to a personal Azure subscription by hand, including end-to-end smoke tests.                                       |
| [adr/](adr/)                                                       | Architecture Decision Records.                                                                                                                                             |
| [incidents/](incidents/)                                           | Post-incident write-ups (currently the 2026-07-19 Functions-host outage).                                                                                                  |

The public-facing site copy is **not** stored here — it lives as typed data files under
`frontend/src` (built in the UI issues) so templates carry no hardcoded content. The source
content pack (which references private CV PII) is kept out of the repo deliberately.
