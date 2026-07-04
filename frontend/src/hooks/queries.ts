import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, post } from "@/lib/api";
import { queryKeys } from "@/lib/query";
import { authStore } from "@/lib/auth-store";
import { encryptVault } from "@/lib/vault";
import type {
  AuthResponse,
  BackupTarget,
  Container,
  DashboardData,
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
    queryFn: () => api<SetupStatus>("/api/setup"),
  });
}

export function usePairing(enabled = true) {
  return useQuery({
    queryKey: queryKeys.pairing,
    queryFn: () => api<PairingTicket>("/api/setup/pairing"),
    enabled,
  });
}

export function useStorageInventory(enabled = true) {
  return useQuery({
    queryKey: queryKeys.storageInventory,
    queryFn: () => api<StorageInventory>("/api/setup/storage/inventory"),
    enabled,
    staleTime: 30_000,
  });
}

export function useStorageStatus(options: { poll?: boolean } = {}) {
  return useQuery({
    queryKey: queryKeys.storageStatus,
    queryFn: () => api<StorageApplyStatus>("/api/setup/storage/status"),
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

export function useOta() {
  return useQuery({
    queryKey: queryKeys.ota,
    queryFn: () => api<OtaStatus>("/api/ota/status"),
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

export function useContainers() {
  return useQuery({
    queryKey: queryKeys.containers,
    queryFn: () => api<Container[]>("/api/containers"),
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
    queryFn: () => api<VaultItem>(`/api/vault/items/${id}`),
    enabled: enabled && Boolean(id),
  });
}

export function useLogin() {
  return useMutation({
    mutationFn: (input: { displayName: string; password: string }) =>
      post<AuthResponse>("/api/identity/login", input),
    onSuccess: (response) => {
      authStore.setFromResponse(response);
    },
  });
}

export function useLogout() {
  return useMutation({
    mutationFn: async () => {
      try {
        await post<void>("/api/identity/logout", {});
      } catch {
        // best-effort: clear local session regardless of server outcome
      }
    },
    onSettled: () => authStore.clear(),
  });
}

export function useCompleteSetup() {
  return useMutation({
    mutationFn: (input: {
      familyName: string;
      ownerDisplayName: string;
      ownerPassword: string;
      deviceName: string;
      pairingCode: string;
    }) => api<SetupResponse>("/api/setup", { body: input }),
    onSuccess: (response) => {
      authStore.setFromResponse(response.auth);
    },
  });
}

export function useStorageRecommendation() {
  return useMutation({
    mutationFn: (profile: StorageUseProfile) =>
      api<StorageRecommendation>("/api/setup/storage/recommendation", {
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
    }) => api<StoragePlan>("/api/setup/storage/plan", { body: input }),
  });
}

export function useStorageApply() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: {
      planId: string;
      confirmation: string;
      recoveryPassphrase: string;
    }) =>
      api<StorageApplyStatus>("/api/setup/storage/apply", { body: input }),
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

export function useCreateBackup() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { repositoryUri: string }) =>
      post<unknown>("/api/backups/one-click", input),
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
      api<unknown>(`/api/vault/items/${id}`, { method: "DELETE" }),
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
      api<unknown>(`/api/apps/installs/${id}`, { method: "DELETE" }),
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
      api(`/api/smb/credentials/${id}`, { method: "DELETE" }),
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
      post<unknown>(`/api/containers/${id}/${action}`, {}),
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
