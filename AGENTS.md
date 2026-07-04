# Repository Guidelines

## Collaboration Preferences

Spend time on thinking; you do not need to use the commentary channel to report progress to me.

## Project Structure & Module Organization

HomeHarbor is a .NET 10 appliance control plane with a Vite React frontend and VitePress docs site. Backend projects live under `src/`: `HomeHarbor.Api` for ASP.NET Core and EF Core, `HomeHarbor.Core` for domain models, `HomeHarbor.WebDav` for WebDAV helpers, and `ImageBuilder`, `Installer`, and `Recovery` for appliance tooling. Tests live in `tests/HomeHarbor.Tests` and `tests/HomeHarbor.FullE2E.Tests`. Frontend source is in `frontend/src`; built assets are served from `src/HomeHarbor.Api/wwwroot`. Documentation source is in `docs/`, defaults to English, and serves Simplified Chinese from `/zh/`. Appliance assets live in `build/`, `scripts/`, `os/`, and `packaging/arch/`; generated outputs belong in `artifacts/`.

## Build, Test, and Development Commands

- `dotnet run --project src/HomeHarbor.Api/HomeHarbor.Api.csproj`: run the API locally.
- `pnpm install`: install frontend and docs workspace dependencies.
- `pnpm frontend:dev`: start Vite with `/api` and `/dav` proxying to the API.
- `pnpm frontend:typecheck`: run TypeScript validation.
- `pnpm frontend:build`: produce the frontend release build.
- `pnpm docs:dev`: start the VitePress docs site.
- `pnpm docs:build`: build the bilingual docs site.
- `dotnet test tests/HomeHarbor.Tests/HomeHarbor.Tests.csproj`: run local unit tests only.
- `dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- plan 0.1.0`: inspect the C# build plan.

## Coding Style & Naming Conventions

Use C# nullable reference types and implicit usings as configured. Follow existing C# style: four-space indentation, file-scoped namespaces, PascalCase types and methods, camelCase locals, and descriptive async/service names. Frontend code uses TypeScript, React function components, two-space indentation, double quotes, and semicolons. Shell scripts use Bash with `set -euo pipefail`.

## Shell-to-C# Tooling Policy

Prefer C# for appliance, build, release, OTA, installer, recovery, validation, JSON/manifest, cryptographic, path-safety, and channel-guard logic. When touching Bash scripts, migrate reusable behavior into `HomeHarbor.Tooling` or the owning .NET executable (`HomeHarbor.Agent`, `HomeHarbor.ImageBuilder`, `HomeHarbor.Installer`, or `HomeHarbor.Recovery`) instead of expanding shell logic.

Shell scripts are allowed only for thin POSIX entrypoints, Arch packaging glue, mkinitcpio hooks, chroot/bootstrap glue, or simple wrappers around external system tools that cannot reasonably be expressed in managed code. Keep those scripts minimal, use `set -euo pipefail`, validate arguments and environment variables, and delegate business rules to C#.

Do not implement JSON parsing, manifest canonicalization, OTA slot decisions, Secure Boot policy, release guard policy, channel deployment invariants, or path traversal checks in shell when C# can own them. Put shared logic in `HomeHarbor.Tooling` and cover it with MSTest unit tests.

When replacing an installed script, keep package, systemd, initramfs, and test entrypoints stable with a temporary wrapper only when needed. Remove wrappers once all callers invoke the C# command directly.

## Database Migrations

EF Core migrations must be generated with the repository-pinned CLI tool, not handwritten. Restore the local tool manifest with `dotnet tool restore`, then run `ASPNETCORE_ENVIRONMENT=Development dotnet tool run dotnet-ef migrations add <Name> --project src/HomeHarbor.Api/HomeHarbor.Api.csproj --startup-project src/HomeHarbor.Api/HomeHarbor.Api.csproj --context HomeHarborDbContext --output-dir Data/Migrations`. Commit the generated migration, designer, and model snapshot together.

## Testing Guidelines

Backend tests use MSTest. Name test classes `*Tests` and use descriptive method names with underscores, for example `ResolvePhysicalPath_Stays_Inside_Family_Area`. Do not add or run any form of local integration, full-function, full E2E, service-stack, browser-to-backend workflow, installer, storage, OTA, or appliance validation on the host machine after code changes; local testing is limited to unit tests, static checks, type checks, builds, and docs builds. Every code change must be validated with an appropriate disposable virtual-machine test before the work is considered complete. VM validation belongs in `tests/HomeHarbor.FullE2E.Tests`, the interactive-test VM flow, or another VM-oriented runner. New integration coverage must be VM-oriented, not host-local. Do not mark validation complete unless VM evidence such as reports, screenshots, logs, or test output exists; if VM validation cannot be run, treat the work as blocked or unverified instead of substituting a local integration test. Run frontend `typecheck` and `build` before UI changes; run `pnpm docs:build` before documentation changes. Full E2E requires libvirt, the `default` network, `guestfish`, `fastboot`, and the image build toolchain.

## Commit & Pull Request Guidelines

No local Git history is present, so use concise imperative commits with an optional scope, such as `api: add OTA status validation` or `frontend: tighten setup form errors`. Pull requests should include the problem, approach, test results, linked issues, and screenshots or recordings for visible UI changes. Call out appliance, storage, OTA, or security-impacting changes explicitly.

## Security & Configuration Tips

Do not commit private release keys, passphrases, machine-specific secrets, or channel credentials. Keep development secrets in `.work/` or environment variables such as `HOMEHARBOR_RELEASE_PRIVATE_KEY` and `HOMEHARBOR_DATA_PASSPHRASE_FILE`. Treat `artifacts/` as generated output unless a release workflow requires it.
