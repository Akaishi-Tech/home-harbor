import { QueryClient } from "@tanstack/react-query";
import { ApiError } from "@/lib/api";

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // Never retry 4xx (a 401 must clear auth, not hammer the endpoint).
      retry: (failureCount, error) => {
        if (
          error instanceof ApiError &&
          error.status >= 400 &&
          error.status < 500
        )
          return false;
        return failureCount < 2;
      },
      staleTime: 15_000,
      refetchOnWindowFocus: false,
    },
    mutations: {
      retry: false,
      // Mutation variables and responses can contain passwords or one-time
      // credentials. Drop inactive mutations immediately instead of retaining
      // them in the default five-minute cache.
      gcTime: 0,
    },
  },
});

export const queryKeys = {
  setup: ["setup"] as const,
  storageInventory: ["setup", "storage", "inventory"] as const,
  storageStatus: ["setup", "storage", "status"] as const,
  session: ["identity", "session"] as const,
  overview: ["overview"] as const,
  policy: ["security", "policy"] as const,
  ota: ["ota"] as const,
  members: ["family", "members"] as const,
  devices: ["devices"] as const,
  backupTargets: ["backups", "targets"] as const,
  peers: ["remote", "peers"] as const,
  catalog: ["apps", "catalog"] as const,
  smbShares: ["smb", "shares"] as const,
  smbCredentials: ["smb", "credentials"] as const,
  containers: ["containers"] as const,
  vaultItems: ["vault", "items"] as const,
  vaultItem: (id: string) => ["vault", "items", id] as const,
} as const;
