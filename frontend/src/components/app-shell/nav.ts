import { type ComponentType } from "react";
import {
  Archive,
  Boxes,
  Globe,
  HardDrive,
  KeyRound,
  LayoutDashboard,
  type LucideProps,
  Settings2,
  Smartphone,
  Users,
} from "lucide-react";

export type NavItem = {
  id: string;
  labelKey: string;
  descriptionKey: string;
  to: string;
  icon: ComponentType<LucideProps>;
  end?: boolean;
};

export const navItems: NavItem[] = [
  {
    id: "overview",
    labelKey: "nav.overview.label",
    descriptionKey: "nav.overview.description",
    to: "/dashboard",
    icon: LayoutDashboard,
    end: true,
  },
  {
    id: "devices",
    labelKey: "nav.devices.label",
    descriptionKey: "nav.devices.description",
    to: "/dashboard/devices",
    icon: Smartphone,
  },
  {
    id: "family",
    labelKey: "nav.family.label",
    descriptionKey: "nav.family.description",
    to: "/dashboard/family",
    icon: Users,
  },
  {
    id: "apps",
    labelKey: "nav.apps.label",
    descriptionKey: "nav.apps.description",
    to: "/dashboard/apps",
    icon: Boxes,
  },
  {
    id: "shares",
    labelKey: "nav.shares.label",
    descriptionKey: "nav.shares.description",
    to: "/dashboard/shares",
    icon: HardDrive,
  },
  {
    id: "backups",
    labelKey: "nav.backups.label",
    descriptionKey: "nav.backups.description",
    to: "/dashboard/backups",
    icon: Archive,
  },
  {
    id: "remote",
    labelKey: "nav.remote.label",
    descriptionKey: "nav.remote.description",
    to: "/dashboard/remote",
    icon: Globe,
  },
  {
    id: "vault",
    labelKey: "nav.vault.label",
    descriptionKey: "nav.vault.description",
    to: "/dashboard/vault",
    icon: KeyRound,
  },
  {
    id: "system",
    labelKey: "nav.system.label",
    descriptionKey: "nav.system.description",
    to: "/dashboard/system",
    icon: Settings2,
  },
];
