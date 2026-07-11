import { i18n } from "@/i18n";
import type { AuthState } from "@/types";

export const AUTH_STORAGE_KEY = "homeharbor.auth";

const MAX_TOKEN_LENGTH = 16_384;

export function isFamilyAdmin(auth: AuthState | null): boolean {
  const role = auth?.member.role.toLowerCase();
  return role === "owner" || role === "admin";
}

export function isFamilyOwner(auth: AuthState | null): boolean {
  return auth?.member.role.toLowerCase() === "owner";
}

export function isFamilyMember(auth: AuthState | null): boolean {
  return isFamilyAdmin(auth) || auth?.member.role.toLowerCase() === "member";
}

export function authFromResponse(response: unknown): AuthState {
  if (!isRecord(response)) {
    throw new Error(i18n.t("api.invalidAuthResponse"));
  }
  const family = parseFamily(response.family);
  const parsed = parseAuthState({
    accessToken: response.accessToken,
    expiresAt: response.expiresAt,
    member: response.member,
    family,
    familyId: family?.id,
  });
  if (!parsed) throw new Error(i18n.t("api.invalidAuthResponse"));
  return parsed;
}

export function authFromSession(
  current: AuthState,
  response: unknown,
): AuthState {
  if (!isRecord(response)) {
    throw new Error(i18n.t("api.invalidSessionResponse"));
  }
  const parsed = parseAuthState({
    ...current,
    expiresAt: response.expiresAt,
    member: response.member,
    family: response.family,
    familyId: response.familyId,
  });
  if (!parsed) throw new Error(i18n.t("api.invalidSessionResponse"));
  return parsed;
}

export function loadAuth(): AuthState | null {
  const raw = readStoredAuth();
  if (!raw) return null;

  try {
    const parsed = parseAuthState(JSON.parse(raw));
    if (!parsed) {
      clearAuth();
      return null;
    }

    const expiresAt = Date.parse(parsed.expiresAt);
    if (!Number.isFinite(expiresAt) || expiresAt <= Date.now()) {
      clearAuth();
      return null;
    }

    return parsed;
  } catch {
    clearAuth();
    return null;
  }
}

export function saveAuth(auth: AuthState): AuthState {
  try {
    sessionStorage.setItem(AUTH_STORAGE_KEY, JSON.stringify(auth));
  } catch {
    // Keep the session in memory when browser storage is unavailable.
  }
  removeLegacyAuth();
  return auth;
}

export function clearAuth(): void {
  try {
    sessionStorage.removeItem(AUTH_STORAGE_KEY);
  } catch {
    // Storage cleanup is best-effort.
  }
  removeLegacyAuth();
}

function parseAuthState(value: unknown): AuthState | null {
  if (!isRecord(value)) return null;

  const accessToken = readRequiredString(value, "accessToken");
  const expiresAt = readRequiredString(value, "expiresAt");
  const familyId = readRequiredString(value, "familyId");
  const member = parseMember(value.member);
  const family = parseFamily(value.family);
  if (!accessToken || !expiresAt || !familyId || !member || !family)
    return null;
  if (accessToken.length > MAX_TOKEN_LENGTH) return null;
  if (family.id !== familyId) return null;

  return { accessToken, expiresAt, member, family, familyId };
}

function parseMember(value: unknown): AuthState["member"] | null {
  if (!isRecord(value)) return null;

  const id = readRequiredString(value, "id");
  const displayName = readRequiredString(value, "displayName");
  const role = readRequiredString(value, "role");
  if (!id || !displayName || !role) return null;

  return { id, displayName, role };
}

function parseFamily(value: unknown): AuthState["family"] | null {
  if (!isRecord(value)) return null;

  const id = readRequiredString(value, "id");
  const name = readRequiredString(value, "name");
  const ownerDisplayName = readRequiredString(value, "ownerDisplayName");
  const createdAt = readRequiredString(value, "createdAt");
  if (!id || !name || !ownerDisplayName || !createdAt) return null;

  return { id, name, ownerDisplayName, createdAt };
}

function readRequiredString(
  source: Record<string, unknown>,
  key: string,
): string | null {
  const value = source[key];
  if (typeof value !== "string") return null;

  const trimmed = value.trim();
  return trimmed ? trimmed : null;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function readStoredAuth(): string | null {
  try {
    const session = sessionStorage.getItem(AUTH_STORAGE_KEY);
    if (session) {
      removeLegacyAuth();
      return session;
    }
  } catch {
    // Continue with in-memory auth only when tab storage is unavailable.
  }

  // Older builds persisted long-lived bearer tokens in localStorage. Revoke
  // their browser persistence and require a fresh login instead of reviving
  // them in a new tab-scoped session.
  removeLegacyAuth();
  return null;
}

function removeLegacyAuth(): void {
  try {
    localStorage.removeItem(AUTH_STORAGE_KEY);
  } catch {
    // Storage cleanup is best-effort.
  }
}
