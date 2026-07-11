# Security Model

HomeHarbor's safety boundaries include authentication, path safety, release signing, Secure Boot/AVB, channel deployment guards, and secret management.

## User Authentication

Users get bearer tokens from `POST /api/identity/login`. JWT validation checks signature and expiration, and also requires a matching member session in the database. Logout or session expiration can therefore revoke a token.

The default authorization fallback policy requires a user JWT. Setup, login, health, and SPA fallback are explicit anonymous exceptions.

## First-Use TLS Trust

HomeHarbor uses a per-appliance Caddy internal CA. Before entering a setup code,
recovery code, or password in a browser, download the public CA certificate from
`http://homeharbor.local/homeharbor-ca.crt`. Install it only after comparing its
SHA-256 fingerprint exactly with the value printed on the physical appliance
console. The console is the authenticated channel; the HTTP download is only a
transport for public certificate bytes and is not trusted by itself.

`homeharbor-tls-trust.service` prints the fingerprint after Caddy creates the CA
and retries if the certificate or a physical console is not ready. The Caddy
state directory persists the CA across reboots. If that state is replaced or
the fingerprint changes unexpectedly, stop and re-establish physical trust
instead of bypassing a browser certificate warning.

## Automation Token

Appliance-internal services use an automation token. The API migration command writes it through `JwtTokenService.WriteAutomationTokenAsync()` to `HomeHarbor:Automation:TokenPath`:

```bash
dotnet run --project src/HomeHarbor.Api/HomeHarbor.Api.csproj -- database-migrate
```

Automation tokens can only access endpoints marked with `AuthorizationPolicies.Automation`. Do not reuse automation endpoints for the user UI and do not expose the automation token to the frontend.

## WebDAV Token

WebDAV uses Basic Auth. These credentials are separate from user login tokens, making it possible to configure long-lived credentials with limited scope for sync clients. If a WebDAV token leaks, delete it through `/api/webdav-tokens` and issue a new one.

## Path Safety

All family file paths must go through `StoragePathPolicy`:

- Reject malformed percent-encoding.
- Reject NUL bytes.
- Reject `.` and `..`.
- Reject physical paths that escape the area root.

SMB, WebDAV, media indexing, backup, and other family-data modules should reuse the same policy instead of inventing a second path interpretation.

## Release Signing

OTA manifests use Ed25519 signatures. Verification:

1. Requires `signatureAlgorithm=Ed25519`.
2. Builds the canonical payload.
3. Verifies `signedPayloadSha256`.
4. Base64-decodes `signature`.
5. Verifies the signature with the release public key.

Missing fields, non-`1` schema, unsupported boot mode, and invalid channel should all fail.

## Secure Boot and AVB

Secure Boot mode is enabled with `HOMEHARBOR_SECURE_BOOT=1`, producing boot mode `secure-boot-raw-uki`. Release and install flows should use channel signing material and must not bypass checks with development unsigned switches.

AVB/verity protects root, modules, firmware, and recovery payloads. The boot cmdline carries sealed digests and verity inputs; initramfs combines them with the boot selector's EFI state to verify and map a read-only EROFS root.

Data storage encryption is configured during Web OOBE. Passphrase mode prompts in `HomeHarborBoot` before the UKI starts and passes the secret through a volatile EFI variable that initramfs consumes and deletes before opening LUKS devices. TPM2 mode enrolls automatic unlock during storage apply and keeps the OOBE recovery passphrase as fallback.

## Channel Guards

Before channel release, run unit and build-plan checks:

```bash
dotnet test tests/HomeHarbor.Tests/HomeHarbor.Tests.csproj
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- plan 0.1.0
```

Release checks reject:

- Missing artifacts.
- Unsafe release key ids.
- Overly broad private key permissions.
- Keys stored inside the repository.
- Manifest signature or payload hash mismatch.
- Unsafe channel host/path values.
- Development unsigned flags in non-dev channels.

## Secret Management

Do not commit:

- Release private keys.
- Secure Boot signing keys used for AVB.
- Secure Boot signing keys.
- Passphrases.
- Channel credentials.
- Machine-local tokens.
- Generated appliance media.

Use `.work/` or environment variables for local temporary material. `.pem`, `.key`, `.raw`, `.iso`, `.img`, and similar files are ignored by default, with an explicit exception for the public Secure Boot certificate.
