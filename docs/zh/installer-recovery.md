# 安装器与恢复

HomeHarbor 提供 full/tiny live installer 和 recovery 环境。installer 负责把 release payload 写入目标磁盘；recovery 负责本地状态查看、恢复正常 boot，以及提供 fastboot TCP 服务。

## Installer

入口项目是 `src/HomeHarbor.Installer`。默认运行 TUI，也提供三个命令：

```bash
HomeHarbor.Installer install-disk --target /dev/sdX --system-ota PATH --kernel-ota PATH --public-key PATH --confirm "ERASE /dev/sdX"
HomeHarbor.Installer install-disk --target /dev/sdX --channel-file PATH --public-key PATH --dry-run
HomeHarbor.Installer verify-ota-manifest <manifest> <ed25519-public-key.pem>
HomeHarbor.Installer boot-state init|set-default|set-oneshot|set-recovery|clear-next|path <esp> [...]
```

主要 installer 参数：

| 名称 | 默认值 | 说明 |
| --- | --- | --- |
| `--mode full\|tiny` | `tiny` | installer mode |
| `--payload-dir PATH` | `/opt/homeharbor-installer/payloads` | ISO 内 payload 路径 |
| `--system-ota PATH` | 空 | 显式 system OTA |
| `--external-payload-dir DIR` | 自动搜索 | 外部 payload 搜索目录；可重复传多个目录 |
| `--public-key PATH` | `/etc/homeharbor/release.pub.pem` | manifest 校验公钥 |
| `--stable-channel-url URL` | GitHub stable channel URL | stable channel metadata |
| `--daily-channel-url URL` | GitHub daily channel URL | daily channel metadata |

`install-disk` 支持 `--list-disks`、`--target`、`--system-ota`、`--system-manifest`、`--kernel-ota`、`--channel-file`、`--public-key`、`--verify-script`、`--confirm`、`--yes` 和 `--dry-run`。storage unlock 选项已经不再属于 installer 参数；`--data-unlock`、`--data-passphrase-file` 和 `--tpm2-pcrs` 会明确失败并提示改用 Web OOBE storage setup。

## Full 与 tiny ISO

live installer 镜像由 C# image builder 流水线构建。

```text
HomeHarbor.ImageBuilder system-build <manifest> <version> [repo-root]
```

full ISO 可以携带完整 payload；tiny ISO 更适合从 release/channel 下载 payload。

开始 Web OOBE 前，应从 `http://homeharbor.local/homeharbor-ca.crt` 安装设备公开
CA，并与物理控制台显示的 SHA-256 指纹逐字核对。浏览器仍显示不受信任证书警告时，
不要输入 setup code 或密码。

## Boot state

boot state 由 `HomeHarbor.Tooling.BootState` 和 EFI boot variables 管理。Installer 与 Agent 都暴露 `boot-state` 子命令，支持：

- `init`
- `set-default`
- `set-oneshot`
- `set-recovery`
- `clear-next`
- `path`

OTA 或 recovery 切换时应通过这些工具更新 boot 状态，而不是手写 EFI variable 或 loader 文件。

## Recovery console

入口项目是 `src/HomeHarbor.Recovery`。默认启动交互式 console：

- `s`：显示 `homeharbor-fastbootd.service` 状态。
- `u`：开启 10 分钟的物理授权窗口，并显示一次性会话令牌。
- `l`：撤销授权窗口及已认证的 fastboot 会话。
- `r`：reboot。
- `n`：保留现有健康的默认 normal-boot 槽位并 reboot。
- `q`：redraw。

默认 state 目录是 `/var/lib/homeharbor/recovery`。

## Fastboot TCP

通过参数启动：

```bash
HomeHarbor.Recovery --fastboot-tcp
```

默认监听：

- 地址：`HOMEHARBOR_FASTBOOTD_LISTEN` 或 `0.0.0.0`
- 端口：`HOMEHARBOR_FASTBOOTD_PORT` 或 `5554`

服务实现 fastboot TCP handshake，并支持 `getvar`、`download`、`flash`、`erase`、`set_active`、`reboot` 和 `reboot-recovery`。锁定时仍可使用只读 `getvar`。每个破坏性命令必须同时满足 10 分钟的物理授权窗口和当前 TCP 会话认证：

1. 在物理 recovery console 按 `u`、输入 `UNLOCK`，并抄录只显示一次的令牌。
2. 在可信工作站启动认证 loopback proxy，并在其隐藏输入提示中输入令牌：

   ```bash
   dotnet run --project src/HomeHarbor.Recovery/HomeHarbor.Recovery.csproj -- --fastboot-auth-proxy <appliance-ip>
   ```

3. 保持 proxy 运行，通过它执行一次标准 fastboot 操作，例如 `fastboot -s tcp:127.0.0.1:5555 flash root_a root.img`。

proxy 只监听 loopback，并让认证和标准 fastboot 操作共用同一个上游 TCP 会话。令牌只能使用一次，也不能授权后续连接。已认证会话断开、按 `l`、生成新令牌或授权到期都会撤销授权。原始令牌不会写入磁盘或命令日志。

## VM 验证

安装和恢复不能在开发主机上用集成测试替代。完整验证需要 libvirt、`default` network、`guestfish`、`fastboot` 和 image build toolchain，并由 VM-oriented tests 或脚本执行。
