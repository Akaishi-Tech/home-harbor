# Third-Party Notices

HomeHarbor is licensed under GPL-3.0-only. The source tree also depends on
third-party components that remain under their respective licenses.

## .NET and NuGet Dependencies

The backend and appliance tools use .NET packages declared in project files,
including ASP.NET Core, Entity Framework Core, Npgsql, BCrypt.Net-Next, QRCoder,
and Microsoft.IdentityModel libraries. Restore the projects with the .NET SDK to
inspect the exact package graph for a given revision.

## Frontend Dependencies

The frontend dependency graph is declared in `frontend/package.json` and locked
in the root `pnpm-lock.yaml`. It includes React, Vite, TypeScript, Tailwind CSS,
Radix UI, TanStack Query, React Hook Form, Zod, Lucide React, and related build
tooling.

## Appliance and Operating System Components

Image and installer builds consume Arch Linux packages listed under `os/` and
`packaging/arch/`. Generated images, packages, OTA bundles, and installer media
are build outputs and are not part of the source release.

## Secure Boot Shim

Secure Boot installer flows may download Fedora's Microsoft-signed shim RPM from
the URL documented in `README.md`. The build verifies the expected SHA256 before
extracting shim and MokManager assets.

## Fonts

The frontend uses Inter and JetBrains Mono through `@fontsource-variable`
packages. Generated font files are produced as part of the frontend build and
are not committed in the source tree.
