# Getting Started

This page gets a clean workspace into a useful development state. A normal HomeHarbor development loop has four parts: the ASP.NET Core API, the Vite React frontend, the VitePress docs site, and a PostgreSQL development database.

## Prerequisites

- Use the .NET SDK pinned by the repository root `global.json`.
- Use Node.js and pnpm for the frontend and docs site.
- Use PostgreSQL for the API development database. The default development connection string is `Host=localhost;Port=5432;Database=homeharbor_dev;Username=homeharbor;Pooling=true`.
- Appliance image, ISO, and full E2E work require the image build toolchain and are not part of the everyday local loop.

## Install JavaScript Dependencies

The frontend and docs are in a shared pnpm workspace. From the repository root:

```bash
pnpm install
```

You can still target a package explicitly when needed:

```bash
pnpm --filter homeharbor-frontend install
pnpm --filter homeharbor-docs install
```

## Prepare the Database

Create or update the development database schema and write the local automation token:

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/HomeHarbor.Api/HomeHarbor.Api.csproj -- database-migrate
```

Run this again after pulling or creating new EF Core migrations.

## Run the API

```bash
dotnet run --project src/HomeHarbor.Api/HomeHarbor.Api.csproj
```

Development settings come from `src/HomeHarbor.Api/appsettings.Development.json`. They place the data directory at `./data`, the Valkey cache socket under `./data/run/valkey/homeharbor.sock`, the JWT signing key at `./data/jwt-signing.key`, and the automation token at `./data/automation.jwt`.

On startup the API:

1. Creates `HomeHarbor:Storage:DataRoot`.
2. Uses Valkey distributed cache when the configured Unix socket is available, or an in-memory fallback in Development.
3. Connects to PostgreSQL with the configured connection string.
4. Creates or reads the JWT signing key.
5. Maps OpenAPI in development.

## Run the Frontend

```bash
pnpm frontend:dev
```

The Vite dev server proxies `/api` and `/dav` to the API through `frontend/vite.config.ts`. The frontend API client uses same-origin paths by default and only targets another origin when `VITE_API_BASE_URL` is set.

## Build the Frontend

```bash
pnpm frontend:build
```

The release frontend build writes to `src/HomeHarbor.Api/wwwroot`, which ASP.NET Core serves as static files. That directory is generated output and is ignored by Git.

## Run the Docs Site

```bash
pnpm docs:dev
```

The docs site uses VitePress. English is served from `/`; Simplified Chinese is served from `/zh/`.

Build the docs:

```bash
pnpm docs:build
```

The output directory is `docs/.vitepress/dist`, which Wrangler uploads to Cloudflare Pages.

## Common Local Checks

```bash
dotnet test tests/HomeHarbor.Tests/HomeHarbor.Tests.csproj
pnpm frontend:typecheck
pnpm frontend:build
pnpm docs:build
```

Do not run full E2E directly on the development host. Full-system validation belongs in a VM and is driven by `tests/HomeHarbor.FullE2E.Tests` or VM-oriented build scripts.
