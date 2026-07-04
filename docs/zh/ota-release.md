# OTA 与发布

HomeHarbor 的 OTA 与发布链路覆盖 update bundle、签名、boot payload、安装器 artifact 和 readiness 检查。

## Release channel

当前 channel 集合由 `HomeHarbor.Tooling.ReleaseChannel` 约束。常见 channel：

- `dev`
- `daily`
- `stable`

channel 读取优先级：

1. `HOMEHARBOR_OTA_CHANNEL_FILE` 指向的文件，默认 `/var/lib/homeharbor/ota/channel`。
2. `HOMEHARBOR_CHANNEL`。
3. 默认 `dev`。

## OTA status API

`GET /api/ota/status` 返回：

- 当前版本：`HOMEHARBOR_VERSION` 或 `0.1.0-dev`。
- 当前 channel。
- 可用 channel。
- update state：存在 pending manifest 时为 `pending-reboot`，否则为 `idle`。
- stage/apply endpoint。

`POST /api/ota/stage` 和 `POST /api/ota/apply` 目前接收 manifest metadata，返回 accepted。实际 appliance updater 负责验证 bundle、写入 inactive slot、更新 boot 环境并 reboot。

## Manifest 要求

manifest 包含这些要求：

- `bootMode` 为 `raw-uki` 或 `secure-boot-raw-uki`。
- channel 必须是当前允许值。
- `packageKind=system` 时 `type=full-system`。
- `packageKind=kernel` 时 `type=kernel-only`。

manifest canonical payload 由固定字段顺序生成。签名算法为 Ed25519，签名校验通过 `openssl pkeyutl -verify -pubin -rawin` 完成。

## Full-system bundle

full-system bundle 包含 rootfs、modules、firmware、recovery、bootloader、fallback boot、vbmeta 和 boot payload。secure boot 模式额外包含 MokManager hash。

关键 payload：

- `rootfs.img`
- `modules.img`
- `firmware.img`
- `recovery.img`
- `boot.efi`
- `bootloader.efi`
- `vbmeta_a.img`
- `vbmeta_b.img`

每个 payload 都应有 hash，manifest 记录这些 hash，并对 canonical payload 签名。

## A/B 与 AVB

镜像布局包含 boot A/B、recovery A/B、vbmeta A/B 和 super 内 root/modules/firmware A/B 逻辑分区。构建过程会把 EROFS payload 写入固定大小逻辑镜像，并附加 AVB hashtree。
AVB descriptor 的 partition name 是 slot-transparent 的（`root`、`modules`、`firmware`、`recovery`）；`vbmeta_a.img` 和 `vbmeta_b.img` 是为了 A/B 分区布局保留的镜像副本。

generic boot cmdline 携带 sealed boot inputs，实际 A/B 选择由 boot selector 通过 EFI state 发布：

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

`HOMEHARBOR_SECURE_BOOT=1` 时 boot mode 为 `secure-boot-raw-uki`。默认 Secure Boot enrollment 流程依赖 Microsoft-signed shim 和 MokManager。HomeHarbor 安装 signed boot selector，使 shim 能通过已 enrollment 的 MOK 验证。

release 构建不能使用 unsigned 例外。非 dev channel 缺少 release public key、Secure Boot 签名 key 或启用 unsigned flag 时应失败。

## 常用构建命令

查看 plan：

```bash
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- plan 0.1.0-dev
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- system-plan system/x86_64/system/manifest.yml 0.1.0-dev "$(pwd)"
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- kernel-package-plan system/x86_64/kernel 0.1.0-dev "$(pwd)"
```

生成基础镜像和 OTA 输入：

```bash
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- system-build system/x86_64/system/manifest.yml 0.1.0-dev "$(pwd)"
```

构建 Arch 包和 kernel-channel artifacts：

```bash
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- arch-package 0.1.0-dev "$(pwd)"
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- kernel-package-build system/x86_64/kernel 0.1.0-dev generic "$(pwd)"
```

直接构建 boot helpers：

```bash
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- build-efi-loader artifacts/HomeHarborBoot.efi "$(pwd)"
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- build-homeharbor-avb artifacts/homeharbor-avb "$(pwd)"
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- build-homeharbor-init artifacts/homeharbor-verity "$(pwd)"
```

## Channel metadata

release metadata 由 C# 构建流水线生成，并由 GitHub release workflow 消费。

## GitHub release workflow

GitHub release workflow 会从 `artifacts/channels/{version}` 发布 daily 和
stable 资产。上传内容包括 system OTA artifact、generic 与 ZFS kernel-channel
OTA artifact、live installer ISO 和 channel metadata。

workflow 在 artifact build 阶段设置 `HOMEHARBOR_RELEASE_SKIP_FULL_E2E=1`，
因此不会自己完成 appliance VM 验证。正式 appliance 发布前，需要单独运行并归档
VM 证据，例如 FullE2E/interactive report、screenshot、log 或测试输出。
