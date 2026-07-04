# Installer and Recovery

HomeHarbor provides full/tiny live installers and a recovery environment. The installer writes release payloads to a target disk. Recovery provides local status, return-to-normal-boot actions, and fastboot TCP service.

## Installer

The entry project is `src/HomeHarbor.Installer`. It normally starts a TUI and also provides three command families:

```bash
HomeHarbor.Installer install-disk --target /dev/sdX --system-ota PATH --kernel-ota PATH --public-key PATH --confirm "ERASE /dev/sdX"
HomeHarbor.Installer install-disk --target /dev/sdX --channel-file PATH --public-key PATH --dry-run
HomeHarbor.Installer verify-ota-manifest <manifest> <ed25519-public-key.pem>
HomeHarbor.Installer boot-state init|set-default|set-oneshot|set-recovery|clear-next|path <esp> [...]
```

Important installer arguments:

| Name | Default | Purpose |
| --- | --- | --- |
| `--mode full\|tiny` | `tiny` | Installer mode |
| `--payload-dir PATH` | `/opt/homeharbor-installer/payloads` | Payload directory inside the ISO |
| `--system-ota PATH` | empty | Explicit system OTA |
| `--external-payload-dir DIR` | auto search | External payload search directory; pass more than once for multiple directories |
| `--public-key PATH` | `/etc/homeharbor/release.pub.pem` | Manifest verification public key |
| `--stable-channel-url URL` | GitHub stable channel URL | Stable channel metadata |
| `--daily-channel-url URL` | GitHub daily channel URL | Daily channel metadata |

`install-disk` accepts `--list-disks`, `--target`, `--system-ota`, `--system-manifest`, `--kernel-ota`, `--channel-file`, `--public-key`, `--verify-script`, `--confirm`, `--yes`, and `--dry-run`. Storage unlock options are no longer installer arguments; `--data-unlock`, `--data-passphrase-file`, and `--tpm2-pcrs` intentionally fail and point users to Web OOBE storage setup.

## Full and Tiny ISOs

Live installer images are built by the C# image builder pipeline.

```text
HomeHarbor.ImageBuilder system-build <manifest> <version> [repo-root]
```

A full ISO can carry the full payload. A tiny ISO is better suited to downloading payloads from release/channel metadata.

## Boot State

Boot state is managed by `HomeHarbor.Tooling.BootState` and EFI boot variables. Installer and Agent both expose `boot-state` subcommands:

- `init`
- `set-default`
- `set-oneshot`
- `set-recovery`
- `clear-next`
- `path`

OTA and recovery transitions should update boot state through these tools instead of hand-writing EFI variables or loader files.

## Recovery Console

The entry project is `src/HomeHarbor.Recovery`. By default it starts an interactive console:

- `s`: show `homeharbor-fastbootd.service` status.
- `r`: reboot.
- `n`: set default normal boot to A/A and reboot.
- `q`: redraw.

The default state directory is `/var/lib/homeharbor/recovery`.

## Fastboot TCP

Start it with:

```bash
HomeHarbor.Recovery --fastboot-tcp
```

Defaults:

- Listen address: `HOMEHARBOR_FASTBOOTD_LISTEN` or `0.0.0.0`.
- Port: `HOMEHARBOR_FASTBOOTD_PORT` or `5554`.

The service implements the fastboot TCP handshake and supports `getvar`, `download`, `flash`, `erase`, `set_active`, `reboot`, and `reboot-recovery`.

## VM Validation

Installation and recovery cannot be replaced by ordinary local integration tests. Full validation needs libvirt, the `default` network, `guestfish`, `fastboot`, and the image build toolchain, and must run through VM-oriented tests or scripts.
