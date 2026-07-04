import { i18n } from "@/i18n";
import type { StorageDevice, StorageTarget } from "@/types";
import { formatBytes } from "@/lib/format";

/** Flatten the nested lsblk-style device tree into a single list. */
export function flattenStorageDevices(
  devices: StorageDevice[],
): StorageDevice[] {
  return devices.flatMap((device) => [
    device,
    ...flattenStorageDevices(device.children ?? []),
  ]);
}

/**
 * Returns the selectable path for a device, or null when it must not be
 * offered for the data pool (system / protected / removable / already mounted).
 */
export function selectableStorageDevicePath(
  device: StorageDevice,
): string | null {
  const path = device.path?.trim();
  if (
    device.type !== "disk" ||
    !path ||
    device.isSystem ||
    device.isProtected ||
    device.isRemovable ||
    hasStorageMountpoints(device)
  ) {
    return null;
  }

  return path;
}

export function storageTargetLabel(target: StorageTarget): string {
  return target.kind === "main-reserved"
    ? i18n.t("storage.target.mainReserved")
    : i18n.t("storage.target.disk");
}

export function storageTargetMeta(target: StorageTarget): string {
  return [
    target.model ?? "storage",
    formatBytes(target.sizeBytes),
    target.transport ?? "local",
  ].join(i18n.t("common.separator"));
}

export function storageTargetBadges(target: StorageTarget): string[] {
  const badges = [storageTargetLabel(target)];
  if (!target.eligible) badges.push(...target.eligibilityReasons);
  return badges;
}

export function hasStorageMountpoints(device: StorageDevice): boolean {
  return (
    device.mountpoints.length > 0 || device.children.some(hasStorageMountpoints)
  );
}

/** Human-readable badges describing a disk's role/health. */
export function diskBadges(device: StorageDevice): string[] {
  const badges: string[] = [];
  if (device.isSystem) badges.push(i18n.t("storage.disk.system"));
  if (device.isProtected) badges.push("HomeHarbor");
  if (device.isRemovable) badges.push(i18n.t("storage.disk.removable"));
  if (device.mountpoints.length) badges.push(device.mountpoints.join(", "));
  if (!device.mountpoints.length && device.children.some(hasStorageMountpoints))
    badges.push(i18n.t("storage.disk.mountedPartitions"));
  if (device.smart?.summary) badges.push(`SMART ${device.smart.summary}`);
  return badges;
}

export function diskMeta(device: StorageDevice): string {
  return [
    device.model ?? i18n.t("storage.disk.fallbackName"),
    formatBytes(device.sizeBytes),
    device.transport ?? "local",
  ].join(i18n.t("common.separator"));
}

export function isPresent<T>(value: T | null | undefined): value is T {
  return value !== null && value !== undefined;
}
