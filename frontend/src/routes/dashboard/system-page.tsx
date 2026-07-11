import { type ReactNode } from "react";
import { toast } from "sonner";
import { useTranslation } from "react-i18next";
import {
  Settings2,
  RefreshCw,
  Images,
  ShieldCheck,
  HardDrive,
} from "lucide-react";
import { SectionHeader } from "@/components/glass/section-header";
import { QueryErrorState } from "@/components/glass/query-error-state";
import {
  GlassCard,
  GlassCardContent,
  GlassCardHeader,
  GlassCardTitle,
} from "@/components/glass/glass-card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { useMediaIndex, useOta, useOverview, usePolicy } from "@/hooks/queries";
import { errorMessage, formatBytes, formatDateTime } from "@/lib/format";
import { cn } from "@/lib/utils";
import { useAuth } from "@/hooks/use-auth";
import { isFamilyAdmin } from "@/lib/auth";

function StatusRow({
  label,
  children,
}: {
  label: string;
  children: ReactNode;
}) {
  return (
    <div className="flex items-center justify-between gap-3 border-b border-border/60 py-2.5 last:border-none">
      <span className="text-sm text-muted-foreground">{label}</span>
      <div className="text-right text-sm font-medium">{children}</div>
    </div>
  );
}

export function SystemPage() {
  const overview = useOverview();
  const canManage = isFamilyAdmin(useAuth());
  const ota = useOta(canManage);
  const policy = usePolicy();
  const mediaIndex = useMediaIndex();
  const { t } = useTranslation();

  const encrypted = overview.data?.security.endToEndEncryption ?? false;
  const files = overview.data?.modules.files;
  const photos = overview.data?.modules.photos;

  function onIndex() {
    mediaIndex.mutate(undefined, {
      onSuccess: () => toast.success(t("toast.mediaIndexUpdated")),
      onError: (error) => toast.error(errorMessage(error)),
    });
  }

  if (overview.isError || ota.isError || policy.isError) {
    return (
      <div className="space-y-6">
        <SectionHeader
          eyebrow={t("pages.system.eyebrow")}
          title={t("pages.system.title")}
          description={t("pages.system.description")}
        />
        <GlassCard>
          <QueryErrorState
            error={overview.error ?? ota.error ?? policy.error}
            onRetry={() => {
              void overview.refetch();
              void ota.refetch();
              void policy.refetch();
            }}
          />
        </GlassCard>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <SectionHeader
        eyebrow={t("pages.system.eyebrow")}
        title={t("pages.system.title")}
        description={t("pages.system.description")}
      />

      <div className="grid gap-3 lg:grid-cols-3">
        <GlassCard>
          <GlassCardHeader>
            <GlassCardTitle className="flex items-center gap-2">
              <Settings2 className="size-4 text-primary" />
              {t("pages.system.updates")}
            </GlassCardTitle>
          </GlassCardHeader>
          <GlassCardContent className="space-y-1">
            <StatusRow label={t("fields.version")}>
              {!canManage ? (
                t("common.unavailable")
              ) : ota.isPending ? (
                <Skeleton className="h-4 w-20" />
              ) : (
                (ota.data?.version ?? t("common.unknown"))
              )}
            </StatusRow>
            <StatusRow label={t("fields.status")}>
              {!canManage ? (
                t("common.unavailable")
              ) : ota.isPending ? (
                <Skeleton className="h-4 w-16" />
              ) : (
                (ota.data?.updateState ?? t("common.unknown"))
              )}
            </StatusRow>
            {canManage ? (
              <p className="mt-3 rounded-xl border border-warning/40 bg-warning/10 px-3 py-2 text-sm text-warning-foreground">
                {t("pages.system.updaterUnavailable")}
              </p>
            ) : null}
          </GlassCardContent>
        </GlassCard>

        <GlassCard>
          <GlassCardHeader>
            <GlassCardTitle className="flex items-center gap-2">
              <ShieldCheck className="size-4 text-primary" />
              {t("pages.system.securityStorage")}
            </GlassCardTitle>
          </GlassCardHeader>
          <GlassCardContent className="space-y-1">
            <StatusRow label={t("pages.system.endToEndEncryption")}>
              {overview.isPending ? (
                <Skeleton className="h-5 w-16" />
              ) : (
                <Badge
                  variant="outline"
                  className={cn(
                    "font-medium",
                    encrypted
                      ? "border-success/40 text-success"
                      : "border-warning/40 text-warning",
                  )}
                >
                  <ShieldCheck className="size-3.5" />
                  {encrypted
                    ? t("pages.system.enabled")
                    : t("pages.system.disabled")}
                </Badge>
              )}
            </StatusRow>
            <StatusRow label={t("pages.system.storageHealth")}>
              {overview.isPending ? (
                <Skeleton className="h-4 w-20" />
              ) : (
                <span className="inline-flex items-center gap-1.5">
                  <HardDrive className="size-3.5 text-muted-foreground" />
                  {overview.data?.storage.status ?? t("common.unknown")}
                </span>
              )}
            </StatusRow>
            <StatusRow label={t("pages.system.checkedAt")}>
              {overview.isPending ? (
                <Skeleton className="h-4 w-32" />
              ) : (
                formatDateTime(overview.data?.storage.checkedAt)
              )}
            </StatusRow>
            <StatusRow label={t("pages.system.localDataMode")}>
              {policy.isPending ? (
                <Skeleton className="h-4 w-20" />
              ) : (
                (policy.data?.storage.mode ?? t("common.unknown"))
              )}
            </StatusRow>
          </GlassCardContent>
        </GlassCard>

        <GlassCard>
          <GlassCardHeader>
            <GlassCardTitle className="flex items-center gap-2">
              <Images className="size-4 text-primary" />
              {t("pages.system.media")}
            </GlassCardTitle>
          </GlassCardHeader>
          <GlassCardContent className="space-y-3">
            <div className="space-y-1">
              <StatusRow label={t("pages.system.files")}>
                {overview.isPending ? (
                  <Skeleton className="h-4 w-24" />
                ) : files ? (
                  t("pages.system.mediaCount", {
                    count: files.count,
                    size: formatBytes(files.bytes),
                  })
                ) : (
                  t("common.unknown")
                )}
              </StatusRow>
              <StatusRow label={t("pages.system.photos")}>
                {overview.isPending ? (
                  <Skeleton className="h-4 w-24" />
                ) : photos ? (
                  t("pages.system.mediaCount", {
                    count: photos.count,
                    size: formatBytes(photos.bytes),
                  })
                ) : (
                  t("common.unknown")
                )}
              </StatusRow>
            </div>
            {canManage ? (
              <Button
                className="w-full"
                onClick={onIndex}
                disabled={mediaIndex.isPending}
              >
                <RefreshCw
                  className={cn(
                    "size-4",
                    mediaIndex.isPending && "animate-spin",
                  )}
                />
                {mediaIndex.isPending
                  ? t("pages.system.indexPending")
                  : t("common.indexMediaLibrary")}
              </Button>
            ) : null}
          </GlassCardContent>
        </GlassCard>
      </div>
    </div>
  );
}
