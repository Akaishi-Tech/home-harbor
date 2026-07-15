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
- `releaseSequence` is a positive integer that increases for every release.
- `packageKind=system` uses `type=full-system`.
- `packageKind=kernel` uses `type=kernel-only`.

The canonical manifest payload, including `releaseSequence`, is generated with fixed field ordering. The signature algorithm is Ed25519 and verification uses `openssl pkeyutl -verify -pubin -rawin`.

For network OTA, the updater compares the signed target sequence with the trusted
sequences embedded in the current immutable root and signed kernel command line.
Updates for one release are deliberately split and applied kernel first:

1. Apply the `kernel/kernel-only` bundle for sequence N. It stages the inactive
   boot, modules, firmware, and recovery slots, and atomically updates the signed
   ESP boot selector and existing fallback Secure Boot paths.
2. Reboot into and commit that kernel.
3. Apply the `system/full-system` bundle for the same sequence N. Its target
   sequence must exactly equal the sequence in the currently running signed
   kernel; a newer system bundle fails before any target partition or ESP write
   and instructs the operator to apply the matching kernel bundle first.
4. Reboot into and commit the new root slot.

Each bundle must strictly advance the component it replaces. A missing,
malformed, replayed, or out-of-order sequence fails closed. The sequence is
independent of the display version and must never be reset or reused.

This check does not prevent a physical offline attacker from replacing every
current root, boot, and verification-state artifact with an older, internally
consistent signed set. Secure Boot and AVB prove authenticity, not freshness.
Preventing that class of rollback requires a hardware-backed monotonic anchor,
such as a TPM NV counter or enforced AVB rollback index.

## System Bundle

A `system/full-system` bundle updates only the root component and its AVB
metadata. It contains:

- `rootfs.img`
- `vbmeta_a.img`
- `vbmeta_b.img`

It intentionally does not carry kernel, modules, firmware, recovery, or ESP
boot assets. Those belong to the matching kernel bundle and must be running
before this bundle can be staged.

## Kernel Bundle

A `kernel/kernel-only` bundle contains:

- `modules.img`
- `firmware.img`
- `recovery.img`
- `boot.efi`
- `HomeHarborBoot.efi`
- `BOOTX64.EFI`
- `mmx64.efi` when Secure Boot is enabled

`HomeHarborBoot.efi` and its signed `bootloaderHash` are mandatory. During
apply, the updater installs it atomically at
`/EFI/HomeHarbor/HomeHarborBoot.efi`, refreshes an existing
`/EFI/BOOT/BOOTX64.EFI`, and in Secure Boot mode refreshes existing
`grubx64.efi` and `mmx64.efi` compatibility paths. Each temporary file is
flushed, renamed on the ESP, read back and hash-checked, then the ESP is synced
before one-shot boot state or pending metadata is changed.

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
- `homeharbor.release_sequence`
- `homeharbor.version`

## Secure Boot

`HOMEHARBOR_SECURE_BOOT=1` sets boot mode to `secure-boot-raw-uki`. The default Secure Boot enrollment flow depends on Microsoft-signed shim and MokManager. HomeHarbor installs a signed boot selector so shim can verify it through the enrolled MOK.

Release builds must not use unsigned exceptions. Non-dev channels should fail when release public key, Secure Boot signing key, or signing material is missing, or when unsigned flags are enabled.

## Common Build Commands

Inspect plans:

```bash
dotnet run --project tools/system-build/src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- plan 0.1.0-dev
dotnet run --project tools/system-build/src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- system-plan system/x86_64/system/manifest.yml 0.1.0-dev "$(pwd)"
dotnet run --project tools/system-build/src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- kernel-package-plan system/x86_64/kernel 0.1.0-dev "$(pwd)"
```

Build base image and OTA inputs:

```bash
HOMEHARBOR_RELEASE_SEQUENCE=1234 \
  dotnet run --project tools/system-build/src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- \
  system-build system/x86_64/system/manifest.yml 0.1.0-dev "$(pwd)"
```

Use a positive value greater than every sequence previously released for the
same appliance lineage. `release-build` has the same requirement.

Build Arch packages and kernel-channel artifacts:

```bash
dotnet run --project tools/system-build/src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- arch-package 0.1.0-dev "$(pwd)"
dotnet run --project tools/system-build/src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- kernel-package-build system/x86_64/kernel 0.1.0-dev generic "$(pwd)"
```

Build boot helpers directly:

```bash
dotnet run --project tools/system-build/src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- build-efi-loader artifacts/HomeHarborBoot.efi "$(pwd)"
dotnet run --project tools/system-build/src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- build-homeharbor-avb artifacts/homeharbor-avb "$(pwd)"
dotnet run --project tools/system-build/src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- build-homeharbor-init artifacts/homeharbor-verity "$(pwd)"
```

## Channel Metadata

Release metadata is produced by the C# build pipeline and consumed by the GitHub release workflow.

## GitHub Release Workflow

The GitHub release workflow publishes daily and stable assets from
`artifacts/channels/{version}`. It uploads system OTA artifacts, generic and ZFS
kernel-channel OTA artifacts, live installer ISOs, and channel metadata.

The workflow passes the monotonically increasing GitHub `run_number` as
`HOMEHARBOR_RELEASE_SEQUENCE` to the image-build container.

The workflow sets `HOMEHARBOR_RELEASE_SKIP_FULL_E2E=1` during artifact builds,
so it does not itself complete appliance VM validation. Before publishing a
formal appliance release, run and archive separate VM evidence such as
FullE2E/interactive reports, screenshots, logs, or test output.
