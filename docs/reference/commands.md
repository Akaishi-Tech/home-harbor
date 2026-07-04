# Command Cheatsheet

## Makefile

```bash
make help
make setup
make dev
make api-dev
make frontend-dev
make docs-dev
make backend-build
make backend-lint
make test-unit
make frontend-typecheck
make frontend-build
make docs-build
make check
make system-plan ARCH=x86_64 VERSION=0.1.0-dev
make efi-loader
make avb-helper
make init-helper
make appliance-build
make system-build
make arch-package VERSION=0.1.0-dev
```

## Workspace

```bash
pnpm install
pnpm frontend:dev
pnpm frontend:typecheck
pnpm frontend:build
pnpm frontend:preview
pnpm docs:dev
pnpm docs:build
pnpm docs:preview
pnpm docs:deploy
```

## API Development

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/HomeHarbor.Api/HomeHarbor.Api.csproj -- database-migrate
dotnet run --project src/HomeHarbor.Api/HomeHarbor.Api.csproj
```

`database-migrate` applies EF Core migrations and writes the automation token.

## Unit Tests

```bash
dotnet test tests/HomeHarbor.Tests/HomeHarbor.Tests.csproj
```

## Appliance Build

```bash
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- plan 0.1.0-dev
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- system-plan system/x86_64/system/manifest.yml 0.1.0-dev "$(pwd)"
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- system-build system/x86_64/system/manifest.yml 0.1.0-dev "$(pwd)"
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- arch-package 0.1.0-dev "$(pwd)"
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- kernel-package-plan system/x86_64/kernel 0.1.0-dev "$(pwd)"
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- kernel-package-build system/x86_64/kernel 0.1.0-dev generic "$(pwd)"
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- build-efi-loader artifacts/HomeHarborBoot.efi "$(pwd)"
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- build-homeharbor-avb artifacts/homeharbor-avb "$(pwd)"
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- build-homeharbor-init artifacts/homeharbor-verity "$(pwd)"
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- generate-efi-avb-public-key artifacts/homeharbor-avb-public-key.h "$(pwd)"
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- system-app-payload <app-key> <rootfs> <destination> <package> [package...]
```

## Installer

```bash
HomeHarbor.Installer --mode full
HomeHarbor.Installer --mode tiny
HomeHarbor.Installer --mode tiny --external-payload-dir <dir>
HomeHarbor.Installer install-disk --list-disks
HomeHarbor.Installer install-disk --target <disk> --system-ota <bundle> --kernel-ota <bundle> --public-key <pem> --confirm "ERASE <disk>"
HomeHarbor.Installer install-disk --target <disk> --channel-file <channel.json> --public-key <pem> --dry-run
HomeHarbor.Installer verify-ota-manifest <manifest> <ed25519-public-key.pem>
HomeHarbor.Installer boot-state init <esp> [slot] [root-slot] [recovery-slot]
HomeHarbor.Installer boot-state set-default <esp> <slot> [root-slot]
HomeHarbor.Installer boot-state set-oneshot <esp> <boot-slot> <root-slot|recovery> [mode]
HomeHarbor.Installer boot-state set-recovery <esp> <slot>
HomeHarbor.Installer boot-state clear-next <esp>
HomeHarbor.Installer boot-state path <esp>
```

Storage unlock options such as `--data-unlock`, `--data-passphrase-file`, and `--tpm2-pcrs` have moved from `install-disk` to Web OOBE storage setup.

## Agent

```bash
HomeHarbor.Agent firstboot
HomeHarbor.Agent postgres-init
HomeHarbor.Agent postgres-bootstrap
HomeHarbor.Agent ensure-caddy-config
HomeHarbor.Agent render-caddyfile
HomeHarbor.Agent storage-health
HomeHarbor.Agent ensure-smb-config
HomeHarbor.Agent apply-smb
HomeHarbor.Agent apply-containers
HomeHarbor.Agent apply-system-apps
HomeHarbor.Agent boot-attempt
HomeHarbor.Agent boot-success
HomeHarbor.Agent ota-apply <bundle> --public-key <pem>
HomeHarbor.Agent ota-commit
HomeHarbor.Agent storage-apply
HomeHarbor.Agent storage-postapply
HomeHarbor.Agent verify-ota-manifest <manifest> <public-key>
HomeHarbor.Agent boot-state set-oneshot <esp> <boot-slot> <root-slot|recovery> [mode]
HomeHarbor.Agent super table <super-device> <logical-partition>
HomeHarbor.Agent super create <mapper-name> <super-device> <logical-partition> [mode]
HomeHarbor.Agent super remove <mapper-name>
```

## Recovery

```bash
HomeHarbor.Recovery
HomeHarbor.Recovery --fastboot-tcp
```

## Common Environment Variables

| Variable | Purpose |
| --- | --- |
| `HOMEHARBOR_CHANNEL` | Build or runtime channel |
| `HOMEHARBOR_VERSION` | Current version exposed by the API |
| `HOMEHARBOR_OTA_CHANNEL_FILE` | OTA channel file path |
| `HOMEHARBOR_OTA_PENDING` | Pending OTA metadata path |
| `HOMEHARBOR_RELEASE_PRIVATE_KEY` | Release manifest signing private key |
| `HOMEHARBOR_RELEASE_PUBLIC_KEY` | Release manifest public key |
| `HOMEHARBOR_RELEASE_KEY_ID` | Channel-safe release key id |
| `HOMEHARBOR_SECURE_BOOT` | Secure Boot build switch |
| `HOMEHARBOR_SECURE_BOOT_KEY` | Secure Boot and AVB signing key |
| `HOMEHARBOR_SECURE_BOOT_CERT` | Secure Boot signing certificate |
| `HOMEHARBOR_SECURE_BOOT_ENROLL` | Installer Secure Boot enrollment mode |
| `HOMEHARBOR_SECURE_BOOT_MOK_ENROLL` | MOK enrollment mode for raw UKI installs |
| `HOMEHARBOR_CHANNEL_PUBLISH_HOST` | Channel publish host |
| `HOMEHARBOR_CHANNEL_PUBLISH_PATH` | Channel publish path |
| `HOMEHARBOR_CHANNEL_URL` | Channel URL |
| `HOMEHARBOR_FASTBOOTD_LISTEN` | Recovery fastboot TCP listen address |
| `HOMEHARBOR_FASTBOOTD_PORT` | Recovery fastboot TCP port |
| `HOMEHARBOR_INSTALLER_UI` | Installer UI selection (`auto` or `tui`) |
