export type Family = {
  id: string;
  name: string;
  ownerDisplayName: string;
  createdAt: string;
};

export type Member = {
  id: string;
  displayName: string;
  role: string;
};

export type AuthResponse = {
  accessToken: string;
  tokenType: "Bearer";
  expiresAt: string;
  member: Member;
  family: Family;
};

export type AuthState = {
  accessToken: string;
  expiresAt: string;
  member: Member;
  family: Family;
  familyId: string;
};

export type SetupStatus = {
  initialized: boolean;
  family: Family | null;
  onboarding: {
    pairing: string;
    qr: string;
    steps: string[];
  };
  storage: StorageApplyStatus & {
    endpoints: {
      inventory: string;
      recommendation: string;
      plan: string;
      apply: string;
      status: string;
    };
  };
};

export type PairingTicket = {
  initialized: boolean;
  code: string;
  pairingUrl: string;
  expiresAt: string;
  qrSvg: string;
  qrPayload: string;
};

export type SetupResponse = {
  family: Family;
  device: {
    id: string;
    displayName: string;
    kind: string;
  };
  owner: Member;
  auth: AuthResponse;
  recoveryCode: string;
  webDav: {
    username: string;
    token: string;
    scope: string;
  };
  encryption: {
    mode: string;
    keyHint: string;
  };
};

export type StorageDevice = {
  name: string | null;
  path: string | null;
  sizeBytes: number;
  type: string | null;
  model: string | null;
  serial: string | null;
  transport: string | null;
  isRotational: boolean;
  isRemovable: boolean;
  mountpoints: string[];
  fileSystem: string | null;
  label: string | null;
  uuid: string | null;
  parentKernelName: string | null;
  isSystem: boolean;
  isProtected: boolean;
  smart: null | {
    passed: boolean | null;
    exitStatus: number | null;
    summary: string;
  };
  warnings: string[];
  children: StorageDevice[];
};

export type StorageInventory = {
  devices: StorageDevice[];
  targets: StorageTarget[];
  mounts: Array<{
    target: string | null;
    source: string | null;
    fileSystem: string | null;
    options: string | null;
  }>;
  protectedDevices: string[];
  warnings: string[];
  fileSystems: StorageFileSystemCapability[];
};

export type StorageFileSystem = "btrfs" | "xfs" | "zfs";

export type StorageRaidMode =
  | "recommended"
  | "single"
  | "mirror"
  | "raid5"
  | "raid6"
  | "raid10"
  | "raidz1"
  | "raidz2";

export type StorageFileSystemCapability = {
  fileSystem: StorageFileSystem | string;
  available: boolean;
  unavailableReason: string | null;
  raidModes: StorageRaidMode[];
  canPrepareOnline: boolean;
};

export type StorageTarget = {
  path: string;
  kind: "main-reserved" | "whole-disk" | string;
  sizeBytes: number;
  model: string | null;
  serial: string | null;
  transport: string | null;
  eligible: boolean;
  eligibilityReasons: string[];
};

export type StorageUseProfile = {
  familyMembers: number;
  phoneCount: number;
  computerCount: number;
  photoVideoIntensity: string;
  mediaLibraryTb: number;
  apps: number;
  backupTargetPreference: string;
  redundancyPreference: string;
};

export type StorageRecommendation = {
  recommendedLayout: string;
  selectedDevices: Array<string | null>;
  backupTargetDevices: Array<string | null>;
  dataProfile: string;
  metadataProfile: string;
  estimatedOneYearBytes: number;
  estimatedThreeYearBytes: number;
  usableBytes: number;
  faultTolerance: string;
  warnings: string[];
};

export type StoragePlan = {
  planId: string;
  layout: string;
  devices: Array<{
    path: string;
    kind: string;
    sizeBytes: number;
    model: string | null;
    serial: string | null;
    transport: string | null;
  }>;
  fileSystem: StorageFileSystem | string;
  raidMode: StorageRaidMode | string;
  raidBackend: "filesystem" | "mdadm" | string;
  unlockMode: "passphrase" | "tpm2" | string;
  dataProfile: string;
  metadataProfile: string;
  usableBytes: number;
  operations: string[];
  destructiveDevices: string[];
  mountChanges: Array<{ target: string; fileSystem: string; options: string }>;
  requiresReboot: boolean;
  requiresBootloaderUnlock: boolean;
  confirmPhrase: string;
  createdAt: string;
  warnings: string[];
};

