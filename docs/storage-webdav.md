# Storage and WebDAV

HomeHarbor organizes user data by family. API, WebDAV, SMB, backup, and media indexing must all enter physical storage through the same path policy to prevent traversal and cross-family access.

## Data Root

The release data root is:

```text
/homeharbor-data
```

Family data paths have this shape:

```text
/homeharbor-data/families/{familyId:N}/{area}/{relativePath}
```

`area` comes from `StorageArea`:

| Area | Directory | Purpose |
| --- | --- | --- |
| `Files` | `files` | General family files |
| `Photos` | `photos` | Photos and media assets |
| `Backups` | `backups` | Local backup data |

## Path Safety

`StoragePathPolicy.NormalizeDavPath` and `ResolvePhysicalPath` are the core safety boundary for WebDAV and storage access.

The policy requires:

- Empty paths normalize to `/`.
- Percent-encoding must be valid.
- Backslashes normalize to `/`.
- Paths must begin with `/`.
- NUL bytes are rejected.
- `.` and `..` segments are rejected.
- The physical path must equal the area root or be inside the area root.

If a path escapes its area, the API throws a path-related exception and middleware returns `400`.

## WebDAV

The WebDAV controller is `src/HomeHarbor.Api/Controllers/WebDavController.cs` and uses:

```text
/dav/{area}/{*path=}
```

Authentication uses Basic Auth, not the frontend bearer token. WebDAV tokens are created during setup and can also be managed through `/api/webdav-tokens`.

Typical client configuration:

- URL: `https://<homeharbor-host>/dav/files/`
- Username: the username returned by setup or the token API.
- Password: the WebDAV token.

Use different areas for different sync tools. Photo sync should use `/dav/photos/`; backup tools should use `/dav/backups/`.

## Storage OOBE

Storage OOBE chooses encryption, unlock mode, data targets, filesystem, and RAID mode during first-run setup. The default remains LUKS2 plus Btrfs recommended mode. XFS is available for exactly one target unless explicit RAID5/RAID6 is selected. Btrfs and XFS RAID5/RAID6 plans use `mdadm` underneath and warn before apply. ZFS uses LUKS2 underneath each selected target and native pool layouts (`single`, `mirror`, `raid10`, `raidz1`, or `raidz2`); Web OOBE maps RAID5 to RAIDZ1 and RAID6 to RAIDZ2. ZFS is available only when the `zfs` kernel channel is booted with its signed `zfs-utils` EROFS `/usr` addon mounted.

The installer leaves the main disk's remaining space as an unformatted `data-candidate` partition; OOBE can use that target, one or more separate disks, or multiple encrypted targets with Btrfs RAID profiles, mdadm-backed RAID5/RAID6, or ZFS pool layouts.

- `GET /api/setup/storage/inventory`
- `POST /api/setup/storage/recommendation`
- `POST /api/setup/storage/plan`
- `POST /api/setup/storage/apply`
- `GET /api/setup/storage/status`

Default protected partition labels:

```text
esp, boot_a, boot_b, super, state, recovery_a, recovery_b,
vbmeta_a, vbmeta_b, data, data-candidate
```

`MinimumInstallableBytes` defaults to 32 GiB. Inventory includes explicit storage targets (`main-reserved` or `whole-disk`) plus eligibility reasons and filesystem capability status. A plan lists destructive targets, filesystem, RAID mode, RAID backend (`filesystem` or `mdadm`), resolved low-level Btrfs profile or ZFS layout metadata, warnings, unlock mode (`passphrase` or `tpm2`), confirmation phrase, and whether bootloader unlock is required.

## Storage Health

`StorageHealthService` and `POST /api/storage/health/check` drive periodic checks. The frontend reads `GET /api/storage/health` and the storage state included in dashboard overview.

The health check endpoint requires an automation token so normal users cannot trigger appliance-internal probing.

## SMB and Container Data

SMB shares and managed containers use API desired state as the source of truth. The agent renders and applies system configuration. New shares or containers should keep desired state and audit data in the API, with the agent responsible for applying runtime state.
