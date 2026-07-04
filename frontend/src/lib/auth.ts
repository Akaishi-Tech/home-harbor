import type { AuthResponse, AuthState } from "@/types";

export const AUTH_STORAGE_KEY = "homeharbor.auth";

export function authFromResponse(response: AuthResponse): AuthState {
  return {
    accessToken: response.accessToken,
    expiresAt: response.expiresAt,
    member: response.member,
    family: response.family,
    familyId: response.family.id,
  };
}

export function loadAuth(): AuthState | null {
  const raw = localStorage.getItem(AUTH_STORAGE_KEY);
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
  localStorage.setItem(AUTH_STORAGE_KEY, JSON.stringify(auth));
  return auth;
}

export function clearAuth(): void {
  localStorage.removeItem(AUTH_STORAGE_KEY);
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
