import { Images, LogOut, RefreshCw, SunMoon } from "lucide-react";
import { useNavigate } from "react-router";
import { useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { toast } from "sonner";
import {
  CommandDialog,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
  CommandSeparator,
} from "@/components/ui/command";
import { navItems } from "@/components/app-shell/nav";
import { useTheme } from "@/providers/theme-provider";
import { useLogout, useMediaIndex } from "@/hooks/queries";
import { errorMessage } from "@/lib/format";

export function CommandMenu({
  open,
  onOpenChange,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { toggle } = useTheme();
  const logout = useLogout();
  const mediaIndex = useMediaIndex();
  const { t } = useTranslation();

  function run(action: () => void) {
    onOpenChange(false);
    action();
  }

  return (
    <CommandDialog
      open={open}
      onOpenChange={onOpenChange}
      title={t("command.title")}
      description={t("command.description")}
    >
      <CommandInput placeholder={t("command.placeholder")} />
      <CommandList>
        <CommandEmpty>{t("common.noMatches")}</CommandEmpty>
        <CommandGroup heading={t("common.goTo")}>
          {navItems.map((item) => {
            const label = t(item.labelKey);
            const description = t(item.descriptionKey);
            return (
              <CommandItem
                key={item.id}
                value={`${label} ${description}`}
                onSelect={() => run(() => navigate(item.to))}
              >
                <item.icon className="size-4" />
                <span>{label}</span>
                <span className="ml-auto text-xs text-muted-foreground">
                  {description}
                </span>
              </CommandItem>
            );
          })}
        </CommandGroup>
        <CommandSeparator />
        <CommandGroup heading={t("common.actions")}>
          <CommandItem
            value={t("command.toggleThemeSearch")}
            onSelect={() => run(toggle)}
          >
            <SunMoon className="size-4" /> {t("common.toggleTheme")}
          </CommandItem>
          <CommandItem
            value={t("common.refreshData")}
            onSelect={() =>
              run(() => {
                queryClient.invalidateQueries();
                toast.success(t("common.refreshingData"));
              })
            }
          >
            <RefreshCw className="size-4" /> {t("common.refreshData")}
          </CommandItem>
          <CommandItem
            value={t("common.indexMediaLibrary")}
            onSelect={() =>
              run(() =>
                mediaIndex.mutate(undefined, {
                  onSuccess: () => toast.success(t("toast.mediaIndexUpdated")),
                  onError: (error) => toast.error(errorMessage(error)),
                }),
              )
            }
          >
            <Images className="size-4" /> {t("common.indexMediaLibrary")}
          </CommandItem>
          <CommandItem
            value={t("common.logout")}
            onSelect={() =>
              run(() =>
                logout.mutate(undefined, {
                  onSettled: () => navigate("/login"),
                }),
              )
            }
          >
            <LogOut className="size-4" /> {t("common.logout")}
          </CommandItem>
        </CommandGroup>
      </CommandList>
    </CommandDialog>
  );
}
