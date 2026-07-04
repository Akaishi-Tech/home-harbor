# 测试策略

HomeHarbor 把本地快速验证和 VM 级完整系统验证严格分开。原因很直接：appliance 的安装、分区、boot、recovery、systemd、fastboot 和 OTA 行为会触碰宿主机敏感资源，不能作为普通本机测试运行。

## 本地允许的测试

本地可以运行：

```bash
dotnet test tests/HomeHarbor.Tests/HomeHarbor.Tests.csproj
pnpm frontend:typecheck
pnpm frontend:build
pnpm docs:build
```

`tests/HomeHarbor.Tests` 使用 MSTest。测试类命名为 `*Tests`，方法名使用描述性下划线格式，例如：

```text
ResolvePhysicalPath_Stays_Inside_Family_Area
```

## 本地不应运行的测试

不要在本地直接运行 integration、full-function 或 E2E 测试。以下行为必须放进 VM：

- 写真实磁盘或分区表。
- 构造 full appliance image 后启动。
- 验证 live installer。
- 验证 recovery fastboot。
- 验证 OTA reboot/rollback。
- 依赖 systemd appliance unit 的完整生命周期。

## Full E2E

完整系统测试位于：

```text
tests/HomeHarbor.FullE2E.Tests
```

它面向 VM，需要：

- libvirt。
- `default` network。
- `guestfish`。
- `fastboot`。
- image build toolchain。

交互式安装验证应通过 VM-oriented 脚本和操作清单驱动，不能把
destructive 步骤搬到普通单元测试。使用唯一的一次性 libvirt domain，
把 disk、screenshot、log 和 report 保存在
`.work/interactive-test/<domain>/`，并且不要销毁或删除不是本次验证创建的
domain 或 disk。

进行 screenshot-driven 安装器和首次启动 Web OOBE 验证时：

1. 预检 `virsh`、`virt-install`、`qemu-img`、`curl`、`jq`、`python3`、
   `sha256sum` 和 `stat`。
2. 如果设置了 `HOMEHARBOR_E2E_ISO`，使用该 ISO；否则从
   `artifacts/channels/channel.json` 或 `artifacts/` 选择最新 full installer
   ISO。
3. 使用 UEFI、关闭 Secure Boot、启用图形显示、USB tablet 输入和 serial
   console 启动。
4. 每次 installer 操作前以及关键状态变化后都截图。输入 `ERASE /dev/vda`
   等 destructive confirmation 前，必须先从截图确认目标 disk。
5. 只有在 VM display 与 serial output 匹配时，才用
   `tests/HomeHarbor.FullE2E.Tests/Tools/virsh-console-io.py` 输入 serial 文本。
6. 安装后的 appliance 启动时挂载额外 data disk，用于 Storage OOBE；等待
   login prompt，获取 DHCP 地址，并检查 `/api/system/health` 与根路径代理。
7. 通过浏览器可访问的 role、label 和 visible text 完成 Web OOBE。优先把
   Storage OOBE 应用到额外 VM disk；如果环境无法安全 apply，则记录 skip
   原因。
8. 保存 report，包含 ISO path/SHA256、domain 与 disk path、截图、installer
   milestone、mouse smoke 结果、appliance IP、health response、OOBE 结果和
   偏离项。

不要把生成的 recovery code、WebDAV token、WireGuard config 或 SMB password
写入公开 log、screenshot、PR 或最终总结。

GitHub release workflow 会构建 artifact 和 channel metadata，但当前设置了
`HOMEHARBOR_RELEASE_SKIP_FULL_E2E=1`。正式 appliance 发布仍然需要单独保存
VM 验证证据，例如 VM-oriented 流程生成的 report、screenshot、log 或测试输出。

## 新增测试时的边界

适合单元测试的内容：

- 路径归一化与 containment。
- manifest canonical payload 和签名失败路径。
- release/security guard。
- boot state 文件逻辑。
- storage OOBE plan/recommendation 纯逻辑。
- API controller 在内存/测试宿主中的行为。

适合 VM 测试的内容：

- systemd unit 顺序。
- Caddy/Samba/Podman 实际 apply。
- mkinitcpio hook。
- EFI/AVB/EROFS boot。
- installer 对真实 block device 的写入。
- fastboot TCP 与 recovery reboot。

## 文档站测试

文档站构建命令：

```bash
pnpm docs:build
```

构建成功会生成 `docs/.vitepress/dist`。该目录被忽略，不提交。
