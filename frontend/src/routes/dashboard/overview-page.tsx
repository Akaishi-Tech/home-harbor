import {
  Archive,
  Boxes,
  Globe,
  HardDrive,
  Images,
  KeyRound,
  ShieldCheck,
  Smartphone,
  FileText,
} from "lucide-react";
import { useTranslation } from "react-i18next";
import { SectionHeader } from "@/components/glass/section-header";
import { StatTile, type StatTone } from "@/components/glass/stat-tile";
import {
  GlassCard,
  GlassCardContent,
  GlassCardHeader,
  GlassCardTitle,
} from "@/components/glass/glass-card";
import { Skeleton } from "@/components/ui/skeleton";
import { Badge } from "@/components/ui/badge";
import { useOta, useOverview, usePolicy } from "@/hooks/queries";
import { formatBytes } from "@/lib/format";
import { cn } from "@/lib/utils";
import type { Overview } from "@/types";

export function OverviewPage() {
  const overview = useOverview();
  const policy = usePolicy();
  const ota = useOta();
  const { t } = useTranslation();

  return (
    <div className="space-y-6">
      <SectionHeader
        eyebrow={t("pages.overview.eyebrow")}
        title={overview.data?.family.name ?? t("pages.overview.titleFallback")}
        description={t("pages.overview.description")}
      />

      {overview.isPending ? (
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-4">
          {Array.from({ length: 8 }).map((_, index) => (
            <Skeleton key={index} className="h-32 rounded-2xl" />
          ))}
        </div>
      ) : overview.data ? (
        <StatGrid modules={overview.data.modules} />
      ) : null}

      <div className="grid gap-3 lg:grid-cols-[1.1fr_1fr]">
        <GlassCard>
          <GlassCardHeader>
            <GlassCardTitle>{t("pages.overview.systemStatus")}</GlassCardTitle>
          </GlassCardHeader>
          <GlassCardContent className="space-y-1">
            <StatusRow
              label={t("pages.overview.endToEndEncryption")}
              value={
                overview.data?.security.endToEndEncryption
                  ? t("pages.overview.enabled")
                  : t("pages.overview.disabled")
              }
              tone={
                overview.data?.security.endToEndEncryption
                  ? "success"
                  : "warning"
              }
            />
            <StatusRow
              label={t("pages.overview.storageHealth")}
              value={overview.data?.storage.status ?? t("common.unknown")}
            />
            <StatusRow
              label="OTA"
              value={
                ota.data
                  ? `${ota.data.version}${t("common.separator")}${ota.data.updateState}`
                  : t("common.unknown")
              }
            />
            <StatusRow
              label={t("pages.overview.localDataMode")}
              value={policy.data?.storage.mode ?? t("common.unknown")}
            />
          </GlassCardContent>
        </GlassCard>

        <GlassCard>
          <GlassCardHeader>
            <GlassCardTitle>{t("pages.overview.dataFootprint")}</GlassCardTitle>
          </GlassCardHeader>
          <GlassCardContent>
            {overview.data ? (
              <DataFootprint modules={overview.data.modules} />
            ) : (
              <Skeleton className="h-24" />
            )}
          </GlassCardContent>
        </GlassCard>
      </div>
    </div>
  );
}