export type StorageApplyStatus = {
  state: "Idle" | "PendingReboot" | "Running" | "Succeeded" | "Failed" | string;
  progress: number;
  message: string;
  error: string | null;
  planId: string | null;
  updatedAt: string;
};

export type Overview = {
  initialized: boolean;
  family: Family;
  modules: {
    files: { count: number; bytes: number; webDav: string };
    photos: { count: number; bytes: number; webDav: string };
    backups: {
      localCount: number;
      localBytes: number;
      targetCount: number;
      latestJob: null | { state: string };
    };
    vault: { count: number; encrypted: boolean };
    devices: { count: number; syncStates: number };
    remoteAccess: { peers: number };
    smb: { shares: number; credentials: number };
    runtime: { apps: number; containers: number };
  };
  security: {
    endToEndEncryption: boolean;
  };
  storage: {
    status: string;
    checkedAt: string | null;
  };
};

export type VaultItemSummary = {
  id: string;
  familyId: string;
  name: string;
  keyHint: string;
  createdAt: string;
  updatedAt: string;
};

export type VaultItem = VaultItemSummary & {
  encryptedPayload: string;
  nonce: string;
};

export type SecurityPolicy = {
  storage: {
    mode: string;
  };
};

export type OtaStatus = {
  version: string;
  updateState: string;
};

export type DashboardData = {
  overview: Overview;
  policy: SecurityPolicy;
  ota: OtaStatus;
  members: Array<Member & { familyId: string; createdAt: string }>;
  devices: Array<{
    id: string;
    familyId: string;
    displayName: string;
    kind: string;
    createdAt: string;
    lastSeenAt: string | null;
  }>;
  targets: Array<{
    id: string;
    familyId: string;
    name: string;
    repositoryUri: string;
  }>;
  peers: Array<{ id: string; familyId: string; name: string; address: string }>;
  catalog: Array<{
    appKey: string;
    displayName: string;
    title: string;
    description: string;
    category: string;
    kind: "container" | "system" | string;
    installMode: string;
    image: string;
    port: number | null;
    version: string;
    manifestUrl: string;
    recommendedInSetup: boolean;
    requiresReboot: boolean;
    available: boolean;
    unavailableReason: string;
    source: string;
    commands: string[];
    installed: boolean;
    installId: string | null;
    desiredState: string | null;
    runtimeState: string | null;
    installedVersion: string | null;
    activeVersion: string | null;
    appRequiresReboot: boolean;
    lastError: string;
    lastAppliedAt: string | null;
  }>;
  smbShares: Array<{
    id: string;
    familyId: string;
    name: string;
    shareName: string;
    path: string;
    readOnly: boolean;
    enabled: boolean;
    runtimeState: string;
    credentialCount: number;
    unc: string;
  }>;
  smbCredentials: Array<{
    id: string;
    shareId: string;
    displayName: string;
    username: string;
    unixUser: string;
    readOnly: boolean;
    enabled: boolean;
    runtimeState: string;
    password: string | null;
  }>;
  containers: Array<{
    id: string;
    name: string;
    image: string;
    desiredState: string;
    runtimeState: string;
    requestedAction: string;
    serviceName: string;
    lastError: string;
    definition: {
      ports: Array<{ hostPort: number; targetPort: number; protocol: string }>;
      volumes: Array<{
        hostPath: string;
        containerPath: string;
        readOnly: boolean;
      }>;
    };
  }>;
};

export type Notice = {
  tone: "ok" | "error";
  message: string;
  detail?: unknown;
};

export type SessionResponse = {
  familyId: string;
  expiresAt: string;
  member: AuthState["member"];
  family: AuthState["family"];
};

export type CatalogItem = DashboardData["catalog"][number];

export type SmbShare = DashboardData["smbShares"][number];
export type SmbCredential = DashboardData["smbCredentials"][number];
export type Container = DashboardData["containers"][number];
export type DeviceRecord = DashboardData["devices"][number];
export type BackupTarget = DashboardData["targets"][number];
export type Peer = DashboardData["peers"][number];
export type MemberRecord = DashboardData["members"][number];
