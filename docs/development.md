# Development Workflow

HomeHarbor aims to keep appliance builds reproducible, release behavior auditable, and user data paths safe. Prefer existing module boundaries and avoid letting one-off shell logic become long-term business rules.

## pnpm Workspace

The frontend and docs are part of one root pnpm workspace:

```text
pnpm-workspace.yaml
frontend/package.json
docs/package.json
```

Use the root install and scripts for normal work:

```bash
pnpm install
pnpm frontend:dev
pnpm frontend:build
pnpm docs:dev
pnpm docs:build
```

The root lockfile is `pnpm-lock.yaml`. Package-local lockfiles are intentionally not used so dependency resolution stays consistent across the UI and docs.

## Directory Responsibilities

| Path | Responsibility |
| --- | --- |
| `src/HomeHarbor.Api` | ASP.NET Core API, EF Core data model, auth, control plane, static frontend hosting |
| `src/HomeHarbor.Core` | Domain records, enums, storage path policy, and shared model types |
| `src/HomeHarbor.WebDav` | WebDAV HTTP methods, status codes, and XML helpers |
| `tools/system-build` | Recursively pinned `Akaishi-Tech/system-build` build engine and CLI |
| `tools/system-build/external/system-utils` | Pinned `Akaishi-Tech/system-utils` A/B, OTA, verified-boot, and runtime utility source |
| `src/HomeHarbor.Agent` | Appliance runtime commands called by systemd |
| `src/HomeHarbor.Installer` | Live installer TUI, manifest verification, and boot-state commands |
| `src/HomeHarbor.Recovery` | Recovery console and fastboot TCP service |
| `frontend` | React control console |
| `docs` | VitePress documentation site and Cloudflare Wrangler configuration |
| `system/x86_64` | System and kernel image build descriptors |
| `boot/assets`, `system`, `os`, `packaging/arch` | HomeHarbor branding, manifests, Arch packages, and systemd integration |
| `tests/HomeHarbor.Tests` | Local MSTest unit tests |
| `tests/HomeHarbor.FullE2E.Tests` | VM-level full-system tests |

## Prefer C# For Product Logic

Appliance, build, release, OTA, installer, recovery, validation, JSON/manifest, cryptographic, path-safety, and channel-guard logic should live in C# first. Reusable build behavior belongs in `Akaishi-Tech/system-build`; reusable A/B and OTA behavior belongs in `Akaishi-Tech/system-utils`. HomeHarbor-specific runtime behavior stays in this repository.

Shell scripts should stay thin:

- POSIX wrappers.
- Arch packaging glue.
- mkinitcpio hooks and install scripts.
- chroot/bootstrap glue.
- System-tool orchestration that is not reasonable to express in managed code.

Do not add JSON parsing, manifest canonicalization, OTA slot decisions, Secure Boot policy, release guards, channel invariants, or path traversal checks in shell when C# can own the behavior.

## Backend Conventions

- Use nullable reference types and implicit usings as configured.
- Use four-space indentation and file-scoped namespaces.
- Use PascalCase for types and methods, camelCase for locals.
- Give async services and commands descriptive names.
- The API requires user JWT auth by default; setup, login, health, and static frontend fallback are explicit anonymous exceptions.
- Automation endpoints must opt into `AuthorizationPolicies.Automation`.

## Frontend Conventions

The frontend uses TypeScript, React function components, Vite, React Router, TanStack Query, and local UI components.

- Use two-space indentation.
- Use double quotes and semicolons.
- Keep data requests in `frontend/src/lib/api.ts` and `frontend/src/hooks/queries.ts`.
- Keep routes in `frontend/src/routes/router.tsx`.
- Login state is supplied by `authStore`; any 401 clears the session and redirects to login.

## Configuration and Secrets

Development-only configuration can live in local environment variables, `.work/`, or the development database. Do not commit:

- Private release keys.
- Secure Boot signing keys.
- Passphrases.
- Channel credentials.
- Machine-local state.
- Appliance images or ISO outputs.

## Checks Before Submitting

After backend or shared C# changes:

```bash
dotnet test tests/HomeHarbor.Tests/HomeHarbor.Tests.csproj
```

After frontend changes:

```bash
pnpm frontend:typecheck
pnpm frontend:build
```

After docs changes:

```bash
pnpm docs:build
```
