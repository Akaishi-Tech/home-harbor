# API Routes

This page summarizes the current HTTP routes by controller. Unless marked anonymous or automation, routes require a user JWT.

## Apps

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/apps/catalog` | App catalog |
| `GET` | `/api/apps/installs` | Installed/desired installs |
| `POST` | `/api/apps/installs` | Create install |
| `DELETE` | `/api/apps/installs/{id}` | Delete install |
| `POST` | `/api/apps/installs/{id}/state` | Update install state |
| `GET` | `/api/apps/reconcile/desired` | Automation: read system app desired state |
| `POST` | `/api/apps/reconcile/result` | Automation: write system app reconcile result |

## Backups

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/backups/targets` | Backup targets |
| `POST` | `/api/backups/targets` | Create backup target |
| `POST` | `/api/backups/targets/{id}/verify` | Verify target |
| `GET` | `/api/backups/jobs` | Backup jobs |
| `POST` | `/api/backups/run` | Run backup |
| `POST` | `/api/backups/one-click` | One-click backup setup |

## Containers

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/containers` | List containers |
| `GET` | `/api/containers/{id}` | Read container |
| `POST` | `/api/containers` | Create container desired state |
| `PUT` | `/api/containers/{id}` | Update container |
| `POST` | `/api/containers/{id}/start` | Request start |
| `POST` | `/api/containers/{id}/stop` | Request stop |
| `POST` | `/api/containers/{id}/restart` | Request restart |
| `DELETE` | `/api/containers/{id}` | Delete |
| `GET` | `/api/containers/reconcile/desired` | Automation: read desired state |
| `POST` | `/api/containers/reconcile/result` | Automation: write reconcile result |

## Devices

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/devices` | Devices |
| `POST` | `/api/devices` | Register device |
| `POST` | `/api/devices/{id}/heartbeat` | Device heartbeat |

## Family

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/family/members` | Members |
| `GET` | `/api/family/permissions` | Permission summary |
| `POST` | `/api/family/members` | Create member |
| `DELETE` | `/api/family/members/{id}` | Delete member |

## Home

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/home/overview` | Dashboard overview |

## Identity

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/identity/login` | Anonymous: login |
| `POST` | `/api/identity/recover-owner` | Anonymous: recover the initial primary owner with a recovery code, or physically enroll a credential-less legacy owner with the console setup code |
| `POST` | `/api/identity/recovery-code/rotate` | Owner only: verify current password and rotate/create the recovery code |
| `GET` | `/api/identity/session` | Current session |
| `POST` | `/api/identity/logout` | Logout |

## Media

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/media/assets` | Media assets |
| `POST` | `/api/media/index` | Trigger indexing |

## Networking

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/networking/certificates` | Certificates |
| `POST` | `/api/networking/certificates/self-signed` | Create self-signed certificate |
| `GET` | `/api/networking/proxy/routes` | Reverse proxy routes |
| `POST` | `/api/networking/proxy/routes` | Create route |
| `GET` | `/api/networking/proxy/caddyfile` | Automation: render Caddyfile |

## OTA

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/ota/status` | OTA status |
| `POST` | `/api/ota/apply` | Plan apply |
| `POST` | `/api/ota/stage` | Stage metadata |

## Recovery

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/recovery/drills` | Recovery drills |
| `POST` | `/api/recovery/drills` | Create recovery drill |

## Remote Access

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/remote/wireguard/peers` | WireGuard peers |
| `POST` | `/api/remote/wireguard/peers` | Create peer |

## Security

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/security/policy` | Security policy summary |

## Setup

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/setup` | Anonymous: initialization status |
| `GET` | `/api/setup/pairing` | Anonymous: pairing ticket |
| `GET` | `/api/setup/pairing.svg` | Anonymous: pairing QR SVG |
| `POST` | `/api/setup` | Anonymous: complete setup |

## Setup Storage

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/setup/storage/inventory` | Anonymous: disk inventory |
| `POST` | `/api/setup/storage/recommendation` | Anonymous: generate recommendation |
| `POST` | `/api/setup/storage/plan` | Anonymous: generate plan |
| `POST` | `/api/setup/storage/apply` | Anonymous: apply plan |
| `GET` | `/api/setup/storage/status` | Anonymous: apply status |

## SMB

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/smb/shares` | SMB shares |
| `POST` | `/api/smb/shares` | Create share |
| `PUT` | `/api/smb/shares/{id}` | Update share |
| `GET` | `/api/smb/credentials` | SMB credentials |
| `POST` | `/api/smb/credentials` | Create credential |
| `DELETE` | `/api/smb/credentials/{id}` | Delete credential |
| `GET` | `/api/smb/config/smb.conf` | Automation: render smb.conf |
| `GET` | `/api/smb/reconcile/desired` | Automation: read desired state |
| `POST` | `/api/smb/reconcile/result` | Automation: write reconcile result |

## Storage

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/storage/health/check` | Automation: execute health check |
| `GET` | `/api/storage/health` | Read health snapshot |

## Sync

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/sync/states` | Sync states |
| `POST` | `/api/sync/states` | Write sync state |

## System

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/system/health` | Anonymous: health check |

## Vault

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/vault/items` | Vault items |
| `GET` | `/api/vault/items/{id}` | Vault item |
| `POST` | `/api/vault/items` | Create item |
| `DELETE` | `/api/vault/items/{id}` | Delete item |

## WebDAV

| Method | Path | Purpose |
| --- | --- | --- |
| `OPTIONS` | `/dav/{area}/{path}` | Basic Auth: capability probe |
| `PROPFIND` | `/dav/{area}/{path}` | Basic Auth: resource properties |
| `GET` | `/dav/{area}/{path}` | Basic Auth: download |
| `HEAD` | `/dav/{area}/{path}` | Basic Auth: file metadata headers |
| `PUT` | `/dav/{area}/{path}` | Basic Auth: upload |
| `MKCOL` | `/dav/{area}/{path}` | Basic Auth: create directory |
| `DELETE` | `/dav/{area}/{path}` | Basic Auth: delete |
| `COPY` | `/dav/{area}/{path}` | Basic Auth: copy |
| `MOVE` | `/dav/{area}/{path}` | Basic Auth: move |
| `PROPPATCH` | `/dav/{area}/{path}` | Basic Auth: returns 405 |
| `LOCK` | `/dav/{area}/{path}` | Basic Auth: returns 405 |
| `UNLOCK` | `/dav/{area}/{path}` | Basic Auth: returns 405 |

## WebDAV Tokens

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/webdav-tokens` | List tokens |
| `POST` | `/api/webdav-tokens` | Create token |
| `DELETE` | `/api/webdav-tokens/{id}` | Delete token |
