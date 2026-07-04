# HomeHarbor Docs

HomeHarbor is a home appliance control plane. It combines a .NET control API, a Vite React console, WebDAV family storage, SMB and container orchestration, OTA packaging, live installer generation, recovery tooling, and VM-level system validation in one reproducible repository.

## Start Here

- [Getting Started](./getting-started.md): local API, frontend, docs, and the smallest useful development loop.
- [Development Workflow](./development.md): repository layout, coding conventions, pnpm workspace usage, and configuration.
- [Architecture](./architecture.md): control plane, runtime agent, image/installer/recovery tooling, and release flow.
- [Backend API](./api.md): authentication, route groups, automation endpoints, and WebDAV.
- [Storage and WebDAV](./storage-webdav.md): family spaces, path safety, storage OOBE, and file access.
- [App Format](./app-format.md): HHAF v1 app manifests, app store indexes, and signed system app behavior.
- [OTA and Release](./ota-release.md): A/B slots, AVB/EROFS, manifest signing, and channel readiness.
- [Docs Deploy](./docs-deploy.md): building the VitePress site and deploying it with Cloudflare Pages and Wrangler.

## Language

English is the default documentation language. The Simplified Chinese version is available under [`/zh/`](./zh/).

## Repository Boundaries

Source lives under `src/`, tests under `tests/`, frontend code under `frontend/`, documentation under `docs/`, and appliance build assets under `build/`, `scripts/`, `os/`, and `packaging/arch/`. Generated outputs belong in `artifacts/`, `.work/`, `src/HomeHarbor.Api/wwwroot/`, or VitePress output directories, and should not be committed.

Local validation is limited to unit tests, frontend checks, and docs builds. Installation, integration, and full end-to-end validation must run inside a virtual machine.
