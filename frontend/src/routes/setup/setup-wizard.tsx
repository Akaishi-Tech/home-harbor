import { useEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "react-router";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import { Check, ChevronRight, HardDrive, ServerCog } from "lucide-react";
import { useTranslation } from "react-i18next";
import { Brand } from "@/components/app-shell/brand";
import {
  GlassCard,
  GlassCardContent,
  GlassCardDescription,
  GlassCardHeader,
  GlassCardTitle,
} from "@/components/glass/glass-card";
import { Gauge } from "@/components/glass/gauge";
import {
  ResultSecretDialog,
  type SecretField,
} from "@/components/glass/result-secret-dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Checkbox } from "@/components/ui/checkbox";
import { Badge } from "@/components/ui/badge";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";
import {
  useCompleteSetup,
  useStorageApply,
  useStorageInventory,
  useStoragePlan,
  useStorageRecommendation,
  useStorageStatus,
  usePairing,
} from "@/hooks/queries";
import {
  diskBadges,
  diskMeta,
  flattenStorageDevices,
  storageTargetBadges,
  storageTargetMeta,
} from "@/lib/storage";
import {
  clampProgress,
  errorMessage,
  formatBytes,
  storageStatusLabel,
} from "@/lib/format";
import { webDavSecrets, stringSecret } from "@/lib/secrets";
import { cn } from "@/lib/utils";
import { i18n } from "@/i18n";
import type {
  StorageFileSystem,
  StorageFileSystemCapability,
  StoragePlan,
  StorageRaidMode,
  StorageRecommendation,
  StorageTarget,
  StorageUseProfile,
} from "@/types";
import { ServicesStep } from "./services-step";

type Step = "inventory" | "plan" | "apply" | "family" | "services";

const STEP_LABELS: Array<{ key: Step; labelKey: string }> = [
  { key: "inventory", labelKey: "setup.steps.inventory" },
  { key: "plan", labelKey: "setup.steps.plan" },
  { key: "apply", labelKey: "setup.steps.apply" },
  { key: "family", labelKey: "setup.steps.family" },
  { key: "services", labelKey: "setup.steps.services" },
];

function defaultStorageProfile(): StorageUseProfile {
  return {
    familyMembers: 4,
    phoneCount: 4,
    computerCount: 2,
    photoVideoIntensity: "normal",
    mediaLibraryTb: 1,
    apps: 4,
    backupTargetPreference: "external",
    redundancyPreference: "conservative",
  };
}

export function SetupWizard() {
  const navigate = useNavigate();
  const { t } = useTranslation();
  const pairing = usePairing(false);
  const inventory = useStorageInventory();
  const recommendationMutation = useStorageRecommendation();
  const planMutation = useStoragePlan();
  const applyMutation = useStorageApply();
  const completeSetup = useCompleteSetup();

  const [step, setStep] = useState<Step>("inventory");
  const [selectedTargets, setSelectedTargets] = useState<
    Array<{ path: string; kind: string }>
  >([]);
  const [unlockMode, setUnlockMode] = useState<"passphrase" | "tpm2">(
    "passphrase",
  );
  const [fileSystem, setFileSystem] = useState<StorageFileSystem>("btrfs");
  const [raidMode, setRaidMode] = useState<StorageRaidMode>("recommended");
  const [advancedProfiles, setAdvancedProfiles] = useState(false);
  const [dataProfile, setDataProfile] = useState("raid1");
  const [metadataProfile, setMetadataProfile] = useState("raid1");
  const [recommendation, setRecommendation] =
    useState<StorageRecommendation | null>(null);
  const [plan, setPlan] = useState<StoragePlan | null>(null);
  const [secrets, setSecrets] = useState<SecretField[] | null>(null);

  const storageStatus = useStorageStatus({ poll: step === "apply" });
  const status = storageStatus.data;

  const initRef = useRef(false);
  useEffect(() => {
    if (initRef.current) return;
    if (inventory.data && storageStatus.data) {
      initRef.current = true;
      const recommended = inventory.data.targets
        .filter((target) => target.eligible)
        .map((target) => ({ path: target.path, kind: target.kind }));
      setSelectedTargets(recommended);
      if (storageStatus.data.state === "Succeeded") setStep("family");
    }
  }, [inventory.data, storageStatus.data]);

  useEffect(() => {
    const modes =
      inventory.data?.fileSystems.find(
        (capability) => capability.fileSystem === fileSystem,
      )?.raidModes ?? [];
    if (modes.length > 0 && !modes.includes(raidMode)) {
      setRaidMode("recommended");
    }
  }, [fileSystem, inventory.data?.fileSystems, raidMode]);

  async function handleStoragePlan() {
    const next = defaultStorageProfile();
    try {
      const nextRecommendation = await recommendationMutation.mutateAsync(next);
      setRecommendation(nextRecommendation);
      const nextPlan = await planMutation.mutateAsync({
        targets: selectedTargets,
        profile: next,
        redundancyPreference: next.redundancyPreference,
        unlockMode,
        fileSystem,
        raidMode,
        dataProfile:
          fileSystem === "btrfs" &&
          advancedProfiles &&
          !usesMdadmFallback(fileSystem, raidMode)
            ? dataProfile
            : undefined,
        metadataProfile:
          fileSystem === "btrfs" &&
          advancedProfiles &&
          !usesMdadmFallback(fileSystem, raidMode)
            ? metadataProfile
            : undefined,
        allowRemovable: false,
      });
      setPlan(nextPlan);
      setStep("plan");
    } catch (error) {
      toast.error(errorMessage(error));
    }
  }

  async function handleApply(recoveryPassphrase: string) {
    if (!plan) return;
    try {
      await applyMutation.mutateAsync({
        planId: plan.planId,
        confirmation: plan.confirmPhrase,
        recoveryPassphrase,
      });
      setStep("apply");
    } catch (error) {
      toast.error(errorMessage(error));
    }
  }

  async function handleFamily(values: FamilyValues) {
    if (status?.state !== "Succeeded") {
      toast.error(t("setup.family.storageRequired"));
      return;
    }

    const refreshedPairing = await pairing.refetch();
    const pairingCode = refreshedPairing.data?.code ?? pairing.data?.code ?? "";
    if (!pairingCode) {
      toast.error(t("setup.family.pairingUnavailable"));
      return;
    }

    completeSetup.mutate(
      {
        familyName: values.familyName,
        ownerDisplayName: values.ownerDisplayName,
        ownerPassword: values.ownerPassword,
        deviceName: values.deviceName,
        pairingCode,
      },
      {
        onSuccess: (response) => {
          toast.success(t("toast.familyCreated"));
          setSecrets([
            ...stringSecret(
              response,
              "recoveryCode",
              t("secretLabels.recoveryCode"),
            ),
            ...webDavSecrets(response),
          ]);
        },
        onError: (error) => toast.error(errorMessage(error)),
      },
    );
  }

  const activeIndex = STEP_LABELS.findIndex((item) => item.key === step);

  return (
    <div className="mx-auto grid min-h-svh w-full max-w-5xl items-center gap-4 p-4 lg:grid-cols-[320px_1fr]">
      <SetupRail
        activeLabel={
          STEP_LABELS[activeIndex]
            ? t(STEP_LABELS[activeIndex].labelKey)
            : t("setup.steps.fallback")
        }
        statusLabel={storageStatusLabel(status?.state)}
      />

      <GlassCard className="w-full">
        <GlassCardHeader>
          <GlassCardTitle className="text-xl">
            {step === "family"
              ? t("setup.titles.family")
              : step === "services"
                ? t("setup.titles.services")
                : t("setup.titles.storage")}
          </GlassCardTitle>
          <GlassCardDescription>
            {t("setup.description")}
          </GlassCardDescription>
          <Stepper activeIndex={activeIndex} />
        </GlassCardHeader>
        <GlassCardContent>
          {step === "inventory" ? (
            <InventoryStep
              targets={inventory.data?.targets ?? []}
              devices={flattenStorageDevices(
                inventory.data?.devices ?? [],
              ).filter((device) => device.type === "disk")}
              selected={selectedTargets}
              unlockMode={unlockMode}
              onUnlockMode={setUnlockMode}
              fileSystems={inventory.data?.fileSystems ?? []}
              fileSystem={fileSystem}
              onFileSystem={setFileSystem}
              raidMode={raidMode}
              onRaidMode={setRaidMode}
              advancedProfiles={advancedProfiles}
              onAdvancedProfiles={setAdvancedProfiles}
              dataProfile={dataProfile}
              onDataProfile={setDataProfile}
              metadataProfile={metadataProfile}
              onMetadataProfile={setMetadataProfile}
              onToggle={(target, checked) =>
                setSelectedTargets((current) =>
                  checked
                    ? [
                        ...current.filter((item) => item.path !== target.path),
                        { path: target.path, kind: target.kind },
                      ]
                    : current.filter((item) => item.path !== target.path),
                )
              }
              warnings={inventory.data?.warnings ?? []}
              pending={
                recommendationMutation.isPending || planMutation.isPending
              }
              onContinue={handleStoragePlan}
            />
          ) : null}

          {step === "plan" && plan ? (
            <PlanStep
              plan={plan}
              recommendation={recommendation}
              pending={applyMutation.isPending}
              onApply={handleApply}
              onBack={() => setStep("inventory")}
            />
          ) : null}

          {step === "apply" ? (
            <ApplyStep
              progress={clampProgress(status?.progress ?? 0)}
              statusLabel={storageStatusLabel(status?.state)}
              message={status?.error ?? status?.message ?? t("setup.apply.waitingRoot")}
              succeeded={status?.state === "Succeeded"}
              onRefresh={() => storageStatus.refetch()}
              onContinue={() => setStep("family")}
            />
          ) : null}

          {step === "family" ? (
            <FamilyStep
              pending={completeSetup.isPending}
              onSubmit={handleFamily}
            />
          ) : null}

          {step === "services" ? (
            <ServicesStep onFinish={() => navigate("/dashboard")} />
          ) : null}
        </GlassCardContent>
      </GlassCard>

      <ResultSecretDialog
        open={secrets !== null}
        onOpenChange={(open) => {
          if (!open) {
            setSecrets(null);
            setStep("services");
          }
        }}
        title={t("setup.secrets.title")}
        description={t("setup.secrets.description")}
        secrets={secrets ?? []}
      />
    </div>
  );
}

function SetupRail({
  activeLabel,
  statusLabel,
}: {
  activeLabel: string;
  statusLabel: string;
}) {
  const { t } = useTranslation();

  return (
    <GlassCard className="p-5">
      <div className="space-y-5">
        <Brand />
        <div className="rounded-2xl border border-border/60 bg-background/40 p-4">
          <div className="flex items-center gap-3">
            <span className="flex size-11 shrink-0 items-center justify-center rounded-xl bg-primary/10 text-primary">
              <ServerCog className="size-5" />
            </span>
            <div className="min-w-0">
              <p className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                {t("setup.rail.progress")}
              </p>
              <p className="truncate text-lg font-semibold">{activeLabel}</p>
            </div>
          </div>
        </div>
        <div className="space-y-2 text-sm text-muted-foreground">
          <p>{t("setup.rail.line1")}</p>
          <p>{t("setup.rail.line2")}</p>
        </div>
        <div className="space-y-2">
          <p className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
            {t("setup.rail.storageStatus")}
          </p>
          <Badge variant="outline" className="border-warning/40 text-warning">
            {statusLabel}
          </Badge>
        </div>
      </div>
    </GlassCard>
  );
}

function Stepper({ activeIndex }: { activeIndex: number }) {
  const { t } = useTranslation();

  return (
    <ol className="mt-3 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs">
      {STEP_LABELS.map((item, index) => {
        const done = index < activeIndex;
        const active = index === activeIndex;
        return (
          <li key={item.key} className="flex items-center gap-2">
            <span
              className={cn(
                "flex items-center gap-1.5 rounded-full px-2.5 py-1 font-medium",
                active && "bg-primary/15 text-primary",
                done && "text-foreground",
                !active && !done && "text-muted-foreground",
              )}
            >
              <span
                className={cn(
                  "flex size-4 items-center justify-center rounded-full text-[0.6rem]",
                  active
                    ? "bg-primary text-primary-foreground"
                    : done
                      ? "bg-success/20 text-success"
                      : "bg-muted text-muted-foreground",
                )}
              >
                {done ? <Check className="size-3" /> : index + 1}
              </span>
              {t(item.labelKey)}
            </span>
            {index < STEP_LABELS.length - 1 ? (
              <ChevronRight className="size-3 text-muted-foreground" />
            ) : null}
          </li>
        );
      })}
    </ol>
  );
}

function InventoryStep({
  targets,
  devices,
  selected,
  unlockMode,
  onUnlockMode,
  fileSystems,
  fileSystem,
  onFileSystem,
  raidMode,
  onRaidMode,
  advancedProfiles,
  onAdvancedProfiles,
  dataProfile,
  onDataProfile,
  metadataProfile,
  onMetadataProfile,
  onToggle,
  warnings,
  pending,
  onContinue,
}: {
  targets: StorageTarget[];
  devices: ReturnType<typeof flattenStorageDevices>;
  selected: Array<{ path: string; kind: string }>;
  unlockMode: "passphrase" | "tpm2";
  onUnlockMode: (mode: "passphrase" | "tpm2") => void;
  fileSystems: StorageFileSystemCapability[];
  fileSystem: StorageFileSystem;
  onFileSystem: (fileSystem: StorageFileSystem) => void;
  raidMode: StorageRaidMode;
  onRaidMode: (raidMode: StorageRaidMode) => void;
  advancedProfiles: boolean;
  onAdvancedProfiles: (enabled: boolean) => void;
  dataProfile: string;
  onDataProfile: (profile: string) => void;
  metadataProfile: string;
  onMetadataProfile: (profile: string) => void;
  onToggle: (target: StorageTarget, checked: boolean) => void;
  warnings: string[];
  pending: boolean;
  onContinue: () => void;
}) {
  const { t } = useTranslation();
  const capability = fileSystems.find(
    (item) => item.fileSystem === fileSystem,
  );
  const selectedModeOptions =
    capability?.raidModes.length ? capability.raidModes : ["recommended"];
  const xfsTargetMismatch =
    fileSystem === "xfs" &&
    !usesMdadmFallback(fileSystem, raidMode) &&
    selected.length !== 1;
  const raidTargetWarning = raidTargetRequirementWarning(
    fileSystem,
    raidMode,
    selected.length,
  );
  const fallbackWarning = usesMdadmFallback(fileSystem, raidMode)
    ? [
        t("setup.inventory.mdadmFallback", {
          fileSystem: fileSystemLabel(fileSystem),
          raidMode: raidModeLabel(raidMode),
        }),
      ]
    : [];
  const canContinue =
    selected.length > 0 &&
    capability?.available !== false &&
    !xfsTargetMismatch &&
    !raidTargetWarning;

  return (
    <div className="space-y-4">
      <div className="space-y-2">
        {targets.map((target) => {
          const checked = selected.some((item) => item.path === target.path);
          return (
            <label
              key={`${target.kind}:${target.path}`}
              className={cn(
                "flex items-start gap-3 rounded-xl border border-border/60 bg-background/30 p-3",
                !target.eligible && "opacity-60",
              )}
            >
              <Checkbox
                className="mt-0.5"
                checked={checked}
                disabled={!target.eligible}
                onCheckedChange={(value) => {
                  onToggle(target, value === true);
                }}
              />
              <span className="min-w-0 space-y-1">
                <span className="block break-all font-medium">
                  {target.path}
                </span>
                <span className="block text-xs text-muted-foreground">
                  {storageTargetMeta(target)}
                </span>
                <span className="block text-xs text-muted-foreground">
                  {storageTargetBadges(target).join(t("common.separator")) ||
                    t("storage.target.usablePool")}
                </span>
              </span>
            </label>
          );
        })}
      </div>

      <div className="grid gap-3 sm:grid-cols-3">
        <label className="space-y-1">
          <span className="text-sm font-medium">{t("fields.fileSystem")}</span>
          <Select
            value={fileSystem}
            onValueChange={(value) =>
              onFileSystem(value === "xfs" || value === "zfs" ? value : "btrfs")
            }
          >
            <SelectTrigger className="w-full">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {(["btrfs", "xfs", "zfs"] as const).map((value) => {
                const item = fileSystems.find(
                  (capability) => capability.fileSystem === value,
                );
                return (
                  <SelectItem key={value} value={value}>
                    {fileSystemLabel(value)}
                    {item?.available === false
                      ? ` (${t("common.unavailable")})`
                      : ""}
                  </SelectItem>
                );
              })}
            </SelectContent>
          </Select>
        </label>

        <label className="space-y-1">
          <span className="text-sm font-medium">{t("fields.raidMode")}</span>
          <Select
            value={raidMode}
            onValueChange={(value) =>
              onRaidMode(normalizeRaidMode(value))
            }
          >
            <SelectTrigger className="w-full">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {selectedModeOptions.map((mode) => (
                <SelectItem key={mode} value={mode}>
                  {raidModeLabel(mode)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </label>

        <label className="space-y-1">
          <span className="text-sm font-medium">{t("fields.unlockMode")}</span>
          <Select
            value={unlockMode}
            onValueChange={(value) =>
              onUnlockMode(value === "tpm2" ? "tpm2" : "passphrase")
            }
          >
            <SelectTrigger className="w-full">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="passphrase">
                {t("storage.unlockModes.passphrase")}
              </SelectItem>
              <SelectItem value="tpm2">
                {t("storage.unlockModes.tpm2")}
              </SelectItem>
            </SelectContent>
          </Select>
        </label>
      </div>

      {fileSystem === "btrfs" && !usesMdadmFallback(fileSystem, raidMode) ? (
        <div className="space-y-3 rounded-xl border border-border/60 bg-background/30 p-3">
          <label className="flex items-center gap-2 text-sm">
            <Checkbox
              checked={advancedProfiles}
              onCheckedChange={(value) => onAdvancedProfiles(value === true)}
            />
            <span className="font-medium">
              {t("setup.inventory.advancedBtrfs")}
            </span>
          </label>
          {advancedProfiles ? (
            <div className="grid gap-3 sm:grid-cols-2">
              <label className="space-y-1">
                <span className="text-sm font-medium">
                  {t("fields.dataProfile")}
                </span>
                <Select value={dataProfile} onValueChange={onDataProfile}>
                  <SelectTrigger className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="single">single</SelectItem>
                    <SelectItem value="raid1">raid1</SelectItem>
                    <SelectItem value="raid10">raid10</SelectItem>
                  </SelectContent>
                </Select>
              </label>
              <label className="space-y-1">
                <span className="text-sm font-medium">
                  {t("fields.metadataProfile")}
                </span>
                <Select
                  value={metadataProfile}
                  onValueChange={onMetadataProfile}
                >
                  <SelectTrigger className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="dup">dup</SelectItem>
                    <SelectItem value="single">single</SelectItem>
                    <SelectItem value="raid1">raid1</SelectItem>
                    <SelectItem value="raid1c3">raid1c3</SelectItem>
                    <SelectItem value="raid10">raid10</SelectItem>
                  </SelectContent>
                </Select>
              </label>
            </div>
          ) : null}
        </div>
      ) : null}

      {capability?.available === false ? (
        <div className="space-y-2 rounded-xl border border-warning/40 bg-warning/10 p-3 text-sm text-warning">
          <p>
            {capability.unavailableReason ??
              t("setup.inventory.unavailableReasonFallback")}
          </p>
        </div>
      ) : null}

      {xfsTargetMismatch ? (
        <WarningList items={[t("setup.inventory.xfsSingleWarning")]} />
      ) : null}

      {raidTargetWarning ? (
        <WarningList items={[raidTargetWarning]} />
      ) : null}

      {fallbackWarning.length ? (
        <WarningList items={fallbackWarning} />
      ) : null}

      {devices.length ? (
        <MiniList
          title={t("setup.inventory.detectedDisks")}
          items={devices.map((device) =>
            [
              device.path ?? device.name ?? "disk",
              diskMeta(device),
              diskBadges(device).join(t("common.separator")),
            ]
              .filter(Boolean)
              .join(" · "),
          )}
        />
      ) : null}

      {warnings.length ? <WarningList items={warnings} /> : null}

      <div className="flex flex-wrap gap-2">
        <Button
          type="button"
          onClick={onContinue}
          disabled={pending || !canContinue}
        >
          {pending
            ? t("setup.inventory.generating")
            : t("setup.inventory.generatePlan")}
        </Button>
      </div>
    </div>
  );
}

function fileSystemLabel(fileSystem: string): string {
  return (
    {
      btrfs: i18n.t("storage.fileSystems.btrfs"),
      xfs: i18n.t("storage.fileSystems.xfs"),
      zfs: i18n.t("storage.fileSystems.zfs"),
    }[fileSystem] ?? fileSystem
  );
}

function raidModeLabel(raidMode: string): string {
  return (
    {
      recommended: i18n.t("storage.raidModes.recommended"),
      single: i18n.t("storage.raidModes.single"),
      mirror: i18n.t("storage.raidModes.mirror"),
      raid5: i18n.t("storage.raidModes.raid5"),
      raid6: i18n.t("storage.raidModes.raid6"),
      raid10: i18n.t("storage.raidModes.raid10"),
      raidz1: i18n.t("storage.raidModes.raidz1"),
      raidz2: i18n.t("storage.raidModes.raidz2"),
    }[raidMode] ?? raidMode
  );
}

function normalizeRaidMode(value: string): StorageRaidMode {
  return value === "single" ||
    value === "mirror" ||
    value === "raid5" ||
    value === "raid6" ||
    value === "raid10" ||
    value === "raidz1" ||
    value === "raidz2"
    ? value
    : "recommended";
}

function usesMdadmFallback(
  fileSystem: StorageFileSystem,
  raidMode: StorageRaidMode,
): boolean {
  return (fileSystem === "btrfs" || fileSystem === "xfs") &&
    (raidMode === "raid5" || raidMode === "raid6");
}

function raidTargetRequirementWarning(
  fileSystem: StorageFileSystem,
  raidMode: StorageRaidMode,
  targetCount: number,
): string | null {
  if (raidMode === "raid5" || raidMode === "raidz1") {
    return targetCount >= 3
      ? null
      : i18n.t("setup.raidWarnings.threeTargets", {
          raidMode: raidModeLabel(raidMode),
        });
  }
  if (raidMode === "raid6" || raidMode === "raidz2") {
    return targetCount >= 4
      ? null
      : i18n.t("setup.raidWarnings.fourTargets", {
          raidMode: raidModeLabel(raidMode),
        });
  }
  if (raidMode === "mirror") {
    return targetCount >= 2
      ? null
      : i18n.t("setup.raidWarnings.mirrorTargets");
  }
  if (fileSystem === "zfs" && raidMode === "raid10") {
    if (targetCount < 4) return i18n.t("setup.raidWarnings.zfsRaid10Targets");
    return targetCount % 2 === 0
      ? null
      : i18n.t("setup.raidWarnings.zfsRaid10Even");
  }
  if (raidMode === "raid10") {
    return targetCount >= 4
      ? null
      : i18n.t("setup.raidWarnings.raid10Targets");
  }
  return null;
}

function PlanStep({
  plan,
  recommendation,
  pending,
  onApply,
  onBack,
}: {
  plan: StoragePlan;
  recommendation: StorageRecommendation | null;
  pending: boolean;
  onApply: (recoveryPassphrase: string) => void;
  onBack: () => void;
}) {
  const [passphrase, setPassphrase] = useState("");
  const [confirmation, setConfirmation] = useState("");
  const passphraseReady = passphrase.length > 0 && passphrase === confirmation;
  const { t } = useTranslation();

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 gap-2">
        <SummaryCell label={t("fields.layout")} value={plan.layout} />
        <SummaryCell
          label={t("fields.fileSystem")}
          value={fileSystemLabel(plan.fileSystem)}
        />
        <SummaryCell label="RAID" value={raidModeLabel(plan.raidMode)} />
        <SummaryCell
          label={t("fields.raidBackend")}
          value={
            plan.raidBackend === "mdadm"
              ? "mdadm"
              : t("setup.plan.fileSystemNative")
          }
        />
        <SummaryCell
          label={t("fields.unlock")}
          value={
            plan.unlockMode === "tpm2"
              ? t("storage.unlockModes.tpm2")
              : t("storage.unlockModes.bootloaderPassphrase")
          }
        />
        <SummaryCell label={t("fields.data")} value={plan.dataProfile} />
        <SummaryCell label={t("fields.metadata")} value={plan.metadataProfile} />
        <SummaryCell
          label={t("fields.usableCapacity")}
          value={formatBytes(plan.usableBytes)}
        />
        {recommendation ? (
          <>
            <SummaryCell
              label={t("setup.plan.oneYearEstimate")}
              value={formatBytes(recommendation.estimatedOneYearBytes)}
            />
            <SummaryCell
              label={t("setup.plan.threeYearEstimate")}
              value={formatBytes(recommendation.estimatedThreeYearBytes)}
            />
            <SummaryCell
              label={t("setup.plan.faultTolerance")}
              value={recommendation.faultTolerance}
            />
          </>
        ) : null}
      </div>

      <MiniList
        title={t("setup.plan.destructiveDevices")}
        items={plan.destructiveDevices}
      />
      <MiniList title={t("setup.plan.operations")} items={plan.operations} />
      <div className="grid gap-3 sm:grid-cols-2">
        <label className="space-y-1">
          <span className="text-sm font-medium">
            {t("setup.plan.recoveryPassphrase")}
          </span>
          <Input
            type="password"
            value={passphrase}
            autoComplete="new-password"
            onChange={(event) => setPassphrase(event.target.value)}
          />
        </label>
        <label className="space-y-1">
          <span className="text-sm font-medium">
            {t("setup.plan.confirmPassphrase")}
          </span>
          <Input
            type="password"
            value={confirmation}
            autoComplete="new-password"
            onChange={(event) => setConfirmation(event.target.value)}
          />
        </label>
      </div>
      {(plan.warnings?.length ?? 0) || recommendation?.warnings.length ? (
        <WarningList
          items={[...(plan.warnings ?? []), ...(recommendation?.warnings ?? [])]}
        />
      ) : null}

      <div className="flex flex-wrap gap-2">
        <Button
          type="button"
          onClick={() => onApply(passphrase)}
          disabled={pending || !passphraseReady}
        >
          {pending ? t("setup.plan.queued") : t("setup.plan.apply")}
        </Button>
        <Button type="button" variant="outline" onClick={onBack}>
          {t("setup.plan.back")}
        </Button>
      </div>
    </div>
  );
}

function ApplyStep({
  progress,
  statusLabel,
  message,
  succeeded,
  onRefresh,
  onContinue,
}: {
  progress: number;
  statusLabel: string;
  message: string;
  succeeded: boolean;
  onRefresh: () => void;
  onContinue: () => void;
}) {
  const { t } = useTranslation();

  return (
    <div className="flex flex-col items-center gap-5 py-2 text-center">
      <Gauge value={progress} label={`${progress}%`} caption={statusLabel} />
      <p className="max-w-sm break-all text-sm text-muted-foreground">
        {message}
      </p>
      <div className="flex flex-wrap justify-center gap-2">
        <Button type="button" variant="outline" onClick={onRefresh}>
          <ServerCog className="size-4" />
          {t("setup.apply.refreshStatus")}
        </Button>
        {succeeded ? (
          <Button type="button" onClick={onContinue}>
            {t("setup.apply.continue")}
          </Button>
        ) : null}
      </div>
    </div>
  );
}

type FamilyValues = {
  familyName: string;
  ownerDisplayName: string;
  ownerPassword: string;
  deviceName: string;
};

function FamilyStep({
  pending,
  onSubmit,
}: {
  pending: boolean;
  onSubmit: (values: FamilyValues) => void;
}) {
  const { t } = useTranslation();
  const familySchema = useMemo(
    () =>
      z.object({
        familyName: z.string().trim().min(1, t("validation.familyNameRequired")),
        ownerDisplayName: z
          .string()
          .trim()
          .min(1, t("validation.ownerNameRequired")),
        ownerPassword: z.string().min(1, t("validation.localPasswordRequired")),
        deviceName: z.string().trim().min(1, t("validation.deviceNameRequired")),
      }),
    [t],
  );
  const form = useForm<FamilyValues>({
    resolver: zodResolver(familySchema),
    defaultValues: {
      familyName: "Home",
      ownerDisplayName: "Owner",
      ownerPassword: "",
      deviceName: "Browser",
    },
  });

  return (
    <Form {...form}>
      <form className="space-y-4" onSubmit={form.handleSubmit(onSubmit)}>
        <FormField
          control={form.control}
          name="familyName"
          render={({ field }) => (
            <FormItem>
              <FormLabel>{t("fields.familyName")}</FormLabel>
              <FormControl>
                <Input {...field} />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />
        <FormField
          control={form.control}
          name="ownerDisplayName"
          render={({ field }) => (
            <FormItem>
              <FormLabel>{t("fields.ownerName")}</FormLabel>
              <FormControl>
                <Input {...field} />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />
        <FormField
          control={form.control}
          name="ownerPassword"
          render={({ field }) => (
            <FormItem>
              <FormLabel>{t("fields.localPassword")}</FormLabel>
              <FormControl>
                <Input type="password" autoComplete="new-password" {...field} />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />
        <FormField
          control={form.control}
          name="deviceName"
          render={({ field }) => (
            <FormItem>
              <FormLabel>{t("fields.thisDevice")}</FormLabel>
              <FormControl>
                <Input {...field} />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />
        <Button type="submit" className="w-full" disabled={pending}>
          <HardDrive className="size-4" />
          {pending ? t("setup.family.creating") : t("setup.family.create")}
        </Button>
      </form>
    </Form>
  );
}

function SummaryCell({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-xl border border-border/60 bg-background/30 p-3">
      <p className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
        {label}
      </p>
      <p className="mt-0.5 break-all text-sm font-medium">{value}</p>
    </div>
  );
}

function MiniList({ title, items }: { title: string; items: string[] }) {
  if (!items.length) return null;
  return (
    <div className="rounded-xl border border-border/60 bg-background/30 p-3">
      <p className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
        {title}
      </p>
      <ul className="mt-1.5 space-y-1 text-sm">
        {items.map((item) => (
          <li key={item} className="flex items-start gap-2 break-all">
            <HardDrive className="mt-0.5 size-3.5 shrink-0 text-muted-foreground" />
            {item}
          </li>
        ))}
      </ul>
    </div>
  );
}

function WarningList({ items }: { items: string[] }) {
  return (
    <ul className="space-y-1 rounded-xl border border-warning/40 bg-warning/10 p-3 text-sm text-warning-foreground">
      {items.map((item) => (
        <li key={item}>{item}</li>
      ))}
    </ul>
  );
}
