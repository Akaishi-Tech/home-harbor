# Security Policy

## Reporting a Vulnerability

Please report suspected vulnerabilities privately through GitHub Security
Advisories for `akaishi-tech/home-harbor` when available. If advisories are not
available, contact the maintainers privately before opening a public issue.

Include:

- Affected component or release artifact.
- Reproduction steps or a proof of concept.
- Expected and observed behavior.
- Any known exposure of keys, tokens, data, update channels, installer media, or
  appliance recovery paths.

Do not include private keys, channel credentials, user data, or exploit code
that is not needed to understand the report.

## Sensitive Material

Never commit:

- Release private keys or Secure Boot signing keys.
- Passphrase files or recovery secrets.
- Channel credentials or deployment lock tokens.
- Machine-local `.work` state, generated images, or release artifacts.

Development secrets should live outside the repository or in ignored local paths.
The checked-in Secure Boot certificate at `certs/homeharbor-secure-boot.crt` is a
public certificate, not a private signing key.

## Supported Versions

Supported versions are communicated through release notes and security
advisories. Security fixes should be validated with the local unit/frontend
checks plus VM-based full-system tests when the affected area requires it.
