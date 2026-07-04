# App Format

HomeHarbor App Format v1 (HHAF v1) is the signed application format used by the app store. It describes both container apps and signed system apps with one manifest shape.

## App Manifest

An app manifest is JSON with:

- `schemaVersion: 1`
- `kind: "homeharbor.app"`
- `appKey`, `version`, `channel`
- `displayName`, `title`, `description`, `category`
- `recommendedInSetup`
- `visibleRoles`
- `install`
- `signatureAlgorithm: "Ed25519"`, `signedPayloadSha256`, `signingKeyId`, `signature`

The signed payload is written by C# with fixed field ordering. Remote manifests are ignored unless their signature validates with the HomeHarbor release public key and their `channel` matches the appliance OTA channel.

Container app manifests use `install.type: "container"` and map to the existing safe `ManagedContainerSpecService` subset: image, ports, environment, volumes, and command. Privileged containers, device mappings, added capabilities, and raw Podman arguments are not part of HHAF v1.

System app manifests use `install.type: "system"` and `mode: "usr-overlay"`. They point to the existing signed system payload manifest through `install.manifestUrl`, declare wrapper `commands`, and can define a `hotCheck` command.

## Store Index

The app store index is also signed JSON:

- `schemaVersion: 1`
- `kind: "homeharbor.app-store"`
- `channel`
- `generatedAt`
- `apps: [{ appKey, version, manifestUrl, manifestSha256 }]`

By default HomeHarbor reads `${HOMEHARBOR_APP_STORE_BASE_URL}/index.json`, or `HOMEHARBOR_APP_STORE_INDEX_URL` when set. If the remote index or any app manifest fails validation, HomeHarbor falls back to the built-in catalog.

## ZFS Utilities

`zfs-utils` is no longer a system app. It is packaged as the `zfs-utils` kernel overlay addon for the `zfs` kernel channel. The addon is an EROFS image referenced by the signed kernel OTA manifest `addons` array and mounted over `/usr` by the initramfs after SHA-256 verification from the `state` partition store.

The generic kernel channel has no addons. ZFS storage capability is available only when the running kernel has the `zfs` module and the mounted addon provides `/usr/bin/zfs` and `/usr/bin/zpool`.
