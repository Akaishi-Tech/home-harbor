# API 路由表

本页按 controller 汇总当前 HTTP 路由。除标注匿名或自动化外，路由默认要求用户 JWT。

## Apps

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/apps/catalog` | 应用 catalog |
| `GET` | `/api/apps/installs` | 已安装/desired installs |
| `POST` | `/api/apps/installs` | 创建 install |
| `DELETE` | `/api/apps/installs/{id}` | 删除 install |
| `POST` | `/api/apps/installs/{id}/state` | 更新 install state |
| `GET` | `/api/apps/reconcile/desired` | 自动化：读取 system app desired state |
| `POST` | `/api/apps/reconcile/result` | 自动化：回写 system app reconcile 结果 |

## Backups

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/backups/targets` | 备份目标 |
| `POST` | `/api/backups/targets` | 新增备份目标 |
| `POST` | `/api/backups/targets/{id}/verify` | 验证目标 |
| `GET` | `/api/backups/jobs` | 备份任务 |
| `POST` | `/api/backups/run` | 运行备份 |
| `POST` | `/api/backups/one-click` | 一键备份配置 |

## Containers

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/containers` | 列出 containers |
| `GET` | `/api/containers/{id}` | 读取 container |
| `POST` | `/api/containers` | 创建 container desired state |
| `PUT` | `/api/containers/{id}` | 更新 container |
| `POST` | `/api/containers/{id}/start` | 请求启动 |
| `POST` | `/api/containers/{id}/stop` | 请求停止 |
| `POST` | `/api/containers/{id}/restart` | 请求重启 |
| `DELETE` | `/api/containers/{id}` | 删除 |
| `GET` | `/api/containers/reconcile/desired` | 自动化：读取 desired state |
| `POST` | `/api/containers/reconcile/result` | 自动化：回写 reconcile 结果 |

## Devices

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/devices` | 设备列表 |
| `POST` | `/api/devices` | 注册设备 |
| `POST` | `/api/devices/{id}/heartbeat` | 设备心跳 |

## Family

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/family/members` | 成员列表 |
| `GET` | `/api/family/permissions` | 权限摘要 |
| `POST` | `/api/family/members` | 创建成员 |
| `DELETE` | `/api/family/members/{id}` | 删除成员 |

## Home

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/home/overview` | dashboard overview |

## Identity

| Method | Path | 说明 |
| --- | --- | --- |
| `POST` | `/api/identity/login` | 匿名：登录 |
| `POST` | `/api/identity/recover-owner` | 匿名：使用 recovery code 恢复初始 primary owner，或用物理控制台 setup code 为无凭据旧版 owner 完成一次性登记 |
| `POST` | `/api/identity/recovery-code/rotate` | 仅 owner：验证当前密码并轮换或补建 recovery code |
| `GET` | `/api/identity/session` | 当前 session |
| `POST` | `/api/identity/logout` | 登出 |

## Media

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/media/assets` | 媒体资产 |
| `POST` | `/api/media/index` | 触发索引 |

## Networking

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/networking/certificates` | 证书列表 |
| `POST` | `/api/networking/certificates/self-signed` | 创建 self-signed 证书 |
| `GET` | `/api/networking/proxy/routes` | reverse proxy routes |
| `POST` | `/api/networking/proxy/routes` | 创建 route |
| `GET` | `/api/networking/proxy/caddyfile` | 自动化：渲染 Caddyfile |

## OTA

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/ota/status` | OTA 状态 |
| `POST` | `/api/ota/apply` | 计划 apply |
| `POST` | `/api/ota/stage` | stage metadata |

## Recovery

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/recovery/drills` | recovery drill 列表 |
| `POST` | `/api/recovery/drills` | 创建 recovery drill |

## Remote Access

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/remote/wireguard/peers` | WireGuard peers |
| `POST` | `/api/remote/wireguard/peers` | 创建 peer |

## Security

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/security/policy` | 安全策略摘要 |

## Setup

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/setup` | 匿名：初始化状态 |
| `GET` | `/api/setup/pairing` | 匿名：pairing ticket |
| `GET` | `/api/setup/pairing.svg` | 匿名：pairing QR SVG |
| `POST` | `/api/setup` | 匿名：完成初始化 |

## Setup Storage

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/setup/storage/inventory` | 匿名：磁盘 inventory |
| `POST` | `/api/setup/storage/recommendation` | 匿名：生成推荐 |
| `POST` | `/api/setup/storage/plan` | 匿名：生成 plan |
| `POST` | `/api/setup/storage/apply` | 匿名：应用 plan |
| `GET` | `/api/setup/storage/status` | 匿名：apply 状态 |

## SMB

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/smb/shares` | SMB shares |
| `POST` | `/api/smb/shares` | 创建 share |
| `PUT` | `/api/smb/shares/{id}` | 更新 share |
| `GET` | `/api/smb/credentials` | SMB credentials |
| `POST` | `/api/smb/credentials` | 创建 credential |
| `DELETE` | `/api/smb/credentials/{id}` | 删除 credential |
| `GET` | `/api/smb/config/smb.conf` | 自动化：渲染 smb.conf |
| `GET` | `/api/smb/reconcile/desired` | 自动化：读取 desired state |
| `POST` | `/api/smb/reconcile/result` | 自动化：回写 reconcile 结果 |

## Storage

| Method | Path | 说明 |
| --- | --- | --- |
| `POST` | `/api/storage/health/check` | 自动化：执行 health check |
| `GET` | `/api/storage/health` | 读取 health snapshot |

## Sync

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/sync/states` | sync states |
| `POST` | `/api/sync/states` | 写入 sync state |

## System

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/system/health` | 匿名：健康检查 |

## Vault

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/vault/items` | vault items |
| `GET` | `/api/vault/items/{id}` | vault item |
| `POST` | `/api/vault/items` | 创建 item |
| `DELETE` | `/api/vault/items/{id}` | 删除 item |

## WebDAV

| Method | Path | 说明 |
| --- | --- | --- |
| `OPTIONS` | `/dav/{area}/{path}` | Basic Auth：能力探测 |
| `PROPFIND` | `/dav/{area}/{path}` | Basic Auth：资源属性 |
| `GET` | `/dav/{area}/{path}` | Basic Auth：下载 |
| `HEAD` | `/dav/{area}/{path}` | Basic Auth：文件 metadata headers |
| `PUT` | `/dav/{area}/{path}` | Basic Auth：上传 |
| `MKCOL` | `/dav/{area}/{path}` | Basic Auth：创建目录 |
| `DELETE` | `/dav/{area}/{path}` | Basic Auth：删除 |
| `COPY` | `/dav/{area}/{path}` | Basic Auth：复制 |
| `MOVE` | `/dav/{area}/{path}` | Basic Auth：移动 |
| `PROPPATCH` | `/dav/{area}/{path}` | Basic Auth：返回 405 |
| `LOCK` | `/dav/{area}/{path}` | Basic Auth：返回 405 |
| `UNLOCK` | `/dav/{area}/{path}` | Basic Auth：返回 405 |

## WebDAV Tokens

| Method | Path | 说明 |
| --- | --- | --- |
| `GET` | `/api/webdav-tokens` | token 列表 |
| `POST` | `/api/webdav-tokens` | 创建 token |
| `DELETE` | `/api/webdav-tokens/{id}` | 删除 token |
