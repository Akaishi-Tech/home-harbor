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
- `releaseSequence` 是正整数，并且每次发布都必须递增。
- `packageKind=system` 时 `type=full-system`。
- `packageKind=kernel` 时 `type=kernel-only`。

manifest canonical payload（包括 `releaseSequence`）由固定字段顺序生成。签名算法为 Ed25519，签名校验通过 `openssl pkeyutl -verify -pubin -rawin` 完成。

执行网络 OTA 时，updater 会把已签名的目标 sequence 与当前 immutable root 和
signed kernel command line 中的可信 sequence 比较。同一 release 的更新被明确拆分，
并且必须按 kernel-first 顺序执行：

1. 先应用 sequence N 的 `kernel/kernel-only` bundle。它会写入 inactive boot、
   modules、firmware 和 recovery slot，并原子更新已签名的 ESP boot selector 以及
   已存在的 Secure Boot fallback 路径。
2. reboot 进入该 kernel 并 commit。
3. 再应用相同 sequence N 的 `system/full-system` bundle。其 target sequence 必须与
   当前正在运行的 signed kernel sequence 完全相等；如果 system bundle sequence 比
   当前 kernel 更新，updater 会在写入任何目标分区或 ESP 之前 fail closed，并提示先
   应用匹配的 kernel bundle。
4. reboot 进入新的 root slot 并 commit。

每个 bundle 都必须严格推进它负责的 component。sequence 缺失、格式错误、重放或
顺序错误都会 fail closed。sequence 与展示用 version 相互独立，不能重置或重复使用。

此检查无法阻止具备物理离线写入能力的攻击者，把当前 root、boot 和验证状态整体替换
成一套更旧但内部一致、签名仍然有效的 artifact。Secure Boot 和 AVB 能证明真实性，
但不能证明新鲜度。要阻止这类 rollback，需要 TPM NV counter 或强制 AVB rollback
index 等硬件支持的单调锚点。

## System bundle

`system/full-system` bundle 只更新 root component 及其 AVB metadata，包含：

- `rootfs.img`
- `vbmeta_a.img`
- `vbmeta_b.img`

它不会携带 kernel、modules、firmware、recovery 或 ESP boot asset；这些内容属于
匹配的 kernel bundle，并且必须先启动该 kernel，才能 stage system bundle。

## Kernel bundle

`kernel/kernel-only` bundle 包含：

- `modules.img`
- `firmware.img`
- `recovery.img`
- `boot.efi`
- `HomeHarborBoot.efi`
- `BOOTX64.EFI`
- 启用 Secure Boot 时的 `mmx64.efi`

`HomeHarborBoot.efi` 及其已签名的 `bootloaderHash` 是必需项。apply 时，updater
会把它原子安装到 `/EFI/HomeHarbor/HomeHarborBoot.efi`，更新已存在的
`/EFI/BOOT/BOOTX64.EFI`，并在 Secure Boot 模式下更新已存在的 `grubx64.efi` 和
`mmx64.efi` 兼容路径。每个临时文件都会先 flush，再在 ESP 上 rename，随后回读并
校验 hash；ESP 完成 sync 后，才会修改 one-shot boot state 或 pending metadata。

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
- `homeharbor.release_sequence`
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
HOMEHARBOR_RELEASE_SEQUENCE=1234 \
  dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- \
  system-build system/x86_64/system/manifest.yml 0.1.0-dev "$(pwd)"
```

该值必须为正数，并且大于同一 appliance lineage 过去发布的所有 sequence；
`release-build` 也有相同要求。

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

workflow 会把单调递增的 GitHub `run_number` 作为
`HOMEHARBOR_RELEASE_SEQUENCE` 传进 image-build container。

workflow 在 artifact build 阶段设置 `HOMEHARBOR_RELEASE_SKIP_FULL_E2E=1`，
因此不会自己完成 appliance VM 验证。正式 appliance 发布前，需要单独运行并归档
VM 证据，例如 FullE2E/interactive report、screenshot、log 或测试输出。
