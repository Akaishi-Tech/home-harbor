# 安全模型

HomeHarbor 的安全边界包括认证、路径安全、发布签名、Secure Boot/AVB、channel 部署防护和机密管理。

## 用户认证

用户通过 `POST /api/identity/login` 获取 bearer token。JWT 校验不仅验证签名和过期时间，还要求数据库中存在对应 member session。这样可以通过 logout 或 session 过期撤销 token。

默认 authorization fallback policy 要求用户 JWT。只有明确标注匿名的 setup、login、health 和 SPA fallback 可以绕过。

## 首次 TLS 信任

HomeHarbor 为每台设备使用独立的 Caddy internal CA。在浏览器中输入 setup code、
recovery code 或密码之前，先从 `http://homeharbor.local/homeharbor-ca.crt`
下载公开 CA 证书。只有当证书的 SHA-256 指纹与设备物理控制台显示的值逐字一致时，
才能安装。物理控制台才是经过认证的通道；HTTP 下载只负责传输公开证书字节，不能
单独提供可信性。

`homeharbor-tls-trust.service` 会在 Caddy 创建 CA 后显示指纹；证书或物理控制台尚未
就绪时会重试。Caddy 状态目录会让 CA 跨重启保持不变。如果该状态被替换或指纹意外
变化，应停止操作并重新建立物理信任，不能直接绕过浏览器的证书警告。

## 自动化 token

appliance 内部服务使用 automation token。API 迁移命令会通过 `JwtTokenService.WriteAutomationTokenAsync()` 写到 `HomeHarbor:Automation:TokenPath`：

```bash
dotnet run --project src/HomeHarbor.Api/HomeHarbor.Api.csproj -- database-migrate
```

自动化 token 只能访问带 `AuthorizationPolicies.Automation` 的端点。不要把自动化端点复用到用户 UI，也不要让前端持有 automation token。

## WebDAV token

WebDAV 使用 Basic Auth。token 与用户登录 token 分离，便于给同步客户端配置有限范围的长期凭据。WebDAV token 泄露时应通过 `/api/webdav-tokens` 删除并重新签发。

## 路径安全

所有家庭文件路径必须通过 `StoragePathPolicy`：

- 拒绝不完整 percent-encoding。
- 拒绝 NUL。
- 拒绝 `.` 和 `..`。
- 拒绝逃逸 area root 的物理路径。

SMB、WebDAV、media index、backup 等访问家庭数据的模块应复用同一策略，避免出现第二套路径解释。

## 发布签名

OTA manifest 使用 Ed25519 签名。校验流程：

1. 要求 `signatureAlgorithm=Ed25519`。
2. 生成 canonical payload。
3. 校验 `signedPayloadSha256`。
4. base64 解码 `signature`。
5. 使用 release public key 验签。

manifest 缺字段、schema 不为 `1`、boot mode 非当前允许值、channel 非允许值时都应失败。

## Secure Boot 与 AVB

secure boot 模式通过 `HOMEHARBOR_SECURE_BOOT=1` 开启，对应 boot mode `secure-boot-raw-uki`。发布和安装流程应使用channel 签名材料，不能用 dev unsigned 开关绕过。

AVB/verity 用于 root、modules、firmware 和 recovery payload。boot cmdline 携带 sealed digest 与 verity 输入，initramfs 结合 boot selector 写入的 EFI state 验证并映射只读 EROFS root。

数据存储加密在 Web OOBE 中配置。passphrase 模式会在 `HomeHarborBoot` 启动 UKI 之前提示输入密码，并通过 volatile EFI variable 传给 initramfs；initramfs 消费后立即删除该变量，再打开 LUKS 设备。TPM2 模式在 storage apply 时注册自动解锁，同时保留 OOBE 中设置的 recovery passphrase 作为 fallback。

## channel 防护

channel 发布前运行 unit 与 build-plan 检查：

```bash
dotnet test tests/HomeHarbor.Tests/HomeHarbor.Tests.csproj
dotnet run --project tools/system-build/src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- plan 0.1.0
```

release 检查会拒绝：

- 缺失 artifact。
- 不安全 release key id。
- 私钥权限过宽。
- key 位于仓库内部。
- manifest 签名或 payload hash 不一致。
- channel host/path 不安全。
- dev unsigned flag 出现在非 dev channel。

## 机密管理

不要提交：

- release private key。
- 用于 AVB 的 Secure Boot 签名 key。
- Secure Boot signing key。
- passphrase。
- channel credential。
- 机器本地 token。
- 生成的 appliance media。

本地开发可使用 `.work/` 或环境变量保存临时材料。`.pem`、`.key`、`.raw`、`.iso`、`.img` 等文件默认被忽略，只有公开 Secure Boot certificate 有明确例外。
