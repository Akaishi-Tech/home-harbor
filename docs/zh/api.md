# 后端 API

`HomeHarbor.Api` 同时服务 JSON API、WebDAV 和release 前端静态文件。开发环境下还会开放 OpenAPI。

## 启动与中间件顺序

API 启动流程：

1. 读取 `HomeHarbor:*` 配置。
2. 根据 `HomeHarbor:Api:UnixSocketPath` 可选监听 Unix socket。
3. 配置基于 Valkey 的 distributed cache；仅在 Development 且配置的 Unix socket 不可用时退回内存 cache。
4. 注册 EF Core Npgsql `HomeHarborDbContext`。
5. 配置 JWT bearer、Basic Auth、authorization fallback policy。
6. 注册 CORS、controllers、OpenAPI、核心服务。
7. 创建数据根目录。
8. 启用默认文件、静态文件、异常到 JSON 的转换、CORS、pre-storage request gate、认证授权、controllers 和 SPA fallback。

database migration 与 automation token 写出由显式迁移命令负责：

```bash
dotnet run --project src/HomeHarbor.Api/HomeHarbor.Api.csproj -- database-migrate
```

## 配置节

| 配置 | 默认值 | 说明 |
| --- | --- | --- |
| `HomeHarbor:Api:UnixSocketPath` | 空 | 可选 API Unix socket listener |
| `HomeHarbor:Api:HttpUpstream` | `127.0.0.1:5181` | 未配置 Unix socket 时的 TCP upstream |
| `HomeHarbor:Storage:DataRoot` | `/homeharbor-data` | 家庭数据根目录 |
| `HomeHarbor:Storage:MaxUploadBytes` | `21474836480` | WebDAV 上传上限 |
| `HomeHarbor:Database:ConnectionString` | Unix socket PostgreSQL | API 数据库 |
| `HomeHarbor:Cache:UnixSocketPath` | `/run/valkey/homeharbor.sock` | distributed cache 的 Valkey Unix socket |
| `HomeHarbor:Cache:InstanceName` | `homeharbor:` | distributed cache key 前缀 |
| `HomeHarbor:Cache:OverviewTtlSeconds` | `30` | dashboard overview cache TTL |
| `HomeHarbor:Jwt:Issuer` | `HomeHarbor` | JWT issuer |
| `HomeHarbor:Jwt:Audience` | `HomeHarbor.Frontend` | JWT audience |
| `HomeHarbor:Jwt:SigningKeyPath` | `/var/lib/homeharbor/api/jwt-signing.key` | 本机 JWT signing key |
| `HomeHarbor:Jwt:AccessTokenDays` | `30` | 用户 token 有效期 |
| `HomeHarbor:Automation:TokenPath` | `/run/homeharbor/automation.jwt` | 自动化 token 输出路径 |
| `HomeHarbor:Automation:TokenDays` | `365` | automation token 有效期 |
| `HomeHarbor:Runtime:RequestDirectory` | `/run/homeharbor` | runtime 请求目录 |
| `HomeHarbor:Runtime:SmbCredentialDirectory` | `/run/homeharbor-smb-credentials` | SMB credential material |
| `HomeHarbor:Runtime:DataUnlockMetadataPath` | `/var/lib/homeharbor/security/data-unlock.json` | storage unlock metadata |
| `HomeHarbor:StorageOobe:StateDirectory` | `/var/lib/homeharbor/storage` | 存储 OOBE 状态目录 |
| `HomeHarbor:StorageOobe:OneShotPassphrasePath` | `/run/homeharbor/storage-apply.passphrase` | storage apply 一次性 passphrase |
| `HomeHarbor:StorageOobe:RequestPath` | `/run/homeharbor/storage-apply.request` | agent storage apply request |
| `HomeHarbor:StorageOobe:MinimumInstallableBytes` | `34359738368` | 可安装存储目标最小容量 |
| `HomeHarbor:Frontend:AllowedOrigins` | 本地 Vite origins | 前端 dev server CORS origins |

开发环境会把数据根、数据库连接串、cache socket/key 前缀、JWT key、automation token 和 runtime 目录覆盖到相对路径。Development 中如果配置的 Valkey socket 不存在，API 会记录 warning 并使用内存 distributed cache。

Storage OOBE ready 之前，pre-storage gate 只允许 `/api/setup*` 和 `/api/system/health`。其他 `/api` 与 `/dav` 请求会返回带 setup 提示的 `503`。

## 认证入口

`POST /api/identity/login` 是匿名入口。登录成功返回 bearer token、过期时间、member 和 family。

用户 token 校验包含两层：

- JWT issuer、audience、signature、lifetime。
- 数据库 member session 存在且未过期，JWT `jti` hash 与 session token hash 一致。

