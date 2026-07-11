import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, post } from "@/lib/api";
import { queryKeys } from "@/lib/query";
import { authStore } from "@/lib/auth-store";
import { authFromResponse } from "@/lib/auth";
import { encryptVault } from "@/lib/vault";
import { i18n } from "@/i18n";
import type {
  BackupTarget,
  Container,
  DashboardData,
  DevicePairingResponse,
  DeviceRecord,
  OtaStatus,
  Overview,
  PairingTicket,
  Peer,
  SecurityPolicy,
  SetupResponse,
  SetupStatus,
  SmbCredential,
  SmbShare,
  StorageApplyStatus,
  StorageFileSystem,
  StorageInventory,
  StoragePlan,
  StorageRecommendation,
  StorageRaidMode,
  StorageUseProfile,
  VaultItem,
  VaultItemSummary,
} from "@/types";

type CatalogItem = DashboardData["catalog"][number];
type MemberRecord = DashboardData["members"][number];
export type ContainerAction = "start" | "stop" | "restart";

export function useSetupStatus() {
  return useQuery({
    queryKey: queryKeys.setup,
    queryFn: ({ signal }) =>
      api<SetupStatus>("/api/setup", { auth: false, signal }),
  });
}

function setupCodeHeaders(setupCode: string): HeadersInit {
  return { "X-HomeHarbor-Setup-Code": setupCode };
}

export function useStorageInventory(setupCode: string, enabled = true) {
  return useQuery({
    queryKey: queryKeys.storageInventory,
    queryFn: ({ signal }) =>
      api<StorageInventory>("/api/setup/storage/inventory", {
        auth: false,
        headers: setupCodeHeaders(setupCode),
        signal,
      }),
    enabled: enabled && Boolean(setupCode),
    staleTime: 30_000,
  });
}

export function useStorageStatus(
  setupCode: string,
  options: { poll?: boolean; enabled?: boolean } = {},
) {
  return useQuery({
    queryKey: queryKeys.storageStatus,
    queryFn: ({ signal }) =>
      api<StorageApplyStatus>("/api/setup/storage/status", {
        auth: false,
        headers: setupCodeHeaders(setupCode),
        signal,
      }),
    enabled: (options.enabled ?? true) && Boolean(setupCode),
    refetchInterval: options.poll ? 2_000 : false,
  });
}

export function useOverview() {
  return useQuery({
    queryKey: queryKeys.overview,
    queryFn: () => api<Overview>("/api/home/overview"),
  });
}

export function usePolicy() {
  return useQuery({
    queryKey: queryKeys.policy,
    queryFn: () => api<SecurityPolicy>("/api/security/policy"),
  });
}

export function useOta(enabled = true) {
  return useQuery({
    queryKey: queryKeys.ota,
    queryFn: () => api<OtaStatus>("/api/ota/status"),
    enabled,
  });
}

export function useMembers() {
  return useQuery({
    queryKey: queryKeys.members,
    queryFn: () => api<MemberRecord[]>("/api/family/members"),
  });
}

export function useDevices() {
  return useQuery({
    queryKey: queryKeys.devices,
    queryFn: () => api<DeviceRecord[]>("/api/devices"),
  });
}

export function useBackupTargets() {
  return useQuery({
    queryKey: queryKeys.backupTargets,
    queryFn: () => api<BackupTarget[]>("/api/backups/targets"),
  });
}

export function usePeers() {
  return useQuery({
    queryKey: queryKeys.peers,
    queryFn: () => api<Peer[]>("/api/remote/wireguard/peers"),
  });
}

export function useCatalog() {
  return useQuery({
    queryKey: queryKeys.catalog,
    queryFn: () => api<CatalogItem[]>("/api/apps/catalog"),
    staleTime: 5 * 60_000,
  });
}

export function useSmbShares() {
  return useQuery({
    queryKey: queryKeys.smbShares,
    queryFn: () => api<SmbShare[]>("/api/smb/shares"),
  });
}

export function useSmbCredentials() {
  return useQuery({
    queryKey: queryKeys.smbCredentials,
    queryFn: () => api<SmbCredential[]>("/api/smb/credentials"),
  });
}

export function useContainers(enabled = true) {
  return useQuery({
    queryKey: queryKeys.containers,
    queryFn: () => api<Container[]>("/api/containers"),
    enabled,
  });
}

export function useVaultItems() {
  return useQuery({
    queryKey: queryKeys.vaultItems,
    queryFn: () => api<VaultItemSummary[]>("/api/vault/items"),
  });
}

