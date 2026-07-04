# 命令速查

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

## API 开发

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/HomeHarbor.Api/HomeHarbor.Api.csproj -- database-migrate
dotnet run --project src/HomeHarbor.Api/HomeHarbor.Api.csproj
```

`database-migrate` 会执行 EF Core migrations 并写出 automation token。

## 单元测试

```bash
dotnet test tests/HomeHarbor.Tests/HomeHarbor.Tests.csproj
```

## Appliance 构建

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

`--data-unlock`、`--data-passphrase-file`、`--tpm2-pcrs` 等 storage unlock 选项已从 `install-disk` 移到 Web OOBE storage setup。

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

## 常用环境变量

| 变量 | 用途 |
| --- | --- |
| `HOMEHARBOR_CHANNEL` | 构建或运行 channel |
| `HOMEHARBOR_VERSION` | API 暴露的当前版本 |
| `HOMEHARBOR_OTA_CHANNEL_FILE` | OTA channel 文件路径 |
| `HOMEHARBOR_OTA_PENDING` | pending OTA metadata 路径 |
| `HOMEHARBOR_RELEASE_PRIVATE_KEY` | release manifest 签名私钥 |
| `HOMEHARBOR_RELEASE_PUBLIC_KEY` | release manifest 公钥 |
| `HOMEHARBOR_RELEASE_KEY_ID` | channel-safe release key id |
| `HOMEHARBOR_SECURE_BOOT` | secure boot 构建开关 |
| `HOMEHARBOR_SECURE_BOOT_KEY` | secure boot 和 AVB 签名 key |
| `HOMEHARBOR_SECURE_BOOT_CERT` | secure boot 签名证书 |
| `HOMEHARBOR_SECURE_BOOT_ENROLL` | installer secure boot enrollment mode |
| `HOMEHARBOR_SECURE_BOOT_MOK_ENROLL` | raw UKI install 的 MOK enrollment mode |
| `HOMEHARBOR_CHANNEL_PUBLISH_HOST` | channel 发布主机 |
| `HOMEHARBOR_CHANNEL_PUBLISH_PATH` | channel 发布路径 |
| `HOMEHARBOR_CHANNEL_URL` | channel URL |
| `HOMEHARBOR_FASTBOOTD_LISTEN` | recovery fastboot TCP 监听地址 |
| `HOMEHARBOR_FASTBOOTD_PORT` | recovery fastboot TCP 端口 |
| `HOMEHARBOR_INSTALLER_UI` | installer UI 选择（`auto` 或 `tui`） |
