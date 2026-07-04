# Contributing

HomeHarbor contributions should keep the source tree reproducible and avoid local-machine assumptions.

## Principles

- Keep module boundaries clear.
- Put business rules in C# first; keep shell scripts as thin entrypoints.
- Do not commit generated outputs.
- Do not commit secrets.
- Clearly call out appliance, storage, OTA, recovery, or security impact in PRs.

## Checks Before Submitting

Backend or tooling:

```bash
dotnet test tests/HomeHarbor.Tests/HomeHarbor.Tests.csproj
```

Frontend:

```bash
pnpm frontend:typecheck
pnpm frontend:build
```

Docs:

```bash
pnpm docs:build
```

Build command smoke check:

```bash
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- plan 0.1.0
```

## Pull Request Contents

PRs should include:

- The problem being solved.
- The approach.
- User-visible impact.
- Test results.
- Appliance, storage, OTA, recovery, or security impact.
- Screenshots or recordings for visible UI changes.

## Documentation Changes

When adding a feature, update docs for:

- User-facing or developer-facing commands.
- New environment variables.
- New API endpoints.
- New generated outputs.
- New safety boundaries.
- New release or installer behavior.

The docs site is built with VitePress. Add new pages to the sidebar in `docs/.vitepress/config.ts`.