export function useVaultItem(id: string | null, enabled = true) {
  return useQuery({
    queryKey: id ? queryKeys.vaultItem(id) : queryKeys.vaultItem(""),
    queryFn: ({ signal }) =>
      api<VaultItem>(`/api/vault/items/${encodeURIComponent(id ?? "")}`, {
        signal,
      }),
    enabled: enabled && Boolean(id),
  });
}

export function useLogin() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (input: { displayName: string; password: string }) => {
      const response = await post<unknown>("/api/identity/login", input, false);
      return authFromResponse(response);
    },
    onSuccess: (auth) => {
      // Query keys are deliberately family-agnostic. Never expose data cached
      // for a previous signed-in member to the new session.
      queryClient.removeQueries();
      authStore.set(auth);
    },
  });
}

export function useRecoverOwner() {
  return useMutation({
    mutationFn: (input: { recoveryCode: string; newPassword: string }) =>
      post<unknown>("/api/identity/recover-owner", input, false),
  });
}

export function useRotateRecoveryCode() {
  return useMutation({
    mutationFn: (input: { currentPassword: string }) =>
      post<unknown>("/api/identity/recovery-code/rotate", input),
  });
}

export function useLogout() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async () => {
      try {
        await post<void>("/api/identity/logout", {});
      } catch {
        // best-effort: clear local session regardless of server outcome
      }
    },
    onSettled: () => {
      authStore.clear();
      queryClient.clear();
    },
  });
}

export function useCompleteSetup() {
  return useMutation({
    mutationFn: async (input: {
      familyName: string;
      ownerDisplayName: string;
      ownerPassword: string;
      deviceName: string;
      pairingCode: string;
    }) => {
      const response = await api<SetupResponse>("/api/setup", {
        auth: false,
        body: input,
      });
      authFromResponse(response?.auth);
      return response;
    },
    onSuccess: (response) => {
      authStore.setFromResponse(response.auth);
    },
  });
}

export function useStorageRecommendation(setupCode: string) {
  return useMutation({
    mutationFn: (profile: StorageUseProfile) =>
      api<StorageRecommendation>("/api/setup/storage/recommendation", {
        auth: false,
        headers: setupCodeHeaders(setupCode),
        body: profile,
      }),
  });
}

export function useStoragePlan() {
  return useMutation({
    mutationFn: (input: {
      targets: Array<{ path: string; kind: string }>;
      profile: StorageUseProfile;
      redundancyPreference: string;
      unlockMode: "passphrase" | "tpm2";
      fileSystem: StorageFileSystem;
      raidMode: StorageRaidMode;
      dataProfile?: string;
      metadataProfile?: string;
      allowRemovable: boolean;
      pairingCode: string;
    }) =>
      api<StoragePlan>("/api/setup/storage/plan", {
        auth: false,
        body: input,
      }),
  });
}

export function useStorageApply() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: {
      planId: string;
      confirmation: string;
      recoveryPassphrase: string;
      pairingCode: string;
    }) =>
      api<StorageApplyStatus>("/api/setup/storage/apply", {
        auth: false,
        body: input,
      }),
    onSuccess: (status) => {
      queryClient.setQueryData(queryKeys.storageStatus, status);
    },
  });
}

export function useCreateDevice() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (input: { displayName: string; kind: string }) => {
      const ticket = await api<PairingTicket>("/api/setup/pairing");
      if (!ticket.code) {
        throw new Error(i18n.t("api.missingPairingCode"));
      }
      return post<unknown>("/api/devices", {
        displayName: input.displayName,
        kind: input.kind,
        pairingCode: ticket.code,
        issueWebDavToken: true,
        scope: "All",
      });
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.devices });
      queryClient.invalidateQueries({ queryKey: queryKeys.overview });
    },
  });
}

export function usePairDevice() {
  return useMutation({
    mutationFn: (input: {
      displayName: string;
      kind: "browser" | "mobile" | "desktop";
      pairingCode: string;
    }) =>
      post<DevicePairingResponse>(
        "/api/devices",
        {
          displayName: input.displayName,
          kind: input.kind,
          pairingCode: input.pairingCode,
          issueWebDavToken: true,
          scope: "All",
        },
        false,
      ),
  });
}

export function useCreateBackup() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { repositoryUri: string }) =>
      post<unknown>("/api/backups/targets", input),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.backupTargets });
      queryClient.invalidateQueries({ queryKey: queryKeys.overview });
    },
  });
}

export function useCreateMember() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: {
      displayName: string;
      role: string;
      password: string;
    }) => post<unknown>("/api/family/members", input),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.members });
      queryClient.invalidateQueries({ queryKey: queryKeys.overview });
    },
  });
}

export function useCreatePeer() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { name: string; endpoint: string }) =>
      post<unknown>("/api/remote/wireguard/peers", input),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.peers });
      queryClient.invalidateQueries({ queryKey: queryKeys.overview });
    },
  });
}

