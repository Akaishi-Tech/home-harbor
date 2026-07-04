# OTA and Release

HomeHarbor's OTA and release pipeline covers update bundles, signing, boot payloads, installer artifacts, and readiness checks.

## Release Channel

Channels are constrained by `HomeHarbor.Tooling.ReleaseChannel`. Common channels:

- `dev`
- `daily`
- `stable`

Channel resolution order:

1. The file pointed to by `HOMEHARBOR_OTA_CHANNEL_FILE`, defaulting to `/var/lib/homeharbor/ota/channel`.
2. `HOMEHARBOR_CHANNEL`.
3. `dev`.

## OTA Status API

`GET /api/ota/status` returns:

- Current version: `HOMEHARBOR_VERSION` or `0.1.0-dev`.
- Current channel.
- Available channels.
- Update state: `pending-reboot` when pending metadata exists, otherwise `idle`.
- Stage/apply endpoints.

`POST /api/ota/stage` and `POST /api/ota/apply` currently accept manifest metadata and return accepted responses. The appliance updater is responsible for validating the bundle, writing the inactive slot, updating the boot environment, and rebooting.

## Manifest Requirements

Manifests include these requirements:

- `bootMode` is `raw-uki` or `secure-boot-raw-uki`.
- `channel` is allowed.
- `packageKind=system` uses `type=full-system`.
- `packageKind=kernel` uses `type=kernel-only`.

The canonical manifest payload is generated with fixed field ordering. The signature algorithm is Ed25519 and verification uses `openssl pkeyutl -verify -pubin -rawin`.

## Full-System Bundle

A full-system bundle contains rootfs, modules, firmware, recovery, bootloader, fallback boot, vbmeta, and boot payloads. Secure Boot mode also includes a MokManager hash.

Key payloads:

- `rootfs.img`
- `modules.img`
- `firmware.img`
- `recovery.img`
- `boot.efi`
- `bootloader.efi`
- `vbmeta_a.img`
- `vbmeta_b.img`

Each payload should have a hash. The manifest records those hashes and signs the canonical payload.

## A/B and AVB

The image layout includes boot A/B, recovery A/B, vbmeta A/B, and A/B root/modules/firmware logical partitions inside `super`. The build writes EROFS payloads into fixed-size logical images and appends AVB hashtrees.
AVB descriptor partition names are slot-transparent (`root`, `modules`, `firmware`, `recovery`); `vbmeta_a.img` and `vbmeta_b.img` are mirrored copies for the A/B partition layout.

Generic boot cmdline carries sealed boot inputs while the boot selector publishes the active A/B choice through EFI state:

- `rd.homeharbor.verity=1`
- `homeharbor.boot_mode`
- `homeharbor.boot_generic`
- `homeharbor.super`
- `homeharbor.kernel_release`
- `homeharbor.vbmeta_a_digest`
- `homeharbor.vbmeta_b_digest`
- `homeharbor.modules_a_verity`
- `homeharbor.modules_b_verity`
- `homeharbor.firmware_a_verity`
- `homeharbor.firmware_b_verity`
- `homeharbor.recovery_a_verity`
- `homeharbor.recovery_b_verity`
- `homeharbor.version`

## Secure Boot

`HOMEHARBOR_SECURE_BOOT=1` sets boot mode to `secure-boot-raw-uki`. The default Secure Boot enrollment flow depends on Microsoft-signed shim and MokManager. HomeHarbor installs a signed boot selector so shim can verify it through the enrolled MOK.

Release builds must not use unsigned exceptions. Non-dev channels should fail when release public key, Secure Boot signing key, or signing material is missing, or when unsigned flags are enabled.

## Common Build Commands

Inspect plans:

```bash
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- plan 0.1.0-dev
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- system-plan system/x86_64/system/manifest.yml 0.1.0-dev "$(pwd)"
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- kernel-package-plan system/x86_64/kernel 0.1.0-dev "$(pwd)"
```

Build base image and OTA inputs:

```bash
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- system-build system/x86_64/system/manifest.yml 0.1.0-dev "$(pwd)"
```

Build Arch packages and kernel-channel artifacts:

```bash
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- arch-package 0.1.0-dev "$(pwd)"
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- kernel-package-build system/x86_64/kernel 0.1.0-dev generic "$(pwd)"
```

Build boot helpers directly:

```bash
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- build-efi-loader artifacts/HomeHarborBoot.efi "$(pwd)"
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- build-homeharbor-avb artifacts/homeharbor-avb "$(pwd)"
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- build-homeharbor-init artifacts/homeharbor-verity "$(pwd)"
```

## Channel Metadata

Release metadata is produced by the C# build pipeline and consumed by the GitHub release workflow.

## GitHub Release Workflow

The GitHub release workflow publishes daily and stable assets from
`artifacts/channels/{version}`. It uploads system OTA artifacts, generic and ZFS
kernel-channel OTA artifacts, live installer ISOs, and channel metadata.

The workflow sets `HOMEHARBOR_RELEASE_SKIP_FULL_E2E=1` during artifact builds,
so it does not itself complete appliance VM validation. Before publishing a
formal appliance release, run and archive separate VM evidence such as
FullE2E/interactive reports, screenshots, logs, or test output.
