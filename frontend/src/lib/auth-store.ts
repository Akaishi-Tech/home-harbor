import { authFromResponse, clearAuth, loadAuth, saveAuth } from "@/lib/auth";
import type { AuthResponse, AuthState } from "@/types";

/**
 * Framework-agnostic auth store. A plain observable (not React context) so it
 * can be read by the non-React `api` token provider and the router's 401
 * handler, while React components subscribe via `useAuth` (useSyncExternalStore).
 */
type Listener = () => void;

let current: AuthState | null = loadAuth();
const listeners = new Set<Listener>();

function emit(): void {
  for (const listener of listeners) listener();
}

export const authStore = {
  get(): AuthState | null {
    return current;
  },
  subscribe(listener: Listener): () => void {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
  set(next: AuthState): AuthState {
    current = saveAuth(next);
    emit();
    return current;
  },
  setFromResponse(response: AuthResponse): AuthState {
    return authStore.set(authFromResponse(response));
  },
  update(patch: Partial<AuthState>): AuthState | null {
    if (!current) return null;
    return authStore.set({ ...current, ...patch });
  },
  clear(): void {
    if (!current) return;
    clearAuth();
    current = null;
    emit();
  },
  token(): string | null {
    return current?.accessToken ?? null;
  },
};
