**English** · [Türkçe](README.tr.md)

# Logistics & Shipment Tracking System (LST)

A shipment operations and tracking system for a single logistics company: shipment creation, hub scans, delivery attempts, an exception
queue, a simulated carrier integration, public tracking for recipients, and a live-updating operations dashboard.

- Backend: ASP.NET Core (.NET 10) Minimal API, modular monolith, EF Core + Npgsql, PostgreSQL 18
- UI: React 19 + TypeScript + Vite, **same origin** as the API; live updates over SignalR
- Design documents: [`docs/01–09`](docs/) · decisions: [`docs/adr/`](docs/adr/) · progress and evidence:
  [`docs/implementation-progress.md`](docs/implementation-progress.md), [`docs/final-verification.md`](docs/final-verification.md)

Project owner: TCoderr

> This repository is a local demo/portfolio application. Passing tests are not a certification of production deployment or traffic capacity; no
> load testing has been done and no capacity claim is made. Out of scope: multi-tenant SaaS, route optimization, GPS/maps, partial delivery,
> post-delivery customer returns, real carrier/payment/notification integrations, AI/LLM features.

## Architecture

```
src/Lst.Api                  Host: composition, auth pipeline, health, SPA hosting, SignalR hub (Realtime/), maintenance commands
src/Lst.Platform             Single LogisticsDbContext + migrations, idempotency, audit, outbox, rate limiting, retention, hub reference data
src/Lst.Modules.Identity     Identity + cookie + antiforgery; current DB role/hub assignment on every request
src/Lst.Modules.Shipments    Shipment domain: state machine, scan/delivery/carrier decisions (pure policies), list, detail, public tracking
src/Lst.Modules.Operations   Exception queue, SLA monitor, KPI summary, audit reads
src/Lst.Modules.Tracking     Carrier webhook (HMAC), durable inbox + processor, SIMEX simulator (Development only)
src/web                      React SPA (operations dashboard + mobile public tracking), component tests, Playwright E2E
tests/Lst.UnitTests          Pure domain/policy and architecture boundary tests
tests/Lst.IntegrationTests   Real PostgreSQL 18 (Testcontainers) + WebApplicationFactory
scripts/                     Local stack, smoke test, migration verification, license notices, source fingerprint
```

Modules use separate schemas within a single database (`shipments`, `operations`, `tracking`, `identity`, `platform`). There is no broker, Redis,
MediatR, generic repository, event sourcing or AutoMapper.

### The flow of a shipment

1. **Creation** (`POST /api/shipments`, OpsAgent/OpsManager): `Idempotency-Key` is required. The shipment + parties + packages + `Created` event +
   audit + outbox message are written in a single transaction. The same key + the same body returns the first response **byte for byte**; a
   different body returns `422`.
2. **Hub scans** (`POST /api/hubs/{hub}/scans`, only a HubOperator assigned to that hub): arrival → departure → out for delivery at the
   destination hub. The decision is made by the pure `ScanPolicy` (Applied / Duplicate / Stale / Rejected); departure is rejected while not all
   packages are at the hub.
3. **Delivery attempt** (idempotent): delivered or failed (reason required). A failure opens an exception; after 3 attempts the allowance is
   used up.
4. **Exception queue**: an agent claims it and resolves it (approve an extra attempt, return to sender, no action needed …). A resolution that
   changes the status runs in the same transaction as the shipment command.
5. **Carrier events**: signed webhook → durable receipt (`202`) → inbox processor (lease + fencing) → Applied / Duplicate / Stale /
   Rejected / PendingReview. Same ID with different content = conflict record + exception.
6. **Outbox**: status/exception changes are dispatched after commit (simulated notification record + live dashboard hint).
7. **Public tracking** (`/t/{code}`): the recipient, without a session, sees only allowlisted fields (no name, address, phone or hub code).

Concurrency: operator commands require `If-Match` (ETag = row version); every status-changing command locks the shipment row.

## Prerequisites

- .NET SDK 10.0.4xx (`global.json`: 10.0.401) · `dotnet tool restore` (dotnet-ef)
- Docker Desktop (PostgreSQL 18 container, Testcontainers, Compose stack)
- Node.js ≥ 22.12 (for development and tests; the build inside the image uses its own Node)
- For E2E: Chrome or `npx playwright install chromium`

## Running locally (recommended: Docker Compose, HTTPS)