`POST /api/identity/logout` 删除当前 session。`GET /api/identity/session` 返回当前 session。

`POST /api/identity/recover-owner` 在验证当前一次性 recovery code 后，匿名重置初始 primary owner 的密码。对于 primary owner password hash 和 family recovery-code hash 都缺失的旧版安装，此端点也接受设备物理控制台显示的一次性 setup code；任一凭据完成登记后，该升级路径立即关闭。恢复目标始终是唯一 display name 与 `FamilySpace.OwnerDisplayName` 相同的成员；新增其他 owner 不会改变恢复目标。恢复成功会撤销该 owner 的 session、使已提交的 code 失效，并只返回一次替换 code。

`POST /api/identity/recovery-code/rotate` 仅允许已认证 owner 调用，并要求提供 `currentPassword`。它可为旧版本创建且没有 recovery-code hash 的家庭补建 code，也可轮换现有 code。数据库只保存 hash；新明文 code 仅返回一次，必须立即安全保存。轮换尝试受限流保护。初始 primary owner 不允许删除。

## Setup 入口

Setup 相关 API 匿名开放，因为设备首次启动时还没有用户：

- `GET /api/setup`：返回初始化状态、pairing 信息和 storage OOBE 状态。
- `GET /api/setup/pairing`：创建或读取 pairing ticket。
- `GET /api/setup/pairing.svg`：返回 pairing QR SVG。
- `POST /api/setup`：加密存储就绪后创建家庭、owner、设备、初始 WebDAV token 和 recovery code。
- `GET /api/setup/storage/inventory`：枚举磁盘、挂载、明确的 OOBE storage targets 和 filesystem capabilities。
- `POST /api/setup/storage/recommendation`：根据家庭规模和使用画像推荐布局。
- `POST /api/setup/storage/plan`：按 target kind、filesystem、RAID mode/backend、解析后的 profile/layout 元数据、warnings 和 unlock mode 生成 destructive storage plan。
- `POST /api/setup/storage/apply`：携带一次性 recovery passphrase，确认并应用 storage plan。
- `GET /api/setup/storage/status`：读取 apply 状态。

## 用户控制 API

登录后前端主要使用这些资源：

- `/api/home/overview`
- `/api/family/members`
- `/api/devices`
- `/api/backups/*`
- `/api/media/*`
- `/api/remote/wireguard/*`
- `/api/vault/items`
- `/api/webdav-tokens`
- `/api/smb/*`
- `/api/apps/*`
- `/api/containers`
- `/api/networking/*`
- `/api/ota/*`
- `/api/security/policy`
- `/api/storage/health`
- `/api/sync/states`

详细 route 列表见 [API 路由表](./reference/api-routes.md)。

## 自动化 API

自动化端点只接受 automation token。它们面向 appliance 内部服务，不应暴露给普通用户流程。automation token 由 `database-migrate` 命令写入 `HomeHarbor:Automation:TokenPath`。

| 端点 | 消费者 | 用途 |
| --- | --- | --- |
| `GET /api/networking/proxy/caddyfile` | `HomeHarbor.Agent render-caddyfile` | 根据 reverse proxy routes 渲染 Caddyfile |
| `GET /api/smb/config/smb.conf` | `HomeHarbor.Agent apply-smb` | 渲染 Samba 配置 |
| `GET /api/smb/reconcile/desired` | SMB reconcile | 读取 share/credential desired state |
| `POST /api/smb/reconcile/result` | SMB reconcile | 回写 runtime state |
| `GET /api/containers/reconcile/desired` | container reconcile | 读取 desired containers |
| `POST /api/containers/reconcile/result` | container reconcile | 回写 runtime state |
| `GET /api/apps/reconcile/desired` | system app reconcile | 读取签名 system app desired state |
| `POST /api/apps/reconcile/result` | system app reconcile | 回写下载/激活状态 |
| `POST /api/storage/health/check` | storage timer/service | 执行 storage health check |

## WebDAV

WebDAV 路径为：

```text
/dav/{area}/{*path}
```

`area` 映射到 `StorageArea`：`files`、`photos`、`backups`。路径会经过 percent-encoding 校验、反斜杠归一、`.`/`..` 拒绝、NUL 拒绝和物理路径 containment 校验。

WebDAV 使用 Basic Auth。用户名和 token 来自 setup 或 `/api/webdav-tokens`，token scope 决定可访问范围。

## 错误处理

API 会把路径相关 `InvalidOperationException` 转成 `400` JSON，把 `UnauthorizedAccessException` 转成 `403` JSON。认证失败仍由 ASP.NET Core authentication/authorization 返回 `401` 或 `403`。