function StatGrid({ modules }: { modules: Overview["modules"] }) {
  const { t } = useTranslation();
  const tiles: Array<{
    label: string;
    value: number | string;
    detail: string;
    icon: typeof FileText;
    tone: StatTone;
    to?: string;
  }> = [
    {
      label: t("pages.overview.files"),
      value: modules.files.count,
      detail: formatBytes(modules.files.bytes),
      icon: FileText,
      tone: "primary",
    },
    {
      label: t("pages.overview.photos"),
      value: modules.photos.count,
      detail: formatBytes(modules.photos.bytes),
      icon: Images,
      tone: "info",
    },
    {
      label: t("pages.overview.externalBackups"),
      value: modules.backups.targetCount,
      detail: modules.backups.latestJob?.state ?? t("pages.overview.notRun"),
      icon: Archive,
      tone: "warning",
      to: "/dashboard/backups",
    },
    {
      label: t("pages.overview.vault"),
      value: modules.vault.count,
      detail: modules.vault.encrypted
        ? t("pages.overview.encrypted")
        : t("pages.overview.pendingEncryption"),
      icon: KeyRound,
      tone: "success",
      to: "/dashboard/vault",
    },
    {
      label: t("pages.overview.devices"),
      value: modules.devices.count,
      detail: t("common.countSyncStates", {
        count: modules.devices.syncStates,
      }),
      icon: Smartphone,
      tone: "primary",
      to: "/dashboard/devices",
    },
    {
      label: t("pages.overview.remoteAccess"),
      value: modules.remoteAccess.peers,
      detail: "WireGuard",
      icon: Globe,
      tone: "info",
      to: "/dashboard/remote",
    },
    {
      label: t("pages.overview.smb"),
      value: modules.smb.shares,
      detail: t("common.countCredentials", {
        count: modules.smb.credentials,
      }),
      icon: HardDrive,
      tone: "primary",
      to: "/dashboard/shares",
    },
    {
      label: t("pages.overview.containers"),
      value: modules.runtime.containers,
      detail: t("common.countAppPlans", { count: modules.runtime.apps }),
      icon: Boxes,
      tone: "success",
      to: "/dashboard/apps",
    },
  ];

  return (
    <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-4">
      {tiles.map((tile) => (
        <StatTile
          key={tile.label}
          label={tile.label}
          value={tile.value}
          detail={tile.detail}
          icon={tile.icon}
          tone={tile.tone}
          to={tile.to}
        />
      ))}
    </div>
  );
}

function StatusRow({
  label,
  value,
  tone,
}: {
  label: string;
  value: string;
  tone?: "success" | "warning";
}) {
  return (
    <div className="flex items-center justify-between border-b border-border/60 py-2.5 last:border-none">
      <span className="text-sm text-muted-foreground">{label}</span>
      {tone ? (
        <Badge
          variant="outline"
          className={cn(
            "font-medium",
            tone === "success" && "border-success/40 text-success",
            tone === "warning" && "border-warning/40 text-warning",
          )}
        >
          <ShieldCheck className="size-3.5" />
          {value}
        </Badge>
      ) : (
        <span className="text-sm font-medium">{value}</span>
      )}
    </div>
  );
}

function DataFootprint({ modules }: { modules: Overview["modules"] }) {
  const { t } = useTranslation();
  const segments = [
    {
      label: t("pages.overview.files"),
      bytes: modules.files.bytes,
      className: "bg-chart-1",
    },
    {
      label: t("pages.overview.photos"),
      bytes: modules.photos.bytes,
      className: "bg-chart-3",
    },
    {
      label: t("pages.overview.localBackups"),
      bytes: modules.backups.localBytes,
      className: "bg-chart-2",
    },
  ];
  const total = segments.reduce((sum, segment) => sum + segment.bytes, 0);

  return (
    <div className="space-y-4">
      <div className="flex items-baseline gap-2">
        <span className="text-2xl font-semibold tabular-nums tracking-tight">
          {formatBytes(total)}
        </span>
        <span className="text-sm text-muted-foreground">
          {t("pages.overview.totalUsed")}
        </span>
      </div>
      <div className="flex h-3 w-full overflow-hidden rounded-full bg-muted/60">
        {total > 0
          ? segments.map((segment) => (
              <div
                key={segment.label}
                className={cn("h-full", segment.className)}
                style={{ width: `${(segment.bytes / total) * 100}%` }}
              />
            ))
          : null}
      </div>
      <div className="grid grid-cols-3 gap-2 text-sm">
        {segments.map((segment) => (
          <div key={segment.label} className="space-y-0.5">
            <div className="flex items-center gap-1.5">
              <span className={cn("size-2 rounded-full", segment.className)} />
              <span className="text-muted-foreground">{segment.label}</span>
            </div>
            <p className="font-medium tabular-nums">
              {formatBytes(segment.bytes)}
            </p>
          </div>
        ))}
      </div>
    </div>
  );
}
