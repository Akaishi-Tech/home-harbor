# HomeHarbor

HomeHarbor is a home appliance control plane. It combines a .NET control API, a
Vite React frontend, WebDAV storage helpers, appliance image tooling, OTA
packaging, live installer generation, recovery tools, and VM-based full-system
validation.

## Repository Layout

- `src/HomeHarbor.Api`: ASP.NET Core API, EF Core data model, auth, storage,
  dashboard, OTA, SMB, container, and recovery endpoints.
- `src/HomeHarbor.Core`: domain records and shared appliance model types.
- `src/HomeHarbor.WebDav`: WebDAV HTTP method and XML helpers.
- `src/HomeHarbor.Tooling`: shared C# tooling for manifest, boot, security,
  tar, and path-safety logic.
- `src/HomeHarbor.Agent`, `src/HomeHarbor.ImageBuilder`,
  `src/HomeHarbor.Installer`, `src/HomeHarbor.Recovery`: appliance-side tools.
- `frontend`: Vite React application. Release assets are generated into
  `src/HomeHarbor.Api/wwwroot` and are not committed.
- `docs`: VitePress documentation site. English is the default locale and
  Simplified Chinese is served from `/zh/`; Cloudflare Pages deployment uses
  `docs/wrangler.toml`.
- `build`, `scripts`, `os`, `packaging/arch`: image, OTA, installer, systemd,
  mkinitcpio, and Arch packaging entrypoints.
- `tests/HomeHarbor.Tests`: local MSTest unit tests.
- `tests/HomeHarbor.FullE2E.Tests`: VM-oriented full-system tests.

## Development

Install frontend and docs dependencies:

```bash
pnpm install
```

Run the API:

```bash
dotnet run --project src/HomeHarbor.Api/HomeHarbor.Api.csproj
```

Run the Vite frontend with `/api` and `/dav` proxied to the API:

```bash
pnpm frontend:dev
```

Build frontend release assets:

```bash
pnpm frontend:build
```

Run the docs site:

```bash
pnpm docs:dev
```

The root `Makefile` provides thin aliases for the common backend, frontend,
docs, and appliance commands:

```bash
make help
make setup
make dev
make build
make check
```

`ARCH` defaults to `x86_64` and is used to derive
`MANIFEST=system/$(ARCH)/system/manifest.yml`, so future architecture
descriptors can use the same targets:

```bash
make system-plan ARCH=x86_64
make system-plan ARCH=<future-arch>
```

`BOOT_HELPER_TARGETS` defaults to `efi-loader avb-helper init-helper` and can
be adjusted when a future architecture needs a different boot helper set.

`pnpm frontend:build` writes generated files under `src/HomeHarbor.Api/wwwroot`.
`pnpm docs:build` writes generated files under `docs/.vitepress/dist`. Both
directories are ignored so the source tree stays clean; packaging and release
flows rebuild generated assets when needed.

## Testing

Local validation is limited to unit tests and frontend checks:

```bash
dotnet test tests/HomeHarbor.Tests/HomeHarbor.Tests.csproj
pnpm frontend:typecheck
pnpm frontend:build
pnpm docs:build
```

Do not run integration or full E2E tests directly on the local machine. Full
function validation belongs in a VM and requires libvirt, the `default` network,
`guestfish`, `fastboot`, and the appliance image build toolchain.

## Packaging and Release

Build Arch packages through the C# image builder entrypoint:

```bash
make arch-package VERSION=0.1.0
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- arch-package 0.1.0 "$(pwd)"
```

The GitHub release workflow builds in an Arch Linux container and publishes
daily or stable release assets. Before enabling it on the public repository,
configure these GitHub Actions secrets:

- `HOMEHARBOR_RELEASE_PRIVATE_KEY_PEM`
- `HOMEHARBOR_RELEASE_PUBLIC_KEY_PEM`
- `HOMEHARBOR_RELEASE_KEY_ID`
- `HOMEHARBOR_SECURE_BOOT_KEY_PEM`
- `HOMEHARBOR_SECURE_BOOT_CERT_PEM`

Keep private release keys, Secure Boot signing keys, passphrases, channel
credentials, and machine-local state outside the repository. Use `.work`
or environment variables for local development secrets.

## Secure Boot Enrollment

HomeHarbor Secure Boot releases are signed by the public certificate checked in
at `certs/homeharbor-secure-boot.crt`.

Certificate details:

- Subject: `CN=HomeHarbor Secure Boot`
- Issuer: `CN=HomeHarbor Secure Boot`
- Valid from: `2026-06-30T14:18:41Z`
- Valid until: `2036-06-27T14:18:41Z`
- DER SHA256 fingerprint:
  `dd56573ce2b017f074bd2514ac9a152d0f95394a77bd6cc2c2f0f39ac538ff41`

For `secure-boot-raw-uki` installs, the installer queues MOK enrollment with
`mokutil --import` during installation. `mokutil` asks for a one-time password.
After installation, reboot, choose `Enroll MOK` in MOK Manager, verify the
HomeHarbor Secure Boot certificate, enter the one-time password, and then enable
Secure Boot in firmware setup.

The MOK flow requires the boot chain to start with a Microsoft-signed shim. By
default, `secure-boot-raw-uki` builds download Fedora's
`shim-x64-16.1-8.x86_64.rpm` from:

```text
https://kojipkgs.fedoraproject.org/packages/shim/16.1/8/x86_64/shim-x64-16.1-8.x86_64.rpm
```

The expected RPM SHA256 is:

```text
ee8787bb9fbd13fcce73de0501938075c24efd78bc1a590826f0c01c7a10986b
```

HomeHarbor extracts `BOOTX64.EFI` from `EFI/BOOT/BOOTX64.EFI` and MokManager
from `EFI/fedora/mmx64.efi`. Set `HOMEHARBOR_OTA_SHIM_SOURCE` and
`HOMEHARBOR_OTA_MOK_MANAGER_SOURCE` only when overriding those defaults.
HomeHarbor installs the signed HomeHarbor boot selector as
`EFI/BOOT/grubx64.efi` so shim can verify it through the enrolled MOK.

## License

HomeHarbor is licensed under GPL-3.0-only. See `LICENSE` and
`THIRD_PARTY_NOTICES.md`.
