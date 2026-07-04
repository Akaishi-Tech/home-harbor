import type { SecretField } from "@/components/glass/result-secret-dialog";
import { i18n } from "@/i18n";

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function str(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() ? value : undefined;
}

function translate(key: string, defaultValue: string): string {
  return i18n.t(key, { defaultValue });
}

/** Extracts WebDAV sync credentials (device onboarding / setup) into secret fields. */
export function webDavSecrets(response: unknown): SecretField[] {
  if (!isRecord(response)) return [];
  const webDav = isRecord(response.webDav) ? response.webDav : undefined;
  if (!webDav) return [];

  const fields: SecretField[] = [];
  const username = str(webDav.username);
  const token = str(webDav.token);
  const scope = str(webDav.scope);
  if (username)
    fields.push({ label: translate("secretLabels.webDavUsername", "WebDAV username"), value: username });
  if (token)
    fields.push({ label: translate("secretLabels.webDavToken", "WebDAV token"), value: token });
  if (scope)
    fields.push({ label: translate("secretLabels.scope", "Scope"), value: scope });
  return fields;
}

/** Generic single-key lookup that returns a secret field when the value is a non-empty string. */
export function stringSecret(
  response: unknown,
  key: string,
  label: string,
  multiline = false,
): SecretField[] {
  if (!isRecord(response)) return [];
  const value = str(response[key]);
  return value ? [{ label, value, multiline }] : [];
}

/** Faithful fallback: the full JSON payload as a copyable block. */
export function jsonSecret(response: unknown, label?: string): SecretField[] {
  if (response === null || response === undefined) return [];
  return [
    {
      label: label ?? translate("secretLabels.details", "Details"),
      value: JSON.stringify(response, null, 2),
      multiline: true,
    },
  ];
}