export function useCreateVaultItem() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (input: {
      name: string;
      username: string;
      password: string;
      vaultSecret: string;
      notes?: string;
    }) => {
      const encrypted = await encryptVault(
        {
          username: input.username,
          password: input.password,
          notes: input.notes ?? "",
        },
        input.vaultSecret,
      );
      return post<unknown>("/api/vault/items", {
        name: input.name,
        encryptedPayload: encrypted.payload,
        nonce: encrypted.nonce,
        keyHint: encrypted.keyHint,
      });
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.overview });
      queryClient.invalidateQueries({ queryKey: queryKeys.vaultItems });
    },
  });
}

export function useDeleteVaultItem() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) =>
      api<unknown>(`/api/vault/items/${encodeURIComponent(id)}`, {
        method: "DELETE",
      }),
    onSuccess: (_response, id) => {
      queryClient.removeQueries({ queryKey: queryKeys.vaultItem(id) });
      queryClient.invalidateQueries({ queryKey: queryKeys.overview });
      queryClient.invalidateQueries({ queryKey: queryKeys.vaultItems });
    },
  });
}

export function useInstallApp() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { appKey: string }) =>
      post<unknown>("/api/apps/installs", input),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.catalog });
      queryClient.invalidateQueries({ queryKey: queryKeys.containers });
      queryClient.invalidateQueries({ queryKey: queryKeys.overview });
    },
  });
}

export function useUninstallApp() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) =>
      api<unknown>(`/api/apps/installs/${encodeURIComponent(id)}`, {
        method: "DELETE",
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.catalog });
      queryClient.invalidateQueries({ queryKey: queryKeys.containers });
      queryClient.invalidateQueries({ queryKey: queryKeys.overview });
    },
  });
}

export function useCreateSmbShare() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: {
      name: string;
      shareName: string;
      readOnly: boolean;
    }) => post<unknown>("/api/smb/shares", { ...input, enabled: true }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.smbShares });
      queryClient.invalidateQueries({ queryKey: queryKeys.overview });
    },
  });
}

export function useCreateSmbCredential() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: {
      shareId?: string;
      displayName: string;
      readOnly: boolean;
    }) => post<unknown>("/api/smb/credentials", input),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.smbCredentials });
      queryClient.invalidateQueries({ queryKey: queryKeys.smbShares });
      queryClient.invalidateQueries({ queryKey: queryKeys.overview });
    },
  });
}

export function useRevokeSmbCredential() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) =>
      api(`/api/smb/credentials/${encodeURIComponent(id)}`, {
        method: "DELETE",
      }),
    onMutate: async (id) => {
      await queryClient.cancelQueries({ queryKey: queryKeys.smbCredentials });
      const previous = queryClient.getQueryData<SmbCredential[]>(
        queryKeys.smbCredentials,
      );
      if (previous) {
        queryClient.setQueryData(
          queryKeys.smbCredentials,
          previous.filter((credential) => credential.id !== id),
        );
      }
      return { previous };
    },
    onError: (_error, _id, context) => {
      if (context?.previous)
        queryClient.setQueryData(queryKeys.smbCredentials, context.previous);
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.smbCredentials });
      queryClient.invalidateQueries({ queryKey: queryKeys.smbShares });
    },
  });
}

export function useCreateContainer() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: {
      name: string;
      image: string;
      ports: Array<{
        hostPort: number;
        containerPort: number;
        protocol: string;
      }>;
      volumes?: Array<{
        hostPath: string;
        containerPath: string;
        readOnly: boolean;
      }>;
    }) => post<unknown>("/api/containers", input),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.containers });
      queryClient.invalidateQueries({ queryKey: queryKeys.overview });
    },
  });
}

export function useContainerAction() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, action }: { id: string; action: ContainerAction }) =>
      post<unknown>(
        `/api/containers/${encodeURIComponent(id)}/${action}`,
        {},
      ),
    onMutate: async ({ id, action }) => {
      await queryClient.cancelQueries({ queryKey: queryKeys.containers });
      const previous = queryClient.getQueryData<Container[]>(
        queryKeys.containers,
      );
      if (previous) {
        queryClient.setQueryData(
          queryKeys.containers,
          previous.map((container) =>
            container.id === id
              ? { ...container, requestedAction: action }
              : container,
          ),
        );
      }
      return { previous };
    },
    onError: (_error, _variables, context) => {
      if (context?.previous)
        queryClient.setQueryData(queryKeys.containers, context.previous);
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.containers });
    },
  });
}

export function useMediaIndex() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => post<unknown>("/api/media/index", {}),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.overview });
    },
  });
}
