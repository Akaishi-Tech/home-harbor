# Testing Strategy

HomeHarbor keeps fast local checks separate from VM-level full-system validation. Appliance installation, partitioning, boot, recovery, systemd, fastboot, and OTA behavior touch sensitive host resources and should not run as ordinary local tests.

## Tests Allowed Locally

Local checks:

```bash
dotnet test tests/HomeHarbor.Tests/HomeHarbor.Tests.csproj
pnpm frontend:typecheck
pnpm frontend:build
pnpm docs:build
```

`tests/HomeHarbor.Tests` uses MSTest. Test classes should be named `*Tests`; method names should be descriptive and use underscores, for example:

```text
ResolvePhysicalPath_Stays_Inside_Family_Area
```

## Tests Not Allowed Locally

Do not run integration, full-function, or E2E tests directly on the development host. These behaviors must run in a VM:

- Writing real disks or partition tables.
- Building and booting full appliance images.
- Validating live installers.
- Validating recovery fastboot.
- Validating OTA reboot/rollback.
- Exercising the complete lifecycle of appliance systemd units.

## Full E2E

Full-system tests live in:

```text
tests/HomeHarbor.FullE2E.Tests
```

They require:

- libvirt.
- the `default` network.
- `guestfish`.
- `fastboot`.
- the image build toolchain.

Interactive install validation should be driven by VM-oriented scripts and
operator checklists, not by moving destructive steps into ordinary unit tests.
Use a unique disposable libvirt domain, keep disks/screenshots/logs/reports
under `.work/interactive-test/<domain>/`, and never destroy a domain or delete a
disk that was not created for the validation run.

For a screenshot-driven installer and first-run Web OOBE validation:

1. Preflight `virsh`, `virt-install`, `qemu-img`, `curl`, `jq`, `python3`,
   `sha256sum`, and `stat`.
2. Use `HOMEHARBOR_E2E_ISO` when set; otherwise select the latest full
   installer ISO from `artifacts/channels/channel.json` or `artifacts/`.
3. Boot with UEFI and Secure Boot disabled, a graphical display, USB tablet
   input, and a serial console.
4. Capture screenshots before each installer action and after each major
   transition. Confirm the target disk visually before typing destructive
   confirmation text such as `ERASE /dev/vda`.
5. Use `tests/HomeHarbor.FullE2E.Tests/Tools/virsh-console-io.py` only for
   serial text entry when the VM display and serial output match.
6. Boot the installed appliance with an extra data disk for Storage OOBE, wait
   for the login prompt, discover the DHCP address, and check
   `/api/system/health` plus the root proxy before browser automation.
7. Complete Web OOBE through accessible browser roles, labels, and visible text.
   Prefer applying Storage OOBE to the extra VM disk; if the environment cannot
   safely apply the plan, record the skip reason.
8. Save a report with ISO path/SHA256, domain and disk paths, screenshots,
   installer milestones, mouse-smoke result, appliance IP, health response,
   OOBE outcome, and any deviations.

Do not put generated recovery codes, WebDAV tokens, WireGuard configs, or SMB
passwords in public logs, screenshots, pull requests, or final summaries.

The GitHub release workflow builds artifacts and channel metadata but currently
sets `HOMEHARBOR_RELEASE_SKIP_FULL_E2E=1`. A formal appliance release still
needs separate VM validation evidence, such as reports, screenshots, logs, or
test output saved from the VM-oriented flow.

## Choosing the Right Test Level

Good unit-test candidates:

- Path normalization and containment.
- Manifest canonical payloads and signature failure paths.
- Release/security guards.
- Boot state file logic.
- Storage OOBE plan/recommendation pure logic.
- API controller behavior in a test host.

Good VM-test candidates:

- systemd unit order.
- Real Caddy/Samba/Podman apply.
- mkinitcpio hooks.
- EFI/AVB/EROFS boot.
- Installer writes to a real block device.
- fastboot TCP and recovery reboot.

## Docs Tests

Build the docs with:

```bash
pnpm docs:build
```

Successful builds generate `docs/.vitepress/dist`, which is ignored and should not be committed.
