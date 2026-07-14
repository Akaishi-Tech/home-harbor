# HomeHarbor SELinux packages

HomeHarbor builds every SELinux-specific Arch package from a maintained
`PKGBUILD` in this directory. The build never enables or downloads binary
packages from the Arch Linux Hardened repository. Upstream Arch and Arch Linux
Hardened recipes may be imported as source provenance, then pinned, reviewed,
rebased, and built in HomeHarbor's disposable rootless build environment.

`manifest.yml` is the authoritative recipe inventory, output-package list, and
dependency-aware build order. The repeated `util-linux-selinux` and
`systemd-selinux` steps intentionally converge their cyclic build dependency.
All archives are placed in a controlled local pacman repository that precedes
the official `core`, `extra`, and `multilib` repositories; no other binary
repository is configured.

Build the version-independent SELinux dependency package set once with:

```sh
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- \
  selinux-dependency-build "$PWD/artifacts/dependencies/selinux" \
  "$PWD/.work/selinux-dependencies" "$PWD"
```

The cache key is printed by `selinux-dependency-key`; it covers the manifest,
every maintained recipe input, patches, service files, shared signing keys, and
the dependency-builder contract, but not HomeHarbor's version or generated
makepkg state. A restored cache must
pass `selinux-dependency-verify`, which checks the exact declared package set,
package metadata, target architecture, and archive hashes.

Build the complete versioned package set from that verified cache with:

```sh
HOMEHARBOR_CHANNEL=dev \
HOMEHARBOR_SELINUX_DEPENDENCY_CACHE="$PWD/artifacts/dependencies/selinux" \
  dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- \
  arch-package 0.1.0-dev "$PWD"
```

When `HOMEHARBOR_SELINUX_DEPENDENCY_CACHE` is unset, local development retains
the self-contained behavior and rebuilds the dependency packages before the
versioned HomeHarbor packages.

The builder validates each recipe's declared outputs, source checksums and PGP
signatures, clears the caller's desktop and credential environment, installs
only locally built SELinux variants, records archive/source provenance, and
rejects stale package sets during image and kernel-channel builds.

The policy is MCS-based and boots enforcing. `homeharbor-selinux-policy` gives
the appliance executables a dedicated auditable domain and supplies the file
contexts needed for immutable EROFS images, writable state, encrypted data,
Samba, PostgreSQL, and rootless Podman. The initial `homeharbor_t` domain is
deliberately unconfined until VM AVC evidence covers all appliance workflows;
future tightening belongs in that maintained policy package.

The libsemanage store remains writable under `/var/lib/selinux`, as its read
and transaction locks require. Before sealing each EROFS image, the builder
moves a hashed copy to `/usr/lib/homeharbor/selinux-store`; an early boot unit
atomically restores that exact store after the persistent `/var` mount. This
also updates the runtime module inventory when an OTA switches root images.
