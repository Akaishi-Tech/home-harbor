import { useEffect, useState } from "react";
import { Outlet } from "react-router";
import { useTranslation } from "react-i18next";
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet";
import { Sidebar } from "@/components/app-shell/sidebar";
import { Topbar } from "@/components/app-shell/topbar";
import { CommandMenu } from "@/components/app-shell/command-menu";

export function AppShell() {
  const [commandOpen, setCommandOpen] = useState(false);
  const [navOpen, setNavOpen] = useState(false);
  const { t } = useTranslation();

  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "k") {
        event.preventDefault();
        setCommandOpen((open) => !open);
      }
    }
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, []);

  return (
    <div className="min-h-svh">
      <div className="mx-auto flex w-full max-w-[1440px] gap-4 px-3 py-4 sm:px-4">
        <aside className="hidden w-64 shrink-0 lg:block">
          <div className="glass-surface sticky top-4 h-[calc(100svh-2rem)] rounded-2xl p-2.5">
            <Sidebar />
          </div>
        </aside>

        <div className="flex min-w-0 flex-1 flex-col">
          <Topbar
            onOpenCommand={() => setCommandOpen(true)}
            onOpenNav={() => setNavOpen(true)}
          />
          <main className="flex-1 py-5">
            <Outlet />
          </main>
        </div>
      </div>

      <Sheet open={navOpen} onOpenChange={setNavOpen}>
        <SheetContent
          side="left"
          className="w-72 border-none bg-transparent p-2.5"
        >
          <SheetHeader className="sr-only">
            <SheetTitle>{t("common.navigation")}</SheetTitle>
          </SheetHeader>
          <div className="glass-surface h-full rounded-2xl p-2.5">
            <Sidebar onNavigate={() => setNavOpen(false)} />
          </div>
        </SheetContent>
      </Sheet>

      <CommandMenu open={commandOpen} onOpenChange={setCommandOpen} />
    </div>
  );
}