```powershell
# 1) Once: creates random secrets, the developer certificate export and demo user passwords (under .local/, outside git)
.\scripts\local-stack.ps1 init -SeedDemo

# 2) Build the image and start the stack (PostgreSQL 18 + one-shot migrator + HTTPS API + UI). -Simulation: carrier simulator
.\scripts\local-stack.ps1 up -SeedDemo -Simulation

# 3) Browser: https://localhost:8443   (if the developer certificate is not trusted, once: dotnet dev-certs https --trust)

.\scripts\local-stack.ps1 status
.\scripts\local-stack.ps1 down            # stops the stack; data is kept
.\scripts\local-stack.ps1 down -Purge     # also deletes the volumes of this project only
```

Linux/macOS/Git Bash equivalent: `bash scripts/ci-e2e-stack.sh .local lst 8443`.

### Demo users and passwords

There are **no passwords** in the repository, and no command prints a password to the screen. `init -SeedDemo` creates a random password for
each user and writes it to `.local/seed/Seed__Passwords__<user>`; you read the password from that file (or you replace the file with your own
password **before starting the stack for the first time**: at least 12 characters, upper/lowercase letters, a digit, a symbol).

| User | Role | Hub | What they can see in the UI |
|---|---|---|---|
| `ops.agent` | Operations agent | — | Shipments, creation, actions, exceptions, summary |
| `ops.manager` | Operations manager | — | + tracking code regeneration, audit, carrier simulation (Demo) |
| `hub.ist` / `hub.ank` / `hub.izm` | Hub operator | IST-01 / ANK-02 / IZM-01 | Scanning, shipments within their own hub's scope |

Demo data is created only in the `Development` environment and on explicit request (`-SeedDemo`); there are no demo shipments, you create
shipments from the UI. The simulator panel is visible only with `Development` + `-Simulation` + the manager role; in Production the route does
not exist.

### Sample demo tour

UI labels below are quoted as they appear on screen (the UI is in Turkish), with an English gloss in parentheses.

Create a shipment as `ops.manager` → on the detail page, **Carrier ata** (assign carrier; SIMEX) → **Carrier simülasyonu** (carrier simulation)
`HappyPath` → the status advances to "Teslim edildi" (delivered) without a refresh. Or, in a second browser, scan the package label from the
**Tarama** (scan) screen as `hub.ist`; the list in the first browser updates live.
Recipient view: the **Public takip sayfasını aç** (open the public tracking page) link on the detail page.

## Development (API + Vite)

```powershell
dotnet tool restore
dotnet dev-certs https --trust
# The connection string and demo passwords are kept in user-secrets (there are no secrets in the repo):
dotnet user-secrets --project src/Lst.Api set "ConnectionStrings:Lst" "Host=localhost;Port=5432;Database=lst_dev;Username=<user>;Password=<password>"
dotnet user-secrets --project src/Lst.Api set "Seed:Passwords:ops.agent" "<password>"    # and the same for the other users
dotnet run --project src/Lst.Api --launch-profile https                                  # https://localhost:7210 (Development: migration + seed)

cd src/web
npm ci
npm run dev                                                                              # http://localhost:5173; /api and /realtime are proxied to the API
```

The Vite proxy carries cookies and CSRF headers through unchanged; because the cookies are `__Host-`-prefixed and `Secure`, the dev server works
over `http://localhost` in Chromium/Firefox. To serve the built UI from the API: `npm run build` and `Spa:DistPath=../web/dist`.

Auth flow: `GET /api/auth/csrf` → `POST /api/auth/login` (`X-CSRF-TOKEN`) → `GET /api/auth/me` (a new token bound to the user) → `X-CSRF-TOKEN` on
every status-changing request. For manual testing: [`requests/shipments.http`](requests/shipments.http).

## Tests and verification

```powershell
dotnet build Lst.slnx -c Release
dotnet test --project tests/Lst.UnitTests/Lst.UnitTests.csproj -c Release
dotnet test --project tests/Lst.IntegrationTests/Lst.IntegrationTests.csproj -c Release   # real PostgreSQL 18 (Testcontainers); without Docker it FAILS, it is not skipped

cd src/web
npm run typecheck; npm run lint; npm run test; npm run build

# E2E: a separate, isolated stack (its own project name, volumes and secrets) + a real browser
..\..\scripts\local-stack.ps1 init -LocalDir .local-e2e -SeedDemo
..\..\scripts\local-stack.ps1 up -Project lst-e2e -LocalDir .local-e2e -Port 18443 -SeedDemo -Simulation
$env:LST_E2E_CHANNEL = "chrome"; npm run e2e
..\..\scripts\local-stack.ps1 down -Project lst-e2e -LocalDir .local-e2e -Purge
```

Other verification scripts (from the repository root):

