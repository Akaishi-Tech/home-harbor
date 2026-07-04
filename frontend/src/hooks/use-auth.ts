import { useSyncExternalStore } from "react";
import { authStore } from "@/lib/auth-store";
import type { AuthState } from "@/types";

export function useAuth(): AuthState | null {
  return useSyncExternalStore(authStore.subscribe, authStore.get, () => null);
}
