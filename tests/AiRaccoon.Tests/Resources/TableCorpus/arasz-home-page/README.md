<p align="center">
  <img src="docs/brand/logo.svg" width="112" alt="arasz.me">
</p>

# arasz-home-page

Rafał Araszkiewicz's personal site plus its contact/CV-request backend. Monorepo:

| Directory                            | What                                                                              |
|--------------------------------------|-----------------------------------------------------------------------------------|
| [`frontend/`](frontend/)             | Angular 22 site (`araszme`), deployed to Vercel.                                  |
| [`backend/`](backend/)               | Azure Functions (Node/TypeScript) — contact intake + CV-request Durable Function. |
| [`infrastructure/`](infrastructure/) | Terraform (azurerm) for the backend, free-tier SKUs.                              |
| [`docs/`](docs/)                     | Design direction, architecture, and ADRs.                                         |
| [`docs/brand/`](docs/brand/README.md) | The site's mark and favicon.                                                      |

## Frontend

```bash
cd frontend
npm ci            # install locked dependencies
npm start         # dev server at http://localhost:4200/
npm run build     # production build → frontend/dist/araszme/browser
npm test          # Vitest
npm run lint      # ESLint
```

Requires Node.js ≥ 24.15 (< 25) and npm ≥ 10 (see `frontend/package.json` engines).

### Vercel deployment

The Vercel **Root Directory is the repo root**. The repo-root `vercel.json` drives the build
into the `frontend/` subdirectory (`cd frontend && npm ci`, `cd frontend && npm run build`,
output `frontend/dist/araszme/browser`). If the Vercel Root Directory is ever changed to
`frontend/`, revert those `cd frontend &&` prefixes to plain `npm ci` / `npm run build`.

## Backend & infrastructure

See [`backend/README.md`](backend/README.md), [`infrastructure/README.md`](infrastructure/README.md),
and [`docs/contact-backend-architecture.md`](docs/contact-backend-architecture.md). Both are
implemented and running in production; Terraform applies and the Functions deploy run from CI
(see [`docs/ci-cd.md`](docs/ci-cd.md) and [`docs/deployment-guide.md`](docs/deployment-guide.md)).
