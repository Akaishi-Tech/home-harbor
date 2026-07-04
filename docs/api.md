# Backend API

`HomeHarbor.Api` serves JSON APIs, WebDAV, and the release frontend static files. In development it also exposes OpenAPI.

## Startup and Middleware

API startup:

1. Reads `HomeHarbor:*` configuration.
2. Optionally listens on a Unix socket from `HomeHarbor:Api:UnixSocketPath`.
3. Configures Valkey-backed distributed cache, with an in-memory fallback only in Development when the configured Unix socket is unavailable.
4. Registers the EF Core Npgsql `HomeHarborDbContext`.
5. Configures JWT bearer auth, Basic Auth, and the authorization fallback policy.
6. Registers CORS, controllers, OpenAPI, and core services.
7. Creates the data root.
8. Enables default files, static files, exception-to-JSON handling, CORS, the pre-storage request gate, auth, controllers, and SPA fallback.

Database migrations and automation-token generation are handled by the explicit migration command:

```bash
dotnet run --project src/HomeHarbor.Api/HomeHarbor.Api.csproj -- database-migrate
```

## Configuration Sections

| Setting | Default | Purpose |
| --- | --- | --- |
| `HomeHarbor:Api:UnixSocketPath` | empty | Optional API Unix socket listener |
| `HomeHarbor:Api:HttpUpstream` | `127.0.0.1:5181` | TCP upstream used when no Unix socket is configured |
| `HomeHarbor:Storage:DataRoot` | `/homeharbor-data` | Family data root |
| `HomeHarbor:Storage:MaxUploadBytes` | `21474836480` | WebDAV upload limit |
| `HomeHarbor:Database:ConnectionString` | Unix socket PostgreSQL | API database |
| `HomeHarbor:Cache:UnixSocketPath` | `/run/valkey/homeharbor.sock` | Valkey Unix socket for distributed cache |
| `HomeHarbor:Cache:InstanceName` | `homeharbor:` | Distributed cache key prefix |
| `HomeHarbor:Cache:OverviewTtlSeconds` | `30` | Dashboard overview cache TTL |
| `HomeHarbor:Jwt:Issuer` | `HomeHarbor` | JWT issuer |
| `HomeHarbor:Jwt:Audience` | `HomeHarbor.Frontend` | JWT audience |
| `HomeHarbor:Jwt:SigningKeyPath` | `/var/lib/homeharbor/jwt-signing.key` | Local JWT signing key |
| `HomeHarbor:Jwt:AccessTokenDays` | `30` | User token lifetime |
| `HomeHarbor:Automation:TokenPath` | `/run/homeharbor/automation.jwt` | Automation token output |
| `HomeHarbor:Automation:TokenDays` | `365` | Automation token lifetime |
| `HomeHarbor:Runtime:RequestDirectory` | `/run/homeharbor` | Runtime request directory |
| `HomeHarbor:Runtime:SmbCredentialDirectory` | `/run/homeharbor/smb-credentials` | SMB credential material |
| `HomeHarbor:Runtime:DataUnlockMetadataPath` | `/var/lib/homeharbor/security/data-unlock.json` | Storage unlock metadata |
| `HomeHarbor:StorageOobe:StateDirectory` | `/var/lib/homeharbor/storage` | Storage OOBE state directory |
| `HomeHarbor:StorageOobe:OneShotPassphrasePath` | `/run/homeharbor/storage-apply.passphrase` | One-shot storage apply passphrase |
| `HomeHarbor:StorageOobe:RequestPath` | `/run/homeharbor/storage-apply.request` | Agent storage apply request |
| `HomeHarbor:StorageOobe:MinimumInstallableBytes` | `34359738368` | Minimum installable storage target size |
| `HomeHarbor:Frontend:AllowedOrigins` | local Vite origins | CORS origins for the frontend dev server |

Development settings override the data root, database connection string, cache socket/key prefix, JWT key path, automation token path, and runtime directories to relative locations. In Development, if the configured Valkey socket is absent, the API logs a warning and uses in-memory distributed cache.

Before storage OOBE is ready, the pre-storage gate allows only `/api/setup*` and `/api/system/health`. Other `/api` and `/dav` requests return `503` with a setup hint.

## Identity

`POST /api/identity/login` is anonymous. On success it returns a bearer token, expiration time, member, and family.

User token validation has two layers:

- JWT issuer, audience, signature, and lifetime validation.
- Database member session lookup, where the JWT `jti` hash must match the stored session token hash and the session must not be expired.

`POST /api/identity/logout` deletes the current session. `GET /api/identity/session` returns the current session.

## Setup

Setup endpoints are anonymous because a first-boot device has no user yet:

- `GET /api/setup`: initialization state, pairing information, and storage OOBE status.
- `GET /api/setup/pairing`: create or read a pairing ticket.
- `GET /api/setup/pairing.svg`: pairing QR SVG.
- `POST /api/setup`: create the family, owner, device, initial WebDAV token, and recovery code after encrypted storage is ready.
- `GET /api/setup/storage/inventory`: enumerate disks, mounts, explicit OOBE storage targets, and filesystem capabilities.
- `POST /api/setup/storage/recommendation`: recommend a layout from the family usage profile.
- `POST /api/setup/storage/plan`: generate a destructive storage plan with target kinds, filesystem, RAID mode/backend, resolved profile/layout metadata, warnings, and unlock mode.
- `POST /api/setup/storage/apply`: confirm and apply a storage plan with a one-shot recovery passphrase.
- `GET /api/setup/storage/status`: read apply status.

## User Control APIs

After login, the frontend mainly uses these resources:

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

See [API Routes](./reference/api-routes.md) for the complete route table.

## Automation APIs

Automation endpoints only accept automation tokens. They are for appliance-internal services and should not be reused as ordinary user flows. The automation token is written by the `database-migrate` command to `HomeHarbor:Automation:TokenPath`.

| Endpoint | Consumer | Purpose |
| --- | --- | --- |
| `GET /api/networking/proxy/caddyfile` | `HomeHarbor.Agent render-caddyfile` | Render a Caddyfile from reverse proxy routes |
| `GET /api/smb/config/smb.conf` | `HomeHarbor.Agent apply-smb` | Render Samba configuration |
| `GET /api/smb/reconcile/desired` | SMB reconcile | Read share/credential desired state |
| `POST /api/smb/reconcile/result` | SMB reconcile | Write runtime state back |
| `GET /api/containers/reconcile/desired` | container reconcile | Read desired containers |
| `POST /api/containers/reconcile/result` | container reconcile | Write runtime state back |
| `GET /api/apps/reconcile/desired` | system app reconcile | Read signed system app desired state |
| `POST /api/apps/reconcile/result` | system app reconcile | Write download/activation state back |
| `POST /api/storage/health/check` | storage timer/service | Execute a storage health check |

## WebDAV

WebDAV paths use:

```text
/dav/{area}/{*path}
```

`area` maps to `StorageArea`: `files`, `photos`, or `backups`. Paths are validated for percent-encoding, normalized away from backslashes, rejected for `.`/`..` and NUL bytes, and resolved with physical path containment checks.

WebDAV uses Basic Auth. Usernames and tokens come from setup or `/api/webdav-tokens`; token scope determines access.

## Error Handling

Path-related `InvalidOperationException` becomes `400` JSON. `UnauthorizedAccessException` becomes `403` JSON. Authentication and authorization failures are handled by ASP.NET Core and return `401` or `403`.
