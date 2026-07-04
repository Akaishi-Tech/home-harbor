# Contributing to HomeHarbor

HomeHarbor contributions should keep the source tree reproducible and avoid
local machine assumptions.

## Development Setup

Use the SDK pinned in `global.json` and install frontend/docs dependencies from
the root pnpm workspace before working on the UI or docs:

```bash
pnpm install
```

Run the API locally:

```bash
dotnet run --project src/HomeHarbor.Api/HomeHarbor.Api.csproj
```

Run the frontend locally:

```bash
pnpm frontend:dev
```

Run the docs locally:

```bash
pnpm docs:dev
```

## Code Guidelines

- Prefer C# for appliance, release, OTA, installer, recovery, validation,
  manifest, cryptographic, path-safety, and channel guard logic.
- Keep shell scripts as thin POSIX entrypoints or wrappers around external
  system tools.
- Do not implement JSON parsing, manifest canonicalization, OTA slot decisions,
  Secure Boot policy, release guard policy, channel deployment invariants, or
  path traversal checks in shell when C# can own them.
- Keep generated outputs out of source commits. `artifacts/`, `.work/`,
  `src/HomeHarbor.Api/wwwroot/`, `node_modules/`, and `bin/obj` are ignored.

## Testing

Run local unit and frontend checks before submitting changes:

```bash
dotnet test tests/HomeHarbor.Tests/HomeHarbor.Tests.csproj
pnpm frontend:typecheck
pnpm frontend:build
pnpm docs:build
```

Do not add or run integration tests directly on the local machine. Full-system
validation must run inside a VM and belongs in
`tests/HomeHarbor.FullE2E.Tests` or VM-oriented build scripts.

## Pull Requests

Pull requests should include:

- The problem being solved.
- The approach and user-visible impact.
- Test results.
- Appliance, storage, OTA, recovery, or security impact, when relevant.
- Screenshots or recordings for visible UI changes.

Never include private release keys, passphrases, channel credentials,
machine-local state, or generated appliance media in a pull request.