| Script | What it does |
|---|---|
| `python scripts/stack_smoke.py --project <p> --local-dir <dir> --port <port>` | HTTPS cookie/CSRF create → replay → detail; the runtime role being unable to modify the audit trail; recreating the API; DB outage/recovery |
| `python scripts/migration_upgrade_check.py` | Empty-DB install + upgrade with data from the schemas of previous slices (disposable container) |
| `python scripts/third_party_notices.py` | Regenerates `THIRD-PARTY-NOTICES.md` and the license texts shipped with the UI |
| `scripts/source-fingerprint.ps1` | Fingerprint of the source state (to record which source the results belong to when there is no git) |

CI: [`.github/workflows/ci.yml`](.github/workflows/ci.yml) — `backend`, `web`, `e2e` jobs (`contents: read` only, actions pinned by commit SHA).
Remote runs: [GitHub Actions](https://github.com/TCoderr/logistics-shipment-tracking/actions). As of 2026-10-12 (UTC+3), the first remote run
(commit `d9ad35b`) **failed** in the `e2e` job and fixes are in progress; no passing remote run is claimed here, and the Actions page shows the
current state. The results of the equivalent local commands are in `docs/final-verification.md`; they are not remote CI results.

The results and fingerprints in `docs/final-verification.md` belong to the source state that was used during the local verification. That
document was written before the repository was pushed; its "remote CI was not run" statements describe that moment. The later translation of
this README into English (`README.md` in English, the Turkish text in `README.tr.md`) is a documentation-only change. Because the fingerprint
script also covers these files, the fingerprint of the current tree is not the same as the recorded values; the tests were not re-run after the
translation, and the recorded results are not results obtained on the current fingerprint.

## Migrations

In production, migrations are **not** applied automatically when the application starts. The table-owner role applies the migration; the
application connects with a **separate** role that has only the grants in [`db/runtime-role-grants.sql`](db/runtime-role-grants.sql) (no DDL; only
`SELECT, INSERT` on the audit table).

```powershell
dotnet ef migrations has-pending-model-changes -p src/Lst.Platform -s src/Lst.Api
dotnet ef migrations script --idempotent -p src/Lst.Platform -s src/Lst.Api -o migrate.sql   # reviewed, then applied with the owner role
# In the Compose stack the same job is done by the one-shot "migrator" service (the image's --migrate command)
```

## Failure, retry and retention behavior

| Situation | Behavior |
|---|---|
| Response lost (create / delivery attempt) | Retry with the same `Idempotency-Key`: the first response is returned as is and no second record is created. The UI resends the same body with the same key; if a field changes, it counts as a new operation and the UI warns |
| Stale screen (`If-Match` mismatch) | `412`; the UI loads the current state and keeps the text already typed |
| Carrier event repeated / out of order / contradictory | Duplicate / Stale (visible in the timeline, does not roll the status back) / PendingReview or conflict → exception |
| Inbox processor crashed | When the lease expires another worker takes over; the old worker cannot write its result (fencing). A transient DB error does not consume the attempt budget; a permanent error is dead-lettered after 5 attempts |
| Outbox delivery failed | Retries with backoff; dead-lettered after 8 attempts. Manager: `GET /api/operations/outbox`, `POST …/{id}/replay` (audited) |
| Live connection dropped | The banner says "reconnecting / data may be stale"; once reconnected, active queries are re-read from the server (the data is correct even if a message was missed) |
| Database outage | `/health/live` 200, `/health/ready` 503; recovers on its own once the database is back |
| Retention (demo assumption, **not evidence of legal compliance**) | PII is masked on completed shipments older than 180 days; raw carrier payloads 30 days; processed outbox 7 days. Manual: `POST /api/operations/retention/run` |

## Known limitations

- **Single instance:** rate limit counters, SignalR groups and the connection registry are in memory (no backplane/Redis).
- **Live channel** produces hints only for status changes and exception open/assign/resolve changes; other changes become visible through the
  60-second reconciliation or a manual refresh. Cutting off an open connection whose authorization has been removed is not instant (default ≤ 15 s).
- **Stateless cookie:** logout does not revoke the ticket on the server; a copied cookie stays valid until it expires (8 hours, sliding).
- There is no management screen for user/role/hub assignment and no OIDC; demo users come from the seed.
- The `return-to-sender` reason is validated but not stored (free text is not written to the audit trail).
- The carrier is simulation only (SIMEX); there are no real external service calls and no real notification delivery.
- A change feed (`changes?after=`) is deliberately absent ([ADR-020](docs/adr/ADR-020-realtime-invalidation-and-resync.md)).
- Performance/capacity has not been measured.

## License

No license has been chosen for the project yet. Distributed third-party components and their licenses: [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
