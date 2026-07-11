import { NavLink } from "react-router";
import { useTranslation } from "react-i18next";
import { cn } from "@/lib/utils";
import { Brand } from "@/components/app-shell/brand";
import { navItems } from "@/components/app-shell/nav";
import { useAuth } from "@/hooks/use-auth";
import { isFamilyAdmin, isFamilyMember } from "@/lib/auth";

export function Sidebar({ onNavigate }: { onNavigate?: () => void }) {
  const { t } = useTranslation();
  const auth = useAuth();
  const visibleItems = navItems.filter(
    (item) =>
      !item.access ||
      (item.access === "admin" && isFamilyAdmin(auth)) ||
      (item.access === "member" && isFamilyMember(auth)),
  );

  return (
    <div className="flex h-full flex-col">
      <div className="px-2 py-3">
        <Brand />
      </div>

      <nav className="flex-1 space-y-1 overflow-y-auto px-1 py-2">
        {visibleItems.map((item) => (
          <NavLink
            key={item.id}
            to={item.to}
            end={item.end}
            onClick={onNavigate}
            className={({ isActive }) =>
              cn(
                "group flex items-center gap-3 rounded-xl px-3 py-2.5 text-sm font-medium transition-colors",
                isActive
                  ? "glass-surface text-foreground"
                  : "text-muted-foreground hover:bg-accent hover:text-foreground",
              )
            }
          >
            {({ isActive }) => (
              <>
                <item.icon
                  className={cn("size-4 shrink-0", isActive && "text-primary")}
                />
                <span>{t(item.labelKey)}</span>
              </>
            )}
          </NavLink>
        ))}
      </nav>

      <div className="px-3 py-3 text-xs text-muted-foreground">
        <p className="font-medium text-foreground/80">
          {t("common.localFirstTitle")}
        </p>
        <p>{t("common.localFirstDescription")}</p>
      </div>
    </div>
  );
}
