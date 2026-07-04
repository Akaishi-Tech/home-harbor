import { i18n } from "@/i18n";

export function formatBytes(bytes: number): string {
  if (!bytes) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let index = 0;
  while (value >= 1024 && index < units.length - 1) {
    value /= 1024;
    index++;
  }
  const maximumFractionDigits = value >= 10 || index === 0 ? 0 : 1;
  const formatted = new Intl.NumberFormat(i18n.language, {
    maximumFractionDigits,
  }).format(value);
  return `${formatted} ${units[index]}`;
}

export function clampProgress(value: number): number {
  if (!Number.isFinite(value)) return 0;
  return Math.max(0, Math.min(100, Math.round(value)));
}

export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export function formatDateTime(value: string | null | undefined): string {
  if (!value) return i18n.t("common.unknown");
  const ms = Date.parse(value);
  if (!Number.isFinite(ms)) return i18n.t("common.unknown");
  return new Date(ms).toLocaleString(i18n.language);
}

export function storageStatusLabel(state?: string): string {
  return state === "Succeeded"
    ? i18n.t("storage.status.succeeded")
    : state === "PendingReboot"
      ? i18n.t("storage.status.pendingReboot")
      : state === "Running"
        ? i18n.t("storage.status.running")
        : state === "Failed"
          ? i18n.t("storage.status.failed")
          : i18n.t("storage.status.waiting");
}
