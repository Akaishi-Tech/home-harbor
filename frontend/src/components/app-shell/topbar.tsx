import { Menu, RefreshCw, Search } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Button } from "@/components/ui/button";
import { LanguageToggle } from "@/components/app-shell/language-toggle";
import { ThemeToggle } from "@/components/app-shell/theme-toggle";
import { UserMenu } from "@/components/app-shell/user-menu";

export function Topbar({
  onOpenCommand,
  onOpenNav,
}: {
  onOpenCommand: () => void;
  onOpenNav: () => void;
}) {
  const queryClient = useQueryClient();
  const { t } = useTranslation();

  return (
    <header className="glass-surface sticky top-4 z-30 flex items-center gap-2 rounded-2xl px-2.5 py-2">
      <Button
        variant="ghost"
        size="icon"
        className="lg:hidden"
        aria-label={t("common.openNavigation")}
        onClick={onOpenNav}
      >
        <Menu className="size-5" />
      </Button>

      <button
        type="button"
        onClick={onOpenCommand}
        className="group flex h-9 flex-1 items-center gap-2 rounded-xl border border-border bg-background/40 px-3 text-sm text-muted-foreground transition-colors hover:text-foreground sm:max-w-xs"
      >
        <Search className="size-4" />
        <span className="flex-1 text-left">{t("common.searchCommand")}</span>
        <kbd className="hidden rounded-md border border-border bg-muted/60 px-1.5 py-0.5 font-mono text-[0.7rem] sm:inline-block">
          ⌘K
        </kbd>
      </button>

      <div className="ml-auto flex items-center gap-1">
        <Button
          variant="ghost"
          size="icon"
          aria-label={t("common.refreshData")}
          onClick={() => queryClient.invalidateQueries()}
        >
          <RefreshCw className="size-4" />
        </Button>
        <LanguageToggle />
        <ThemeToggle />
        <UserMenu />
      </div>
    </header>
  );
}
