import { i18n } from "@/i18n";

const encoder = new TextEncoder();
const decoder = new TextDecoder();

const VAULT_KEY_HINT_PREFIX = "homeharbor-vault-pbkdf2-sha256-v1";
const VAULT_KEY_ITERATIONS = 310_000;
const VAULT_KEY_SALT_BYTES = 16;
const MIN_SUPPORTED_ITERATIONS = 100_000;
const MAX_SUPPORTED_ITERATIONS = 1_000_000;
export const MIN_VAULT_SECRET_LENGTH = 12;
export const MAX_VAULT_SECRET_LENGTH = 1_024;

export type EncryptedVaultPayload = {
  payload: string;
  nonce: string;
  keyHint: string;
};

export type EncryptedVaultItem = {
  encryptedPayload: string;
  nonce: string;
  keyHint: string;
};

export type DecryptedVaultPayload = {
  username: string;
  password: string;
  notes: string;
  updatedAt: string;
};

export async function encryptVault(
  payload: Record<string, string>,
  vaultSecret: string,
): Promise<EncryptedVaultPayload> {
  if (
    vaultSecret.length < MIN_VAULT_SECRET_LENGTH ||
    vaultSecret.length > MAX_VAULT_SECRET_LENGTH
  ) {
    throw new Error(i18n.t("pages.vault.weakSecret"));
  }

  const salt = crypto.getRandomValues(new Uint8Array(VAULT_KEY_SALT_BYTES));
  const keyHint = buildKeyHint(salt, VAULT_KEY_ITERATIONS);
  const key = await deriveVaultKey(vaultSecret, salt, VAULT_KEY_ITERATIONS, [
    "encrypt",
  ]);
  const nonce = crypto.getRandomValues(new Uint8Array(12));
  const encrypted = await crypto.subtle.encrypt(
    { name: "AES-GCM", iv: nonce },
    key,
    encoder.encode(
      JSON.stringify({ ...payload, updatedAt: new Date().toISOString() }),
    ),
  );

  return {
    payload: bytesToBase64(new Uint8Array(encrypted)),
    nonce: bytesToBase64(nonce),
    keyHint,
  };
}

export async function decryptVault(
  item: EncryptedVaultItem,
  vaultSecret: string,
): Promise<DecryptedVaultPayload> {
  const hint = parseKeyHint(item.keyHint);
  if (!hint) {
    throw new Error(i18n.t("pages.vault.legacyError"));
  }

  try {
    const key = await deriveVaultKey(vaultSecret, hint.salt, hint.iterations, [
      "decrypt",
    ]);
    const decrypted = await crypto.subtle.decrypt(
      { name: "AES-GCM", iv: base64ToBytes(item.nonce) },
      key,
      base64ToBytes(item.encryptedPayload),
    );
    return parsePayload(decoder.decode(decrypted));
  } catch (error) {
    if (error instanceof SyntaxError) {
      throw new Error(i18n.t("pages.vault.invalidItemFormat"));
    }

    throw new Error(i18n.t("pages.vault.decryptFailed"));
  }
}

export function hasRecoverableVaultKey(keyHint: string): boolean {
  return parseKeyHint(keyHint) !== null;
}

async function deriveVaultKey(
  vaultSecret: string,
  salt: Uint8Array<ArrayBuffer>,
  iterations: number,
  usages: KeyUsage[],
): Promise<CryptoKey> {
  if (vaultSecret.length === 0) {
    throw new Error(i18n.t("pages.vault.secretRequiredPunctuated"));
  }

  const secretKey = await crypto.subtle.importKey(
    "raw",
    encoder.encode(vaultSecret),
    "PBKDF2",
    false,
    ["deriveKey"],
  );

  return crypto.subtle.deriveKey(
    {
      name: "PBKDF2",
      hash: "SHA-256",
      salt,
      iterations,
    },
    secretKey,
    { name: "AES-GCM", length: 256 },
    false,
    usages,
  );
}

function buildKeyHint(
  salt: Uint8Array<ArrayBuffer>,
  iterations: number,
): string {
  return `${VAULT_KEY_HINT_PREFIX}:${iterations}:${bytesToBase64Url(salt)}`;
}

function parseKeyHint(
  keyHint: string,
): { iterations: number; salt: Uint8Array<ArrayBuffer> } | null {
  const [prefix, iterationsValue, saltValue, extra] = keyHint.split(":");
  if (extra !== undefined || prefix !== VAULT_KEY_HINT_PREFIX || !saltValue) {
    return null;
  }

  const iterations = Number(iterationsValue);
  if (
    !Number.isSafeInteger(iterations) ||
    iterations < MIN_SUPPORTED_ITERATIONS ||
    iterations > MAX_SUPPORTED_ITERATIONS
  ) {
    return null;
  }

  try {
    const salt = base64UrlToBytes(saltValue);
    return salt.length > 0 ? { iterations, salt } : null;
  } catch {
    return null;
  }
}

function parsePayload(value: string): DecryptedVaultPayload {
  const parsed = JSON.parse(value);
  if (!isRecord(parsed)) throw new SyntaxError("invalid vault payload");

  return {
    username: readString(parsed.username),
    password: readString(parsed.password),
    notes: readString(parsed.notes),
    updatedAt: readString(parsed.updatedAt),
  };
}

function readString(value: unknown): string {
  return typeof value === "string" ? value : "";
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function bytesToBase64(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}

function bytesToBase64Url(bytes: Uint8Array): string {
  return bytesToBase64(bytes)
    .replaceAll("+", "-")
    .replaceAll("/", "_")
    .replace(/=+$/, "");
}

function base64ToBytes(value: string): Uint8Array<ArrayBuffer> {
  const binary = atob(value);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++) {
    bytes[index] = binary.charCodeAt(index);
  }
  return bytes;
}

function base64UrlToBytes(value: string): Uint8Array<ArrayBuffer> {
  const base64 = value.replaceAll("-", "+").replaceAll("_", "/");
  return base64ToBytes(base64.padEnd(Math.ceil(base64.length / 4) * 4, "="));
}
